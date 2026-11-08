// KernelCrowdDriver.cs —— 千人演练场群体驱动（TASK-A-R9-13 建，TASK-A-AND-08 规模化改造）。
// 混合架构的「kernel 管群体」半边：单个 ZKernelWorld 实例持有 1000 实体的
// {Position, Velocity, Tag, Health, Alive} 五列 SoA 数据；经 ITickQueue 的 Logic 档
// 每帧驱动。
//
// A-AND-08 的三条规模化口径（与 A-R9-13 的差别）：
//  1. **每帧更新全程游标原地读写，零逐实体 P/Invoke**：A-R9-13 是「游标读 →
//     积分 → 逐实体 SetComponent 回写」（每帧 1000×2 次 P/Invoke）；本版在
//     EachBatches 回调内经列指针直接读写原生 SoA（ABI 契约：回调内禁布局变更、
//     列内容可经指针原地读写），每帧只剩 1 次 Begin + (批数+1) 次 Next × 2 段。
//     托管影子数组（_posBuf/_pendingPos/_seen 等）全部删除——防「名义迁移实际还是数组」。
//  2. **状态五列全在 kernel**：Position/Velocity/Tag（A-R9-13）+ Health/Alive
//     （A-AND-08 新增）。生命衰减→死亡（Alive 置 0）→定时重生（Alive 置 1）
//     全部是列数据的原地写入，零布局变更。
//  3. **渲染矩阵从 kernel 列构建**：第二段只读游标（Position+Tag+Alive）把
//     kernel 位置列写成实例矩阵——矩阵是 DrawMeshInstanced 的托管接口拷贝，
//     不是模拟状态；模拟状态唯一宿主是原生 World。
//
// 不是 MonoBehaviour，不实现 Update：与英雄侧（MonoBehaviour FSM）严格分轨。

using System;
using System.Diagnostics;
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
    /// 原地读写的安全边界：游标回调内只做列数据写入（位置积分 / 生命衰减 / 存活
    /// 翻转），禁止 Spawn/Destroy/Add/Remove（原生以布局版本号防护）；行与行之间
    /// 无数据依赖，同一行「读旧值 → 写新值」自洽，跨 chunk 不互相污染。
    /// 托管侧仅保留两样东西：<see cref="_entities"/>（生成时拿到的代际句柄，读回
    /// 断言用）与 <see cref="_matrices"/>（DrawMeshInstanced 的托管接口拷贝）。
    /// </remarks>
    public sealed class KernelCrowdDriver : ITickable, IDisposable
    {
        private readonly int _count;
        private readonly ZkEntity[] _entities;
        private readonly Matrix4x4[] _matrices;

        private ZKernelWorld _world;
        private ITickQueue _queue;
        private ZkMask _behaviorMask; // Position|Velocity|Tag|Health|Alive
        private ZkMask _matrixMask;   // Position|Tag|Alive
        private ZkMask _pullMask;     // Position（B-R9-17 基线口径的只读拉取）
        private bool _initialized;
        private bool _disposed;

        // 游标批回调（实例方法委托，Initialize 时各建一次——Tick 路径零委托分配）；
        // 跨回调状态经字段传递（Logic 档单线程，无重入）。
        private ZkEachBatchHandler _behaviorBatchHandler;
        private ZkEachBatchHandler _matrixBatchHandler;
        private ZkEachBatchHandler _pullBatchHandler;
        private float _dt;
        private int _aliveSeen;
        private int _behaviorBatches;
        private int _matrixBatches;
        private double _sumAccumulator;

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
        }

        /// <summary>实体数。</summary>
        public int EntityCount => _count;

        /// <summary>原生 World 句柄包装（测试读 GetStats / 组件读回用）。未初始化时为 null。</summary>
        public ZKernelWorld World => _world;

        /// <summary>实例矩阵数组（与 <see cref="KernelCrowdRenderer"/> 共享，零拷贝）。
        /// 这是渲染接口拷贝；模拟状态的唯一宿主是原生 World。</summary>
        public Matrix4x4[] Matrices => _matrices;

        /// <summary>生成时的实体代际句柄（组件读回断言用；index 须在 0..EntityCount-1）。</summary>
        public ZkEntity GetEntity(int index)
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _entities[index];
        }

        /// <summary>最近一次 <see cref="Tick"/> 的行为段游标累计交付行数（应等于实体数；死亡行仍交付）。</summary>
        public uint LastTotalRows { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 行为段的游标批数（= chunk 推进次数）。</summary>
        public uint LastBehaviorBatches { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 矩阵段的游标批数。</summary>
        public uint LastMatrixBatches { get; private set; }

        /// <summary>
        /// 最近一次 <see cref="Tick"/> 结束时存活实体数（矩阵段从 kernel Alive 列统计）。
        /// </summary>
        public int LastAliveCount { get; private set; }

        /// <summary>
        /// 最近一次 <see cref="Tick"/> 的 ZK_* P/Invoke 次数（按真实批数推导，非估算猜测）：
        /// 每段游标 = 1 次 ZK_EachBatchBegin + (批数 + 1) 次 ZK_EachBatchNext（末次返回 0 终止）；
        /// 两段合计 = 2×Begin + (LastBehaviorBatches + LastMatrixBatches + 2)×Next。
        /// 行为/矩阵段内零 SetComponent/GetComponent——这是对 A-R9-13（每帧 ~2003 次）
        /// 的结构性修正，满足「每帧一次 Begin+若干 Next」预算。
        /// </summary>
        public int LastTickPInvokeCalls => 4 + (int)(LastBehaviorBatches + LastMatrixBatches);

        /// <summary>最近一次 <see cref="Tick"/> 的「内核迭代」耗时（微秒）：两段游标 + 积分 + 矩阵构造。</summary>
        public double LastKernelIterationUs { get; private set; }

        /// <summary>
        /// kernel 真实性：原生 World 存活（已建且未销毁）。为 false 时 <see cref="Tick"/>
        /// 直接返回（行为停止、矩阵冻结）——world 销毁 → 行为停止的断言依据。
        /// </summary>
        public bool KernelAlive => _initialized && !_disposed && _world != null && !_world.IsDisposed;

        /// <summary>已释放。</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// 创建单个 <see cref="ZKernelWorld"/>、注册 5 个组件、生成实体并写入初始数据
        /// （初始数据走 SetComponent——仅初始化一次，不计入每帧预算），最后把自己注册到
        /// <paramref name="queue"/> 的 <see cref="TickStage.Logic"/> 档
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
            _world.RegisterComponent<CrowdHealth>(KernelCrowdIds.Health);
            _world.RegisterComponent<CrowdAlive>(KernelCrowdIds.Alive);
            _behaviorMask = ZkMask.Of(KernelCrowdIds.Position)
                .With(KernelCrowdIds.Velocity)
                .With(KernelCrowdIds.Tag)
                .With(KernelCrowdIds.Health)
                .With(KernelCrowdIds.Alive);
            _matrixMask = ZkMask.Of(KernelCrowdIds.Position)
                .With(KernelCrowdIds.Tag)
                .With(KernelCrowdIds.Alive);
            _pullMask = ZkMask.Of(KernelCrowdIds.Position);
            _behaviorBatchHandler = BehaviorBatch;
            _matrixBatchHandler = MatrixBatch;
            _pullBatchHandler = PullBatch;

            for (int i = 0; i < _count; ++i)
            {
                ZkEntity entity = _world.Spawn(_behaviorMask);
                _entities[i] = entity;
                var pos = new CrowdPosition(i, 0.25f, 0f);
                var vel = new CrowdVelocity(0.5f + (i % 3) * 0.1f, 0f, 0f);
                _world.SetComponent(entity, KernelCrowdIds.Position, pos);
                _world.SetComponent(entity, KernelCrowdIds.Velocity, vel);
                _world.SetComponent(entity, KernelCrowdIds.Tag, new CrowdTag((uint)i));
                _world.SetComponent(entity, KernelCrowdIds.Health,
                    new CrowdHealth(KernelCrowdIds.InitialHealth((uint)i)));
                _world.SetComponent(entity, KernelCrowdIds.Alive, CrowdAlive.Yes);
                _matrices[i] = BuildMatrix(pos.X, pos.Y, (uint)i);
            }

            _initialized = true;
            _queue.Register(this, TickStage.Logic);
        }

        /// <summary>
        /// 每帧（Logic 档）：① 行为段——五列游标**原地读写**（积分/回绕 + 生命衰减 +
        /// 死亡/存活翻转，零逐实体 P/Invoke）；② 矩阵段——只读游标从 kernel
        /// Position/Tag/Alive 列重建实例矩阵（死亡实例矩阵置零 = 不可见）。
        /// 计时只覆盖本方法（内核迭代段）。World 已销毁时直接返回（行为停止）。
        /// </summary>
        public void Tick(float dt)
        {
            if (!KernelAlive)
            {
                return; // kernel 真实性：World 没了 → 行为停止（矩阵冻结、不抛异常）
            }

            long t0 = Stopwatch.GetTimestamp();
            _dt = dt;
            _behaviorBatches = 0;
            _matrixBatches = 0;
            _aliveSeen = 0;
            LastTotalRows = _world.EachBatches(_behaviorMask, _behaviorBatchHandler);
            _ = _world.EachBatches(_matrixMask, _matrixBatchHandler);
            LastBehaviorBatches = (uint)_behaviorBatches;
            LastMatrixBatches = (uint)_matrixBatches;
            LastAliveCount = _aliveSeen;
            LastKernelIterationUs = ElapsedUs(t0);
        }

        /// <summary>
        /// 纯只读游标拉取（Position 列全量求和），等价 B-R9-17 的 10.0 µs 基线口径：
        /// 供性能守卫把「游标迭代本身」与「含原地写回的每帧更新」分开对照。
        /// World 已销毁时返回 0（行为停止口径）。
        /// </summary>
        public double PullSum()
        {
            if (!KernelAlive)
            {
                return 0.0;
            }

            _sumAccumulator = 0.0;
            _ = _world.EachBatches(_pullMask, _pullBatchHandler);
            return _sumAccumulator;
        }

        /// <summary>注销 tick 并释放原生 World（幂等；World 被外部销毁时亦安全）。</summary>
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

        // ---- 游标批回调（Logic 档单线程调用；状态经字段传递） ----------------------

        /// <summary>
        /// 行为段：五列布局（升序 compId 位序 0..4 → columns[0..4]，见
        /// <see cref="KernelCrowdIds.BehaviorColumnCount"/>）。
        /// 存活行：积分位置（X 序号回绕）+ 生命衰减，衰减到 ≤0 即死亡（Alive=0，
        /// Health 置重生窗起点 -Delay·Heal）；死亡行：生命回复，回到 ≥0 即重生
        /// （Alive=1，Health 重置为该实体初始值）。全部为列指针原地写，零 P/Invoke。
        /// </summary>
        private unsafe void BehaviorBatch(IntPtr[] columns, uint rows)
        {
            ++_behaviorBatches;
            var pos = (CrowdPosition*)columns[0];
            var vel = (CrowdVelocity*)columns[1];
            var tag = (CrowdTag*)columns[2];
            var health = (CrowdHealth*)columns[3];
            var alive = (CrowdAlive*)columns[4];
            float dt = _dt;

            for (uint r = 0; r < rows; ++r)
            {
                if (alive[r].Value != 0)
                {
                    CrowdPosition p = pos[r];
                    CrowdVelocity v = vel[r];
                    float nx = p.X + v.Dx * dt;
                    if (nx >= _count)
                    {
                        nx -= _count; // 序号回绕，保持圆盘稳定（A-R9-13 同款）
                    }

                    pos[r] = new CrowdPosition(nx, p.Y + v.Dy * dt, p.Z + v.Dz * dt);

                    float h = health[r].Value - KernelCrowdIds.DecayPerSecond * dt;
                    if (h <= 0f)
                    {
                        health[r] = new CrowdHealth(
                            -KernelCrowdIds.RespawnDelaySeconds * KernelCrowdIds.RespawnHealPerSecond);
                        alive[r] = CrowdAlive.No;
                    }
                    else
                    {
                        health[r] = new CrowdHealth(h);
                    }
                }
                else
                {
                    float h = health[r].Value + KernelCrowdIds.RespawnHealPerSecond * dt;
                    if (h >= 0f)
                    {
                        health[r] = new CrowdHealth(KernelCrowdIds.InitialHealth(tag[r].Value));
                        alive[r] = CrowdAlive.Yes;
                    }
                    else
                    {
                        health[r] = new CrowdHealth(h);
                    }
                }
            }
        }

        /// <summary>
        /// 矩阵段：三列布局（Position|Tag|Alive → columns[0..2]），只读。
        /// 存活行按 kernel 位置列重建实例矩阵并计数；死亡行矩阵置零（缩放 0 =
        /// 不可见；实例数不变，单次 DrawMeshInstanced 提交判据不受影响）。
        /// </summary>
        private unsafe void MatrixBatch(IntPtr[] columns, uint rows)
        {
            ++_matrixBatches;
            var pos = (CrowdPosition*)columns[0];
            var tag = (CrowdTag*)columns[1];
            var alive = (CrowdAlive*)columns[2];

            for (uint r = 0; r < rows; ++r)
            {
                uint t = tag[r].Value;
                if (t >= (uint)_count)
                {
                    continue; // 防御：tag 列损坏时不越界写矩阵（A-R9-13 同款守卫）
                }

                if (alive[r].Value != 0)
                {
                    _matrices[t] = BuildMatrix(pos[r].X, pos[r].Y, t);
                    ++_aliveSeen;
                }
                else
                {
                    _matrices[t] = Matrix4x4.zero;
                }
            }
        }

        /// <summary>只读拉取段：单列（Position）全量求和（B-R9-17 基线口径）。</summary>
        private unsafe void PullBatch(IntPtr[] columns, uint rows)
        {
            var pos = (CrowdPosition*)columns[0];
            double sum = _sumAccumulator;
            for (uint r = 0; r < rows; ++r)
            {
                sum += pos[r].X;
            }

            _sumAccumulator = sum;
        }

        /// <summary>实例矩阵公式（单一事实源，<see cref="CrowdArrayBaseline"/> 共用）。</summary>
        internal static Matrix4x4 BuildMatrix(float x, float y, uint tag, int count)
        {
            float angle = x * KernelCrowdIds.GoldenAngle;
            float radius = KernelCrowdIds.FieldRadius *
                Mathf.Sqrt((x + 0.5f) / count);
            var world = new Vector3(
                Mathf.Cos(angle) * radius,
                y + 0.3f + (tag % 5u) * 0.15f,
                Mathf.Sin(angle) * radius);
            return Matrix4x4.TRS(world, Quaternion.identity,
                Vector3.one * KernelCrowdIds.InstanceScale);
        }

        private Matrix4x4 BuildMatrix(float x, float y, uint tag)
        {
            return BuildMatrix(x, y, tag, _count);
        }

        private static double ElapsedUs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0 / Stopwatch.Frequency;
        }
    }
}
