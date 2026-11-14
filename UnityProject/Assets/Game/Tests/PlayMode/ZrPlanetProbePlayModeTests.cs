// ZrPlanetProbePlayModeTests.cs —— TASK-B-PLANET-11：编辑器内验收集成（真帧循环取证）。
//
// 三组证据（任务简报 §1；设计稿 §10 判据的编辑器口径）：
//   1. 三档截图（1080p）：近（贴地 600m）/中（258km，--planet2 帧 160 同档）/远
//      （8000km 天底全圆盘）各一张，planet_ed_near/mid/far.png 入 Docs/Parity/——
//      地平线/高程带/limb 目检素材（真帧循环内由生产 SRP 贴屏路径产出）。
//   2. 球面行走账目三变体（1km/5km/50km，贴地直线口径）：真帧循环内逐帧
//      - 账目对账：createTotal−destroyTotal==liveChunks（原生全局恒等式）+
//        托管/原生 create/destroy/live 增量逐项相等（全程零漂移）；
//      - 分帧预算：每 tick 操作成本和 ≤ 9000µs（B-PLANET-07 默认预算）、近档操作
//        ≤ 1（近档硬帽）；
//      - 重定位联动：WorldOrigin(4000m) 触发重定位时，SetCameraOrigin 收到的新
//        原点值正确（A-WORLD-01 口径：无重定位帧与 UnityToWorld 逐位一致；
//        重定位帧绝对位置在 float 往返量化内守恒 <1e-3m）；
//      - 逐帧像素连续性（跳变检测：相邻帧亮度差有界——坐标跳变表现为整幅位移）。
//   3. 行走前后截图像素对照：同绝对位姿复访（走出去再走回），重渲染应近乎逐位
//      一致（地形数学确定性 + chunk 几何 double 域同源 + test_planet.cpp:93-96
//      「重定位前后渲染偏移逐位不变」的 PlayMode 侧复验）。
//
// 场景接线（纯程序化，零美术依赖——ParityBench 契约风格）：相机 rig + WorldOrigin
// （4000m 自动重定位，生产默认）+ WorldOriginPlanetLink（LateUpdate 每帧推送）+
// ZrPlanetStreamingDriver（Update 流式 Tick，生产默认配置零偏置）。不落 .unity 资产：
// 装配即测试生命周期（WorldOriginPlayModeTests 先例），避免场景文件与并行任务的
// .meta 争用。
//
// SRP 说明：项目默认管线是 URP（A 轨像素用例基线）。本 fixture 在 UnitySetUp 把
// 管线切到 ZRender（与 ZRenderBoot.TryEnableFromCommandLine 完全同一机制：
// QualitySettings.renderPipeline + GraphicsSettings.defaultRenderPipeline 两处都设），
// UnityTearDown 还原。生产侧「运行期禁止跨管线热切」的约定不受影响——
// RenderPipelineSelector 的编辑期门未动，这是测试装备的自包含切换（用完即还）。
// 门控：非 D3D12 环境 Assert.Ignore（与 B-PLANET-07 EditMode 集成用例同口径）。
//
// 行走几何：face0（n=(0,0,1)，t1=(1,0,0)——CubeSphereDir 基）u 轴测地线，弧长参数
// θ = arc/R；贴地高度 = SampleHeight(C+dir·R)+600m（与流式锚点同一纯函数，D9 口径：
// 贴地锚定纯函数）。50km ≪ chunk 弧长 312.7km（不跨流式格边界，设计稿 §10 如实注记）；
// 相机方向转动 50km/R = 0.449° < 排名重建阈值 chunkHalfAng·0.5 = 0.703°——行走全程
// 期望零 chunk 操作（账目冻结态下考验逐帧量化一致性 + 重定位联动）。
//
// TASK-B-PLANET-15（S 级）两项补强：
//   1. 内容锚断言：B-PLANET-11 验收发现 nonBlack>0.2 对"池化陈旧内存"恒真（污染样本
//      是人群场景，亮度判据全过）——本轮给全部像素证据加行星特征锚：高程带绿色域占比
//      （近/远/行走）+ limb 圆盘几何（远档四角清屏色/盘心地形）+ 天空/地形带（近/中）
//      + 管线回读对照（RT 中心像素 == 原生共享纹理中心回读的 sRGB 变换值——探针相机
//      跌出 cameras[0] 时 RT 停在池化陈旧内存，此锚必失配）。标定依据：9598c90 验收
//      修复版探针 PNG（TempLogs/acc-p11-myfixture-pngs）与污染样本
//      （TempLogs/acc-p11-construction-pngs）离线解码对比 + probe.log 中心回读逐位
//      sRGB 复算（见各常量注释）。
//   2. 双相机场景用例：第二相机（带 RT——污染场景形态）存在时，主相机渲染不受影响
//      （同位姿重渲染逐像素一致）+ 第二相机被管线显式跳过并打一行日志（ZRenderPipeline
//      选型 b 的 PlayMode 侧取证）。探针相机 depth=-1000（唯一最小）保证 SRP 相机数组
//      （按 depth 升序）恒以探针为 cameras[0]——B-PLANET-11 污染的直接根因是探针相机
//      与泄漏相机同为默认 depth 0、排序并列后泄漏相机（先建）排前。
//
// TASK-B-PLANET-15 接管轮（2026-11-15）：depth=-1000 只解决相机排序，不解决**场景泄漏**——
// 验收实证：同一份 fixture 独跑 5/5 绿、全量套件 5/5 红，差别是前序场景类用例
// LoadSceneAsync("SCN_Game") 后未卸载（探针帧里 renderer=54 可读=6，隔离时 0）。本轮在
// fixture 启动时做显式环境隔离（空场景 + 销毁遗留根对象 + 关掉非探针相机 + 断言 Renderer
// 为 0），环境不干净就红，不再静默取证；fixture 结束拆场景，不污染后续用例。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Domain.World;
using Game.View.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Render;
using ZFrame.Render.Bindings;
using ZFrame.Render.Planet;

namespace Game.View.PlayMode.Tests
{
    public sealed class ZrPlanetProbePlayModeTests
    {
        // ---- 探针口径常量 -----------------------------------------------------------

        /// <summary>三档截图分辨率（1080p 硬口径——共享目标按 Screen 尺寸创建）。</summary>
        private const int CaptureWidth = 1920;
        private const int CaptureHeight = 1080;

        /// <summary>行走前后对照证据图分辨率（半分辨率，控证据体积；对照数值取自全分辨率）。</summary>
        private const int EvidenceWidth = 960;
        private const int EvidenceHeight = 540;

        /// <summary>行星半径（planet_math.h:40 锁定值；与流式管理器共用同一常量）。</summary>
        private const double Radius = ZrPlanetChunkStreamer.NativePlanetRadiusMeters;

        /// <summary>贴地对地高度（--planet2 kP2GroundAltM 同档）。</summary>
        private const double GroundAltMeters = 600.0;

        /// <summary>中档海拔。**口径注记**：设计稿/planet2 先例是 258km，但流式排名重建
        /// 阈值（B-PLANET-07 UpdateRanking：lenDrift>5% 或 turn>0.703°）下，600m→258km 的
        /// 纯海拔跳变 drift=4.04%&lt;5%、turn=0——不触发重建，期望集仍是贴地 4 块（本轮
        /// 实测 live=4 的直接证据，见施工报告遗留第 1 条）。400km 时 drift=6.27%&gt;5% 正常
        /// 重建（~265 块两档同框），故中档取 400km（视觉同档：地平线曲率+两档 chunk）。
        /// 258km 纯爬升覆盖缺口按 MISSING 口径回主控裁决，不在本任务改生产阈值。</summary>
        private const double MidAltMeters = 400_000.0;

        /// <summary>远档海拔（天底全圆盘：视直径 2·asin(R/(R+alt)) ≈ 52.6° < 60° FOV，limb 可见）。</summary>
        private const double FarAltMeters = 8_000_000.0;

        /// <summary>近/中/远三档俯仰角（度，正角 = 向行星表面下俯）：近档微俯 5°（地平线
        /// 0.786° 俯角，略高于画面中心）；中档下俯 17°（地平线 19.7° 俯角，近对准画面中心）；
        /// 远档天底（>89° 走天底分支）。</summary>
        private const double NearPitchDeg = 5.0;
        private const double MidPitchDeg = 17.0;
        private const double FarPitchDeg = 90.0;

        /// <summary>收敛判定：连续稳定帧数与上限（预算 12 远档/tick 下远档 ~1900 块 ≈160 tick）。</summary>
        private const int ConvergeStableFrames = 20;
        private const int ConvergeMaxFrames = 1500;

        /// <summary>B-PLANET-07 默认预算口径（ZrPlanetStreamConfig.CreateDefault）——断言用镜像值。</summary>
        private const int DefaultWorkMicrosPerTick = 9000;
        private const int DefaultNearOpsPerTick = 1;

        /// <summary>0..255 亮度尺上「像素移动」的判定阈值（≈PixelProbeRig.MoveEpsilon 口径）。</summary>
        private const double LumaMoveEpsilon = 4.0;

        /// <summary>重定位绝对位置守恒容差（A-WORLD-01 V3：float 往返量化，1e-3m）。</summary>
        private const double RebaseDriftToleranceMeters = 1e-3;

        // ---- TASK-B-PLANET-15 内容锚口径 ---------------------------------------------
        // 标定：9598c90 验收修复版探针 PNG 离线解码（TempLogs/acc-p11-myfixture-pngs，
        // 解码脚本 TempLogs/task-b-planet-15/decode_png.py）+ 污染样本对照
        // （TempLogs/acc-p11-construction-pngs）+ probe.log 原生中心回读 sRGB 复算。

        /// <summary>探针相机渲染深度：唯一最小值。SRP 的 cameras 数组按 depth 升序排布，
        /// 泄漏相机（既有场景类用例遗留，默认 depth 0）与探针（原默认 0）并列时先建者
        /// 排前——B-PLANET-11 污染场景里探针因此跌出 cameras[0]。-1000 消除并列：无论
        /// 会话里有几台遗留相机，探针恒居 cameras[0]，双相机场景取证正确（简报判据 2）。</summary>
        private const float ProbeCameraDepth = -1000f;

        /// <summary>行星特征色（高程带绿色域）占比下限——近档/行走贴地位姿。标定：正确帧
        /// 实测 0.5472（planet_ed_near.png），池化污染样本（人群场景）同谓词实测 0.0000；
        /// 取 0.15 ≈ 实测的 1/3.6，绿域缺失（污染/陈旧内存）必然失配。</summary>
        private const double PlanetGreenFractionFloorNear = 0.15;

        /// <summary>行星特征色（绿色域）占比下限——远档天底圆盘。标定：正确帧实测 0.1166
        /// （绿/米双色高程带铺盘），取 0.04（≈1/2.9 余量）。中档不设绿锚：400km 距离雾把
        /// 地形压成雾色平面，正确帧实测同为 0.0000（物理行为，非污染）。</summary>
        private const double PlanetGreenFractionFloorFar = 0.04;

        /// <summary>清屏色（读回域）：zrhi_d3d12_unity_target.cpp kClearColor={0.05,0.10,0.15}
        /// 的 8-bit UNORM 值 (13,26,38) 经线性→sRGB 读回变换后的 (64,90,108)。注意：它不是
        /// "污染特征"——正确行星帧的天空/盘外区域正是此色（生产通道无天穹，设计稿 §12-5），
        /// 池化污染样本同样含它；它只用于 limb 圆盘几何锚（盘外=清屏色、盘心=地形色）。</summary>
        private static readonly int[] ClearScreenRgb = { 64, 90, 108 };

        /// <summary>清屏色比对容差（8-bit 通道）。</summary>
        private const int ClearScreenTolerance = 3;

        /// <summary>天空/地形带断言的行带高度（1080p 的 5% = 54 行）。</summary>
        private const int SkyBandRows = CaptureHeight / 20;

        /// <summary>limb 圆盘几何锚的角部 patch 边长（px）。</summary>
        private const int LimbCornerPatchPx = 48;

        /// <summary>limb 圆盘几何锚的盘心 patch 半边长（px）。</summary>
        private const int LimbCenterPatchHalfPx = 20;

        /// <summary>双相机用例：第二相机加入前后同位姿重渲染的移动像素占比上限
        /// （实测 ≈6e-6——同 1km 复访量级；0.02 与 1km 复访上限同口径）。</summary>
        private const double DualCameraUnaffectedMovedLimit = 0.02;

        // 行走几何限定在 face0 u 轴测地线上（face0 基取自 CubeSphereDir：
        // n=(0,0,1)，t1=(1,0,0)——弧长 50km ≪ chunk 弧长 312.7km，不跨流式格边界）。

        // ---- 装备状态 ---------------------------------------------------------------

        private bool _d3d12Ready;
        private RenderPipelineAsset _probePipelineAsset;
        private RenderPipelineAsset _prevDefaultPipeline;
        private RenderPipelineAsset _prevQualityPipeline;

        private GameObject _cameraGo;
        private Camera _camera;
        private GameObject _originGo;
        private WorldOrigin _origin;
        private GameObject _linkGo;
        private WorldOriginPlanetLink _link;
        private GameObject _driverGo;
        private ZrPlanetStreamingDriver _driver;
        private RenderTexture _captureTarget;
        private RenderTexture _evidenceTarget;
        private Texture2D _readback;

        private ulong _baseCreateTotal;
        private ulong _baseDestroyTotal;
        private int _lastRebaseCount;
        private ZrPlanetStatsNative _lastStats;
        private readonly double[] _originReadback = new double[3];
        private readonly List<string> _rebaseTrace = new List<string>(64);
        private Color32[] _lastCapture;
        private float _jumpMagnitude;

        // TASK-B-PLANET-15：多相机显式跳过日志的捕获窗（UnitySetUp 起、UnityTearDown 止）。
        // 覆盖"本测试管线实例的第一次多相机 Render"——无论那发生在 SetUp 首帧（全量套件
        // 泄漏相机会话）还是双相机用例加入第二相机的那一帧（fixture 隔离会话），两种
        // 会话形态下断言等价成立（日志每管线实例只打一次，见 ZRenderPipeline 选型 b）。
        private readonly List<string> _multiCameraLogWindow = new List<string>(4);

        // TASK-B-PLANET-15 接管轮：环境隔离状态（~ProbeEmpty 场景 + 探针对象名前缀）。
        private const string ProbeSceneName = "~ProbeEmpty";
        private const string ProbeObjectPrefix = "~PlanetProbe";

        private Scene _probeScene;
        private bool _probeSceneOwned;
        private string _isolationSummary = string.Empty;

        // ---- 装备生命周期 -----------------------------------------------------------

        [UnitySetUp]
        public IEnumerator SetUpProbe()
        {
            _d3d12Ready = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            if (!_d3d12Ready)
            {
                yield break; // 各用例开头 Assert.Ignore（SetUp 里不做失败判定）
            }

            // 环境隔离必须发生在切换管线**之前**：ZRenderPipeline 实例首次 Render 时扫一次
            // 场景（ZRenderSceneBridge.Rescan），泄漏物件若在首次扫描时还在，会整场留在
            // 提交表里，事后销毁救不回来。
            yield return IsolateEnvironmentAsync();

            // SRP 切换（与 ZRenderBoot.TryEnableFromCommandLine 同一机制，用后即还）。
            _prevDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            _prevQualityPipeline = QualitySettings.renderPipeline;
            _probePipelineAsset = ScriptableObject.CreateInstance<ZRenderPipelineAsset>();
            _probePipelineAsset.name = "ZRenderPipeline (planet probe)";
            QualitySettings.renderPipeline = _probePipelineAsset;
            GraphicsSettings.defaultRenderPipeline = _probePipelineAsset;

            // 1080p 口径：ZRenderPipeline.EnsureNativeTarget 按 Screen 尺寸建原生共享目标。
            // 批处理编辑器的 Game View 默认 640×480——先把 Game View 精确调到 1920×1080
            // （Screen.width/height 随之生效，共享目标自动重建到同尺寸）。拿不到 1080p
            // 就明确失败（1080p 是判据口径，不降级；此时回主控裁决，不偷换分辨率）。
            if (Screen.width != CaptureWidth || Screen.height != CaptureHeight)
            {
                yield return ResizeGameViewToAsync(CaptureWidth, CaptureHeight);
            }

            Assert.AreEqual(CaptureWidth, Screen.width,
                "Screen.width 必须为 1920（1080p 探针口径：共享目标按 Screen 尺寸创建）");
            Assert.AreEqual(CaptureHeight, Screen.height,
                "Screen.height 必须为 1080");

            _captureTarget = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "~PlanetProbe1080p",
            };
            _captureTarget.Create();
            _evidenceTarget = new RenderTexture(EvidenceWidth, EvidenceHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "~PlanetProbeEvidence",
            };
            _evidenceTarget.Create();
            _readback = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGBA32, false);

            // 进程级清场 + 增量基线（托管/原生对账用增量口径——原生总计跨用例累计）。
            ZrPlanetNative.ClearAll();
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out ZrPlanetStatsNative baseStats));
            _baseCreateTotal = baseStats.CreateTotal;
            _baseDestroyTotal = baseStats.DestroyTotal;

            // 多相机日志捕获窗：先订阅再装配/渲染（ZRenderPipeline 选型 b 的日志在
            // 第一次多相机 Render 时打一次——窗口必须覆盖 SetUp 的前两帧）。
            _multiCameraLogWindow.Clear();
            Application.logMessageReceived += OnProbeLogMessageReceived;

            BuildRig();
            DisableForeignCameras();      // 除探针相机外全关（不依赖 depth 排序）
            AssertEnvironmentClean("setup");

            yield return null;
            yield return null; // 门控 → streamer.Initialize（SetWorld 一次）→ 首轮 Tick
        }

        [UnityTearDown]
        public IEnumerator TearDownProbe()
        {
            Application.logMessageReceived -= OnProbeLogMessageReceived;
            DestroyRig();
            ZrPlanetNative.ClearAll();

            if (_readback != null)
            {
                UnityEngine.Object.Destroy(_readback);
                _readback = null;
            }

            _lastCapture = null;

            if (_captureTarget != null)
            {
                _captureTarget.Release();
                UnityEngine.Object.Destroy(_captureTarget);
                _captureTarget = null;
            }

            if (_evidenceTarget != null)
            {
                _evidenceTarget.Release();
                UnityEngine.Object.Destroy(_evidenceTarget);
                _evidenceTarget = null;
            }

            QualitySettings.renderPipeline = _prevQualityPipeline;
            GraphicsSettings.defaultRenderPipeline = _prevDefaultPipeline;
            if (_probePipelineAsset != null)
            {
                UnityEngine.Object.Destroy(_probePipelineAsset);
                _probePipelineAsset = null;
            }

            yield return null; // URP 还原生效一帧（后续 URP 像素用例不受影响）

            // 隔离场景拆掉（先让上一行的销毁生效，再卸载场景——否则会卸掉正在销毁的宿主）。
            yield return UnloadProbeSceneAsync();
        }

        // ---- 环境隔离（TASK-B-PLANET-15 接管轮）--------------------------------------
        // 取证前提：探针帧只能由"只有探针自己"的场景产生。全量套件里前序用例
        // （AffordanceScenePlayModeTests / SurvivalLoopPlayModeTests 等）LoadSceneAsync
        // ("SCN_Game") 后不卸载，探针帧被 54 个遗留 renderer 占据，绿域锚 0.0——
        // depth 排序补丁管不到这一层。这里把隔离做成 fixture 的**前置硬条件**。

        /// <summary>新建空场景 + 清空遗留场景根对象（隔离第 1/2 步）。
        /// 保留：探针自身；DontDestroyOnLoad 里**无渲染物**的持久管理器（框架单例，不是
        /// 污染源，误删会打坏后续用例）。销毁：其余全部场景根（含带渲染物/相机的持久根）。</summary>
        private IEnumerator IsolateEnvironmentAsync()
        {
            _probeScene = SceneManager.CreateScene(ProbeSceneName);
            _probeSceneOwned = _probeScene.IsValid();
            if (_probeSceneOwned)
            {
                SceneManager.SetActiveScene(_probeScene); // 此后 new GameObject 都落这里
            }

            int renderersBefore = UnityEngine.Object.FindObjectsOfType<Renderer>(true).Length;
            int destroyedRoots = 0;
            int keptPersistent = 0;
            Transform[] transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null || transform.parent != null)
                {
                    continue; // 只处理场景根（子物件随根一起销毁，避免逐个 Destroy 抖动）
                }

                GameObject go = transform.gameObject;
                if (go.scene == _probeScene || IsProbeObject(go))
                {
                    continue;
                }

                if (IsPersistentScene(go.scene) && !HasVisualContent(go))
                {
                    keptPersistent++;
                    continue;
                }

                UnityEngine.Object.Destroy(go);
                destroyedRoots++;
            }

            // Destroy 延迟到帧末：两帧后残留才是真实残留（断言才有意义）。
            yield return null;
            yield return null;

            _isolationSummary =
                $"隔离前 renderer={renderersBefore} 销毁根={destroyedRoots} 保留持久管理器={keptPersistent}";
            TestContext.Out.WriteLine($"[B-PLANET-15] isolate(pre): {_isolationSummary}");
        }

        /// <summary>关掉除探针相机以外的全部相机（隔离第 3 步）。用 FindObjectsOfType(true)
        /// 而不是 Camera.allCameras：后者只含 enabled 相机，已禁用的泄漏相机也要按住，
        /// 免得后续用例/系统把它的 enabled 打开（不依赖 depth 排序）。</summary>
        private void DisableForeignCameras()
        {
            int activeBefore = Camera.allCameras.Length;
            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            int disabled = 0;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null || camera == _camera)
                {
                    continue;
                }

                if (camera.enabled)
                {
                    camera.enabled = false;
                    disabled++;
                }
            }

            TestContext.Out.WriteLine(
                $"[B-PLANET-15] isolate(cameras): allCameras {activeBefore} → {Camera.allCameras.Length}，" +
                $"本帧禁用={disabled}");
        }

        /// <summary>隔离结果硬断言（第 4 步）：除探针自身外全局不得有 Renderer，启用相机只能是
        /// 探针相机。不满足就 Assert.Fail 并点名残留对象/场景——**环境不干净必须红**，绝不
        /// 静默继续取证（B-PLANET-11 的教训：脏环境的画面照样 nonBlack，锚才发现是污染）。</summary>
        private void AssertEnvironmentClean(string phase)
        {
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            int foreign = 0;
            StringBuilder residue = new StringBuilder();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsProbeObject(renderer.gameObject))
                {
                    continue;
                }

                foreign++;
                if (residue.Length < 4000)
                {
                    GameObject go = renderer.gameObject;
                    string sceneName = go.scene.IsValid() ? go.scene.name : "<无场景>";
                    residue.Append($"\n  - {renderer.GetType().Name} '{go.name}'（场景='{sceneName}'）");
                }
            }

            string foreignCamera = "<none>";
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i] != _camera)
                {
                    foreignCamera = cameras[i].name;
                    break;
                }
            }

            TestContext.Out.WriteLine(
                $"[B-PLANET-15] isolate({phase}): renderer={renderers.Length} 非探针残留={foreign} " +
                $"启用相机={cameras.Length} 场景数={SceneManager.sceneCount} " +
                $"探针场景='{(_probeScene.IsValid() ? _probeScene.name : "<none>")}' {_isolationSummary}");

            Assert.AreEqual(0, foreign,
                $"环境隔离失败（{phase}）：探针取证要求场景里只有探针自身的物件，现存 {foreign} 个" +
                "非探针 Renderer——全量套件下前序用例遗留的 SCN_Game 场景会使行星绿域锚静默失效" +
                $"（B-PLANET-11 污染根因），环境不干净不允许继续取证。残留对象：{residue}");

            Assert.AreEqual("<none>", foreignCamera,
                $"环境隔离失败（{phase}）：除探针相机外不得有启用相机（现存 '{foreignCamera}'）——" +
                "残留相机会抢 cameras[0]，行星通道画面落不到探针 RT 上");
        }

        /// <summary>拆隔离场景（fixture 结束不污染后续用例）。只剩探针场景时 Unity 不允许
        /// 卸载最后一个场景——此时留空场景，对后续用例等价于空场。</summary>
        private IEnumerator UnloadProbeSceneAsync()
        {
            if (!_probeSceneOwned || !_probeScene.IsValid())
            {
                _probeSceneOwned = false;
                yield break;
            }

            if (SceneManager.sceneCount <= 1)
            {
                TestContext.Out.WriteLine(
                    "[B-PLANET-15] isolate(teardown): 只剩探针场景，保留空场景（Unity 不允许卸载最后一个场景）");
                _probeSceneOwned = false;
                yield break;
            }

            AsyncOperation unload = SceneManager.UnloadSceneAsync(_probeScene);
            if (unload != null)
            {
                while (!unload.isDone)
                {
                    yield return null;
                }
            }

            TestContext.Out.WriteLine(
                $"[B-PLANET-15] isolate(teardown): 已卸载 '{ProbeSceneName}'，剩余场景数={SceneManager.sceneCount}");
            _probeSceneOwned = false;
        }

        /// <summary>探针自身（含双相机用例临时创建的 ~PlanetProbeSecondCamera）。</summary>
        private static bool IsProbeObject(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            Transform root = go.transform.root;
            return root != null && root.name.StartsWith(ProbeObjectPrefix, StringComparison.Ordinal);
        }

        /// <summary>DontDestroyOnLoad 场景（Unity 用它装跨场景常驻对象）。</summary>
        private static bool IsPersistentScene(Scene scene)
        {
            return scene.IsValid() && string.Equals(scene.name, "DontDestroyOnLoad", StringComparison.Ordinal);
        }

        /// <summary>该根对象下是否有会入画的东西（Renderer/Camera）。有 = 污染源，销毁。</summary>
        private static bool HasVisualContent(GameObject go)
        {
            return go.GetComponentInChildren<Renderer>(true) != null
                || go.GetComponentInChildren<Camera>(true) != null;
        }

        // ---- 用例 1：三档截图（1080p）-----------------------------------------------

        [UnityTest]
        public IEnumerator Probe_ThreeTierScreenshots_CaptureEvidence()
        {
            if (!_d3d12Ready)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                yield break;
            }

            Assert.IsTrue(_probePipelineAsset == GraphicsSettings.currentRenderPipeline,
                "探针必须在 ZRender 管线下跑（GraphicsSettings.currentRenderPipeline）");
            Assert.IsNotNull(_driver.Streamer, "流式管理器必须已初始化（v3 门控通过）");
            Assert.IsFalse(_driver.GateFailed, "门控不得失败");

            // 近档：贴地 600m，沿 face0 赤道看向地平线——地平线 + 高程带 + 近档 chunk。
            yield return PoseAndSettleAsync(0.0, GroundAltMeters, NearPitchDeg, 10f, 400_000f);
            yield return AssertFrameCoverageAsync("near");
            yield return CaptureLumaAsync();
            double nonBlack = NonBlackFraction(_lastCapture);
            Assert.Greater(nonBlack, 0.2, "近档帧必须画出地形（非清屏像素占比）");
            AssertPlanetGreenAnchor("near", PlanetGreenFractionFloorNear);
            AssertBandAnchors("near", expectSkyTop: true, expectTerrainBottom: true);
            yield return AssertCaptureMatchesPipelineCenterAsync("near");
            TestContext.Out.WriteLine(
                $"[B-PLANET-11] near(600m): nonBlack={nonBlack:F3} live={_lastStats.LiveChunks} " +
                $"drawn={_lastStats.DrawnLastFrame} culled={_lastStats.CulledLastFrame} " +
                $"vbytes={_lastStats.VertexBytes} planetMsLast={_lastStats.LastFrameMs:F3}");
            yield return WritePngAsync("planet_ed_near.png", _captureTarget);

            // 中档：400km（两档 chunk 同框——见 MidAltMeters 口径注记）——地平线曲率 + 两档 chunk。
            yield return PoseAndSettleAsync(0.0, MidAltMeters, MidPitchDeg, 200_000f, 2_500_000f);
            yield return AssertFrameCoverageAsync("mid");
            yield return CaptureLumaAsync();
            nonBlack = NonBlackFraction(_lastCapture);
            Assert.Greater(nonBlack, 0.2, "中档帧必须画出地形（非清屏像素占比）");
            // 中档无绿域锚：距离雾把地形压成雾色平面（正确帧绿色域实测 0.0000，非污染）。
            AssertBandAnchors("mid", expectSkyTop: true, expectTerrainBottom: true);
            yield return AssertCaptureMatchesPipelineCenterAsync("mid");
            TestContext.Out.WriteLine(
                $"[B-PLANET-11] mid(400km): nonBlack={nonBlack:F3} live={_lastStats.LiveChunks} " +
                $"drawn={_lastStats.DrawnLastFrame} culled={_lastStats.CulledLastFrame} " +
                $"vbytes={_lastStats.VertexBytes} planetMsLast={_lastStats.LastFrameMs:F3}");
            yield return WritePngAsync("planet_ed_mid.png", _captureTarget);

            // 远档：8000km 天底全圆盘——limb 可见 + 全远档 chunk（距离 > 990km 迟滞带上沿）。
            yield return PoseAndSettleAsync(0.0, FarAltMeters, FarPitchDeg, 4_000_000f, 9_000_000f);
            yield return AssertFrameCoverageAsync("far");
            yield return CaptureLumaAsync();
            nonBlack = NonBlackFraction(_lastCapture);
            Assert.Greater(nonBlack, 0.2, "远档帧必须画出行星圆盘（非清屏像素占比）");
            Assert.AreEqual(0u, _lastStats.SeamCheckFails, "远档全程共享边核查不得失败");
            AssertPlanetGreenAnchor("far", PlanetGreenFractionFloorFar);
            AssertLimbDiskAnchors("far");
            yield return AssertCaptureMatchesPipelineCenterAsync("far");
            TestContext.Out.WriteLine(
                $"[B-PLANET-11] far(8000km): nonBlack={nonBlack:F3} live={_lastStats.LiveChunks} " +
                $"drawn={_lastStats.DrawnLastFrame} culled={_lastStats.CulledLastFrame} " +
                $"vbytes={_lastStats.VertexBytes} planetMsLast={_lastStats.LastFrameMs:F3}");
            yield return WritePngAsync("planet_ed_far.png", _captureTarget);
        }

        // ---- 用例 2/3/4：球面行走账目三变体 -----------------------------------------

        [UnityTest]
        public IEnumerator Probe_Walk1km_AccountZeroDrift_NoRebase_QuantizationConsistency()
        {
            if (!_d3d12Ready)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                yield break;
            }

            WalkContext ctx = new WalkContext("walk1k", 0.0, 1_000.0, 100, 0.02);
            yield return RunWalkAsync(ctx);

            Assert.AreEqual(0, ctx.RebasesRecorded,
                "1km 行走（<4000m 阈值）不得触发重定位——该变体考验纯逐帧量化一致性");
            AssertRevisitAndContinuity(ctx);
        }

        [UnityTest]
        public IEnumerator Probe_Walk5km_AccountZeroDrift_RebaseLinkageVerified()
        {
            if (!_d3d12Ready)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                yield break;
            }

            WalkContext ctx = new WalkContext("walk5k", 0.0, 5_000.0, 250, 0.05);
            yield return RunWalkAsync(ctx);

            Assert.GreaterOrEqual(ctx.RebasesRecorded, 1,
                "5km 行走（去程跨 4000m 阈值）必须触发至少一次重定位（触发点见 rebase trace）");
            AssertRevisitAndContinuity(ctx);
        }

        [UnityTest]
        public IEnumerator Probe_Walk50km_AccountZeroDrift_MultiRebaseAndContinuity()
        {
            if (!_d3d12Ready)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                yield break;
            }

            WalkContext ctx = new WalkContext("walk50k", 0.0, 50_000.0, 500, 0.05);
            yield return RunWalkAsync(ctx);

            // 去程 12 次（4/8/…/48km）+ 回程 ~13 次（含终点恰在阈值）——下界取 20 压边角。
            Assert.GreaterOrEqual(ctx.RebasesRecorded, 20,
                "50km 往返必须触发 ≥20 次重定位（每次 4km；触发点见 rebase trace）");
            AssertRevisitAndContinuity(ctx);
        }

        /// <summary>跳变检测判读（三层，各层口径独立成立）：
        /// 1) 复访重渲染：同绝对位姿走出去再走回，重渲染移动像素占比 ≈ 0（实测 ~6e-6）——
        ///    任何持久化的坐标/重定位误差都表现为整幅位移（最灵敏的一层）；
        /// 2) 重定位帧连续性：rebase 帧的移动像素占比必须落在常规运动视差带内
        ///    （maxOnRebase &lt; max(0.25, 1.5×maxOnOther + 0.05)）——重定位失败（原点错帧）
        ///    会表现为 rebase 帧整幅位移离群（实测 rebase 帧 0.117 &lt; 常规帧 0.19）；
        /// 3) 单帧粗跳变：任意帧移动像素占比 &lt; 0.5——运动视差随局部地形特征密度波动
        ///    （100m/帧实测中位 0.011 / 峰值 0.19，长尾非跳变），真跳变（原点错帧 4km 级）
        ///    表现为全幅位移（&gt;0.5）。
        /// 每帧移动占比的下限证据：pipeline 数学断言（原点逐位 + 1e-3m 守恒）逐帧独立成立。</summary>
        private void AssertRevisitAndContinuity(WalkContext ctx)
        {
            double revisitMeanAbs = FrameDiff(ctx.BeforePixels, ctx.AfterPixels, out double revisitMoved);

            List<double> sorted = new List<double>(ctx.MovedFractions);
            sorted.Sort();
            double median = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0.0;
            double maxOnRebase = 0.0;
            double maxOnOther = 0.0;
            for (int i = 0; i < ctx.MovedFractions.Count; i++)
            {
                bool rebaseInBetween = i == 0
                    || ctx.RebaseCounts[i] != ctx.RebaseCounts[i - 1];
                if (rebaseInBetween)
                {
                    maxOnRebase = Math.Max(maxOnRebase, ctx.MovedFractions[i]);
                }
                else
                {
                    maxOnOther = Math.Max(maxOnOther, ctx.MovedFractions[i]);
                }
            }

            double rebaseBand = Math.Max(0.25, 1.5 * maxOnOther + 0.05);
            TestContext.Out.WriteLine(
                $"[B-PLANET-11] {ctx.Tag}: rebases={ctx.RebasesRecorded} frames={ctx.MovedFractions.Count} " +
                $"revisit movedFraction={revisitMoved:F6} meanAbs={revisitMeanAbs:F4} " +
                $"frameMoved median={median:F6} max={ctx.MaxMovedFraction:F6} " +
                $"maxOnRebase={maxOnRebase:F6} maxOnOther={maxOnOther:F6} rebaseBand={rebaseBand:F4} " +
                $"lodOpFrames={ctx.LodOpFrames}");
            foreach (string line in _rebaseTrace)
            {
                TestContext.Out.WriteLine($"[B-PLANET-11] {ctx.Tag} rebase {line}");
            }

            Assert.Less(revisitMoved, ctx.RevisitMovedFractionLimit,
                $"{ctx.Tag} 复访重渲染不得出现可见跳变（坐标/重定位跳变表现为整幅像素位移）");
            Assert.Less(maxOnRebase, rebaseBand,
                $"{ctx.Tag} 重定位帧连续性：rebase 帧像素位移必须落在常规运动视差带内");
            Assert.Less(ctx.MaxMovedFraction, 0.5,
                $"{ctx.Tag} 单帧粗跳变：任意帧不得出现全幅位移级的不连续");
        }

        // ---- 用例 5：双相机场景（TASK-B-PLANET-15）-----------------------------------

        /// <summary>双相机场景取证（B-PLANET-11 污染场景形态的正面用例）：
        /// 1) 主相机渲染不受影响：第二相机（带 RT）加入前后，探针相机同位姿重渲染逐像素
        ///    一致（movedFraction 实测 ≈6e-6）+ 内容锚全绿——管线仍渲 cameras[0]（探针
        ///    相机 depth=-1000 恒居首，第二相机 depth=-999 只作被跳过的 cameras[1]）；
        /// 2) 第二相机被显式跳过且有日志：多相机会话首帧打一行「[ZRender] 多相机会话」
        ///    日志（每管线实例一次）——静默降级变显式可诊断（ZRenderPipeline 选型 b）。
        /// 日志断言用捕获窗（UnitySetUp 订阅起）而非 LogAssert：窗覆盖本测试管线实例的
        /// 第一次多相机 Render——fixture 隔离会话落在加入第二相机的那帧，全量套件泄漏
        /// 相机会话落在 SetUp 首帧，两种形态断言等价成立。</summary>
        [UnityTest]
        public IEnumerator Probe_DualCamera_MainCameraUnaffected_SecondCameraExplicitlySkipped()
        {
            if (!_d3d12Ready)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                yield break;
            }

            Assert.IsTrue(_probePipelineAsset == GraphicsSettings.currentRenderPipeline,
                "探针必须在 ZRender 管线下跑（GraphicsSettings.currentRenderPipeline）");
            Assert.IsFalse(_driver.GateFailed, "门控不得失败");

            // 基线（fixture 隔离会话此时的单相机会话）：贴地姿态收敛 + 全套内容锚。
            yield return PoseAndSettleAsync(0.0, GroundAltMeters, NearPitchDeg, 10f, 400_000f);
            yield return AssertFrameCoverageAsync("dualcam-base");
            yield return CaptureLumaAsync();
            Color32[] baseline = _lastCapture;
            AssertPlanetGreenAnchor("dualcam-base", PlanetGreenFractionFloorNear);
            yield return AssertCaptureMatchesPipelineCenterAsync("dualcam-base");

            // 第二相机：RT 相机形态（与探针同形），depth 只比探针高一档 → SRP 升序排布
            // 下的 cameras[1]。若会话里另有泄漏相机（depth 0），它们排第二相机之后，
            // 不影响本用例的 cameras[0]/cameras[1] 语义。
            GameObject secondGo = new GameObject("~PlanetProbeSecondCamera");
            Camera secondCamera = secondGo.AddComponent<Camera>();
            secondCamera.depth = ProbeCameraDepth + 1f;
            secondCamera.clearFlags = CameraClearFlags.SolidColor;
            secondCamera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            secondCamera.allowHDR = false;
            secondCamera.allowMSAA = false;
            RenderTexture secondTarget = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32)
            {
                name = "~PlanetProbeSecondRT",
            };
            secondTarget.Create();
            secondCamera.targetTexture = secondTarget;

            try
            {
                yield return null;
                yield return null; // 多相机首帧：管线打显式跳过日志；探针相机照常渲染

                Assert.Greater(_multiCameraLogWindow.Count, 0,
                    "多相机会话必须有「[ZRender] 多相机会话」显式跳过日志" +
                    "（ZRenderPipeline 选型 b：静默降级已禁）");
                foreach (string line in _multiCameraLogWindow)
                {
                    TestContext.Out.WriteLine($"[B-PLANET-15] multi-camera log: {line}");
                }

                yield return CaptureLumaAsync();
                double meanAbs = FrameDiff(baseline, _lastCapture, out double moved);
                Assert.Less(moved, DualCameraUnaffectedMovedLimit,
                    $"第二相机加入后主相机同位姿重渲染必须逐像素不受影响（moved={moved:F6}）");
                Assert.Greater(PlanetGreenFraction(_lastCapture), PlanetGreenFractionFloorNear,
                    "第二相机加入后行星绿色域内容锚必须仍然成立");
                yield return AssertCaptureMatchesPipelineCenterAsync("dualcam-dual");
                TestContext.Out.WriteLine(
                    $"[B-PLANET-15] dualcam: moved={moved:F6} meanAbs={meanAbs:F4} " +
                    $"green={PlanetGreenFraction(_lastCapture):F4} secondDepth={secondCamera.depth}");
            }
            finally
            {
                if (secondCamera != null)
                {
                    secondCamera.targetTexture = null;
                }

                UnityEngine.Object.Destroy(secondGo);
                secondTarget.Release();
                UnityEngine.Object.Destroy(secondTarget);
            }

            yield return null; // 清场帧：第二相机销毁生效（后续用例回到单相机会话）
        }

        // ---- 行走机制 ----------------------------------------------------------------

        /// <summary>单变体完整协议：起点收敛 → 前照（全分辨率亮度 + 半分辨率 PNG）→
        /// 去程逐帧（账目/接线/预算/连续性/重定位守恒）→ 回程逐帧 → 终态收敛 →
        /// 复访照 → 前后像素对照。</summary>
        private IEnumerator RunWalkAsync(WalkContext ctx)
        {
            _rebaseTrace.Clear();
            yield return PoseAndSettleAsync(ctx.StartArc, GroundAltMeters, NearPitchDeg, 10f, 400_000f);

            _lastRebaseCount = _origin.RebaseCount;
            ctx.RebaseCountStart = _origin.RebaseCount;
            yield return CaptureLumaAsync();
            ctx.BeforePixels = _lastCapture;
            // TASK-B-PLANET-15 内容锚：行走取证帧也必须有行星绿色域（贴地位姿，实测
            // 0.5472）——行走三层像素判据（复访/rebase 连续性/单帧粗跳变）全是自比对照，
            // 无内容锚时池化陈旧内存的"零移动"也能蒙混过关（B-PLANET-11 验收盲区）。
            AssertPlanetGreenAnchor(ctx.Tag + ":before", PlanetGreenFractionFloorNear);
            yield return WritePngAsync(ctx.Tag + "_before.png", _evidenceTarget);

            double stepArc = ctx.LengthMeters / ctx.Steps;
            for (int step = 1; step <= ctx.Steps; step++)
            {
                yield return AdvanceAndCheckAsync(ctx, ctx.StartArc + stepArc * step, step);
            }

            for (int step = ctx.Steps - 1; step >= 0; step--)
            {
                yield return AdvanceAndCheckAsync(ctx, ctx.StartArc + stepArc * step, step);
            }

            yield return SettleAsync(ctx.Tag + ":return");
            _lastRebaseCount = _origin.RebaseCount;
            yield return CaptureLumaAsync();
            ctx.AfterPixels = _lastCapture;
            yield return WritePngAsync(ctx.Tag + "_after.png", _evidenceTarget);
        }

        /// <summary>单步推进（真帧循环一帧）：先复核上帧渲染态（账目/接线/重定位/连续性），
        /// 再把相机推到 arc 的双精度姿态（Unity 位置 = float(camAbs − offset)，offset 由
        /// WorldOrigin 自动重定位维护），交给 WorldOrigin.Update / Driver.Update /
        /// Link.LateUpdate / SRP 渲染。</summary>
        private IEnumerator AdvanceAndCheckAsync(WalkContext ctx, double arc, int step)
        {
            // (a) 上帧渲染态复核（此时相机仍在上一姿态；本帧 Update 可能刚做过重定位）。
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out _lastStats));
            Assert.IsTrue(_lastStats.CreateTotal - _lastStats.DestroyTotal == _lastStats.LiveChunks,
                $"[{ctx.Tag} step {step} @arc {arc:F0}] 账目恒等式破坏：create={_lastStats.CreateTotal} " +
                $"destroy={_lastStats.DestroyTotal} live={_lastStats.LiveChunks}");

            ZrPlanetChunkStreamer streamer = _driver.Streamer;
            Assert.IsNotNull(streamer, $"[{ctx.Tag}] 流式管理器中途丢失");
            Assert.AreEqual((ulong)streamer.CreatedTotal, _lastStats.CreateTotal - _baseCreateTotal,
                $"[{ctx.Tag} step {step}] 托管/原生创建增量必须逐项相等（零漂移）");
            Assert.AreEqual((ulong)streamer.DestroyedTotal, _lastStats.DestroyTotal - _baseDestroyTotal,
                $"[{ctx.Tag} step {step}] 托管/原生销毁增量必须逐项相等（零漂移）");
            Assert.AreEqual((ulong)streamer.LiveCount, _lastStats.LiveChunks,
                $"[{ctx.Tag} step {step}] 托管/原生存活 chunk 数必须相等");
            Assert.IsTrue(streamer.LastAccountOk, $"[{ctx.Tag} step {step}] 流式管理器自检必须通过");
            AssertTickBudget(streamer, ctx.Tag);

            int rebaseDelta = _origin.RebaseCount - _lastRebaseCount;
            WorldPosition expected = _origin.UnityToWorld(_camera.transform.position);
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetCameraOrigin(_originReadback));
            if (rebaseDelta == 0)
            {
                Assert.AreEqual(expected.X, _originReadback[0],
                    $"[{ctx.Tag} step {step}] SetCameraOrigin X 必须与 WorldOrigin 权威值逐位一致");
                Assert.AreEqual(expected.Y, _originReadback[1],
                    $"[{ctx.Tag} step {step}] SetCameraOrigin Y 必须与 WorldOrigin 权威值逐位一致");
                Assert.AreEqual(expected.Z, _originReadback[2],
                    $"[{ctx.Tag} step {step}] SetCameraOrigin Z 必须与 WorldOrigin 权威值逐位一致");
            }
            else
            {
                double drift = Math.Sqrt(
                    (expected.X - _originReadback[0]) * (expected.X - _originReadback[0])
                    + (expected.Y - _originReadback[1]) * (expected.Y - _originReadback[1])
                    + (expected.Z - _originReadback[2]) * (expected.Z - _originReadback[2]));
                Assert.Less(drift, RebaseDriftToleranceMeters,
                    $"[{ctx.Tag} step {step}] 重定位帧 SetCameraOrigin 必须守恒（float 往返量化内）");
                _rebaseTrace.Add($"#{_origin.RebaseCount} step={step} arc={arc:F1}m " +
                                 $"camAbs=({_originReadback[0]:E4},{_originReadback[1]:E4},{_originReadback[2]:E4}) " +
                                 $"drift={drift:F6}m unityPosAfter=({_camera.transform.position.x:F2},{_camera.transform.position.y:F2},{_camera.transform.position.z:F2})");
                Assert.Less(_camera.transform.position.magnitude, 4200f,
                    "重定位后相机 Unity 坐标必须回到阈值内");
            }

            _lastRebaseCount = _origin.RebaseCount;
            ctx.RebasesRecorded = _origin.RebaseCount - ctx.RebaseCountStart;

            if (streamer.LastOps.Count > 0)
            {
                ctx.LodOpFrames++;
            }

            // 连续性扫描：本步开头读回的 RT 是上一帧渲染结果（双槽 readable 语义），
            // 相邻两步读回即相邻两帧渲染——坐标跳变/重定位失败在此表现为整幅位移。
            // 逐帧移动占比 + 采集时刻的累计重定位数入表（判读时区分重定位帧/常规视差帧）。
            Color32[] previousFrame = ctx.CurrentPixels;
            int rebaseCountAtCapture = _origin.RebaseCount;
            yield return CaptureLumaAsync();
            ctx.CurrentPixels = _lastCapture;
            ctx.RebaseCounts.Add(rebaseCountAtCapture);
            if (previousFrame != null)
            {
                double meanAbs = FrameDiff(previousFrame, ctx.CurrentPixels, out double moved);
                ctx.MovedFractions.Add(moved);
                if (moved > ctx.MaxMovedFraction)
                {
                    ctx.MaxMovedFraction = moved;
                }

                if (meanAbs > ctx.MaxMeanAbs)
                {
                    ctx.MaxMeanAbs = meanAbs;
                }
            }

            // (b) 推进姿态：目标绝对位姿（double 域）→ Unity 位置（先 double 相减再一次 cast）。
            ComputeSpherePose(arc, GroundAltMeters, NearPitchDeg,
                out double camAbsX, out double camAbsY, out double camAbsZ, out Vector3 forward, out Vector3 up);
            WorldPosition offset = _origin.OriginOffset;
            _cameraGo.transform.position = new Vector3(
                (float)(camAbsX - offset.X), (float)(camAbsY - offset.Y), (float)(camAbsZ - offset.Z));
            _jumpMagnitude = _cameraGo.transform.position.magnitude;
            _cameraGo.transform.rotation = Quaternion.LookRotation(forward, up);

            yield return null;

            // (c) 绝对位姿守恒（含重定位帧——A-WORLD-01 V3 口径；容差取跳姿幅值的 float
            // 量化界，行走档 pos ≤ 4.2km → ULP 0.244mm，1e-3m 下界兜底）。
            WorldPosition achieved = _origin.UnityToWorld(_camera.transform.position);
            double poseDrift = Math.Sqrt(
                (achieved.X - camAbsX) * (achieved.X - camAbsX)
                + (achieved.Y - camAbsY) * (achieved.Y - camAbsY)
                + (achieved.Z - camAbsZ) * (achieved.Z - camAbsZ));
            double quantum = Math.Pow(2.0, Math.Floor(Math.Log(Math.Max(_jumpMagnitude, 1e-6), 2.0)) - 23.0);
            Assert.Less(poseDrift, Math.Max(RebaseDriftToleranceMeters, 4.0 * quantum),
                $"[{ctx.Tag} step {step}] 绝对位姿必须守恒（jump={_jumpMagnitude:F1}m drift={poseDrift:F6}m）");
        }

        /// <summary>分帧预算断言（PlayMode 侧镜像 B-PLANET-07 A8）：本帧 tick 发出的操作
        /// 成本和 ≤ 默认预算；近档（升级/近档创建）操作 ≤ 硬帽 1。</summary>
        private void AssertTickBudget(ZrPlanetChunkStreamer streamer, string label)
        {
            int nearOps = 0;
            int micros = 0;
            IReadOnlyList<ZrPlanetStreamOp> ops = streamer.LastOps;
            for (int i = 0; i < ops.Count; i++)
            {
                micros += ops[i].EstimatedMicros;
                if (ops[i].Kind == ZrPlanetStreamOpKind.SetLodUpgrade
                    || (ops[i].Kind == ZrPlanetStreamOpKind.Create && ops[i].LodLevel == ZrPlanetChunkStreamer.LodNear))
                {
                    nearOps++;
                }
            }

            Assert.LessOrEqual(nearOps, DefaultNearOpsPerTick,
                $"{label} 近档硬帽（≤{DefaultNearOpsPerTick}/tick）被破坏");
            Assert.LessOrEqual(micros, DefaultWorkMicrosPerTick,
                $"{label} 帧预算（{DefaultWorkMicrosPerTick}µs）被破坏");
        }

        // ---- 姿态与收敛 ---------------------------------------------------------------

        /// <summary>跳到给定球面姿态（弧长/海拔/俯仰）→ 自动重定位归位 → 收敛流式。</summary>
        private IEnumerator PoseAndSettleAsync(double arc, double altMeters, double pitchDeg,
            float nearMeters, float farMeters)
        {
            ApplyPose(arc, altMeters, pitchDeg, nearMeters, farMeters);
            yield return null;
            yield return null; // WorldOrigin.Update 重定位 + Link.LateUpdate 推送 + 渲染

            ComputeSpherePose(arc, altMeters, pitchDeg,
                out double camAbsX, out double camAbsY, out double camAbsZ, out _, out _);
            WorldPosition achieved = _origin.UnityToWorld(_camera.transform.position);
            double drift = Math.Sqrt(
                (achieved.X - camAbsX) * (achieved.X - camAbsX)
                + (achieved.Y - camAbsY) * (achieved.Y - camAbsY)
                + (achieved.Z - camAbsZ) * (achieved.Z - camAbsZ));
            // 容差随**跳姿时刻**的 Unity 位置幅值走（重定位后 pos≈0，量化界必须取跳姿幅值）：
            // float 往返量化界 = 幅值所在 2^e 档的 ULP（2^(e−23)），rebase 守恒该量化值。
            // 贴地/行走档（≤4km，重定位保障）才是 A-WORLD-01 的 1e-3m 口径；跨档跳姿
            // （首跳 6371km、中档 258km / 远档 8000km 差值）幅值大，ULP 达 0.125~0.5m。
            float jumpMagnitude = _jumpMagnitude;
            double quantum = Math.Pow(2.0, Math.Floor(Math.Log(Math.Max(jumpMagnitude, 1e-6), 2.0)) - 23.0);
            double tolerance = Math.Max(RebaseDriftToleranceMeters, 4.0 * quantum);
            Assert.Less(drift, tolerance,
                $"pose@arc{arc:F0} 跳姿后的绝对位姿必须在 float 量化界内守恒（jump={jumpMagnitude:F1}m tol={tolerance:F4}m drift={drift:F4}m）");

            yield return SettleAsync($"pose@{arc:F0}");
            _lastRebaseCount = _origin.RebaseCount;
        }

        private void ApplyPose(double arc, double altMeters, double pitchDeg, float nearMeters, float farMeters)
        {
            ComputeSpherePose(arc, altMeters, pitchDeg,
                out double camAbsX, out double camAbsY, out double camAbsZ, out Vector3 forward, out Vector3 up);
            WorldPosition offset = _origin.OriginOffset;
            _cameraGo.transform.position = new Vector3(
                (float)(camAbsX - offset.X), (float)(camAbsY - offset.Y), (float)(camAbsZ - offset.Z));
            _jumpMagnitude = _cameraGo.transform.position.magnitude; // 量化容差用（重定位前幅值）
            _cameraGo.transform.rotation = Quaternion.LookRotation(forward, up);
            _camera.nearClipPlane = nearMeters;
            _camera.farClipPlane = farMeters;
        }

        /// <summary>球面姿态（double 域）：face0 赤道测地线，θ = arc/R；贴地高度 = 
        /// SampleHeight（与流式锚点同一纯函数）+ 海拔；俯仰正角 = 向行星表面下俯。</summary>
        private void ComputeSpherePose(double arcMeters, double altMeters, double pitchDeg,
            out double camAbsX, out double camAbsY, out double camAbsZ, out Vector3 forward, out Vector3 up)
        {
            double theta = arcMeters / Radius;
            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);
            double dirX = sin;        // dir = n·cosθ + t1·sinθ（face0：n=(0,0,1)，t1=(1,0,0)）
            double dirZ = cos;
            double tanX = cos;        // 切向 = d(dir)/dθ = t1·cosθ − n·sinθ
            double tanZ = -sin;

            int sampleResult = ZrPlanetNative.SampleHeight(dirX * Radius, 0.0, dirZ * Radius, out float heightM);
            Assert.AreEqual(ZrPlanetNative.Ok, sampleResult, "SampleHeight 失败（SetWorld 未就绪？）");
            double r = Radius + heightM + altMeters;
            camAbsX = dirX * r;
            camAbsY = 0.0;
            camAbsZ = dirZ * r;

            double pitch = pitchDeg * (Math.PI / 180.0);
            if (pitchDeg > 89.0)
            {
                // 天底视角：forward = −dir（指向行星中心），up 用切向（LookRotation 需不共线）。
                forward = new Vector3((float)-dirX, 0f, (float)-dirZ);
                up = new Vector3((float)tanX, 0f, (float)tanZ);
            }
            else
            {
                double fx = tanX * Math.Cos(pitch) - dirX * Math.Sin(pitch);
                double fz = tanZ * Math.Cos(pitch) - dirZ * Math.Sin(pitch);
                forward = new Vector3((float)fx, 0f, (float)fz);
                up = new Vector3((float)dirX, 0f, (float)dirZ);
            }
        }

        /// <summary>流式收敛：连续 ConvergeStableFrames 帧「零待办 + 零延后 + 账目对齐」。
        /// 预算断言全程逐帧执行（收敛期即分帧摊布的直接证据）。</summary>
        private IEnumerator SettleAsync(string label)
        {
            ZrPlanetChunkStreamer streamer = _driver.Streamer;
            Assert.IsNotNull(streamer, $"[{label}] 流式管理器必须已初始化");
            Assert.IsFalse(_driver.GateFailed, $"[{label}] 门控不得失败");

            int stable = 0;
            int frames = 0;
            while (frames < ConvergeMaxFrames && stable < ConvergeStableFrames)
            {
                yield return null;
                frames++;
                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out _lastStats));
                AssertTickBudget(streamer, label);

                bool settled = _lastStats.CreateTotal - _lastStats.DestroyTotal == _lastStats.LiveChunks
                    && _lastStats.PendingUploads == 0u
                    && _lastStats.PendingLod == 0u
                    && streamer.DeferredWorkCount == 0
                    && (ulong)streamer.LiveCount == _lastStats.LiveChunks;
                stable = settled ? stable + 1 : 0;
            }

            Assert.LessOrEqual(ConvergeStableFrames, stable,
                $"[{label}] 流式未在 {ConvergeMaxFrames} 帧内收敛（live={_lastStats.LiveChunks} " +
                $"pendingUploads={_lastStats.PendingUploads} pendingLod={_lastStats.PendingLod} " +
                $"deferred={streamer.DeferredWorkCount}）");
            TestContext.Out.WriteLine(
                $"[B-PLANET-11] {label}: converged in {frames} frames, live={_lastStats.LiveChunks} " +
                $"createTotal={_lastStats.CreateTotal - _baseCreateTotal} " +
                $"destroyTotal={_lastStats.DestroyTotal - _baseDestroyTotal}");
        }

        /// <summary>渲染覆盖断言：drawn+culled 覆盖全部存活块（原生剔除口径）且有真实绘制
        /// （真帧循环冒烟——行星通道确实画了东西）。</summary>
        private IEnumerator AssertFrameCoverageAsync(string label)
        {
            yield return null;
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out _lastStats));
            Assert.AreEqual((ulong)_lastStats.LiveChunks,
                (ulong)_lastStats.DrawnLastFrame + (ulong)_lastStats.CulledLastFrame,
                $"[{label}] drawn + culled 必须覆盖全部存活块");
            Assert.Greater(_lastStats.DrawnLastFrame, 0u, $"[{label}] 必须有真实绘制（真帧循环冒烟）");
        }

        // ---- 装备装配 -----------------------------------------------------------------

        /// <summary>批处理编辑器里把 Game View 渲染区调到精确 width×height。口径实证
        /// （[probe] 日志）：EditorWindow.position 是**逻辑像素**，编辑器播放态
        /// Screen.width/height 是**物理像素**（本机 OS 200% DPI：请求 800×600 窗口实测
        /// Screen=1600×1158）。做法：小窗口标定 DPI 缩放比与 chrome（工具栏等），按
        /// 「窗口逻辑尺寸 = (目标物理渲染区 + chrome)/缩放比」落位，再按实测差迭代校正。
        /// 失败时调用方的 Screen 断言以实测值明确暴露（1080p 拿不到=判据未达成）。</summary>
        private static IEnumerator ResizeGameViewToAsync(int width, int height)
        {
            System.Type gameViewType = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                yield break;
            }

            UnityEditor.EditorWindow gameView = UnityEditor.EditorWindow.GetWindow(gameViewType);
            if (gameView == null)
            {
                yield break;
            }

            Debug.Log($"[probe] pre: Screen={Screen.width}x{Screen.height} " +
                      $"currentRes={Screen.currentResolution.width}x{Screen.currentResolution.height} dpi={Screen.dpi}");

            // 小窗口标定：缩放比 = 请求逻辑宽 / 实测物理宽；chrome = 请求物理框 − 实测渲染区。
            const int CalibrationWidth = 800;
            const int CalibrationHeight = 600;
            gameView.position = new Rect(0f, 0f, CalibrationWidth, CalibrationHeight);
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            float scale = Math.Max(Screen.width, 1) / (float)CalibrationWidth; // 物理每逻辑像素（本机 2.0）
            float chromeWidth = CalibrationWidth * scale - Screen.width;
            float chromeHeight = CalibrationHeight * scale - Screen.height;
            Debug.Log($"[probe] calib: scale={scale:F3} chrome={chromeWidth:F1}x{chromeHeight:F1} " +
                      $"screen={Screen.width}x{Screen.height}");

            if (scale <= 0f)
            {
                yield break;
            }

            float logicalWidth = (width + chromeWidth) / scale;
            float logicalHeight = (height + chromeHeight) / scale;
            gameView.position = new Rect(0f, 0f, logicalWidth, logicalHeight);
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            // 迭代校正（整数取整/取饰残留 ±px）。
            for (int iter = 0; iter < 3 && (Screen.width != width || Screen.height != height); iter++)
            {
                logicalWidth += (width - Screen.width) / scale;
                logicalHeight += (height - Screen.height) / scale;
                gameView.position = new Rect(0f, 0f, logicalWidth, logicalHeight);
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                }
            }

            Debug.Log($"[probe] final: Screen={Screen.width}x{Screen.height}");
        }

        private void BuildRig()
        {
            _cameraGo = new GameObject("~PlanetProbeCamera");
            _cameraGo.tag = "MainCamera";
            _camera = _cameraGo.AddComponent<Camera>();
            // TASK-B-PLANET-15：唯一最小深度。SRP cameras 数组按 depth 升序排布——
            // 泄漏相机（既有场景类用例遗留，默认 0）与探针并列 0 时先建者排前，探针跌出
            // cameras[0] → 行星 blit 落别处、探针 RT 停在池化陈旧内存（B-PLANET-11 污染
            // 根因）。-1000 消除并列：双相机/泄漏会话下探针恒居 cameras[0]。
            _camera.depth = ProbeCameraDepth;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 10f;
            _camera.farClipPlane = 400_000f;
            _camera.targetTexture = _captureTarget;
            _cameraGo.transform.position = Vector3.zero; // 首个姿态步用双精度姿态覆写

            _originGo = new GameObject("~PlanetProbeOrigin");
            _origin = _originGo.AddComponent<WorldOrigin>();
            _origin.AutoRebase = true; // 生产默认：4000m 阈值每帧自检
            _origin.TargetCamera = _camera;

            _linkGo = new GameObject("~PlanetProbeLink");
            _link = _linkGo.AddComponent<WorldOriginPlanetLink>();
            UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject(_link);
            serialized.FindProperty("_origin").objectReferenceValue = _origin;
            serialized.FindProperty("_targetCamera").objectReferenceValue = _camera;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _driverGo = new GameObject("~PlanetProbeDriver");
            _driver = _driverGo.AddComponent<ZrPlanetStreamingDriver>();
            // 生产默认配置零偏置（center=0 / seed 1337 / 预算 9000µs / 近档硬帽 1 / 32×32 格）。
        }

        private void DestroyRig()
        {
            if (_cameraGo != null)
            {
                UnityEngine.Object.Destroy(_cameraGo);
                _cameraGo = null;
                _camera = null;
            }

            if (_originGo != null)
            {
                UnityEngine.Object.Destroy(_originGo);
                _originGo = null;
                _origin = null;
            }

            if (_linkGo != null)
            {
                UnityEngine.Object.Destroy(_linkGo);
                _linkGo = null;
                _link = null;
            }

            if (_driverGo != null)
            {
                UnityEngine.Object.Destroy(_driverGo);
                _driverGo = null;
                _driver = null;
            }
        }

        // ---- 捕获与像素统计 -------------------------------------------------------------

        /// <summary>把主捕获目标（1920×1080）读回亮度缓冲（连续性/复访对照用）。
        /// 结果写入 <see cref="_lastCapture"/>（迭代器不能带 out 参数；调用方取引用）。</summary>
        private IEnumerator CaptureLumaAsync()
        {
            yield return null; // 确保当前相机目标已渲染（双槽 readable 滞后一帧也已覆盖）

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _captureTarget;
            _readback.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
            _readback.Apply(false, false);
            _lastCapture = _readback.GetPixels32();
            RenderTexture.active = previous;
        }

        /// <summary>把相机切到给定目标渲两帧（双槽 readable 落定）后读回 PNG 落盘
        /// Docs/Parity/，最后还原主捕获目标。KernelCrowdPixelEvidence 先例同策。</summary>
        private IEnumerator WritePngAsync(string fileName, RenderTexture target)
        {
            RenderTexture previousTarget = _camera.targetTexture;
            _camera.targetTexture = target;
            yield return null;
            yield return null;

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = target;
            var tex = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = previousActive;

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            _camera.targetTexture = previousTarget;

            string path = Path.Combine(ParityDirectory(), fileName);
            File.WriteAllBytes(path, png);
            TestContext.Out.WriteLine($"[B-PLANET-11] wrote {path} ({png.Length} bytes, {target.width}x{target.height})");
        }

        private static string ParityDirectory()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Docs", "Parity"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>非清屏（非黑）像素占比——地形/圆盘确实入画的下限证据。</summary>
        private static double NonBlackFraction(Color32[] pixels)
        {
            int lit = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.r > 8 || p.g > 8 || p.b > 8)
                {
                    lit++;
                }
            }

            return (double)lit / pixels.Length;
        }

        // ---- 内容锚（TASK-B-PLANET-15）-----------------------------------------------
        // 盲区背景：B-PLANET-11 验收发现 nonBlack>0.2 对池化陈旧内存恒真（污染样本是
        // 人群场景），三档截图被静默替换。本节锚把"画面必须含有行星特征"变成显式断言。

        /// <summary>多相机显式跳过日志捕获回调（选型 b 的 PlayMode 侧证据）。</summary>
        private void OnProbeLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (condition != null && condition.Contains("[ZRender] 多相机会话"))
            {
                _multiCameraLogWindow.Add(condition);
            }
        }

        /// <summary>行星特征色占比：高程带绿色域判定——绿通道显著高于红/蓝且足够亮。
        /// 谓词标定：planet2.hlsl 草地基色 float3(0.18,0.30,0.15) 与实测地形绿（读回域
        /// (108,135,106)/(119,153,143)/(110,131,98)，9598c90 探针 PNG）全落谓词内；池化
        /// 污染签名（灰 (130,133,139)——r≈g≈b；天空/清屏 (64,90,108)、雾 (132,146,164)
        /// ——b&gt;g）全被拒（污染样本同谓词实测 0.0000）。</summary>
        private static double PlanetGreenFraction(Color32[] pixels)
        {
            int green = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.g >= 24 && p.g > p.r + 6 && p.g > p.b + 6)
                {
                    green++;
                }
            }

            return (double)green / pixels.Length;
        }

        /// <summary>绿色域内容锚（下限见各档常量注释；实测值打印进 xml 供复核）。</summary>
        private void AssertPlanetGreenAnchor(string label, double floor)
        {
            double green = PlanetGreenFraction(_lastCapture);
            Assert.Greater(green, floor,
                $"{label} 内容锚：行星特征色（高程带绿色域）必须出现——" +
                "nonBlack 对池化陈旧内存恒真的盲区已封（B-PLANET-11 验收缺陷）");
            TestContext.Out.WriteLine($"[B-PLANET-15] {label}: greenFrac={green:F4} (floor={floor})");
        }

        /// <summary>清屏色（读回域 sRGB 值 (64,90,108)±3）判定。</summary>
        private static bool IsClearScreenLike(Color32 p)
        {
            return Math.Abs(p.r - ClearScreenRgb[0]) <= ClearScreenTolerance
                && Math.Abs(p.g - ClearScreenRgb[1]) <= ClearScreenTolerance
                && Math.Abs(p.b - ClearScreenRgb[2]) <= ClearScreenTolerance;
        }

        /// <summary>行带 [y0,y1) 内满足 match 的像素占比。行 0 = 图像底部（GetPixels32
        /// 行序，与 PNG 的顶行 0 相反——PNG 顶带 = 本函数的 [H-54,H) 带）。</summary>
        private static double BandFraction(Color32[] pixels, int width, int y0, int y1,
            Func<Color32, bool> match)
        {
            long hit = 0;
            long total = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    total++;
                    if (match(pixels[y * width + x]))
                    {
                        hit++;
                    }
                }
            }

            return total > 0 ? (double)hit / total : 0.0;
        }

        /// <summary>角部 patch 内清屏色占比。行 0 = 底部；角部对称，翻转不敏感。</summary>
        private static double CornerClearFraction(Color32[] pixels, int width, int height,
            bool left, bool bottom)
        {
            int x0 = left ? 0 : width - LimbCornerPatchPx;
            int y0 = bottom ? 0 : height - LimbCornerPatchPx;
            long hit = 0;
            long total = 0;
            for (int y = y0; y < y0 + LimbCornerPatchPx; y++)
            {
                for (int x = x0; x < x0 + LimbCornerPatchPx; x++)
                {
                    total++;
                    if (IsClearScreenLike(pixels[y * width + x]))
                    {
                        hit++;
                    }
                }
            }

            return (double)hit / total;
        }

        /// <summary>盘心 patch（中心 ±<see cref="LimbCenterPatchHalfPx"/>）内清屏色占比。</summary>
        private static double CenterPatchClearFraction(Color32[] pixels, int width, int height)
        {
            int cx = width / 2;
            int cy = height / 2;
            long hit = 0;
            long total = 0;
            for (int y = cy - LimbCenterPatchHalfPx; y < cy + LimbCenterPatchHalfPx; y++)
            {
                for (int x = cx - LimbCenterPatchHalfPx; x < cx + LimbCenterPatchHalfPx; x++)
                {
                    total++;
                    if (IsClearScreenLike(pixels[y * width + x]))
                    {
                        hit++;
                    }
                }
            }

            return (double)hit / total;
        }

        /// <summary>天空/地形带几何锚（近/中档）：生产通道无天穹（设计稿 §12-5）——画面
        /// 顶部行带必须是清屏色带；底部行带必须是地形（非整幅清屏）。标定：正确帧顶/底带
        /// 清屏占比实测 1.0000/0.0000（9598c90 near/mid PNG）。</summary>
        private void AssertBandAnchors(string label, bool expectSkyTop, bool expectTerrainBottom)
        {
            double skyTop = BandFraction(_lastCapture, CaptureWidth,
                CaptureHeight - SkyBandRows, CaptureHeight, IsClearScreenLike);
            double bottomClear = BandFraction(_lastCapture, CaptureWidth,
                0, SkyBandRows, IsClearScreenLike);
            if (expectSkyTop)
            {
                Assert.GreaterOrEqual(skyTop, 0.95,
                    $"{label} 内容锚：画面顶部行带必须是清屏色带（生产通道无天穹——设计稿 §12-5）");
            }

            if (expectTerrainBottom)
            {
                Assert.LessOrEqual(bottomClear, 0.05,
                    $"{label} 内容锚：画面底部行带必须是地形/雾面（不得整幅清屏——池化空白 RT 在此失配）");
            }

            TestContext.Out.WriteLine(
                $"[B-PLANET-15] {label}: skyTopClear={skyTop:F4} bottomClear={bottomClear:F4}");
        }

        /// <summary>limb 圆盘几何锚（远档天底）：盘外（四角 48px patch）= 清屏色（实测
        /// 1.0000），盘心（±20px patch）= 地形非清屏色（实测 0.0000），圆盘非清屏覆盖
        /// ≥0.08（实测 0.1921）——圆盘形状/无泄漏/入画三合一。</summary>
        private void AssertLimbDiskAnchors(string label)
        {
            double tl = CornerClearFraction(_lastCapture, CaptureWidth, CaptureHeight, true, false);
            double tr = CornerClearFraction(_lastCapture, CaptureWidth, CaptureHeight, false, false);
            double bl = CornerClearFraction(_lastCapture, CaptureWidth, CaptureHeight, true, true);
            double br = CornerClearFraction(_lastCapture, CaptureWidth, CaptureHeight, false, true);
            double centerClear = CenterPatchClearFraction(_lastCapture, CaptureWidth, CaptureHeight);
            double disk = 1.0 - BandFraction(_lastCapture, CaptureWidth, 0, CaptureHeight, IsClearScreenLike);
            Assert.GreaterOrEqual(tl, 0.95, $"{label} 内容锚：limb 盘外左上角必须是清屏色");
            Assert.GreaterOrEqual(tr, 0.95, $"{label} 内容锚：limb 盘外右上角必须是清屏色");
            Assert.GreaterOrEqual(bl, 0.95, $"{label} 内容锚：limb 盘外左下角必须是清屏色");
            Assert.GreaterOrEqual(br, 0.95, $"{label} 内容锚：limb 盘外右下角必须是清屏色");
            Assert.LessOrEqual(centerClear, 0.05, $"{label} 内容锚：盘心必须是地形（非清屏色）");
            Assert.GreaterOrEqual(disk, 0.08,
                $"{label} 内容锚：行星圆盘必须入画（非清屏覆盖实测 0.1921；池化陈旧内存/整幅清屏在此失配）");
            TestContext.Out.WriteLine(
                $"[B-PLANET-15] {label}: limb corners(tl/tr/bl/br)={tl:F3}/{tr:F3}/{bl:F3}/{br:F3} " +
                $"centerClear={centerClear:F4} diskFrac={disk:F4}");
        }

        /// <summary>线性 8-bit → sRGB 8-bit（IEC 61966-2-1 精确公式）。项目为 Linear
        /// 色彩空间：原生共享纹理（UNORM 直出）经 blit 进 sRGB RT，ReadPixels 回读即
        /// sRGB 编码值——标定：probe.log 回读 (47,81,70)/(40,58,31) 经本式与探针 PNG
        /// 中心 (119,153,143)/(110,131,98) 六通道逐位复算全中。</summary>
        private static int SrgbEncode(byte linear)
        {
            double c = linear / 255.0;
            double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return (int)Math.Round(s * 255.0);
        }

        /// <summary>管线回读对照锚（污染探测器）：探针 RT 中心像素必须等于原生共享纹理
        /// 中心像素回读（ZrUnityTargetStatsNative.CenterRGBA——原生 RenderFrame 自己的
        /// 1×1 readback）的 sRGB 变换值 ±1。注意原生回读是**每 kReadbackInterval=30 帧
        /// 一次的诊断采样**（zrhi_d3d12_unity_target.cpp:50，非逐帧镜像）——所以先轮询
        /// 等一次**新鲜**回读（ReadbackCount 递增，≤90 帧）：新鲜回读的源帧恰为本帧渲染
        /// 所 blit 的同一帧（帧首消费 = 上上帧内容 = RT 现内容），静止位姿下逐位可比。
        /// 探针相机不是 cameras[0]（B-PLANET-11 污染场景）时 RT 停在池化陈旧内存，此锚
        /// 必失配。调用前提：SettleAsync 后的静止位姿（±1 = blit 浮点采样舍入界）。</summary>
        private IEnumerator AssertCaptureMatchesPipelineCenterAsync(string label)
        {
            Assert.AreEqual(ZrUnityTargetNative.Ok,
                ZrUnityTargetNative.GetTargetStats(out ZrUnityTargetStatsNative before),
                $"[{label}] 取原生目标统计失败（共享目标未就绪？）");
            uint freshCount = before.ReadbackCount + 1u;
            ZrUnityTargetStatsNative stats = before;
            bool fresh = false;
            for (int guard = 0; guard < 90; guard++)
            {
                yield return null;
                Assert.AreEqual(ZrUnityTargetNative.Ok,
                    ZrUnityTargetNative.GetTargetStats(out stats));
                if (stats.ReadbackCount >= freshCount)
                {
                    fresh = true;
                    break;
                }
            }

            Assert.IsTrue(fresh,
                $"[{label}] 90 帧内未等到一次新鲜中心回读（kReadbackInterval=30 节拍失效？）");
            Assert.IsNotNull(stats.CenterRGBA, $"[{label}] 原生中心回读缺失");
            Assert.AreEqual(4, stats.CenterRGBA.Length, $"[{label}] 原生中心回读布局异常");

            Color32 center = _lastCapture[(CaptureHeight / 2) * CaptureWidth + (CaptureWidth / 2)];
            int expectR = SrgbEncode((byte)stats.CenterRGBA[0]);
            int expectG = SrgbEncode((byte)stats.CenterRGBA[1]);
            int expectB = SrgbEncode((byte)stats.CenterRGBA[2]);
            Assert.LessOrEqual(Math.Abs(center.r - expectR), 1,
                $"[{label}] 内容锚：RT 中心 R={center.r} 必须=原生回读 R={stats.CenterRGBA[0]} 的 " +
                $"sRGB 变换值 {expectR}±1（池化陈旧内存/贴错目标在此失配——B-PLANET-11 污染探测器）");
            Assert.LessOrEqual(Math.Abs(center.g - expectG), 1,
                $"[{label}] 内容锚：RT 中心 G={center.g} 必须=原生回读 G={stats.CenterRGBA[1]} 的 " +
                $"sRGB 变换值 {expectG}±1");
            Assert.LessOrEqual(Math.Abs(center.b - expectB), 1,
                $"[{label}] 内容锚：RT 中心 B={center.b} 必须=原生回读 B={stats.CenterRGBA[2]} 的 " +
                $"sRGB 变换值 {expectB}±1");
            TestContext.Out.WriteLine(
                $"[B-PLANET-15] {label}: center-anchor rt=({center.r},{center.g},{center.b}) " +
                $"native=({stats.CenterRGBA[0]},{stats.CenterRGBA[1]},{stats.CenterRGBA[2]}) " +
                $"srgb=({expectR},{expectG},{expectB}) readbackCount={stats.ReadbackCount}");
        }

        /// <summary>两帧亮度差统计（0..255 亮度尺）：均值绝对差 + 移动像素占比。</summary>
        private static double FrameDiff(Color32[] before, Color32[] after, out double movedFraction)
        {
            Assert.AreEqual(before.Length, after.Length, "对照帧像素数必须一致");
            double sumAbs = 0.0;
            long moved = 0;
            for (int i = 0; i < before.Length; i++)
            {
                double lumaBefore = 0.2126 * before[i].r + 0.7152 * before[i].g + 0.0722 * before[i].b;
                double lumaAfter = 0.2126 * after[i].r + 0.7152 * after[i].g + 0.0722 * after[i].b;
                double delta = lumaAfter - lumaBefore;
                sumAbs += Math.Abs(delta);
                if (Math.Abs(delta) > LumaMoveEpsilon)
                {
                    moved++;
                }
            }

            movedFraction = (double)moved / before.Length;
            return sumAbs / before.Length;
        }

        // ---- 行走上下文 -----------------------------------------------------------------

        /// <summary>单变体行走的状态（NUnit 每用例新建实例，字段即用例作用域）。</summary>
        private sealed class WalkContext
        {
            public WalkContext(string tag, double startArc, double lengthMeters, int steps,
                double revisitMovedFractionLimit)
            {
                Tag = tag;
                StartArc = startArc;
                LengthMeters = lengthMeters;
                Steps = steps;
                RevisitMovedFractionLimit = revisitMovedFractionLimit;
            }

            public readonly string Tag;
            public readonly double StartArc;
            public readonly double LengthMeters;
            public readonly int Steps;
            /// <summary>复访重渲染的移动像素占比上限（1km 无重定位 0.02 / 5km·50km 含重定位 0.05）。</summary>
            public readonly double RevisitMovedFractionLimit;

            /// <summary>复访前/后的全分辨率读回（同绝对位姿的确定性重渲染对照）。</summary>
            public Color32[] BeforePixels;
            public Color32[] AfterPixels;

            /// <summary>上一帧读回（逐帧连续性滚动对照）。</summary>
            public Color32[] CurrentPixels;

            /// <summary>逐帧移动像素占比序列 + 各采集时刻的累计重定位数（归因用）。</summary>
            public readonly List<double> MovedFractions = new List<double>(1024);
            public readonly List<int> RebaseCounts = new List<int>(1024);

            public int RebaseCountStart;
            public int RebasesRecorded;
            public double MaxMovedFraction;
            public double MaxMeanAbs;
            public int LodOpFrames;
        }
    }
}
