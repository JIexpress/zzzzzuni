// PlanetRuntimeProbe.cs —— 行星运行时探针：Windows IL2CPP Player 原生链 + Android 托管等效
// （TASK-B-PLANET-12；D8 裁决：Android 判据对象 = 行星能力的托管等效实现）。
//
// 激活口径（其余任何启动路径零行为变化——生产 boot/编辑器/测试不受影响）：
//   1. Windows Player 命令行带 -zrender-planet（与 ZRenderBoot 的 -zrender 同机制、
//      可叠加）→ 原生链取证模式：门控 + ZrPlanetChannel 激活 + WorldOrigin 接线 +
//      chunk 流式 Driver（生产默认配置）+ P/Invoke 口径证据（uint64 句柄/structSize
//      封送/double 直传）+ C# 高度移植 vs 原生 SampleHeight float 位模式对照 + 帧耗；
//   2. Android 设备（Application.platform==Android，无命令行通道——Unity Android 不
//      接收桌面式命令行参数）→ 托管等效模式：D8 三件套真跑一次——WorldOrigin 重定位
//      （生产组件 4000m 阈值 + 行走触发）+ LOD 迟滞决策（C# 镜像 DecideLodLevel 扫描）
//      + C# 高度采样（PlanetTerrainMath 移植 vs 内嵌的原生链 float 位锚逐位对照）
//      + 设备帧耗。原生门控在 Android 上预期失败（zrender.dll 仅 Windows x64）——
//      失败本身按门控纪律记录为"整体关闭 + 一行日志"的证据。
//
// 证据通道：Debug.Log（Player.log / adb logcat tag=Unity），行首 [PlanetProbe] 便于
// grep。**刻意不截图**——B-PLANET-11 验收确立的纪律：多相机缺口（B-PLANET-15）下
// 像素取证须 fixture 隔离；本探针改用 log/数字证据避开截图链路。
//
// 高度锚（DeviceHeightSamples）：16 个方向的原生链 float 位模式——来源 = 桌面
// EditMode ZrPlanetManagedHeightTests（真实 zrender.dll SampleHeight 逐位对照，100+
// 512 点全绿）+ planet-budget/abi-precision 探针 JSON 锚点。设备 C# 应与之逐位一致
// （IEEE754 双精度 + round-to-nearest 在 ARM64/x64 语义一致；位差 = 平台数学差异，
// 逐点归因）。
//
// 自限：固定帧数后输出一行 SUMMARY 并自我销毁（rig 一起清场——Driver.OnDestroy 走
// ClearAll 兜底），不长期驻留。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Game.Domain.World;
using Game.View.World;
using UnityEngine;
using ZFrame.Render;
using ZFrame.Render.Bindings;
using ZFrame.Render.Planet;

namespace Game.Bootstrap
{
    [DisallowMultipleComponent]
    public sealed class PlanetRuntimeProbe : MonoBehaviour
    {
        /// <summary>桌面 Player 取证命令行开关（与 -zrender 同机制，可叠加）。</summary>
        public const string CommandLineFlag = "-zrender-planet";

        private const int WalkFrames = 330;
        private const int LogIntervalFrames = 30;
        private const double StepMeters = 100.0;
        private const double HoverAltitudeMeters = 600.0;
        private const uint ProbeSeed = 1337u;

        private static readonly (string Label, int Face, double U, double V, uint ExpectedBits)[]
            DeviceHeightSamples =
        {
            ("anchor1039", 0, 0.030753968253968145, 0.030753968253968145, 1132348487u),
            ("anchor1070", 0, 0.06150793650793651, 0.06150793650793651, 3293333269u),
            ("anchor1008", 0, 0.0, 0.0, 3280328978u),
            ("anchor1071x1039", 0, 0.0625, 0.030753968253968145, 3291679894u),
            ("faceC0", 0, 0.0, 0.0, 3280328978u),
            ("faceC1", 1, 0.0, 0.0, 3281968498u),
            ("faceC2", 2, 0.0, 0.0, 3285628662u),
            ("faceC3", 3, 0.0, 0.0, 3298210327u),
            ("faceC4", 4, 0.0, 0.0, 1113107636u),
            ("faceC5", 5, 0.0, 0.0, 1146379846u),
            ("edge0u+", 0, 0.5, 0.0, 3275025111u),
            ("edge0v-", 0, 0.0, -0.5, 1133039541u),
            ("mid1", 1, 0.25, -0.25, 1137961569u),
            ("mid4", 4, -0.125, 0.125, 3282462060u),
            ("lo", 3, -0.0625, 0.03125, 3290990147u),
            ("hi", 5, 0.0625, -0.03125, 1130138350u),
        };

        private readonly List<float> _frameMs = new List<float>(WalkFrames + 8);
        private Camera _camera;
        private WorldOrigin _origin;
        private WorldOriginPlanetLink _link;
        private ZrPlanetStreamingDriver _driver;
        private GameObject _cameraGo;
        private GameObject _originGo;
        private GameObject _linkGo;
        private GameObject _driverGo;
        private float _lastRealtime;
        private int _frame;
        private double _maxAbsDriftMeters;
        private bool _heightCheckDone;
        private bool _finished;

        /// <summary>原生链取证模式（桌面 + 命令行开关）。</summary>
        public bool NativeMode { get; private set; }

        /// <summary>托管等效模式（Android 设备）。</summary>
        public bool ManagedOnlyMode { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Application.isEditor)
            {
                return; // 编辑器零行为变化（EditMode/PlayMode 测试自有覆盖）
            }

            bool android = Application.platform == RuntimePlatform.Android;
            bool flag = HasCommandLineFlag(CommandLineFlag);
            if (!android && !flag)
            {
                return; // 正常启动零行为变化
            }

            var host = new GameObject("~PlanetRuntimeProbe");
            UnityEngine.Object.DontDestroyOnLoad(host);
            PlanetRuntimeProbe probe = host.AddComponent<PlanetRuntimeProbe>();
            probe.NativeMode = flag && !android;
            probe.ManagedOnlyMode = android;
            Debug.Log($"[PlanetProbe] armed: nativeMode={probe.NativeMode} managedOnly={probe.ManagedOnlyMode} " +
                      $"platform={Application.platform} graphicsApi={SystemInfo.graphicsDeviceType} " +
                      $"zrenderPipeline={ZRenderBoot.IsActive}");
        }

        private static bool HasCommandLineFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Start()
        {
            _lastRealtime = Time.realtimeSinceStartup;

            // ---- rig（纯程序化，零美术依赖；不用 MainCamera tag——不抢游戏的 Camera.main）----
            _cameraGo = new GameObject("~PlanetProbeCamera");
            _camera = _cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 10f;
            _camera.farClipPlane = 400_000f;
            _camera.cullingMask = 0; // 本探针只要相机位姿（WorldOrigin 判据），不渲任何层——
                                     // 零像素取证（B-PLANET-11 验收纪律：log/数字证据）。

            _originGo = new GameObject("~PlanetProbeOrigin");
            _origin = _originGo.AddComponent<WorldOrigin>();
            _origin.AutoRebase = true; // 生产默认：4000m 阈值每帧自检
            _origin.TargetCamera = _camera;

            _linkGo = new GameObject("~PlanetProbeLink");
            _link = _linkGo.AddComponent<WorldOriginPlanetLink>();
            _link.Configure(_origin, _camera); // Player 内无 UnityEditor.SerializedObject

            _driverGo = new GameObject("~PlanetProbeDriver");
            _driver = _driverGo.AddComponent<ZrPlanetStreamingDriver>();
            // 生产默认配置零偏置（center=0 / seed 1337 / 32×32 / 预算 9000µs / 近档硬帽 1）。

            // ---- 封送口径自证（IL2CPP 下 Marshal.SizeOf 必须与 zplugin.h 断言一致）----
            Debug.Log(
                "[PlanetProbe] marshal sizes: world=" + Marshal.SizeOf<ZrPlanetWorldNative>() +
                " (expect 32) desc=" + Marshal.SizeOf<ZrPlanetChunkDescNative>() +
                " (expect 56) stats=" + Marshal.SizeOf<ZrPlanetStatsNative>() + " (expect 80)");

            // ---- 门控（桌面原生链与 Android 托管等效共用同一真实门控）----
            try
            {
                ZrPlanetNative.EnsurePlanetChannelReady();
                Debug.Log("[PlanetProbe] gate: PASS (zrender.dll loaded, ApiVersion>=3) — P/Invoke lazy resolution OK.");
            }
            catch (Exception gateError)
            {
                Debug.Log("[PlanetProbe] gate: FAIL (" + gateError.GetType().Name + ": " + gateError.Message + ")" +
                          (ManagedOnlyMode
                              ? " — Android 托管等效模式按预期无原生通道（D8）：能力走 C# 移植，门控整体关闭证据成立。"
                              : " — 桌面原生链预期可过门控，此失败即为口径证据（不静默）。"));
            }

            // ---- LOD 迟滞决策扫描（C# 镜像纯函数——两模式都跑，证据行一致可比）----
            LogLodDecisionSweep();

            if (ManagedOnlyMode)
            {
                // Android：C# 高度移植 vs 内嵌原生链 float 位锚（桌面 EditMode 已证该锚
                // 与真实 dll 逐位一致）——设备侧逐位对照立即输出，不等 SUMMARY。
                LogManagedHeightAnchorCheck();
            }
        }

        private void Update()
        {
            if (_finished)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            float frameMs = (_frame > 0 ? (now - _lastRealtime) * 1000f : 0f);
            _lastRealtime = now;
            if (_frame >= 5)
            {
                _frameMs.Add(frameMs); // 前 5 帧预热不计
            }

            ++_frame;

            // 相机沿 face0 u 轴测地线推进（θ=arc/R，u=tanθ；贴地 = C# 移植高度 + 600m 悬停）。
            double arc = (_frame - 1) * StepMeters;
            double theta = arc / PlanetTerrainMath.PlanetRadiusMeters;
            double u = Math.Tan(theta);
            PlanetTerrainMath.CubeSphereDir(0, u, 0.0, out double dx, out double dy, out double dz);
            double ground = PlanetTerrainMath.TerrainHeightM(ProbeSeed, dx, dy, dz);
            double radius = PlanetTerrainMath.PlanetRadiusMeters + ground + HoverAltitudeMeters;
            WorldPosition camAbs = new WorldPosition(dx * radius, dy * radius, dz * radius);
            Vector3 unityPos = _origin.WorldToUnity(camAbs);
            _camera.transform.position = unityPos;

            // 绝对位姿漂移（下一帧实测——重定位帧的守恒证据）：把相机 Unity 坐标加宽回
            // double 绝对域，与目标 camAbs 求差（含 WorldOrigin 本帧重定位后的状态）。
            WorldPosition absObserved = _origin.UnityToWorld(_camera.transform.position);
            double drift = WorldPosition.Distance(absObserved, camAbs);
            if (drift > _maxAbsDriftMeters)
            {
                _maxAbsDriftMeters = drift;
            }

            // 原生链：SetWorld 由 Driver 的 streamer.Initialize 完成（G6 唯一托管调用点）；
            // 本探针绝不 SetWorld——用 SampleHeight 的 ErrState 探测世界就绪后再做对照。
            if (NativeMode && !_heightCheckDone)
            {
                int probeRc = ZrPlanetNative.SampleHeight(0.0, 0.0, PlanetTerrainMath.PlanetRadiusMeters, out _);
                if (probeRc != ZrPlanetNative.ErrState)
                {
                    _heightCheckDone = true;
                    LogNativeHeightComparison();
                    LogStreamingStats("world-ready");
                }
            }

            if (_frame % LogIntervalFrames == 0)
            {
                if (NativeMode)
                {
                    LogStreamingStats($"tick frame={_frame}");
                }
                else
                {
                    Debug.Log($"[PlanetProbe] managed frame={_frame} arc={arc:F0}m rebaseCount={_origin.RebaseCount} " +
                              $"originOffset={_origin.OriginOffset} driftMax={_maxAbsDriftMeters:F6}m frameMsAvg={AvgFrameMs():F2}");
                }
            }

            if (_frame >= WalkFrames)
            {
                LogSummary(arc);
                _finished = true;
                DestroyRig();
            }
        }

        private void LogLodDecisionSweep()
        {
            // 两档迟滞（B-TECH-04 口径：900km 阈值 / ±90km 迟滞）——C# 镜像与原生
            // PlanetLodLevel 逐式同构（B-PLANET-07 J2）。设备证据 = 决策曲线逐点打印。
            var sb = new StringBuilder("[PlanetProbe] lod sweep (nearDist=900km hyst=90km): ");
            for (double distKm = 780; distKm <= 1020; distKm += 30)
            {
                double dist = distKm * 1000.0;
                sb.Append(distKm)
                    .Append("km:")
                    .Append(ZrPlanetChunkStreamer.DecideLodLevel(0, dist, 900_000.0, 90_000.0))
                    .Append("/")
                    .Append(ZrPlanetChunkStreamer.DecideLodLevel(1, dist, 900_000.0, 90_000.0))
                    .Append(' ');
            }

            Debug.Log(sb.ToString());
        }

        private void LogNativeHeightComparison()
        {
            int matched = 0;
            var sb = new StringBuilder("[PlanetProbe] height compare (managed port vs native SampleHeight, float bits): ");
            foreach (var s in DeviceHeightSamples)
            {
                PlanetTerrainMath.CubeSphereDir(s.Face, s.U, s.V, out double dx, out double dy, out double dz);
                double px = dx * PlanetTerrainMath.PlanetRadiusMeters;
                double py = -PlanetTerrainMath.PlanetRadiusMeters + dy * PlanetTerrainMath.PlanetRadiusMeters;
                double pz = dz * PlanetTerrainMath.PlanetRadiusMeters;
                int rc = ZrPlanetNative.SampleHeight(px, py, pz, out float apiFloat);
                uint apiBits = rc == ZrPlanetNative.Ok
                    ? (uint)BitConverter.SingleToInt32Bits(apiFloat)
                    : 0xDEADBADu;
                uint managedBits = (uint)BitConverter.SingleToInt32Bits((float)DeviceSampleManaged(s));
                bool equal = apiBits == managedBits;
                if (equal)
                {
                    ++matched;
                }

                sb.Append(s.Label).Append('=').Append(equal ? "EQ" : $"DIFF(native={apiBits:X8} managed={managedBits:X8})").Append(' ');
            }

            Debug.Log($"{sb.ToString().TrimEnd()} — matched {matched}/{DeviceHeightSamples.Length}");
        }

        /// <summary>设备锚对照：C# 移植计算值（无原生参与）与内嵌原生链 float 位逐位比对。</summary>
        private void LogManagedHeightAnchorCheck()
        {
            int matched = 0;
            var sb = new StringBuilder("[PlanetProbe] managed anchors (C# port vs embedded native-derived float bits): ");
            foreach (var s in DeviceHeightSamples)
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits((float)DeviceSampleManaged(s));
                bool equal = bits == s.ExpectedBits;
                if (equal)
                {
                    ++matched;
                }
                else
                {
                    sb.Append(s.Label).Append($": got {bits:X8} expected {s.ExpectedBits:X8} ");
                }
            }

            Debug.Log($"{sb.ToString().TrimEnd()} — {matched}/{DeviceHeightSamples.Length} bitwise equal");
        }

        private double DeviceSampleManaged((string Label, int Face, double U, double V, uint ExpectedBits) s)
        {
            PlanetTerrainMath.CubeSphereDir(s.Face, s.U, s.V, out double dx, out double dy, out double dz);
            double px = dx * PlanetTerrainMath.PlanetRadiusMeters;
            double py = -PlanetTerrainMath.PlanetRadiusMeters + dy * PlanetTerrainMath.PlanetRadiusMeters;
            double pz = dz * PlanetTerrainMath.PlanetRadiusMeters;
            double relX = px;
            double relY = py - (-PlanetTerrainMath.PlanetRadiusMeters);
            double relZ = pz;
            double len = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);
            return PlanetTerrainMath.TerrainHeightM(ProbeSeed, relX / len, relY / len, relZ / len);
        }

        private void LogStreamingStats(string label)
        {
            if (ZrPlanetNative.GetStats(out ZrPlanetStatsNative stats) != ZrPlanetNative.Ok)
            {
                Debug.Log($"[PlanetProbe] {label}: GetStats failed (channel state见上方门控行)");
                return;
            }

            Debug.Log(
                $"[PlanetProbe] {label}: live={stats.LiveChunks} create={stats.CreateTotal} destroy={stats.DestroyTotal} " +
                $"lod={stats.LodChangeTotal} drawn={stats.DrawnLastFrame} culled={stats.CulledLastFrame} " +
                $"vbytes={stats.VertexBytes} lastMs={stats.LastFrameMs:F3} avgMs={stats.AvgFrameMs:F3} " +
                $"identity={(stats.CreateTotal - stats.DestroyTotal == stats.LiveChunks ? "OK" : "BROKEN")} " +
                $"rebase={_origin.RebaseCount} driftMax={_maxAbsDriftMeters:F6}m frameMsAvg={AvgFrameMs():F2}");
        }

        private float AvgFrameMs()
        {
            if (_frameMs.Count == 0)
            {
                return 0f;
            }

            float sum = 0f;
            for (int i = 0; i < _frameMs.Count; ++i)
            {
                sum += _frameMs[i];
            }

            return sum / _frameMs.Count;
        }

        private float PercentileFrameMs(float fraction)
        {
            if (_frameMs.Count == 0)
            {
                return 0f;
            }

            var sorted = new List<float>(_frameMs);
            sorted.Sort();
            int index = Math.Min(sorted.Count - 1, Math.Max(0, (int)(fraction * sorted.Count + 0.5f)));
            return sorted[index];
        }

        private void LogSummary(double finalArc)
        {
            if (NativeMode)
            {
                LogStreamingStats($"SUMMARY frame={_frame}");
            }

            if (ManagedOnlyMode)
            {
                LogManagedHeightAnchorCheck();
                Debug.Log($"[PlanetProbe] SUMMARY managed: frame={_frame} arc={finalArc:F0}m " +
                          $"rebaseCount={_origin.RebaseCount} driftMax={_maxAbsDriftMeters:F6}m " +
                          $"frameMs p50={PercentileFrameMs(0.5f):F2} mean={AvgFrameMs():F2} p95={PercentileFrameMs(0.95f):F2}");
            }
            else
            {
                Debug.Log($"[PlanetProbe] SUMMARY native: frame={_frame} arc={finalArc:F0}m " +
                          $"rebaseCount={_origin.RebaseCount} driftMax={_maxAbsDriftMeters:F6}m " +
                          $"frameMs p50={PercentileFrameMs(0.5f):F2} mean={AvgFrameMs():F2} p95={PercentileFrameMs(0.95f):F2} " +
                          $"pipeline={ZRenderBoot.IsActive}");
            }

            Debug.Log("[PlanetProbe] done (probe self-removed; rig cleared)");
        }

        private void DestroyRig()
        {
            if (_driverGo != null)
            {
                Destroy(_driverGo); // Driver.OnDestroy → streamer.Dispose → ClearAll 兜底
                _driverGo = null;
                _driver = null;
            }

            if (_linkGo != null)
            {
                Destroy(_linkGo);
                _linkGo = null;
                _link = null;
            }

            if (_originGo != null)
            {
                Destroy(_originGo);
                _originGo = null;
                _origin = null;
            }

            if (_cameraGo != null)
            {
                Destroy(_cameraGo);
                _cameraGo = null;
                _camera = null;
            }

            Destroy(gameObject);
        }
    }
}
