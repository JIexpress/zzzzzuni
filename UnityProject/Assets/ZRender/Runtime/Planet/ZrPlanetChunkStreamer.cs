// ZrPlanetChunkStreamer.cs —— 行星 chunk 流式管理器（TASK-B-PLANET-07，设计稿 §11-07）。
//
// 职责（简报 §1-1）：按相机位置做可见性驱动的 ChunkCreate/ChunkDestroy/ZR_Planet_SetLod
// 决策，并把 B-PLANET-04 实测的单 chunk 生成成本（远档 ≈516µs / 近档 ≈7,446µs，
// TASK-B-PLANET-04.md E8）摊布到多帧上——预算内才发操作，防 7.4ms 级单帧尖峰。
//
// 三条选型（施工报告有完整论证）：
//  1. SetLod 决策 = PlanetLodLevel（planet_math.h:454）迟滞口径的 C# 逐式镜像
//     <see cref="DecideLodLevel"/>（严格不等式边界：恰在阈值不切换）。不用 P/Invoke
//     纯函数决策：原生没有"决策"导出（六导出只管应用），新增导出要动 Native（越本
//     任务白名单）；且决策是 O(可见 chunk) 的托管纯数学，逐 chunk 跨 ABI 反而更贵。
//  2. 分帧预算 = 成本加权预算（每 tick 估算工作量 ≤ MaxWorkMicrosPerTick）+ 近档硬帽
//     MaxNearTierOpsPerTick=1。成本估算含原生侧 RenderFrame 的 seam 核查摊入（见常量注释）。
//  3. 可见性口径 = 原生地平线曲率剔除的托管镜像（unity_target.cpp 帧首 (d) 段：
//     horizonAng + chunk 半角 + 0.05 rad 裕度），并额外加一个 chunk 半角的销毁迟滞带，
//     防在地平线边界上反复 Create/Destroy。
//
// G6 双快照同步责任（B-PLANET-04 裁决转交本任务定契约）：<see cref="Initialize"/> 是
// **全进程唯一**的托管 SetWorld 调用点——zplugin 权威快照与渲染侧镜像在同一次 SetWorld
// 调用内先后写入（B-PLANET-04 G6 实现），托管侧只调一次就无漂移窗口；v1 初始化期契约
// （设计稿 §6.1/§12-11 单行星静态语义）。每帧接线方（WorldOriginPlanetLink）只推
// SetCameraOrigin，绝不触碰 SetWorld。
//
// 线程：主线程专用（与 ChunkCreate/SetLod 的原生主线程契约一致，设计稿 §8）。
// 分配纪律：表/排名/操作列表全部预分配复用，Tick 稳态零 GC 分配。

using System;
using System.Collections.Generic;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Planet
{
    /// <summary>流式管理器配置（全部字段都有守卫，非法值在 Initialize 抛 ArgumentException）。</summary>
    public struct ZrPlanetStreamConfig
    {
        /// <summary>行星中心绝对坐标（行星中心系，米；与 SetWorld 的 centerAbs 同源）。</summary>
        public double CenterAbsX;
        public double CenterAbsY;
        public double CenterAbsZ;

        /// <summary>地形噪声种子（SetWorld 与 SampleHeight 共用；B-TECH-04 确定性契约）。</summary>
        public uint Seed;

        /// <summary>面内 chunk 格数（1..4096；现状 32——设计稿 §4.2）。</summary>
        public uint CellsPerEdge;

        /// <summary>近档距离阈值（米；B-TECH-04 口径 900km）。</summary>
        public double NearDistMeters;

        /// <summary>迟滞半带宽（米；B-TECH-04 口径 ±90km）。</summary>
        public double HystMeters;

        /// <summary>每 tick 估算工作量上限（微秒）。默认 9000 ≈ 1 次近档全成本 + 零头。</summary>
        public int MaxWorkMicrosPerTick;

        /// <summary>每 tick 近档操作（创建/升档）硬帽。默认 1——近档单次真实成本 ≈8.4ms，
        /// 放行 2 次就是 16.8ms 尖峰，会吃光整个 60fps 帧预算。</summary>
        public int MaxNearTierOpsPerTick;

        /// <summary>远档操作估算成本（微秒）——见 <see cref="ZrPlanetChunkStreamer"/> 成本注释。</summary>
        public int EstimatedFarChunkMicros;

        /// <summary>近档操作估算成本（微秒）。</summary>
        public int EstimatedNearChunkMicros;

        /// <summary>B-TECH-04 缺省口径（900km / ±90km / 32×32）+ B-PLANET-04 成本实测的预算默认值。</summary>
        public static ZrPlanetStreamConfig CreateDefault(double centerX, double centerY, double centerZ, uint seed)
        {
            return new ZrPlanetStreamConfig
            {
                CenterAbsX = centerX,
                CenterAbsY = centerY,
                CenterAbsZ = centerZ,
                Seed = seed,
                CellsPerEdge = 32u,
                NearDistMeters = 900_000.0,
                HystMeters = 90_000.0,
                MaxWorkMicrosPerTick = 9000,
                MaxNearTierOpsPerTick = 1,
                EstimatedFarChunkMicros = 700,
                EstimatedNearChunkMicros = 8500,
            };
        }
    }

    /// <summary>一次流式操作（Tick 后可读；列表为复用缓冲，只在下一次 Tick 前有效）。</summary>
    public enum ZrPlanetStreamOpKind
    {
        /// <summary>ChunkCreate 成功。</summary>
        Create,
        /// <summary>SetLod 升档（远→近）。</summary>
        SetLodUpgrade,
        /// <summary>SetLod 退档（近→远）。</summary>
        SetLodDowngrade,
        /// <summary>ChunkDestroy（可见性出局）。</summary>
        Destroy,
    }

    /// <summary>一次流式操作的记录（EstimatedMicros = 计入预算的原生侧估算成本）。</summary>
    public struct ZrPlanetStreamOp
    {
        public ZrPlanetStreamOpKind Kind;
        public uint FaceId;
        public uint Cx;
        public uint Cy;
        public uint LodLevel;
        public int EstimatedMicros;
    }

    /// <summary>
    /// chunk 流式管理器（纯 C#，无 MonoBehaviour——由 <see cref="ZrPlanetStreamingDriver"/>
    /// 或测试直接驱动 <see cref="Tick"/>）。
    ///
    /// 成本模型（估算常量的构成，全部有来源）：
    ///  - 远档 700µs = ChunkCreate 全路径实测 516µs（B-PLANET-04 E8）+ seam 核查扫描
    ///    ~80µs（4 邻 × O(8192) 槽扫，G4 估算 0.5s/6144 块）+ 有邻居时共享边重生成
    ///    ~110µs（4 缘 × 16 顶点 × 2 侧 × ~1.7µs/顶点，3×3 网格均值量级）。
    ///  - 近档 8500µs = 实测 7,446µs + 扫描 ~80µs + 有邻居重生成 ~880µs
    ///    （4 缘 × 64 × 2 × ~1.7µs）。
    ///  注意 seam 核查发生在**下一次 RenderFrame**（延迟一帧，zrhi (b)/(c) 段），这里的
    ///  估算是把它的成本提前记到发起操作的那一帧头上——预算的目的是让"发起+核查"的
    ///  总账不超帧预算，见施工报告 G4-R1 一节。
    /// </summary>
    public sealed class ZrPlanetChunkStreamer
    {
        // ---- 原生常量镜像（planet_math.h / zrhi_d3d12_unity_target.cpp；改一侧必须同步）----

        /// <summary>kPlanetRadiusM（planet_math.h:40，锁定）。</summary>
        public const double NativePlanetRadiusMeters = 6.371e6;

        /// <summary>kPlanetResFar（远档 16×16 顶点）。</summary>
        public const uint TierResFar = 16u;

        /// <summary>kPlanetResNear（近档 64×64 顶点）。</summary>
        public const uint TierResNear = 64u;

        /// <summary>lodLevel 0 = 远档（zplugin.h 组 3 注释口径）。</summary>
        public const uint LodFar = 0u;

        /// <summary>lodLevel 1 = 近档。</summary>
        public const uint LodNear = 1u;

        // 原生地平线剔除的裕度（unity_target.cpp (d) 段：半 chunk 角 + 0.05 rad）。
        private const double CullMarginRad = 0.05;
        private const double Pi = 3.14159265358979323846;

        private readonly struct RankEntry : IComparable<RankEntry>
        {
            public readonly double Cos;
            public readonly int Index;

            public RankEntry(double cos, int index)
            {
                Cos = cos;
                Index = index;
            }

            // cos 降序 = 离相机方向最近优先（Array.Sort 升序语义 → 反着比）。
            public int CompareTo(RankEntry other) => other.Cos.CompareTo(Cos);
        }

        private sealed class LiveChunk
        {
            public ulong Handle;
            public uint FaceId;
            public uint Cx;
            public uint Cy;
            /// <summary>托管侧记录的期望档位（与原生 desiredLod 同步——SetLod 已发出）。</summary>
            public uint DesiredLod;
        }

        private ZrPlanetStreamConfig _config;
        private bool _initialized;
        private bool _disposed;
        private bool _accountErrorLogged;
        private bool _fullLogged;

        // chunk 静态表：face*cells*cells + cy*cells + cx → 方向/锚点（Initialize 一次建成）。
        private double[] _dirX;
        private double[] _dirY;
        private double[] _dirZ;
        private double[] _anchorX;
        private double[] _anchorY;
        private double[] _anchorZ;
        private int _chunkCount;

        // 可见性状态（相机移动超阈值时重算）。
        private RankEntry[] _rank;
        private double[] _cosToCam;
        private byte[] _desired; // 0=不需要 1=迟滞带（存活则留） 2=必须存在
        private double _cosCreateLimit;
        private double _cosKeepLimit;
        private bool _hasRanking;
        private double _lastCamX;
        private double _lastCamY;
        private double _lastCamZ;

        // 活跃集：(face,cx,cy) → 句柄。key = 表索引（线性布局，唯一对应该三元组）。
        private readonly Dictionary<int, LiveChunk> _live = new Dictionary<int, LiveChunk>(1024);
        private readonly List<int> _destroyScratch = new List<int>(256);
        private readonly List<ZrPlanetStreamOp> _lastOps = new List<ZrPlanetStreamOp>(64);

        // 复用的封送结构（ChunkCreate/SetWorld 热路径零分配）。
        private ZrPlanetChunkDescNative _desc;
        private ZrPlanetWorldNative _world;
        private ZrPlanetStatsNative _statsAfter;

        // ---- 诊断计数（托管侧口径；与原生 GetStats 的对账见 AccountOk）----------------

        /// <summary>本管理器累计成功 Create 数（托管视角）。</summary>
        public long CreatedTotal { get; private set; }

        /// <summary>本管理器累计成功 Destroy 数。</summary>
        public long DestroyedTotal { get; private set; }

        /// <summary>本管理器累计 SetLod 数（升+退）。</summary>
        public long SetLodTotal { get; private set; }

        /// <summary>当前活跃 chunk 数（托管视角，应与 GetStats().LiveChunks 一致）。</summary>
        public int LiveCount => _live.Count;

        /// <summary>最近一次排名重建后的"必须存在"块数（诊断）。</summary>
        public int DesiredCount { get; private set; }

        /// <summary>最近一次 Tick 因预算未清完的待办数（诊断：>0 表示预算在起作用）。</summary>
        public int DeferredWorkCount { get; private set; }

        /// <summary>最近一次 Tick 的对账结果：createTotal − destroyTotal == liveChunks。</summary>
        public bool LastAccountOk { get; private set; } = true;

        /// <summary>最近一次 Tick 发出的操作（复用列表，下次 Tick 前有效）。</summary>
        public IReadOnlyList<ZrPlanetStreamOp> LastOps => _lastOps;

        /// <summary>是否已 Initialize（v1 一次性契约）。</summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// LOD 迟滞决策——原生 PlanetLodLevel（planet_math.h:454）的 C# 逐式镜像：
        /// 进近档需严格 dist &lt; nearDist − hyst；退远档需严格 dist &gt; nearDist + hyst；
        /// 迟滞带内与恰在阈值上**不切换**（与原生 doctest 同边界语义）。
        /// </summary>
        public static int DecideLodLevel(int prevLod, double distM, double nearDistM, double hystM)
        {
            if (prevLod >= 1)
            {
                return (distM > nearDistM + hystM) ? 0 : 1;
            }

            return (distM < nearDistM - hystM) ? 1 : 0;
        }

        /// <summary>
        /// 初始化：过门控的调用方负责；本方法调一次 SetWorld（G6：全进程唯一托管调用点）
        /// 并预计算全部 chunk 的方向/锚点表（含 SampleHeight 逐锚点高程——6144 次亚毫秒级
        /// 纯函数调用，一次性成本）。
        /// </summary>
        public void Initialize(ZrPlanetStreamConfig config)
        {
            if (_initialized)
            {
                throw new InvalidOperationException(
                    "ZrPlanetChunkStreamer.Initialize 只允许调用一次（G6：SetWorld 为初始化期" +
                    "唯一托管调用点；重复初始化请 Dispose 后重建）。");
            }

            ValidateConfig(config);
            _config = config;
            uint cells = _config.CellsPerEdge;
            _chunkCount = (int)(6u * cells * cells);

            // SetWorld（G6 双快照在同一原生调用内写入；structSize 版本化约定）。
            _world.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ZrPlanetWorldNative>();
            _world.Seed = _config.Seed;
            _world.CenterAbs = new[] { _config.CenterAbsX, _config.CenterAbsY, _config.CenterAbsZ };
            int setWorldResult = ZrPlanetNative.SetWorld(ref _world);
            if (setWorldResult != ZrPlanetNative.Ok)
            {
                throw new InvalidOperationException(
                    $"ZR_Planet_SetWorld 失败（{setWorldResult}）——流式管理器无法初始化。");
            }

            BuildChunkTable();
            _rank = new RankEntry[_chunkCount];
            _cosToCam = new double[_chunkCount];
            _desired = new byte[_chunkCount];
            _desc.OriginAbs = new double[3]; // 复用的封送缓冲（FillDesc 逐元素改写，零分配）
            _initialized = true;
        }

        /// <summary>
        /// 每 tick 推进：可见性重算（按需）→ 销毁出局块（无预算，P/Invoke 微秒级）→
        /// 预算内的 Create/SetLod（近/远档成本加权）→ 与 ZR_Planet_GetStats 对账。
        /// camAbs 为相机绝对坐标（行星中心系，米，double）——由接线方推给
        /// SetCameraOrigin 的同一个值（本类经 GetCameraOrigin 读回，见 Driver）。
        /// </summary>
        public void Tick(double camAbsX, double camAbsY, double camAbsZ)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Tick 之前必须 Initialize。");
            }

            ThrowIfDisposed();
            _lastOps.Clear();

            double relX = camAbsX - _config.CenterAbsX;
            double relY = camAbsY - _config.CenterAbsY;
            double relZ = camAbsZ - _config.CenterAbsZ;
            double camLen = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);

            UpdateRanking(relX, relY, relZ, camLen);

            // ---- 销毁出局块（销毁无 GPU 成本：句柄立即失效，VB 下一 RenderFrame 释放）----
            _destroyScratch.Clear();
            foreach (KeyValuePair<int, LiveChunk> pair in _live)
            {
                LiveChunk chunk = pair.Value;
                int index = TableIndex(chunk.FaceId, chunk.Cx, chunk.Cy);
                if (_cosToCam[index] < _cosKeepLimit)
                {
                    _destroyScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _destroyScratch.Count; i++)
            {
                LiveChunk chunk = _live[_destroyScratch[i]];
                int destroyResult = ZrPlanetNative.ChunkDestroy(chunk.Handle);
                if (destroyResult == ZrPlanetNative.Ok)
                {
                    _live.Remove(_destroyScratch[i]);
                    DestroyedTotal++;
                    RecordOp(ZrPlanetStreamOpKind.Destroy, chunk.FaceId, chunk.Cx, chunk.Cy,
                        chunk.DesiredLod, 0);
                }
                else
                {
                    // 句柄失效只可能来自外部 ClearAll（本类契约下不应发生）；摘除并继续。
                    _live.Remove(_destroyScratch[i]);
                }
            }

            // ---- 预算内创建 / 升退档（排名序：离相机方向最近的先服务）--------------------
            int workUsed = 0;
            int nearOps = 0;
            int deferred = 0;
            for (int r = 0; r < _chunkCount; r++)
            {
                RankEntry entry = _rank[r];
                if (entry.Cos < _cosCreateLimit)
                {
                    break; // 排名按 cos 降序——从这里起全部出局
                }

                int index = entry.Index;
                uint cells = _config.CellsPerEdge;
                uint faceId = (uint)(index / (cells * cells));
                uint rem = (uint)(index % (cells * cells));
                uint cy = rem / cells;
                uint cx = rem % cells;
                double dist = ChunkDistance(index, camAbsX, camAbsY, camAbsZ);

                if (_live.TryGetValue(index, out LiveChunk chunk))
                {
                    // 存活块：迟滞决策是否变档（SetLod 下一 RenderFrame 应用——延迟一帧）。
                    int desiredLod = DecideLodLevel((int)chunk.DesiredLod, dist,
                        _config.NearDistMeters, _config.HystMeters);
                    if (desiredLod == (int)chunk.DesiredLod)
                    {
                        continue;
                    }

                    bool upgrade = desiredLod == (int)LodNear;
                    int cost = upgrade ? _config.EstimatedNearChunkMicros : _config.EstimatedFarChunkMicros;
                    if (upgrade && nearOps >= _config.MaxNearTierOpsPerTick)
                    {
                        deferred++;
                        continue;
                    }

                    if (workUsed + cost > _config.MaxWorkMicrosPerTick)
                    {
                        deferred++;
                        continue;
                    }

                    int lodResult = ZrPlanetNative.SetLod(chunk.Handle, (uint)desiredLod, 0u);
                    if (lodResult == ZrPlanetNative.Ok)
                    {
                        chunk.DesiredLod = (uint)desiredLod;
                        SetLodTotal++;
                        workUsed += cost;
                        if (upgrade)
                        {
                            nearOps++;
                            RecordOp(ZrPlanetStreamOpKind.SetLodUpgrade, faceId, cx, cy, LodNear, cost);
                        }
                        else
                        {
                            RecordOp(ZrPlanetStreamOpKind.SetLodDowngrade, faceId, cx, cy, LodFar, cost);
                        }
                    }
                }
                else
                {
                    // 新块：初始档位按迟滞口径（prevLod=0）决定，创建即可带近档。
                    int lod = DecideLodLevel(0, dist, _config.NearDistMeters, _config.HystMeters);
                    bool near = lod == (int)LodNear;
                    int cost = near ? _config.EstimatedNearChunkMicros : _config.EstimatedFarChunkMicros;
                    if (near && nearOps >= _config.MaxNearTierOpsPerTick)
                    {
                        deferred++;
                        continue;
                    }

                    if (workUsed + cost > _config.MaxWorkMicrosPerTick)
                    {
                        deferred++;
                        continue;
                    }

                    FillDesc(faceId, cx, cy, (uint)lod);
                    int createResult = ZrPlanetNative.ChunkCreate(ref _desc, out ulong handle);
                    if (createResult == ZrPlanetNative.Ok)
                    {
                        _live[index] = new LiveChunk
                        {
                            Handle = handle,
                            FaceId = faceId,
                            Cx = cx,
                            Cy = cy,
                            DesiredLod = (uint)lod,
                        };
                        CreatedTotal++;
                        workUsed += cost;
                        if (near)
                        {
                            nearOps++;
                        }

                        RecordOp(ZrPlanetStreamOpKind.Create, faceId, cx, cy, (uint)lod, cost);
                    }
                    else if (createResult == ZrPlanetNative.ErrFull)
                    {
                        // 槽位满（8192）：本 tick 停发创建（日志一次），销毁仍继续以腾位。
                        if (!_fullLogged)
                        {
                            _fullLogged = true;
                            Debug.LogWarning("[ZRender] 行星 chunk 槽位满（8192）：本 tick 停止创建，" +
                                             "等待可见性出局释放。");
                        }

                        deferred++;
                        break;
                    }
                    else
                    {
                        Debug.LogError($"[ZRender] ChunkCreate 失败 rc={createResult} " +
                                       $"(face={faceId} cx={cx} cy={cy})——跳过该块。");
                    }
                }

                if (workUsed >= _config.MaxWorkMicrosPerTick)
                {
                    break;
                }
            }

            DeferredWorkCount = deferred;
            DesiredCount = CountDesired();

            // ---- 账目对账（设计稿 §7.3 第 2 层：恒等式由托管侧断言/自检）----------------
            GetStatsOrThrow(out _statsAfter);
            LastAccountOk = _statsAfter.CreateTotal - _statsAfter.DestroyTotal
                == (ulong)_statsAfter.LiveChunks;
            if (!LastAccountOk && !_accountErrorLogged)
            {
                _accountErrorLogged = true;
                Debug.LogError("[ZRender] 行星 chunk 账目恒等式破坏：" +
                               $"createTotal={_statsAfter.CreateTotal} destroyTotal={_statsAfter.DestroyTotal} " +
                               $"liveChunks={_statsAfter.LiveChunks}——存在泄漏或重复销毁。");
            }
        }

        /// <summary>场景卸载/销毁：一次性清空全部 chunk（设计稿 §7.3 第 3 层兜底）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_initialized)
            {
                ZrPlanetNative.ClearAll();
                _live.Clear();
            }
        }

        /// <summary>读原生统计快照（诊断/测试对账入口）。</summary>
        public bool TryGetNativeStats(out ZrPlanetStatsNative stats)
        {
            return ZrPlanetNative.GetStats(out stats) == ZrPlanetNative.Ok;
        }

        // ---- 内部实现 ------------------------------------------------------------------

        private static void ValidateConfig(ZrPlanetStreamConfig config)
        {
            if (config.CellsPerEdge < 1u || config.CellsPerEdge > 4096u)
            {
                throw new ArgumentException($"CellsPerEdge 必须 1..4096（实际 {config.CellsPerEdge}）");
            }

            if (!(config.NearDistMeters > 0.0) || !(config.HystMeters > 0.0)
                || double.IsNaN(config.NearDistMeters) || double.IsInfinity(config.NearDistMeters)
                || double.IsNaN(config.HystMeters) || double.IsInfinity(config.HystMeters))
            {
                throw new ArgumentException("NearDistMeters/HystMeters 必须为正有限值。");
            }

            if (config.MaxWorkMicrosPerTick < 0 || config.MaxNearTierOpsPerTick < 1
                || config.EstimatedFarChunkMicros < 0 || config.EstimatedNearChunkMicros < 0)
            {
                throw new ArgumentException("预算参数非法（MaxWorkMicrosPerTick ≥ 0，MaxNearTierOpsPerTick ≥ 1）。");
            }

            if (!IsFinite(config.CenterAbsX) || !IsFinite(config.CenterAbsY) || !IsFinite(config.CenterAbsZ))
            {
                throw new ArgumentException("行星中心坐标含 NaN/Inf。");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>建 chunk 静态表：方向 = CubeSphereDir（planet_math.h 逐式镜像），锚点 =
        /// C + dir·(R + SampleHeight)。锚点含地形高程（与 zrhi_d3d12_window --planet3 的
        /// desc.originAbs 口径一致：chunk 中心球面点）。</summary>
        private void BuildChunkTable()
        {
            uint cells = _config.CellsPerEdge;
            _dirX = new double[_chunkCount];
            _dirY = new double[_chunkCount];
            _dirZ = new double[_chunkCount];
            _anchorX = new double[_chunkCount];
            _anchorY = new double[_chunkCount];
            _anchorZ = new double[_chunkCount];

            // 锚点取远档分辨率的面内中心索引（stride/2 整除——与窗口先例同式）。
            int stride = (int)TierResFar - 1;
            int total = (int)cells * stride;
            int centerOffset = stride / 2;

            for (uint face = 0u; face < 6u; face++)
            {
                for (uint cy = 0u; cy < cells; cy++)
                {
                    for (uint cx = 0u; cx < cells; cx++)
                    {
                        int gU = (int)cx * stride + centerOffset;
                        int gV = (int)cy * stride + centerOffset;
                        double u = 2.0 * (double)gU / (double)total - 1.0; // ChunkFaceCoord 同式
                        double v = 2.0 * (double)gV / (double)total - 1.0;
                        CubeSphereDir((int)face, u, v, out double dx, out double dy, out double dz);

                        // SampleHeight 的输入取 C + dir·R（方向与锚点同向，h 与原生逐位同源）。
                        double sampleX = _config.CenterAbsX + dx * NativePlanetRadiusMeters;
                        double sampleY = _config.CenterAbsY + dy * NativePlanetRadiusMeters;
                        double sampleZ = _config.CenterAbsZ + dz * NativePlanetRadiusMeters;
                        int sampleResult = ZrPlanetNative.SampleHeight(sampleX, sampleY, sampleZ,
                            out float heightM);
                        if (sampleResult != ZrPlanetNative.Ok)
                        {
                            throw new InvalidOperationException(
                                $"SampleHeight 失败 rc={sampleResult}（face={face} cx={cx} cy={cy}）" +
                                "——SetWorld 必须先于锚点表构建。");
                        }

                        double radius = NativePlanetRadiusMeters + heightM;
                        int index = TableIndex(face, cx, cy);
                        _dirX[index] = dx;
                        _dirY[index] = dy;
                        _dirZ[index] = dz;
                        _anchorX[index] = _config.CenterAbsX + dx * radius;
                        _anchorY[index] = _config.CenterAbsY + dy * radius;
                        _anchorZ[index] = _config.CenterAbsZ + dz * radius;
                    }
                }
            }
        }

        /// <summary>相机移动超阈值时重算可见性排名（口径镜像原生地平线剔除 + 半块迟滞带）。</summary>
        private void UpdateRanking(double relX, double relY, double relZ, double camLen)
        {
            double chunkHalfAng = (Pi / 2.0) / (double)_config.CellsPerEdge * 0.5;

            bool rebuild;
            if (!_hasRanking)
            {
                rebuild = true;
            }
            else
            {
                double lastLen = Math.Sqrt(_lastCamX * _lastCamX + _lastCamY * _lastCamY
                    + _lastCamZ * _lastCamZ);
                double dot = (_lastCamX * relX + _lastCamY * relY + _lastCamZ * relZ)
                    / Math.Max(lastLen * camLen, 1e-12);
                double turn = Math.Acos(Math.Min(1.0, Math.Max(-1.0, dot)));
                double lenDrift = lastLen > 0.0 ? Math.Abs(camLen - lastLen) / lastLen : 1.0;
                rebuild = turn > chunkHalfAng * 0.5 || lenDrift > 0.05;
            }

            if (!rebuild)
            {
                return;
            }

            // 相机方向（camLen==0 → 与心重合：方向退化，按全期望处理——合法编排不该进入，
            // 但守卫不能少）。
            double invLen = camLen > 0.0 ? 1.0 / camLen : 0.0;
            double camDirX = relX * invLen;
            double camDirY = relY * invLen;
            double camDirZ = relZ * invLen;

            // 地平线角（原生同式：acos(min(1, R/len))；len ≤ R 时为 0——只剩朝向半球）。
            double horizonAng = camLen > 0.0
                ? Math.Acos(Math.Min(1.0, NativePlanetRadiusMeters / camLen))
                : 0.0;
            double createAng = horizonAng + chunkHalfAng + CullMarginRad;
            double keepAng = createAng + chunkHalfAng; // 销毁迟滞带：多让出一个半块角
            _cosCreateLimit = Math.Cos(Math.Min(createAng, Pi));
            _cosKeepLimit = Math.Cos(Math.Min(keepAng, Pi));

            for (int i = 0; i < _chunkCount; i++)
            {
                double cos = _dirX[i] * camDirX + _dirY[i] * camDirY + _dirZ[i] * camDirZ;
                _cosToCam[i] = cos;
                _desired[i] = cos >= _cosCreateLimit ? (byte)2 : (cos >= _cosKeepLimit ? (byte)1 : (byte)0);
                _rank[i] = new RankEntry(cos, i);
            }

            Array.Sort(_rank, 0, _chunkCount);
            _hasRanking = true;
            _lastCamX = relX;
            _lastCamY = relY;
            _lastCamZ = relZ;
        }

        private int CountDesired()
        {
            int count = 0;
            for (int i = 0; i < _chunkCount; i++)
            {
                if (_desired[i] == 2)
                {
                    count++;
                }
            }

            return count;
        }

        private double ChunkDistance(int index, double camAbsX, double camAbsY, double camAbsZ)
        {
            double dx = _anchorX[index] - camAbsX;
            double dy = _anchorY[index] - camAbsY;
            double dz = _anchorZ[index] - camAbsZ;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private int TableIndex(uint face, uint cx, uint cy)
        {
            // 线性布局（与数组尺寸 6·cells² 一致）：face·cells² + cy·cells + cx。
            uint cells = _config.CellsPerEdge;
            return (int)(face * cells * cells + cy * cells + cx);
        }

        private void FillDesc(uint faceId, uint cx, uint cy, uint lod)
        {
            int index = TableIndex(faceId, cx, cy);
            _desc.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ZrPlanetChunkDescNative>();
            _desc.FaceId = faceId;
            _desc.LodLevel = lod;
            _desc.Cx = cx;
            _desc.Cy = cy;
            _desc.CellsPerEdge = _config.CellsPerEdge;
            _desc.NeighborMask = 0u; // v1：校验+记录、不改几何（设计稿 §5.2/D6），留 B-PLANET-08 定版
            _desc.Reserved0 = 0u;
            _desc.OriginAbs[0] = _anchorX[index];
            _desc.OriginAbs[1] = _anchorY[index];
            _desc.OriginAbs[2] = _anchorZ[index];
        }

        private void RecordOp(ZrPlanetStreamOpKind kind, uint faceId, uint cx, uint cy, uint lod,
            int estimatedMicros)
        {
            _lastOps.Add(new ZrPlanetStreamOp
            {
                Kind = kind,
                FaceId = faceId,
                Cx = cx,
                Cy = cy,
                LodLevel = lod,
                EstimatedMicros = estimatedMicros,
            });
        }

        private void GetStatsOrThrow(out ZrPlanetStatsNative stats)
        {
            int result = ZrPlanetNative.GetStats(out stats);
            if (result != ZrPlanetNative.Ok)
            {
                throw new InvalidOperationException($"ZR_Planet_GetStats 失败（{result}）。");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ZrPlanetChunkStreamer));
            }
        }

        /// <summary>
        /// 标准化立方体映射的 C# 逐式镜像（planet_math.h CubeSphereDir/CubeFaceBasisOf）：
        /// p = n + u·t1 + v·t2 → p/|p|。跨面棱两侧 (u,v) 为 ±1 端点时逐位一致（构造性）。
        /// </summary>
        internal static void CubeSphereDir(int faceId, double u, double v,
            out double x, out double y, out double z)
        {
            double nx;
            double ny;
            double nz;
            double t1x;
            double t1y;
            double t1z;
            double t2x;
            double t2y;
            double t2z;
            switch (faceId)
            {
                case 0: nx = 0.0; ny = 0.0; nz = 1.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 1: nx = 1.0; ny = 0.0; nz = 0.0; t1x = 0.0; t1y = 0.0; t1z = -1.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 2: nx = 0.0; ny = 0.0; nz = -1.0; t1x = -1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 3: nx = -1.0; ny = 0.0; nz = 0.0; t1x = 0.0; t1y = 0.0; t1z = 1.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 4: nx = 0.0; ny = 1.0; nz = 0.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 0.0; t2z = -1.0; break;
                default: nx = 0.0; ny = -1.0; nz = 0.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 0.0; t2z = 1.0; break;
            }

            double px = nx + u * t1x + v * t2x;
            double py = ny + u * t1y + v * t2y;
            double pz = nz + u * t1z + v * t2z;
            double invLen = 1.0 / Math.Sqrt(px * px + py * py + pz * pz);
            x = px * invLen;
            y = py * invLen;
            z = pz * invLen;
        }
    }
}
