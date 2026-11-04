// KernelCrowdDriver.cs —— 千人演练场群体驱动（TASK-A-R9-13）。
// 混合架构的「kernel 管群体」半边：单个 ZKernelWorld 实例持有 1000 实体的
// {Position, Velocity, Tag} SoA 数据；经 ITickQueue 的 Logic 档每帧驱动，
// 用 EachBatches 游标批量读列 → 积分 → SetComponent 回写。
// 不是 MonoBehaviour，不实现 Update：与英雄侧（MonoBehaviour FSM）严格分轨。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体模拟驱动（<see cref="ITickable"/> + <see cref="IDisposable"/>）。
    /// 生命周期：<see cref="Initialize"/> 建 World/注册/生成 → 每帧
    /// <see cref="Tick"/>（Logic 档）→ <see cref="Dispose"/> 注销并释放原生 World
    /// （原生句柄表仅 8 槽，必须显式释放）。
    /// </summary>
    /// <remarks>
    /// 回写路径的取舍：游标只交付 SoA 列指针、不交付实体句柄，故用 Tag 列携带
    /// 实体序号（0..N-1）定位 <c>_entities[i]</c>，不依赖「行序 == spawn 序」这一
    /// 内部实现细节。积分结果先按序号收集，游标耗尽后统一 SetComponent 回写
    /// （SetComponent 不改布局、安全；放在迭代外还能避免迭代内写回影响后续批的读取）。
    /// 回调内全程零布局变更（无 Spawn/Destroy/Add/Remove）。
    /// </remarks>
    public sealed class KernelCrowdDriver : ITickable, IDisposable
    {
        private readonly int _count;
        private readonly ZkEntity[] _entities;
        private readonly Matrix4x4[] _matrices;
        private readonly float[] _posBuf;
        private readonly float[] _velBuf;
        private readonly int[] _tagBuf;
        private readonly CrowdPosition[] _pendingPos;
        private readonly CrowdVelocity[] _pendingVel;
        private readonly bool[] _seen;

        private ZKernelWorld _world;
        private ITickQueue _queue;
        private ZkMask _mask;
        private bool _initialized;
        private bool _disposed;

        /// <summary>构造驱动（尚未创建原生 World）。</summary>
        /// <param name="entityCount">实体数；默认 <see cref="KernelCrowdIds.DefaultEntityCount"/>。</param>
        public KernelCrowdDriver(int entityCount = KernelCrowdIds.DefaultEntityCount)
        {
            if (entityCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entityCount), "实体数必须 > 0");
            }

            _count = entityCount;
            _entities = new ZkEntity[entityCount];
            _matrices = new Matrix4x4[entityCount];
            _posBuf = new float[entityCount * 3];
            _velBuf = new float[entityCount * 3];
            _tagBuf = new int[entityCount];
            _pendingPos = new CrowdPosition[entityCount];
            _pendingVel = new CrowdVelocity[entityCount];
            _seen = new bool[entityCount];
        }

        /// <summary>实体数。</summary>
        public int EntityCount => _count;

        /// <summary>原生 World 句柄包装（测试读 GetStats / 做 roundtrip 用）。未初始化时为 null。</summary>
        public ZKernelWorld World => _world;

        /// <summary>实例矩阵数组（与 <see cref="KernelCrowdRenderer"/> 共享，零拷贝）。</summary>
        public Matrix4x4[] Matrices => _matrices;

        /// <summary>最近一次 <see cref="Tick"/> 的游标累计交付行数（应等于实体数）。</summary>
        public uint LastTotalRows { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 的「内核迭代」耗时（微秒）：游标读 + 积分 + 回写 + 矩阵构造。</summary>
        public double LastKernelIterationUs { get; private set; }

        /// <summary>已释放。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 创建单个 <see cref="ZKernelWorld"/>、注册 3 个组件、生成实体并写入初始数据，
        /// 最后把自己注册到 <paramref name="queue"/> 的 <see cref="TickStage.Logic"/> 档
        /// （Logic 在暂停时被 GameLoopDriver 冻结，符合玩法语义）。
        /// </summary>
        public void Initialize(ITickQueue queue)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (_initialized)
            {
                return;
            }

            _queue = queue;
            _world = new ZKernelWorld();
            _world.RegisterComponent<CrowdPosition>(KernelCrowdIds.Position);
            _world.RegisterComponent<CrowdVelocity>(KernelCrowdIds.Velocity);
            _world.RegisterComponent<CrowdTag>(KernelCrowdIds.Tag);
            _mask = ZkMask.Of(KernelCrowdIds.Position)
                .With(KernelCrowdIds.Velocity)
                .With(KernelCrowdIds.Tag);

            for (int i = 0; i < _count; ++i)
            {
                ZkEntity entity = _world.Spawn(_mask);
                _entities[i] = entity;
                var pos = new CrowdPosition(i, 0.25f, 0f);
                var vel = new CrowdVelocity(0.5f + (i % 3) * 0.1f, 0f, 0f);
                _world.SetComponent(entity, KernelCrowdIds.Position, pos);
                _world.SetComponent(entity, KernelCrowdIds.Velocity, vel);
                _world.SetComponent(entity, KernelCrowdIds.Tag, new CrowdTag((uint)i));
                _matrices[i] = BuildMatrix(pos.X, pos.Y, (uint)i);
            }

            _initialized = true;
            _queue.Register(this, TickStage.Logic);
        }

        /// <summary>
        /// 每帧（Logic 档）：EachBatches 游标批量读三列 → 积分 → 游标耗尽后
        /// 按 Tag 序号 SetComponent 回写 → 重建实例矩阵。计时只覆盖本方法（内核迭代段）。
        /// </summary>
        public void Tick(float dt)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            long t0 = Stopwatch.GetTimestamp();
            Array.Clear(_seen, 0, _count);
            uint total = _world.EachBatches(_mask, (columns, rows) =>
            {
                int n = (int)rows * 3;
                Marshal.Copy(columns[0], _posBuf, 0, n);
                Marshal.Copy(columns[1], _velBuf, 0, n);
                Marshal.Copy(columns[2], _tagBuf, 0, (int)rows);
                for (uint r = 0; r < rows; ++r)
                {
                    uint tag = (uint)_tagBuf[r];
                    if (tag >= (uint)_count)
                    {
                        continue;
                    }

                    float x = _posBuf[r * 3];
                    float y = _posBuf[r * 3 + 1];
                    float z = _posBuf[r * 3 + 2];
                    float dx = _velBuf[r * 3];
                    float dy = _velBuf[r * 3 + 1];
                    float dz = _velBuf[r * 3 + 2];

                    float nx = x + dx * dt;
                    if (nx >= _count)
                    {
                        nx -= _count; // 序号回绕，保持圆盘稳定
                    }

                    _pendingPos[tag] = new CrowdPosition(nx, y + dy * dt, z + dz * dt);
                    _pendingVel[tag] = new CrowdVelocity(dx, dy, dz);
                    _seen[tag] = true;
                }
            });

            LastTotalRows = total;

            for (int i = 0; i < _count; ++i)
            {
                if (!_seen[i])
                {
                    continue;
                }

                _world.SetComponent(_entities[i], KernelCrowdIds.Position, _pendingPos[i]);
                _world.SetComponent(_entities[i], KernelCrowdIds.Velocity, _pendingVel[i]);
                _matrices[i] = BuildMatrix(_pendingPos[i].X, _pendingPos[i].Y, (uint)i);
            }

            LastKernelIterationUs = ElapsedUs(t0);
        }

        /// <summary>
        /// 纯只读游标拉取（pos.X 全量求和），等价 B-R9-17 的 10.0 µs 基线口径：
        /// 供性能守卫把「游标迭代本身」与「含 SetComponent 回写的每帧更新」分开对照。
        /// </summary>
        public double PullSum()
        {
            if (_disposed || !_initialized)
            {
                return 0.0;
            }

            double sum = 0.0;
            _world.EachBatches(_mask, (columns, rows) =>
            {
                int n = (int)rows * 3;
                Marshal.Copy(columns[0], _posBuf, 0, n);
                for (uint r = 0; r < rows; ++r)
                {
                    sum += _posBuf[r * 3];
                }
            });

            return sum;
        }

        /// <summary>注销 tick 并释放原生 World（幂等）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            _queue?.Unregister(this);
            _queue = null;
            _world?.Dispose();
            _world = null;
        }

        private Matrix4x4 BuildMatrix(float x, float y, uint tag)
        {
            float angle = x * KernelCrowdIds.GoldenAngle;
            float radius = KernelCrowdIds.FieldRadius *
                Mathf.Sqrt((x + 0.5f) / _count);
            var world = new Vector3(
                Mathf.Cos(angle) * radius,
                y + 0.3f + (tag % 5u) * 0.15f,
                Mathf.Sin(angle) * radius);
            return Matrix4x4.TRS(world, Quaternion.identity,
                Vector3.one * KernelCrowdIds.InstanceScale);
        }

        private static double ElapsedUs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0 / Stopwatch.Frequency;
        }
    }
}
