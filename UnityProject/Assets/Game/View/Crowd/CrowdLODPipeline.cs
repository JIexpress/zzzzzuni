// CrowdLODPipeline.cs —— 人群四档 LOD 管线（TASK-A-AND-09，用户绑定约束档位表）。
//
// 职责边界（简报口径）：
//  1. **数据源统一**：所有档位的实体状态（位置/速度）每帧只从 ZKernelWorld 游标
//     读一次（A-AND-08 迁移后的群体列，布局 = KernelCrowdIds：Position=0 /
//     Velocity=1 / Tag=2）——渲染档位只改表示法，不改数据源、不持有模拟状态。
//  2. **档位分配**：距离驱动 + 迟滞（升档阈值严于降档阈值，带内不振荡）+
//     重要性标记（剧情 NPC 永远 L0、永不降级）。
//  3. **数量硬上限**（CrowdLodLimits，常量不可配置）：L0>20 → 自动降级最远的
//     非剧情 L0；L1>50 → 自动降级最远的 L1；L2 预算耗尽 → 拒绝注册（返回 false）。
//     总注册数上限 = MaxL2（L2 是最宽的桶，降级必有落位）。
//  4. **表示法**：L0 = 注册时提供的 NpcAnimatorDriver NPC（A-ANIM-01 产物，
//     管线只消费：接管 transform 同步与激活开关）；L1 = 程序化低模人形
//     （CrowdLodLowPolyHumanoid，300 面）DrawMeshInstanced 批量提交；
//     L2 = billboard 剪影四边形（CrowdLodBillboardSprite）按帧转向相机批量提交。
//  5. **L3 播片（A-CUT-01 已实现，但不走分配器）**：过场由
//     <c>CutsceneDirector</c> 用 <see cref="SetAllSuppressed"/> 把整条表示层
//     全局让位给播片——L0 隐藏、L1/L2 不提交、kernel 不读。分配器**仍然永不
//     产出 L3**，因此本类的档位/hard-cap 语义（L0≤20/L1≤50/L2≤512）不受过场影响。
//
// 不是 MonoBehaviour（沿 KernelCrowdDriver 分轨约定）：注册到 ITickQueue 的
// View 档，Logic 档（模拟）先行，本管线每帧在 View 档读 kernel → 定档 → 同步表示。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game.View.Animation;
using Game.View.Effects;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace Game.View.Crowd
{
    /// <summary>
    /// 人群四档 LOD 管线（<see cref="ITickable"/> + <see cref="IDisposable"/>）。
    /// 生命周期：构造（绑定 World/相机/配置）→ <see cref="Register"/> 实体 →
    /// <see cref="Initialize"/>（入队 View 档）→ 每帧 <see cref="Tick"/> →
    /// <see cref="Dispose"/>（释放表示资产；**不**释放外部 World）。
    /// </summary>
    public sealed class CrowdLODPipeline : ITickable, IDisposable
    {
        /// <summary>L3 播片档的边界说明（本任务只留枚举位与边界文档，不实现）。</summary>
        internal const string L3BoundaryMessage =
            "L3 播片未实现（TASK-A-AND-09 边界）：需要过场/固定机位判定 + VideoPlayer 播片资产管线，" +
            "留待过场任务。分配器永不产出 L3；若到达此分支即契约被破坏。";

        private static readonly string[] L1ShaderCandidates =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
        };

        private static readonly string[] L2ShaderCandidates =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default",
        };

        /// <summary>单个注册实体的可变状态（私有嵌套；公开口径走 <see cref="CrowdLodEntityState"/>）。</summary>
        private sealed class Reg
        {
            public uint Tag;
            public bool IsStoryNpc;
            public NpcAnimatorDriver L0Representation; // null = 不可为 L0（最高 L1）
            public CrowdLodLevel Level;
            public Vector3 Position;        // kernel 位置列读回（每帧覆盖）
            public float YawDeg;            // kernel 速度列推导（速度≈0 时保持）
            public Quaternion BillboardRotation = Quaternion.identity;
            public Matrix4x4 RepresentationMatrix = Matrix4x4.identity; // 最近提交实例矩阵（L1/L2）
        }

        private readonly ZKernelWorld _world;
        private readonly Transform _camera;
        private readonly CrowdLodConfig _config;
        private readonly Dictionary<uint, int> _tagToReg = new Dictionary<uint, int>();
        private readonly List<Reg> _regs = new List<Reg>();
        private readonly Matrix4x4[] _drawBatch = new Matrix4x4[KernelCrowdIds.MaxInstancesPerDraw];

        private Matrix4x4[] _l1Matrices = new Matrix4x4[1];
        private Matrix4x4[] _l2Matrices = new Matrix4x4[1];
        private Mesh _l1Mesh;
        private Mesh _l2Quad;
        private Material _l1Material;
        private Material _l2Material;
        private bool _representationsDisabled;

        // 过场（A-CUT-01）：L3 播片期间整条表示层被全局抑制——L0 隐藏、
        // L1/L2 不提交、kernel 不再读取。L3 不通过逐实体档位产出，分配器的
        // 硬上限语义（A-AND-09）因此完全不变。
        private bool _suppressed;
        private static readonly List<CrowdLODPipeline> s_live = new List<CrowdLODPipeline>();

        private ZkMask _readMask;
        private ZkEachBatchHandler _readBatchHandler;
        private ITickQueue _queue;
        private bool _initialized;
        private bool _disposed;
        private uint _readBatches;

        /// <summary>构造管线（尚未入队）。World 须已按 A-AND-08 群体布局注册组件。</summary>
        /// <param name="world">外部 DataKernel World（数据源唯一宿主；管线不拥有、不释放）。</param>
        /// <param name="camera">距离参照相机 Transform（null = 原点，测试/离线口径）。</param>
        /// <param name="config">距离阈值配置（null = 默认迟滞带 13..17 / 36..44 m）。</param>
        public CrowdLODPipeline(ZKernelWorld world, Transform camera, CrowdLodConfig config = null)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (world.IsDisposed)
            {
                throw new ArgumentException("World 已释放", nameof(world));
            }

            _world = world;
            _camera = camera;
            _config = config ?? new CrowdLodConfig();
            _config.Validate();
            _readMask = ZkMask.Of(KernelCrowdIds.Position)
                .With(KernelCrowdIds.Velocity)
                .With(KernelCrowdIds.Tag);
            _readBatchHandler = ReadBatch;
            s_live.Add(this);
        }

        /// <summary>当前存活的管线数（过场抑制的覆盖面；测试与诊断用）。</summary>
        public static int LiveCount => s_live.Count;

        /// <summary>本管线是否被过场抑制（true = 表示层整体让位给播片）。</summary>
        public bool IsSuppressed => _suppressed;

        /// <summary>
        /// 过场门闸入口（A-CUT-01）：对**所有存活管线**统一抑制/恢复。
        /// 抑制时隐藏 L0、清零 L1/L2 计数并停止 kernel 读取——看不见的群体
        /// 不应该继续消耗 CPU。恢复后下一帧 Tick 会按距离重新定档。
        /// </summary>
        public static void SetAllSuppressed(bool suppressed)
        {
            for (int i = s_live.Count - 1; i >= 0; --i)
            {
                s_live[i].SetSuppressed(suppressed);
            }
        }

        /// <summary>单实例抑制（幂等）。</summary>
        public void SetSuppressed(bool suppressed)
        {
            if (_suppressed == suppressed)
            {
                return;
            }

            _suppressed = suppressed;
            if (!suppressed)
            {
                return;
            }

            for (int i = 0; i < _regs.Count; ++i)
            {
                DeactivateL0(_regs[i]);
            }

            LastL1Instances = 0;
            LastL2Instances = 0;
            LastL1SubmitCalls = 0;
            LastL2SubmitCalls = 0;
        }

        // ---- 只读状态 ----------------------------------------------------------

        /// <summary>已注册实体数（= 注册成功的 tag 数；硬上限 <see cref="CrowdLodLimits.MaxL2"/>）。</summary>
        public int Count => _regs.Count;

        /// <summary>当前 L0 实体数（≤ <see cref="CrowdLodLimits.MaxL0"/>，管线断言锁死）。</summary>
        public int L0Count => CountLevel(CrowdLodLevel.L0FullPrecision);

        /// <summary>当前 L1 实体数（≤ <see cref="CrowdLodLimits.MaxL1"/>）。</summary>
        public int L1Count => CountLevel(CrowdLodLevel.L1LowPoly);

        /// <summary>当前 L2 实体数（≤ <see cref="CrowdLodLimits.MaxL2"/>）。</summary>
        public int L2Count => CountLevel(CrowdLodLevel.L2Billboard);

        /// <summary>最近一次 <see cref="Tick"/> 游标累计交付行数（数据源统一的证据口径）。</summary>
        public uint LastKernelReadRows { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 游标批数（chunk 推进次数）。</summary>
        public uint LastKernelReadBatches { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 的 L1 提交：DrawMeshInstanced 真实调用次数。</summary>
        public int LastL1SubmitCalls { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 的 L2 提交：DrawMeshInstanced 真实调用次数。</summary>
        public int LastL2SubmitCalls { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 的 L1 实例数。</summary>
        public int LastL1Instances { get; private set; }

        /// <summary>最近一次 <see cref="Tick"/> 的 L2 实例数。</summary>
        public int LastL2Instances { get; private set; }

        /// <summary>表示资产可用（shader 均解析成功）；false = 档位照常分配、绘制自禁用。</summary>
        public bool IsRepresentationAvailable => !_representationsDisabled
            && _l1Material != null && _l2Material != null && _l1Mesh != null && _l2Quad != null;

        /// <summary>L1 低模人形三角面数（程序化网格的确定性口径）。</summary>
        public int L1TriangleCount => CrowdLodLowPolyHumanoid.TriangleCount;

        /// <summary>构造时的配置（只读口径；阈值不可运行期改——改档位语义须重建管线）。</summary>
        public CrowdLodConfig Config => _config;

        // ---- 注册 --------------------------------------------------------------

        /// <summary>
        /// 注册实体（注册即定初始档位并做硬上限裁决；不抛异常的容量拒绝以
        /// 返回 false 表达）。实体须已 <c>Spawn</c> 于构造时的 World 并持有
        /// Position/Velocity/Tag 三列，Tag 列值 = <paramref name="tag"/>
        /// （游标行 → 注册槽的映射键，A-AND-08 口径）。
        /// </summary>
        /// <param name="entity">kernel 实体句柄。</param>
        /// <param name="tag">该实体的 Tag 列值（管线内唯一）。</param>
        /// <param name="isStoryNpc">剧情 NPC（重要性标记）：永远 L0、永不降级。</param>
        /// <param name="l0Representation">L0 全精度表示（A-ANIM-01 的 NPC）；
        /// null = 该实体不可为 L0（最高 L1）。剧情 NPC 必须提供。</param>
        /// <returns>true = 已注册；false = 容量拒绝（总注册数或剧情 L0 槽已满）。</returns>
        /// <exception cref="ArgumentException">tag 重复 / 剧情 NPC 缺 L0 表示。</exception>
        /// <exception cref="InvalidOperationException">World 已释放。</exception>
        public bool Register(ZkEntity entity, uint tag, bool isStoryNpc, NpcAnimatorDriver l0Representation)
        {
            ThrowIfDisposed();
            if (_world.IsDisposed)
            {
                throw new InvalidOperationException("World 已释放，拒绝注册");
            }

            if (isStoryNpc && l0Representation == null)
            {
                throw new ArgumentException("剧情 NPC 必须提供 L0 全精度表示（剧情 NPC 永远 L0）");
            }

            if (_tagToReg.ContainsKey(tag))
            {
                throw new ArgumentException($"tag {tag} 已注册（Tag 列值须管线内唯一）", nameof(tag));
            }

            // L2 预算耗尽 → 拒绝（总注册数上限 = MaxL2；L3 未实现无处可降）。
            if (_regs.Count >= CrowdLodLimits.MaxL2)
            {
                return false;
            }

            // 剧情 L0 槽已满 → 拒绝（剧情 NPC 永不降级，无法腾位）。
            if (isStoryNpc && CountLevel(CrowdLodLevel.L0FullPrecision, storyOnly: true) >= CrowdLodLimits.MaxL0)
            {
                return false;
            }

            // 初始状态一次性 Copy 读（仅注册期，不计入每帧预算；每帧路径只走游标）。
            CrowdPosition kp = _world.GetComponent<CrowdPosition>(entity, KernelCrowdIds.Position);
            CrowdVelocity kv = _world.GetComponent<CrowdVelocity>(entity, KernelCrowdIds.Velocity);
            var reg = new Reg
            {
                Tag = tag,
                IsStoryNpc = isStoryNpc,
                L0Representation = l0Representation,
                Position = new Vector3(kp.X, kp.Y, kp.Z),
                YawDeg = YawFromVelocity(kv.Dx, kv.Dz, 0f),
            };
            reg.Level = AssignInitialLevel(reg);

            _regs.Add(reg);
            _tagToReg.Add(tag, _regs.Count - 1);
            if (l0Representation != null)
            {
                // 管线接管全精度 NPC 的 transform（kernel 数据驱动）；带脑视图的
                // 驱动由此进入 ManualDrive，避免双写 transform（A-ANIM-01 消费口径）。
                l0Representation.ManualDrive = true;
            }

            EnforceCaps();
            RebuildBuffers();
            return true;
        }

        /// <summary>非剧情、无 L0 表示的简化注册（纯低模/billboard 群体）。</summary>
        public bool Register(ZkEntity entity, uint tag)
        {
            return Register(entity, tag, isStoryNpc: false, l0Representation: null);
        }

        /// <summary>按 tag 读取实体 LOD 快照（无该 tag 返回 false）。</summary>
        public bool TryGet(uint tag, out CrowdLodEntityState state)
        {
            if (!_tagToReg.TryGetValue(tag, out int i))
            {
                state = default;
                return false;
            }

            Reg reg = _regs[i];
            state = new CrowdLodEntityState(reg.Level, reg.Position, reg.YawDeg,
                reg.BillboardRotation, reg.RepresentationMatrix, reg.IsStoryNpc,
                reg.L0Representation != null);
            return true;
        }

        /// <summary>按 tag 读取 L0 全精度表示（未注册/无表示返回 false）。</summary>
        public bool TryGetL0Representation(uint tag, out NpcAnimatorDriver representation)
        {
            if (_tagToReg.TryGetValue(tag, out int i) && _regs[i].L0Representation != null)
            {
                representation = _regs[i].L0Representation;
                return true;
            }

            representation = null;
            return false;
        }

        // ---- 生命周期 ----------------------------------------------------------

        /// <summary>
        /// 校验 World 布局、构建表示资产并注册到 <paramref name="queue"/> 的
        /// <see cref="TickStage.View"/> 档（Logic 档模拟先行，本管线在 View 档消费）。
        /// </summary>
        public void Initialize(ITickQueue queue)
        {
            ThrowIfDisposed();
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (_initialized)
            {
                return;
            }

            ValidateKernelLayout();
            _queue = queue;
            _initialized = true;
            _queue.Register(this, TickStage.View);
            BuildRepresentations();
        }

        /// <summary>
        /// 每帧（View 档）：① 单次游标读 kernel（Position|Velocity|Tag 三列，
        /// **唯一数据源**）；② 距离+迟滞+剧情标记定档；③ 硬上限裁决；④ 三档表示
        /// 同步/提交。World 已释放时静默返回（冻结不抛，kernel 真实性口径）。
        /// </summary>
        public void Tick(float dt)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            if (_world == null || _world.IsDisposed)
            {
                return; // kernel 真实性：数据源没了 → 渲染冻结、不抛异常
            }

            if (_suppressed)
            {
                // 过场中：表示层让位给播片，连 kernel 都不读。
                for (int i = 0; i < _regs.Count; ++i)
                {
                    DeactivateL0(_regs[i]);
                }

                LastL1Instances = 0;
                LastL2Instances = 0;
                return;
            }

            // ① 单次游标：全部档位的实体状态（无任何其它位置来源）。
            _readBatches = 0;
            LastKernelReadRows = _world.EachBatches(_readMask, _readBatchHandler);
            LastKernelReadBatches = _readBatches;

            // ② 档位重估：剧情标记 → 恒 L0；其余距离驱动 + 迟滞（每帧至多一档）。
            Vector3 camPos = CameraPosition;
            for (int i = 0; i < _regs.Count; ++i)
            {
                Reg reg = _regs[i];
                reg.Level = reg.IsStoryNpc
                    ? CrowdLodLevel.L0FullPrecision
                    : HysteresisStep(reg, camPos);
            }

            // ③ 硬上限裁决（自动降级最远者）。
            EnforceCaps();

            // ④ 表示同步：每实体恰好落在一个档位分支（切换帧新表示出现在
            //    kernel 坐标上 = 与旧表示位置/朝向连续）。
            int l1 = 0;
            int l2 = 0;
            for (int i = 0; i < _regs.Count; ++i)
            {
                Reg reg = _regs[i];
                switch (reg.Level)
                {
                    case CrowdLodLevel.L0FullPrecision:
                        SyncL0(reg);
                        break;

                    case CrowdLodLevel.L1LowPoly:
                        DeactivateL0(reg);
                        reg.RepresentationMatrix = Matrix4x4.TRS(reg.Position,
                            Quaternion.Euler(0f, reg.YawDeg, 0f), Vector3.one);
                        _l1Matrices[l1++] = reg.RepresentationMatrix;
                        break;

                    case CrowdLodLevel.L2Billboard:
                        DeactivateL0(reg);
                        Vector3 toCam = camPos - reg.Position;
                        reg.BillboardRotation = toCam.sqrMagnitude > 1e-8f
                            ? Quaternion.LookRotation(toCam)
                            : reg.BillboardRotation;
                        reg.RepresentationMatrix = Matrix4x4.TRS(reg.Position, reg.BillboardRotation, Vector3.one);
                        _l2Matrices[l2++] = reg.RepresentationMatrix;
                        break;

                    default:
                        // L3 播片：枚举位保留，分配器永不产出（边界文档见类头）。
                        throw new InvalidOperationException(L3BoundaryMessage);
                }
            }

            SubmitLevel(_l1Mesh, _l1Material, _l1Matrices, l1, isL2: false);
            SubmitLevel(_l2Quad, _l2Material, _l2Matrices, l2, isL2: true);
        }

        /// <summary>注销 tick 并释放管线自建的表示资产（幂等；外部 World 不动）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            s_live.Remove(this);
            _queue?.Unregister(this);
            _queue = null;
            DestroyObject(_l1Mesh);
            _l1Mesh = null;
            DestroyObject(_l2Quad);
            _l2Quad = null;
            DestroyObject(_l1Material);
            _l1Material = null;
            DestroyObject(_l2Material);
            _l2Material = null;
            _tagToReg.Clear();
            _regs.Clear();
        }

        /// <summary>运行期销毁自建资产（EditMode 测试环境用 DestroyImmediate，避免编辑器销毁报错）。</summary>
        private static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(obj);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        // ---- 内部：游标与档位 ----------------------------------------------------

        /// <summary>
        /// 游标批回调（View 档单线程）：三列布局（Position|Velocity|Tag →
        /// columns[0..2]，掩码升序 compId 位序）。按 Tag 列回填注册槽的
        /// 位置/朝向——三档共用的唯一位置来源。列指针只读（本管线不改数据）。
        /// </summary>
        private unsafe void ReadBatch(IntPtr[] columns, uint rows)
        {
            ++_readBatches;
            var pos = (CrowdPosition*)columns[0];
            var vel = (CrowdVelocity*)columns[1];
            var tag = (CrowdTag*)columns[2];

            for (uint r = 0; r < rows; ++r)
            {
                if (!_tagToReg.TryGetValue(tag[r].Value, out int i))
                {
                    continue; // World 内未注册到本管线的实体：跳过（不消费）
                }

                Reg reg = _regs[i];
                CrowdPosition p = pos[r];
                reg.Position = new Vector3(p.X, p.Y, p.Z);
                reg.YawDeg = YawFromVelocity(vel[r].Dx, vel[r].Dz, reg.YawDeg);
            }
        }

        /// <summary>
        /// 迟滞步进：据当前档位与距离给出本帧档位（每帧至多一档，天然防跳档）。
        /// 升档用 Up 阈值、降档用 Down 阈值，Up &lt; Down 构成迟滞带——带内保持
        /// 当前档位，边界往返不振荡。
        /// </summary>
        private CrowdLodLevel HysteresisStep(Reg reg, Vector3 camPos)
        {
            float d = Vector3.Distance(reg.Position, camPos);
            switch (reg.Level)
            {
                case CrowdLodLevel.L0FullPrecision:
                    return d > _config.L0DownDistance ? CrowdLodLevel.L1LowPoly : reg.Level;

                case CrowdLodLevel.L1LowPoly:
                    if (d > _config.L1DownDistance)
                    {
                        return CrowdLodLevel.L2Billboard;
                    }

                    return d < _config.L0UpDistance && reg.L0Representation != null
                        ? CrowdLodLevel.L0FullPrecision
                        : reg.Level;

                case CrowdLodLevel.L2Billboard:
                    return d < _config.L1UpDistance ? CrowdLodLevel.L1LowPoly : reg.Level;

                default:
                    // L3 播片：枚举位保留，分配器永不产出（边界文档见类头）。
                    throw new InvalidOperationException(L3BoundaryMessage);
            }
        }

        /// <summary>注册期初始档位（冷启动：无上一帧档位，按 Up 阈值定档）。</summary>
        private CrowdLodLevel AssignInitialLevel(Reg reg)
        {
            if (reg.IsStoryNpc)
            {
                return CrowdLodLevel.L0FullPrecision;
            }

            float d = Vector3.Distance(reg.Position, CameraPosition);
            if (d <= _config.L0UpDistance && reg.L0Representation != null)
            {
                return CrowdLodLevel.L0FullPrecision;
            }

            return d <= _config.L1UpDistance
                ? CrowdLodLevel.L1LowPoly
                : CrowdLodLevel.L2Billboard;
        }

        /// <summary>
        /// 硬上限裁决（报告选型：L0/L1 超限 → 自动降级最远者；L2 超限 → 注册拒绝）。
        /// 剧情 NPC 永不降级；最远并列时后注册者先降（确定性）。
        /// </summary>
        private void EnforceCaps()
        {
            while (CountLevel(CrowdLodLevel.L0FullPrecision) > CrowdLodLimits.MaxL0)
            {
                int victim = FarthestIndex(CrowdLodLevel.L0FullPrecision, storyOnly: false);
                if (victim < 0)
                {
                    break; // 全为剧情 NPC（注册口径已防；防御性跳出）
                }

                _regs[victim].Level = CrowdLodLevel.L1LowPoly;
            }

            while (CountLevel(CrowdLodLevel.L1LowPoly) > CrowdLodLimits.MaxL1)
            {
                int victim = FarthestIndex(CrowdLodLevel.L1LowPoly, storyOnly: false);
                if (victim < 0)
                {
                    break;
                }

                _regs[victim].Level = CrowdLodLevel.L2Billboard;
            }
        }

        /// <summary>指定档位中最远（并列取注册序最大）的非剧情实体下标；无则 -1。</summary>
        private int FarthestIndex(CrowdLodLevel level, bool storyOnly)
        {
            int best = -1;
            float bestDist = -1f;
            Vector3 camPos = CameraPosition;
            for (int i = 0; i < _regs.Count; ++i)
            {
                Reg reg = _regs[i];
                if (reg.Level != level)
                {
                    continue;
                }

                if (storyOnly && !reg.IsStoryNpc)
                {
                    continue;
                }

                if (!storyOnly && reg.IsStoryNpc)
                {
                    continue; // 降级候选永不包含剧情 NPC
                }

                float d = Vector3.Distance(reg.Position, camPos);
                if (d >= bestDist) // >= 并列取更大注册序（后注册者先降）
                {
                    bestDist = d;
                    best = i;
                }
            }

            return best;
        }

        private int CountLevel(CrowdLodLevel level, bool storyOnly = false)
        {
            int n = 0;
            for (int i = 0; i < _regs.Count; ++i)
            {
                Reg reg = _regs[i];
                if (reg.Level == level && (!storyOnly || reg.IsStoryNpc))
                {
                    ++n;
                }
            }

            return n;
        }

        // ---- 内部：表示法 --------------------------------------------------------

        /// <summary>L0：全精度 NPC 激活并同步到 kernel 位姿（位置/朝向连续的唯一来源）。</summary>
        private void SyncL0(Reg reg)
        {
            NpcAnimatorDriver rep = reg.L0Representation;
            if (rep == null)
            {
                reg.Level = CrowdLodLevel.L1LowPoly; // 防御：无表示者不可占 L0
                return;
            }

            rep.transform.SetPositionAndRotation(reg.Position,
                Quaternion.Euler(0f, reg.YawDeg, 0f));
            if (!rep.gameObject.activeSelf)
            {
                rep.gameObject.SetActive(true);
            }
        }

        /// <summary>L1/L2：全精度表示让位（每实体每帧恰激活一档表示）。</summary>
        private void DeactivateL0(Reg reg)
        {
            if (reg.L0Representation != null && reg.L0Representation.gameObject.activeSelf)
            {
                reg.L0Representation.gameObject.SetActive(false);
            }
        }

        /// <summary>按 1023 上限分批 DrawMeshInstanced（沿 KernelCrowdRenderer 判据）。</summary>
        private void SubmitLevel(Mesh mesh, Material material, Matrix4x4[] matrices, int count, bool isL2)
        {
            int submissions = 0;
            if (isL2)
            {
                LastL2Instances = count;
                LastL2SubmitCalls = 0;
            }
            else
            {
                LastL1Instances = count;
                LastL1SubmitCalls = 0;
            }

            // EditMode（测试/编辑器空闲态）无逐帧渲染管线：跳过实际提交，
            // 档位/矩阵逻辑照常走（PlayMode 含 batchmode 时 Application.isPlaying=true）。
            if (_disposed || _representationsDisabled || mesh == null || material == null
                || count <= 0 || !Application.isPlaying)
            {
                return;
            }

            int offset = 0;
            while (offset < count)
            {
                int batch = Math.Min(KernelCrowdIds.MaxInstancesPerDraw, count - offset);
                Array.Copy(matrices, offset, _drawBatch, 0, batch);
                Graphics.DrawMeshInstanced(mesh, 0, material, _drawBatch, batch, null,
                    ShadowCastingMode.Off, false);
                offset += batch;
                ++submissions;
            }

            if (isL2)
            {
                LastL2SubmitCalls = submissions;
            }
            else
            {
                LastL1SubmitCalls = submissions;
            }
        }

        private void BuildRepresentations()
        {
            _l1Mesh = CrowdLodLowPolyHumanoid.BuildMesh();
            _l2Quad = CrowdLodBillboardSprite.BuildQuad();
            _l1Material = ResolveMaterial(L1ShaderCandidates, "~CrowdLodL1Material", null);
            _l2Material = ResolveMaterial(L2ShaderCandidates, "~CrowdLodL2Material",
                CrowdLodBillboardSprite.BuildSilhouetteTexture());
            if (_l1Material == null || _l2Material == null)
            {
                _representationsDisabled = true;
                UnityEngine.Debug.LogWarning(
                    "[A-AND-09] 未解析到可用 shader，LOD 表示绘制自禁用（档位分配照常，如实受限）。");
            }
        }

        private static Material ResolveMaterial(string[] candidates, string name, Texture2D mainTexture)
        {
            foreach (string shaderName in candidates)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null || !shader.isSupported)
                {
                    continue;
                }

                var material = new Material(shader)
                {
                    name = name,
                    enableInstancing = true,
                    mainTexture = mainTexture,
                };
                if (shaderName == L2ShaderCandidates[0])
                {
                    ApplyTransparentSurface(material);
                }

                return material;
            }

            return null;
        }

        /// <summary>URP shader 的透明表面设置（剪影边缘 alpha 混合；Sprites/Default 天然透明，多余属性写入无害）。</summary>
        private static void ApplyTransparentSurface(Material material)
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private void RebuildBuffers()
        {
            int capacity = Math.Max(1, _regs.Count);
            _l1Matrices = new Matrix4x4[capacity];
            _l2Matrices = new Matrix4x4[capacity];
        }

        private void ValidateKernelLayout()
        {
            try
            {
                uint posSize = _world.GetComponentSize(KernelCrowdIds.Position);
                uint velSize = _world.GetComponentSize(KernelCrowdIds.Velocity);
                uint tagSize = _world.GetComponentSize(KernelCrowdIds.Tag);
                if (posSize != Marshal.SizeOf<CrowdPosition>()
                    || velSize != Marshal.SizeOf<CrowdVelocity>()
                    || tagSize != Marshal.SizeOf<CrowdTag>())
                {
                    throw new InvalidOperationException(
                        "World 组件布局与 A-AND-08 群体口径不符（KernelCrowdIds.Position/Velocity/Tag）");
                }
            }
            catch (ZKernelException e)
            {
                throw new InvalidOperationException(
                    "CrowdLODPipeline 要求 World 先注册群体组件（KernelCrowdIds 布局，A-AND-08 口径）", e);
            }
        }

        private Vector3 CameraPosition => _camera != null ? _camera.position : Vector3.zero;

        /// <summary>kernel 速度列 → yaw（度）；速度≈0 保持 fallback（与 NpcAnimatorDriver 的位移朝向同式）。</summary>
        private static float YawFromVelocity(float dx, float dz, float fallback)
        {
            return dx * dx + dz * dz > 1e-8f
                ? Mathf.Atan2(dx, dz) * Mathf.Rad2Deg
                : fallback;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CrowdLODPipeline));
            }
        }
    }
}
