// =============================================================================
// LinearBridgeProbe.cs —— linear-bridge 探针（TASK-B-PROBE-02）
//
// 回答什么：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P3——「ZRender 桥在
// Linear 下画面比原生直出略暗」目前是形容词。本探针把它变成数：同一场景、同一
// 机位、同分辨率 1280×720，分别经 URP（活跃管线）与 ZRender 桥（共享纹理回读）
// 真实渲染各取一帧，算亮度均值差与 |Δ| 分位。HANDOFF 渲染体检节的
// 「UNORM 视图画面偏暗是已知代价」从此有数（出处：HANDOFF.md 渲染体检第 4 行，
// A-R9-19 记录：CreateExternalTexture(linear:true) 的 UNORM 视图为既有折衷）。
//
// 方法（约法三章四判据的落实，见 Docs/Logs/TASK-B-PROBE-02.md §0）：
//   真实输入：临时场景按 ParityBench/--parity 契约常量搭建（相机 pos(0,1.6,-4)
//   朝 +Z、fov 60、地面 20×20 灰 0.5、Cube A 暖白、Cube B 红、方向光；差异：
//   探针关阴影（桥路径 scene_draw.hlsl 无阴影，开阴影只会给 URP 侧单方加项）、
//   URP 背景取原生清屏色同值 (0.05,0.10,0.15)（各管线按自身语义清屏，背景贡献
//   恒定差，靠 centerCrop 指标与 p50 分开读）。场景是程序化临时场景，不依赖
//   任何 .unity 文件，退出即弃。
//   URP 路径：活跃管线（URP）直接 camera.Render() → ARGB32(sRGB 读写口径) RT
//   → ReadPixels。语义 = URP 直出的最终显示值（ParityBench 同口径）。
//   ZRender 桥路径：只走既有 Bindings 公开门面（ZrUnityTargetNative /
//   ZrUnitySceneNative，不改任何运行时代码与 ABI）：临时纹理反查设备
//   AttachDeviceFromTexture → CreateSharedTarget(1280×720) → GetTargetResourceAt
//   → Texture2D.CreateExternalTexture(BGRA32, linear:true)（与
//   ZRenderPipeline.cs:243 同参的 UNORM 视图——被量化的正是这条视图）
//   → SetCamera/BeginScene/DrawMesh×3/EndScene → RenderFrame×3（第 1 帧 PSO
//   暖启动；末帧返回时 readable 槽 = 上一帧，帧首 fence 已等完）→ 用生产
//   Resources/ZRenderBlit 材质（_FlipY 默认 1，与生产一致）Graphics.Blit 进
//   Linear 口径 RT → ReadPixels。语义 = 共享纹理原始字节直通（UNORM 视图按线性
//   采样、blit 不转换——与生产桥贴屏路径同构；等价于原生共享纹理里的字节）。
//   指标：Rec.709 亮度（0-255 域）逐像素：均值（两图）、均值差（带符号）、
//   |Δ| 均值、|Δ| p50/p95/p99（nearest-rank ceil(p*N)-1）、|Δ|>2 像素占比、
//   亮度差百分比；全帧 + 中央 640×360 crop 两组。
//   缺就是缺：任一环节失败 → 对应路径 status=MISSING + 原因，metrics 不填 0、
//   不写"通过"。桥不可用/设备移除/场景缺失/0 绘制全部走 MISSING。
//   可复核：JSON + PNG×3 带时间戳写 Tools/Probes/out/（文件名白名单 + out 目录
//   边界断言，与 env-probe.js 同口径）；JSON 记录 HEAD/环境/契约/原生统计全量，
//   第三人重跑可对上。
//
// 探针不接入任何运行时流程：不改 GraphicsSettings（不切管线）、不写 ProjectSettings、
// 不注册任何回调；跑完即弃，激活管线全程保持原样。
//
// 用法（菜单）：ZRender > Probes > Linear Bridge Compare（URP vs ZRender 桥亮度差）
//   ——交互跑前请保存当前场景（探针会 NewScene 换临时场景，结束时回开原场景；
//   有未保存改动时拒绝执行）。
// 用法（批处理，勿带 -quit，本方法自带 EditorApplication.Exit）：
//   "C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode
//     -projectPath <UnityProject>
//     -logFile Tools/Probes/out/linear-bridge-run.log
//     -executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine
// 产出（Tools/Probes/out/，时间戳 = UTC）：
//   linear-bridge-<时间戳>.json          全量数字与口径
//   linear-bridge-<时间戳>-urp.png       URP 直出帧
//   linear-bridge-<时间戳>-zrbridge.png  ZRender 桥帧（共享纹理直通）
//   linear-bridge-<时间戳>-diff.png      |Δ| 亮度差图（×4 增益灰度，JSON 记录增益）
// 退出码：0 = 探针完成并写档（status 可能是 OK 或 MISSING——MISSING 是数据不是
// 故障，原因在 JSON）；1 = 探针本体异常（连档都没写成）。
// =============================================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Probes
{
    public static class LinearBridgeProbe
    {
        // ---- 契约常量（与 ParityBench.cs / zrhi_d3d12_window.cpp --parity 同源；
        //      分辨率 1280×720 同 --parity；背景与阴影两处按探针口径偏离，已注明）----
        private const int kWidth = 1280;
        private const int kHeight = 720;
        private const int kUrpWarmupFrames = 1;  // ColorPipelineSetup 教训：首帧变体未编译完不可信
        private const int kNativeFrames = 3;     // 第 1 帧 PSO 暖启动；末帧返回后 readable=上一帧（fence 已等完）
        private const float kDiffPngGain = 4f;   // 差值图增益（|Δ|×4 clamp 255；JSON 记录）
        private const float kNativeClearColorR = 0.05f;  // zrhi_d3d12_unity_target.cpp:46 kClearColor
        private const float kNativeClearColorG = 0.10f;
        private const float kNativeClearColorB = 0.15f;

        // 原生场景路径的固定光照（探针不改原生侧，这些是它画出来的真实参数）：
        // zrhi_d3d12_unity_target.cpp:1100-1121（lightDir/lightColor）+ kSceneHlsl 环境项。
        private static readonly float[] kNativeLightDir = { 0.5f, 0.8f, 0.6f };  // 提交前原生归一化
        private static readonly float[] kNativeLightColor = { 1.0f, 0.95f, 0.9f, 1.0f };
        private static readonly float[] kNativeAmbient = { 0.45f, 0.47f, 0.52f };

        // ---- 输出安全（env-probe.js 同口径：basename 白名单 + out 目录边界断言；
        //      不对动态内容调 path 函数，文件名只由常量前缀 + 过白名单的时间戳拼成）----
        private static readonly Regex OutputNameWhitelist =
            new Regex("^[A-Za-z0-9_-]+(\\.[A-Za-z0-9_-]+)+$", RegexOptions.Compiled);
        private static readonly Regex StampWhitelist = new Regex("^[A-Za-z0-9-]+$", RegexOptions.Compiled);

        private readonly struct Stage
        {
            public Stage(string name, string status, string detail)
            {
                Name = name;
                Status = status;
                Detail = detail;
            }

            public string Name { get; }
            public string Status { get; }
            public string Detail { get; }
        }

        /// <summary>原生上传的一个物件（探针一次性建，结构与生产 ZRenderSceneBridge.Entry 同构）。</summary>
        private sealed class DrawEntry
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public ulong Handle;
            public float[] Rgba = { 1f, 1f, 1f, 1f };
        }

        // ---- 运行期状态（单次执行工具；静态便于菜单/批处理两个入口共用）------------
        private static readonly List<Stage> s_stages = new List<Stage>();
        private static readonly List<string> s_reasons = new List<string>();
        private static string s_previousScenePath;

        [MenuItem("ZRender/Probes/Linear Bridge Compare（URP vs ZRender 桥亮度差）")]
        public static void RunFromMenu()
        {
            if (!Application.isBatchMode && EditorSceneManager.GetActiveScene().isDirty)
            {
                Debug.LogError("[LinearBridgeProbe] 当前场景有未保存改动：探针会换临时场景，" +
                               "请先保存（Ctrl+S）再跑，或改用批处理入口。");
                return;
            }

            int code = Run();
            Debug.Log($"[LinearBridgeProbe] 菜单跑完，exit={code}（产物见 Tools/Probes/out/）。");
        }

        /// <summary>批处理入口：自带 Exit，命令行勿再加 -quit。</summary>
        public static void RunFromCommandLine()
        {
            int exitCode;
            try
            {
                exitCode = Run();
            }
            catch (Exception error)
            {
                Debug.Log($"[LinearBridgeProbe] EXCEPTION（探针本体异常，无档）:\n{error}");
                exitCode = 1;
            }

            Debug.Log($"[LinearBridgeProbe] done, exit={exitCode}");
            EditorApplication.Exit(exitCode);
        }

        private static int Run()
        {
            s_stages.Clear();
            s_reasons.Clear();

            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outRoot = Path.Combine(repoRoot, "Tools", "Probes", "out");
            Directory.CreateDirectory(outRoot);

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture);
            if (!StampWhitelist.IsMatch(stamp))
            {
                throw new InvalidOperationException("时间戳不合白名单，拒绝生成文件名: " + stamp);
            }

            // 前提快照（缺什么记什么，不填默认值）
            string urpAssetName = null;
            var activePipeline = GraphicsSettings.currentRenderPipeline;
            if (activePipeline != null)
            {
                urpAssetName = activePipeline.name;
            }

            // ---- 场景（URP 与桥共用；任何一步失败 → 两路 MISSING）--------------------
            var build = BuildContractScene();
            s_stages.Add(new Stage("scene.build", build.Ok ? "OK" : "MISSING", build.Detail));
            if (!build.Ok)
            {
                s_reasons.Add("scene.build: " + build.Detail);
            }

            // ---- URP 路径 ------------------------------------------------------------
            JObj urpPath = new JObj();
            urpPath.Add("status", "MISSING");
            byte[] urpPixels = null;
            if (!build.Ok)
            {
                urpPath.Add("reason", "场景未建成，跳过（见 reasons）");
            }
            else if (!(activePipeline is UniversalRenderPipelineAsset))
            {
                string reason = "currentRenderPipeline 不是 URP（实际: " +
                                (activePipeline == null ? "(null，内置管线)" : activePipeline.GetType().Name) +
                                "）；URP 直出帧缺。";
                urpPath.Add("reason", reason);
                s_reasons.Add("urp.path: " + reason);
            }
            else
            {
                try
                {
                    CaptureResult captured = CaptureUrpFrame(build.Camera);
                    urpPath.Add("status", "OK");
                    urpPath.Add("rt", "ARGB32 sRGB 读写 + 24d（显示域：URP 最终输出）");
                    urpPath.Add("meanLuma", Round(captured.MeanLuma));
                    urpPath.Add("meanR", Round(captured.MeanR));
                    urpPath.Add("meanG", Round(captured.MeanG));
                    urpPath.Add("meanB", Round(captured.MeanB));
                    urpPixels = captured.Pixels;
                    s_stages.Add(new Stage("urp.render", "OK",
                        $"meanLuma={captured.MeanLuma:F2}"));
                }
                catch (Exception error)
                {
                    string reason = "URP 帧渲染/回读失败: " + error.GetType().Name + ": " + error.Message;
                    urpPath.Add("reason", reason);
                    s_reasons.Add("urp.render: " + reason);
                    s_stages.Add(new Stage("urp.render", "MISSING", reason));
                }
            }

            // ---- ZRender 桥路径 ------------------------------------------------------
            JObj zrPath = new JObj();
            zrPath.Add("status", "MISSING");
            byte[] zrPixels = null;
            JObj nativeStats = null;
            JObj sceneStats = null;
            if (!build.Ok)
            {
                zrPath.Add("reason", "场景未建成，跳过（见 reasons）");
            }
            else
            {
                BridgeRun bridge = RunBridgePath(build);
                foreach (Stage stage in bridge.Stages)
                {
                    s_stages.Add(stage);
                }

                if (bridge.Ok)
                {
                    zrPath.Add("status", "OK");
                    zrPath.Add("rt", "ARGB32 Linear 读写（共享纹理原始字节直通）");
                    zrPath.Add("externalTexture", "BGRA32 linear:true（UNORM 视图，与 ZRenderPipeline.cs:243 同参）");
                    zrPath.Add("blitShader", "Resources/ZRenderBlit（_FlipY=1 默认，与生产一致）");
                    zrPath.Add("meanLuma", Round(bridge.MeanLuma));
                    zrPath.Add("meanR", Round(bridge.MeanR));
                    zrPath.Add("meanG", Round(bridge.MeanG));
                    zrPath.Add("meanB", Round(bridge.MeanB));
                    zrPixels = bridge.Pixels;
                    nativeStats = bridge.NativeStats;
                    sceneStats = bridge.SceneStats;
                }
                else
                {
                    zrPath.Add("reason", bridge.Reason);
                    s_reasons.Add("zrbridge: " + bridge.Reason);
                }
            }

            // ---- 指标（两帧齐了才算；不填 0）----------------------------------------
            JObj metrics = new JObj();
            bool metricsOk = urpPixels != null && zrPixels != null;
            JObj diffPngInfo = new JObj();
            diffPngInfo.Add("path", null);
            if (!metricsOk)
            {
                metrics.Add("status", "MISSING");
                metrics.Add("reason", urpPixels == null && zrPixels == null
                    ? "两帧均缺，无指标"
                    : urpPixels == null
                        ? "URP 帧缺，无指标"
                        : "ZRender 桥帧缺，无指标");
            }
            else
            {
                metrics.Add("status", "OK");
                metrics.Add("domain", "Rec.709 luma（0-255 字节域）：0.2126R+0.7152G+0.0722B");
                metrics.Add("percentileMethod", "nearest-rank ceil(p*N)-1，作用于 |Δ| 分布");
                metrics.Add("deltaSign", "Δ = ZRender 桥帧 - URP 帧（正 = 桥更亮）");

                JObj frame = ComputeDiffMetrics(urpPixels, zrPixels, kWidth, kHeight, 0, 0, kWidth, kHeight);
                metrics.Add("frame", frame);
                // 中央半幅 crop：把"背景/清屏恒定差"与"物件差"分开读（region 记录在档）
                int cropW = kWidth / 2;
                int cropH = kHeight / 2;
                JObj crop = ComputeDiffMetrics(urpPixels, zrPixels, kWidth, kHeight, kWidth / 4, kHeight / 4,
                    cropW, cropH);
                crop.Add("region", new List<object> { kWidth / 4, kHeight / 4, cropW, cropH });
                metrics.Add("centerCrop", crop);

                diffPngInfo.Add("path", CheckedOutputPath(outRoot, "linear-bridge-" + stamp + "-diff.png"));
                diffPngInfo.Add("gain", kDiffPngGain);
                diffPngInfo.Add("encoding", "灰度 = min(255, |Δluma| * gain)（帧级）");
                WriteDiffPng((string)diffPngInfo.Get("path"), urpPixels, zrPixels, kWidth, kHeight);
            }

            // ---- 产物：两帧 PNG（哪路 OK 写哪路；MISSING 路不造假图）----------------
            string urpPngPath = null;
            if (urpPixels != null)
            {
                urpPngPath = CheckedOutputPath(outRoot, "linear-bridge-" + stamp + "-urp.png");
                WritePng(urpPngPath, urpPixels, kWidth, kHeight);
            }

            string zrPngPath = null;
            if (zrPixels != null)
            {
                zrPngPath = CheckedOutputPath(outRoot, "linear-bridge-" + stamp + "-zrbridge.png");
                WritePng(zrPngPath, zrPixels, kWidth, kHeight);
            }

            if (urpPngPath != null)
            {
                urpPath.Add("png", urpPngPath);
            }

            if (zrPngPath != null)
            {
                zrPath.Add("png", zrPngPath);
            }

            // ---- 结论行（README 登记行直接可引用的一句）------------------------------
            string overallStatus = metricsOk ? "OK" : "MISSING";
            string summary;
            if (metricsOk)
            {
                var frame = (JObj)metrics.Get("frame");
                double meanUrp = ToDouble(frame.Get("meanLumaUrp"));
                double meanZr = ToDouble(frame.Get("meanLumaZr"));
                double deltaPct = ToDouble(frame.Get("brightnessDeltaPct"));
                double p95 = ToDouble(frame.Get("p95AbsDelta"));
                double p99 = ToDouble(frame.Get("p99AbsDelta"));
                string direction = deltaPct >= 0 ? "亮" : "暗";
                summary = $"ZRender 桥输出较 URP 直出 {direction} {Math.Abs(deltaPct):F1}%" +
                          $"（均值 luma URP={meanUrp:F2} ZR={meanZr:F2}；|Δ| p95={p95:F2} p99={p99:F2}，0-255 域）";
            }
            else
            {
                summary = "MISSING：" + string.Join("; ", s_reasons.ToArray());
            }

            // ---- JSON 档 -------------------------------------------------------------
            var outputs = new List<object>();
            if (urpPngPath != null) outputs.Add(urpPngPath);
            if (zrPngPath != null) outputs.Add(zrPngPath);
            if (metricsOk) outputs.Add(diffPngInfo.Get("path"));
            string jsonPath = CheckedOutputPath(outRoot, "linear-bridge-" + stamp + ".json");

            var payload = new JObj();
            payload.Add("probeId", "linear-bridge");
            payload.Add("probedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            payload.Add("repoHead", ReadGitHead(repoRoot));
            payload.Add("repro", "Unity.exe -batchmode -projectPath <UnityProject> " +
                                 "-executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine " +
                                 "-logFile Tools/Probes/out/linear-bridge-run.log（勿带 -quit）");
            payload.Add("host", HostSnapshot(urpAssetName));
            payload.Add("plugin", PluginSnapshot());
            payload.Add("scene", build.Contract);
            payload.Add("capture", CaptureSpecSnapshot());
            payload.Add("stages", StagesToList());
            payload.Add("urpPath", urpPath);
            payload.Add("zrBridgePath", zrPath);
            payload.Add("nativeStats", nativeStats);
            payload.Add("sceneStats", sceneStats);
            payload.Add("metrics", metrics);
            payload.Add("diffPng", metricsOk ? diffPngInfo : null);
            payload.Add("outputs", outputs);
            payload.Add("status", overallStatus);
            payload.Add("reasons", s_reasons.Count > 0 ? new List<object>(s_reasons) : new List<object>());
            payload.Add("summary", summary);

            File.WriteAllText(jsonPath, ToJson(payload), new UTF8Encoding(false));

            // ---- 恢复现场：回开原场景（临时场景随之丢弃；批处理退出前做也无害）------
            if (!string.IsNullOrEmpty(s_previousScenePath))
            {
                try
                {
                    EditorSceneManager.OpenScene(s_previousScenePath);
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[LinearBridgeProbe] 回开原场景失败（探针结果不受影响）: " + error.Message);
                }
            }

            // ---- 控制台表格 ----------------------------------------------------------
            var table = new StringBuilder();
            table.AppendLine("linear-bridge 探针结果（TASK-B-PROBE-02）");
            foreach (Stage stage in s_stages)
            {
                table.AppendLine($"{stage.Status,-7} | {stage.Name,-22} | {stage.Detail}");
            }

            table.AppendLine();
            table.AppendLine("结论: " + summary);
            table.AppendLine("JSON : " + jsonPath);
            if (urpPngPath != null) table.AppendLine("URP  : " + urpPngPath);
            if (zrPngPath != null) table.AppendLine("桥   : " + zrPngPath);
            if (metricsOk) table.AppendLine("差值 : " + diffPngInfo.Get("path"));
            if (s_reasons.Count > 0)
            {
                table.AppendLine("缺的项没有填默认值，按 MISSING 记录。");
            }

            Debug.Log(table.ToString());
            return 0;
        }

        // ---- 场景搭建（程序化临时场景；契约常量与 ParityBench 同源）================

        private sealed class SceneBuild
        {
            public bool Ok;
            public string Detail;
            public Camera Camera;
            public List<DrawEntry> Entries = new List<DrawEntry>();
            public JObj Contract = new JObj();
        }

        private static SceneBuild BuildContractScene()
        {
            var result = new SceneBuild();
            try
            {
                // 交互模式下拒绝覆盖带未保存改动的场景（菜单入口已挡过一次，批处理兜底）
                var previous = EditorSceneManager.GetActiveScene();
                s_previousScenePath = previous.IsValid() ? previous.path : string.Empty;
                if (!Application.isBatchMode && previous.isDirty)
                {
                    result.Detail = "当前场景有未保存改动，拒绝执行（先保存再跑）";
                    return result;
                }

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.12f, 0.12f, 0.12f, 1f); // --parity 契约环境

                var litShader = Shader.Find("Universal Render Pipeline/Lit");
                var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                var blitShader = Shader.Find("ZRender/Blit");
                if (litShader == null || unlitShader == null || blitShader == null)
                {
                    result.Detail = "Shader.Find 失败: Lit=" + (litShader != null) +
                                    " Unlit=" + (unlitShader != null) + " ZRenderBlit=" + (blitShader != null);
                    return result;
                }

                // 地面：Unity Plane 原生 10×10 → scale 2 = 20×20 @y=0（--parity 契约）
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.position = Vector3.zero;
                ground.transform.localScale = new Vector3(2f, 1f, 2f);
                var matGround = new Material(litShader) { name = "MAT_ProbeGround" };
                matGround.color = new Color(0.5f, 0.5f, 0.5f, 1f);
                matGround.SetFloat("_Smoothness", 0.1f);
                ground.GetComponent<MeshRenderer>().sharedMaterial = matGround;

                // Cube A：URP Lit 暖白（--parity 契约）
                var cubeA = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeA.name = "CubeA";
                cubeA.transform.position = new Vector3(0f, 0.5f, 0f);
                var matA = new Material(litShader) { name = "MAT_ProbeCubeA" };
                matA.color = new Color(0.9f, 0.85f, 0.75f, 1f);
                matA.SetFloat("_Smoothness", 0.5f);
                cubeA.GetComponent<MeshRenderer>().sharedMaterial = matA;

                // Cube B：无光照红（--parity 契约；桥路径会画成 Lambert+环境项的同基色）
                var cubeB = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeB.name = "CubeB";
                cubeB.transform.position = new Vector3(1.8f, 0.5f, 0.8f);
                var matB = new Material(unlitShader) { name = "MAT_ProbeCubeB" };
                matB.color = new Color(0.9f, 0.3f, 0.25f, 1f);
                cubeB.GetComponent<MeshRenderer>().sharedMaterial = matB;

                // 方向光（--parity 契约；阴影关——桥路径 scene_draw.hlsl 无阴影，对齐两路）
                var lightGo = new GameObject("DirLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.96f, 0.9f, 1f);
                light.intensity = 1.2f;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation =
                    Quaternion.LookRotation(new Vector3(-0.5f, -1f, -0.3f).normalized);

                // 相机（--parity 契约机位；背景取原生清屏色同值——各管线按自身语义清屏）
                var camGo = new GameObject("ProbeCam");
                var cam = camGo.AddComponent<Camera>();
                camGo.transform.position = new Vector3(0f, 1.6f, -4f);
                camGo.transform.rotation = Quaternion.identity; // Unity 相机默认朝 +Z
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 60f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor =
                    new Color(kNativeClearColorR, kNativeClearColorG, kNativeClearColorB, 1f);
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderPostProcessing = false; // 无 Volume 场景，双保险

                // 探针物件表（原生提交顺序 = 生产桥同构：先扫到的先画）
                result.Entries.Add(MakeEntry(ground.GetComponent<MeshRenderer>()));
                result.Entries.Add(MakeEntry(cubeA.GetComponent<MeshRenderer>()));
                result.Entries.Add(MakeEntry(cubeB.GetComponent<MeshRenderer>()));

                result.Camera = cam;
                result.Ok = true;
                result.Detail = "临时场景（退出即弃）：" + result.Entries.Count + " 个 renderer + 方向光";

                // 契约入档（可复核的"真实输入"描述）
                var contract = result.Contract;
                contract.Add("name", "(procedural temp scene, --parity contract constants)");
                contract.Add("resolution", new List<object> { kWidth, kHeight });
                contract.Add("camera", CamObj(new List<object> { 0f, 1.6f, -4f }, 60f, 0.3f, 60f));
                contract.Add("objects", new List<object>
                {
                    ObjectObj("Ground", "Plane(scale2 = 20x20)", "URP Lit", new List<object> { 0.5, 0.5, 0.5 }, 0.1),
                    ObjectObj("CubeA", "Cube @ (0,0.5,0)", "URP Lit", new List<object> { 0.9, 0.85, 0.75 }, 0.5),
                    ObjectObj("CubeB", "Cube @ (1.8,0.5,0.8)", "URP Unlit", new List<object> { 0.9, 0.3, 0.25 }, null),
                });
                contract.Add("urpLight", LightObj());
                contract.Add("ambient", "Flat 0.12（--parity 契约）");
                contract.Add("shadows", "Off（探针口径：桥路径 scene_draw.hlsl 无阴影；URP 关阴影对齐两路）");
                contract.Add("nativeFixedLight", new JObj
                {
                    ["lightDir"] = ToObjList(kNativeLightDir),
                    ["lightColor"] = ToObjList(kNativeLightColor),
                    ["ambient"] = ToObjList(kNativeAmbient),
                    ["source"] = "zrhi_d3d12_unity_target.cpp:1100-1121 + kSceneHlsl（探针不改原生侧）",
                });
                contract.Add("nativeClearColor", new List<object>
                {
                    kNativeClearColorR, kNativeClearColorG, kNativeClearColorB,
                });
                contract.Add("backgroundNote", "URP 背景取原生清屏色同值；各管线按自身语义清屏，" +
                                               "背景区贡献恒定差（用 centerCrop 与 p50 分开读）");
                return result;
            }
            catch (Exception error)
            {
                result.Detail = "场景搭建异常: " + error.GetType().Name + ": " + error.Message;
                return result;
            }
        }

        private static DrawEntry MakeEntry(MeshRenderer renderer)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            var entry = new DrawEntry
            {
                Renderer = renderer,
                Mesh = filter != null ? filter.sharedMesh : null,
            };
            FillColor(renderer, entry.Rgba);
            return entry;
        }

        /// <summary>
        /// 物件基色（与生产 ZRenderSceneBridge.FillColor 同构：优先 _BaseColor，其次
        /// _Color，缺省白）。生产那份是 internal（友元只给 Tests），探针按既有行为
        /// 复刻，不改运行时代码；两个 URP shader 都有 _BaseColor，常规都走第一支。
        /// </summary>
        private static void FillColor(Renderer renderer, float[] rgba)
        {
            Color color = Color.white;
            Material material = renderer.sharedMaterial;
            if (material != null)
            {
                if (material.HasProperty(ShaderPropertyBaseColor))
                {
                    color = material.GetColor(ShaderPropertyBaseColor);
                }
                else if (material.HasProperty(ShaderPropertyColor))
                {
                    color = material.GetColor(ShaderPropertyColor);
                }
            }

            rgba[0] = color.r;
            rgba[1] = color.g;
            rgba[2] = color.b;
            rgba[3] = color.a;
        }

        private static readonly int ShaderPropertyBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int ShaderPropertyColor = Shader.PropertyToID("_Color");

        // ---- URP 路径（活跃管线直出；ParityBench 同口径）============================

        private sealed class CaptureResult
        {
            public byte[] Pixels;
            public double MeanLuma;
            public double MeanR;
            public double MeanG;
            public double MeanB;
        }

        private static CaptureResult CaptureUrpFrame(Camera camera)
        {
            var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
            try
            {
                camera.targetTexture = rt;
                for (int i = 0; i < kUrpWarmupFrames; i++)
                {
                    camera.Render(); // 暖启动：shader/PSO 编译不入帧
                }

                camera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = null;
            }

            byte[] pixels = tex.GetRawTextureData();
            // RT 回读行序 = 画面视觉行序（行 0 = 顶部；ParityBench 同口径直写不翻转，
            // 实拍验证：地平线/立方体位置与物理朝向一致，勿再翻转）。
            CaptureResult result = ChannelMeans(pixels);
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            return result;
        }

        // ---- ZRender 桥路径（既有 Bindings 公开门面；不改运行时代码/ABI）============

        private sealed class BridgeRun
        {
            public bool Ok;
            public string Reason;
            public readonly List<Stage> Stages = new List<Stage>();
            public byte[] Pixels;
            public double MeanLuma;
            public double MeanR;
            public double MeanG;
            public double MeanB;
            public JObj NativeStats;
            public JObj SceneStats;
        }

        private static BridgeRun RunBridgePath(SceneBuild build)
        {
            var run = new BridgeRun();
            var externalTextures = new Texture2D[2];
            var meshesToFree = new List<ulong>();
            var bridgeRt = new RenderTexture(kWidth, kHeight, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Material blitMaterial = null;
            Texture2D deviceProbeTexture = null;
            bool targetCreated = false;

            try
            {
                // b1 图形 API
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                {
                    run.Reason = "当前图形 API = " + SystemInfo.graphicsDeviceType +
                                 "，不是 Direct3D12（桥只在 D3D12 下可用，ZRenderPipeline.cs 同门控）";
                    run.Stages.Add(new Stage("zrbridge.gate", "MISSING", run.Reason));
                    return run;
                }

                run.Stages.Add(new Stage("zrbridge.gate", "OK", "Direct3D12"));

                // b2 插件
                try
                {
                    ZrPluginInfo info = ZrPlugin.EnsureLoaded();
                    run.Stages.Add(new Stage("zrbridge.plugin", "OK",
                        $"api={info.ApiVersion} version={info.Version}"));
                }
                catch (Exception error)
                {
                    run.Reason = "zrender.dll 不可用: " + error.GetType().Name + ": " + error.Message;
                    run.Stages.Add(new Stage("zrbridge.plugin", "MISSING", run.Reason));
                    return run;
                }

                // b3 接入宿主设备（生产 ZRenderPipeline.AttachHostDevice 同法：临时纹理反查）
                deviceProbeTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                deviceProbeTexture.Apply(false, true);
                IntPtr nativeTexture = deviceProbeTexture.GetNativeTexturePtr();
                if (nativeTexture == IntPtr.Zero)
                {
                    run.Reason = "取不到探针纹理的原生指针，无法接入设备（设备移除/驱动异常？）";
                    run.Stages.Add(new Stage("zrbridge.attach", "MISSING", run.Reason));
                    return run;
                }

                int attachResult = ZrUnityTargetNative.AttachDeviceFromTexture(nativeTexture);
                if (attachResult != ZrUnityTargetNative.Ok)
                {
                    run.Reason = "ZR_Unity_AttachDeviceFromTexture 失败: " + attachResult;
                    run.Stages.Add(new Stage("zrbridge.attach", "MISSING", run.Reason));
                    return run;
                }

                run.Stages.Add(new Stage("zrbridge.attach", "OK", "宿主设备已包装"));
                UnityEngine.Object.DestroyImmediate(deviceProbeTexture);
                deviceProbeTexture = null;

                // b4 共享目标（探针定尺寸 1280×720，与 URP 帧同分辨率——不读 Screen.*）
                int createResult = ZrUnityTargetNative.CreateSharedTarget(kWidth, kHeight);
                if (createResult != ZrUnityTargetNative.Ok)
                {
                    run.Reason = "ZR_Unity_CreateSharedTarget(" + kWidth + "x" + kHeight + ") 失败: " + createResult;
                    run.Stages.Add(new Stage("zrbridge.target", "MISSING", run.Reason));
                    return run;
                }

                targetCreated = true;
                run.Stages.Add(new Stage("zrbridge.target", "OK", kWidth + "x" + kHeight + " BGRA8 ×2 槽"));

                // b5 外部纹理（UNORM 视图——linear:true，与生产同参；这正是被量化的视图）
                for (int slot = 0; slot < externalTextures.Length; slot++)
                {
                    int resourceResult =
                        ZrUnityTargetNative.GetTargetResourceAt((uint)slot, out IntPtr resource);
                    if (resourceResult != ZrUnityTargetNative.Ok || resource == IntPtr.Zero)
                    {
                        run.Reason = "取共享目标资源失败: slot=" + slot + " result=" + resourceResult;
                        run.Stages.Add(new Stage("zrbridge.external", "MISSING", run.Reason));
                        return run;
                    }

                    externalTextures[slot] = Texture2D.CreateExternalTexture(
                        kWidth, kHeight, TextureFormat.BGRA32, false, true, resource);
                    if (externalTextures[slot] == null)
                    {
                        run.Reason = "Texture2D.CreateExternalTexture 返回 null（slot=" + slot +
                                     "；D3D12 资源指针不被接受？）";
                        run.Stages.Add(new Stage("zrbridge.external", "MISSING", run.Reason));
                        return run;
                    }

                    externalTextures[slot].name = "ZRenderProbeSharedTarget" + slot;
                }

                run.Stages.Add(new Stage("zrbridge.external", "OK", "2 槽外部纹理（BGRA32 linear:true）"));

                // b6 提交 + 渲染（生产桥同序：SetCamera → BeginScene → DrawMesh×N → EndScene）。
                // targetTexture 只为让相机 aspect 钉在 16:9（原生帧不经过 Unity 渲染，
                // 但矩阵取自这台相机；生产里 shared target 与屏幕同尺寸，aspect 天然一致）。
                build.Camera.targetTexture = bridgeRt;
                int drawnLast = 0;
                int uploadFailures = 0;
                int drawFailures = 0;
                for (int frame = 0; frame < kNativeFrames; frame++)
                {
                    if (!SubmitScene(build.Camera, build.Entries, meshesToFree, ref uploadFailures,
                            ref drawFailures, out drawnLast, out string submitReason))
                    {
                        run.Reason = submitReason;
                        run.Stages.Add(new Stage("zrbridge.submit", "MISSING", run.Reason));
                        return run;
                    }

                    int renderResult = ZrUnityTargetNative.RenderFrame();
                    if (renderResult != ZrUnityTargetNative.Ok)
                    {
                        run.Reason = "ZR_Unity_RenderFrame 失败（第 " + (frame + 1) + " 帧）: " + renderResult;
                        run.Stages.Add(new Stage("zrbridge.renderframe", "MISSING", run.Reason));
                        return run;
                    }
                }

                build.Camera.targetTexture = null;

                if (drawnLast <= 0)
                {
                    run.Reason = "场景提交 0 绘制（原生会回落画演示 cube，帧不代表探针场景）" +
                                 " uploadFailures=" + uploadFailures + " drawFailures=" + drawFailures;
                    run.Stages.Add(new Stage("zrbridge.submit", "MISSING", run.Reason));
                    return run;
                }

                run.Stages.Add(new Stage("zrbridge.submit", "OK",
                    $"{kNativeFrames} 帧 × {drawnLast} 绘制（mesh 种类={build.Entries.Count}，" +
                    $"uploadFailures={uploadFailures} drawFailures={drawFailures}）"));

                // b7 可采样槽（末帧返回时 readable = 上一帧，帧首 fence 已等完）
                int readableResult = ZrUnityTargetNative.GetReadableTargetIndex(out uint readableSlot);
                if (readableResult != ZrUnityTargetNative.Ok || readableSlot >= externalTextures.Length)
                {
                    run.Reason = "GetReadableTargetIndex 失败: result=" + readableResult +
                                 " slot=" + readableSlot;
                    run.Stages.Add(new Stage("zrbridge.readable", "MISSING", run.Reason));
                    return run;
                }

                // b8 生产 blit 材质直通 → Linear RT → 回读（= 共享纹理原始字节）
                Shader blitShader = Shader.Find("ZRender/Blit");
                if (blitShader == null)
                {
                    run.Reason = "Shader.Find(ZRender/Blit) 失败（Resources 下的 blit 着色器不在？）";
                    run.Stages.Add(new Stage("zrbridge.readback", "MISSING", run.Reason));
                    return run;
                }

                blitMaterial = new Material(blitShader) { name = "ZRenderProbeBlit" };
                Graphics.Blit(externalTextures[readableSlot], bridgeRt, blitMaterial);
                var readTex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = bridgeRt;
                    readTex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
                    readTex.Apply();
                    RenderTexture.active = null;
                }
                finally
                {
                    RenderTexture.active = null;
                }

                byte[] raw = readTex.GetRawTextureData();
                run.Pixels = raw; // 行序口径同 URP 路径（视觉行序，不翻转）；两路逐像素对齐
                UnityEngine.Object.DestroyImmediate(readTex);

                CaptureResult means = ChannelMeans(run.Pixels);
                run.MeanLuma = means.MeanLuma;
                run.MeanR = means.MeanR;
                run.MeanG = means.MeanG;
                run.MeanB = means.MeanB;
                run.Stages.Add(new Stage("zrbridge.readback", "OK",
                    $"readableSlot={readableSlot} meanLuma={means.MeanLuma:F2}"));

                // b9 原生/场景统计快照（真调用计数与中心像素回读，可复核用）
                if (ZrUnityTargetNative.GetTargetStats(out ZrUnityTargetStatsNative stats)
                    == ZrUnityTargetNative.Ok)
                {
                    run.NativeStats = new JObj
                    {
                        ["renderFrameCount"] = (long)stats.RenderFrameCount,
                        ["width"] = (long)stats.Width,
                        ["height"] = (long)stats.Height,
                        ["readbackCount"] = (long)stats.ReadbackCount,
                        ["centerRGBA"] = UintList(stats.CenterRGBA),
                        ["sharedHandle"] = (long)stats.SharedHandle,
                    };
                }

                if (ZrUnitySceneNative.GetSceneStats(out ZrUnitySceneStatsNative scene)
                    == ZrUnitySceneNative.Ok)
                {
                    run.SceneStats = new JObj
                    {
                        ["drawCount"] = (long)scene.DrawCount,
                        ["meshCount"] = (long)scene.MeshCount,
                        ["uploadCount"] = (long)scene.UploadCount,
                        ["sceneFrameCount"] = (long)scene.SceneFrameCount,
                    };
                }

                run.Ok = true;
                return run;
            }
            catch (DllNotFoundException error)
            {
                run.Reason = "zrender.dll 缺失: " + error.Message;
                run.Stages.Add(new Stage("zrbridge.exception", "MISSING", run.Reason));
                return run;
            }
            catch (Exception error)
            {
                run.Reason = "桥路径异常: " + error.GetType().Name + ": " + error.Message;
                run.Stages.Add(new Stage("zrbridge.exception", "MISSING", run.Reason));
                return run;
            }
            finally
            {
                // 释放顺序与生产一致：先销毁 Unity 外部纹理，再放原生资源（防悬空指针）
                for (int slot = 0; slot < externalTextures.Length; slot++)
                {
                    if (externalTextures[slot] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(externalTextures[slot]);
                        externalTextures[slot] = null;
                    }
                }

                if (targetCreated)
                {
                    ZrUnityTargetNative.ReleaseSharedTarget();
                }

                foreach (ulong handle in meshesToFree)
                {
                    ZrUnitySceneNative.DestroyMesh(handle);
                }

                if (deviceProbeTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(deviceProbeTexture);
                }

                if (blitMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(blitMaterial);
                }

                // 失败路径兜底：SubmitScene/RenderFrame 失败的 return 不经过成功路径的
                // targetTexture 复位，这里统一摘掉（桥 RT 随即释放，防相机持悬空目标）。
                build.Camera.targetTexture = null;
                bridgeRt.Release();
            }
        }

        /// <summary>
        /// 提交一帧（与生产 ZRenderSceneBridge.Submit 同构；矩阵/打包逻辑与 ZrSceneMath
        /// 同源——生产那份是 internal（友元只给 Tests），探针复刻不改运行时代码；
        /// 契约变化时两侧须同步）。返回 false 时 reason 给出失败点。
        /// </summary>
        private static bool SubmitScene(Camera camera, List<DrawEntry> entries, List<ulong> meshesToFree,
            ref int uploadFailures, ref int drawFailures, out int drawn, out string reason)
        {
            drawn = 0;
            reason = null;

            var view = new float[16];
            var proj = new float[16];
            ToNativeRowMajor(camera.worldToCameraMatrix, view);
            Matrix4x4 gpuProjection =
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
            gpuProjection = NormalizeProjection(gpuProjection, camera.nearClipPlane, camera.farClipPlane);
            ToNativeRowMajor(gpuProjection, proj);

            int setResult = ZrUnitySceneNative.SetCamera(view, proj);
            if (setResult != ZrUnitySceneNative.Ok)
            {
                reason = "ZR_Unity_SetCamera 失败: " + setResult;
                return false;
            }

            int beginResult = ZrUnitySceneNative.BeginScene();
            if (beginResult != ZrUnitySceneNative.Ok)
            {
                reason = "ZR_Unity_BeginScene 失败: " + beginResult;
                return false;
            }

            Plane[] planes = new Plane[6];
            GeometryUtility.CalculateFrustumPlanes(camera, planes);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            foreach (DrawEntry entry in entries)
            {
                Renderer renderer = entry.Renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                {
                    continue; // 视锥外（CPU 近似剔除，与生产同策）
                }

                if (entry.Mesh == null)
                {
                    drawFailures++;
                    continue;
                }

                if (entry.Handle == 0UL)
                {
                    positions.Clear();
                    normals.Clear();
                    uvs.Clear();
                    triangles.Clear();
                    positions.AddRange(entry.Mesh.vertices);
                    normals.AddRange(entry.Mesh.normals);
                    uvs.AddRange(entry.Mesh.uv);
                    triangles.AddRange(entry.Mesh.triangles);
                    int vertexCount = positions.Count;
                    int indexCount = triangles.Count;
                    if (vertexCount == 0 || indexCount == 0)
                    {
                        uploadFailures++;
                        continue;
                    }

                    var vertices = new float[vertexCount * VertexStrideFloats];
                    if (PackVertices(positions, normals, uvs, vertices) != vertexCount)
                    {
                        uploadFailures++;
                        continue;
                    }

                    var indices = new uint[indexCount];
                    for (int i = 0; i < indexCount; i++)
                    {
                        indices[i] = (uint)triangles[i];
                    }

                    int createResult = ZrUnitySceneNative.CreateMesh(vertices, (uint)vertexCount,
                        VertexStrideFloats, indices, (uint)indexCount, out ulong handle);
                    if (createResult != ZrUnitySceneNative.Ok || handle == 0UL)
                    {
                        uploadFailures++;
                        continue;
                    }

                    entry.Handle = handle;
                    meshesToFree.Add(handle);
                }

                var world = new float[16];
                ToNativeRowMajor(renderer.localToWorldMatrix, world);
                int drawResult = ZrUnitySceneNative.DrawMesh(entry.Handle, world, entry.Rgba);
                if (drawResult != ZrUnitySceneNative.Ok)
                {
                    drawFailures++;
                    continue;
                }

                drawn++;
            }

            int endResult = ZrUnitySceneNative.EndScene();
            if (endResult != ZrUnitySceneNative.Ok)
            {
                reason = "ZR_Unity_EndScene 失败: " + endResult;
                return false;
            }

            return true;
        }

        // ---- 纯数学（与 ZrSceneMath 同源；生产版本被 EditMode 单测钉住，副本只服务探针）====

        private const int VertexStrideFloats = 8; // pos3f + normal3f + uv2f（32B/顶点）

        /// <summary>Unity 列主序矩阵 → 本引擎行主序数组（存储转置，数学含义不变）。</summary>
        private static void ToNativeRowMajor(Matrix4x4 matrix, float[] destination)
        {
            for (int row = 0; row < 4; row++)
            {
                int rowOffset = row * 4;
                for (int column = 0; column < 4; column++)
                {
                    destination[rowOffset + column] = matrix[row, column];
                }
            }
        }

        /// <summary>投影归一化（NDC +Y 朝上 + [0,1] 深度；与 ZrSceneMath.NormalizeProjection 同源）。</summary>
        private static Matrix4x4 NormalizeProjection(Matrix4x4 projection, float nearPlane, float farPlane)
        {
            if (projection.m11 < 0f)
            {
                projection.m10 = -projection.m10;
                projection.m11 = -projection.m11;
                projection.m12 = -projection.m12;
                projection.m13 = -projection.m13;
            }

            float depthAtNear = DepthAtViewDepth(projection, nearPlane);
            float depthAtFar = DepthAtViewDepth(projection, farPlane);
            if (depthAtNear > depthAtFar)
            {
                var flip = Matrix4x4.zero;
                flip.m00 = 1f;
                flip.m11 = 1f;
                flip.m22 = -1f;
                flip.m23 = 1f;
                flip.m33 = 1f;
                projection = flip * projection;
            }

            return projection;
        }

        private static float DepthAtViewDepth(Matrix4x4 projection, float depth)
        {
            float z = projection.m22 * -depth + projection.m23;
            float w = projection.m32 * -depth + projection.m33;
            return (Mathf.Abs(w) > 1e-6f) ? z / w : 0f;
        }

        /// <summary>顶点打包 pos/normal/uv → 8 float/顶点（法线归一化；零向量给 +Y）。</summary>
        private static int PackVertices(List<Vector3> positions, List<Vector3> normals,
            List<Vector2> uvs, float[] destination)
        {
            int vertexCount = positions.Count;
            if (vertexCount <= 0 || destination.Length < vertexCount * VertexStrideFloats)
            {
                return 0;
            }

            bool hasNormals = normals != null && normals.Count == vertexCount;
            bool hasUvs = uvs != null && uvs.Count == vertexCount;
            Vector3 fallbackNormal = Vector3.up;
            int at = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = hasNormals ? normals[i] : fallbackNormal;
                float lengthSquared = normal.sqrMagnitude;
                normal = lengthSquared > 1e-12f
                    ? normal * (1f / Mathf.Sqrt(lengthSquared))
                    : fallbackNormal;
                Vector2 uv = hasUvs ? uvs[i] : Vector2.zero;

                destination[at + 0] = positions[i].x;
                destination[at + 1] = positions[i].y;
                destination[at + 2] = positions[i].z;
                destination[at + 3] = normal.x;
                destination[at + 4] = normal.y;
                destination[at + 5] = normal.z;
                destination[at + 6] = uv.x;
                destination[at + 7] = uv.y;
                at += VertexStrideFloats;
            }

            return vertexCount;
        }

        // ---- 指标与图像 =============================================================

        private static JObj ComputeDiffMetrics(byte[] a, byte[] b, int width, int height,
            int cropX, int cropY, int cropW, int cropH)
        {
            int count = 0;
            double sumA = 0, sumB = 0, sumAbs = 0;
            int overTwo = 0;
            var absDeltas = new List<double>(cropW * cropH);
            for (int y = cropY; y < cropY + cropH; y++)
            {
                int row = y * width * 4;
                for (int x = cropX; x < cropX + cropW; x++)
                {
                    int p = row + x * 4;
                    double la = Luma(a[p], a[p + 1], a[p + 2]);
                    double lb = Luma(b[p], b[p + 1], b[p + 2]);
                    double delta = lb - la;
                    sumA += la;
                    sumB += lb;
                    sumAbs += Math.Abs(delta);
                    if (Math.Abs(delta) > 2.0)
                    {
                        overTwo++;
                    }

                    absDeltas.Add(Math.Abs(delta));
                    count++;
                }
            }

            double meanA = sumA / count;
            double meanB = sumB / count;
            absDeltas.Sort();
            var metrics = new JObj
            {
                ["pixels"] = (long)count,
                ["meanLumaUrp"] = Round(meanA),
                ["meanLumaZr"] = Round(meanB),
                ["meanSignedDelta"] = Round(meanB - meanA),
                ["meanAbsDelta"] = Round(sumAbs / count),
                ["brightnessDeltaPct"] = meanA > 1e-9 ? Round(100.0 * (meanB - meanA) / meanA) : null,
                ["p50AbsDelta"] = Round(Percentile(absDeltas, 50)),
                ["p95AbsDelta"] = Round(Percentile(absDeltas, 95)),
                ["p99AbsDelta"] = Round(Percentile(absDeltas, 99)),
                ["pctPixelsAbsDeltaOver2"] = Round(100.0 * overTwo / count),
            };
            return metrics;
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0)
            {
                return 0;
            }

            int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Count) index = sorted.Count - 1;
            return sorted[index];
        }

        private static double Luma(byte r, byte g, byte b)
        {
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static CaptureResult ChannelMeans(byte[] pixels)
        {
            int pixelCount = pixels.Length / 4;
            double sumR = 0, sumG = 0, sumB = 0;
            for (int p = 0; p < pixels.Length; p += 4)
            {
                sumR += pixels[p];
                sumG += pixels[p + 1];
                sumB += pixels[p + 2];
            }

            return new CaptureResult
            {
                Pixels = pixels,
                MeanR = sumR / pixelCount,
                MeanG = sumG / pixelCount,
                MeanB = sumB / pixelCount,
                MeanLuma = 0.2126 * (sumR / pixelCount) + 0.7152 * (sumG / pixelCount)
                           + 0.0722 * (sumB / pixelCount),
            };
        }

        private static void WritePng(string path, byte[] rgba, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                tex.LoadRawTextureData(rgba);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void WriteDiffPng(string path, byte[] a, byte[] b, int width, int height)
        {
            var gray = new byte[width * height * 4];
            for (int p = 0; p < width * height; p++)
            {
                int src = p * 4;
                double delta = Math.Abs(Luma(a[src], a[src + 1], a[src + 2])
                                        - Luma(b[src], b[src + 1], b[src + 2]));
                byte value = (byte)Math.Min(255.0, Math.Round(delta * kDiffPngGain));
                gray[src] = value;
                gray[src + 1] = value;
                gray[src + 2] = value;
                gray[src + 3] = 255;
            }

            WritePng(path, gray, width, height);
        }

        // ---- 输出安全（env-probe.js 同口径）=========================================

        private static string CheckedOutputPath(string outRoot, string fileName)
        {
            if (!OutputNameWhitelist.IsMatch(fileName))
            {
                throw new InvalidOperationException("输出文件名不合白名单，拒绝写文件: " + fileName);
            }

            string root = Path.GetFullPath(outRoot);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            string full = Path.GetFullPath(Path.Combine(root, fileName));
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("输出路径越出探针 out 目录，拒绝写文件: " + fileName);
            }

            if (Path.GetFileName(full) != fileName)
            {
                throw new InvalidOperationException("basename 与输入不一致，拒绝写文件: " + fileName);
            }

            return full;
        }

        // ---- 快照与 JSON ============================================================

        private static JObj HostSnapshot(string urpAssetName)
        {
            return new JObj
            {
                ["unityVersion"] = Application.unityVersion,
                ["colorSpace"] = PlayerSettings.colorSpace.ToString(),
                ["graphicsDeviceType"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDeviceName"] = SystemInfo.graphicsDeviceName,
                ["graphicsDeviceVersion"] = SystemInfo.graphicsDeviceVersion,
                ["graphicsMemorySizeMB"] = (long)SystemInfo.graphicsMemorySize,
                ["processorType"] = SystemInfo.processorType,
                ["os"] = SystemInfo.operatingSystem,
                ["qualityLevel"] = QualitySettings.names.Length > 0
                    ? QualitySettings.names[QualitySettings.GetQualityLevel()]
                    : "(unknown)",
                ["activePipeline"] = urpAssetName ?? "(null)",
            };
        }

        private static JObj PluginSnapshot()
        {
            try
            {
                ZrPluginInfo info = ZrPlugin.EnsureLoaded();
                return new JObj
                {
                    ["apiVersion"] = (long)info.ApiVersion,
                    ["version"] = info.Version,
                    ["buildInfo"] = info.BuildInfo,
                };
            }
            catch (Exception error)
            {
                return new JObj { ["status"] = "MISSING", ["reason"] = error.Message };
            }
        }

        private static JObj CaptureSpecSnapshot()
        {
            return new JObj
            {
                ["urp"] = new JObj
                {
                    ["rt"] = "ARGB32 sRGB 读写 + 24d",
                    ["semantic"] = "URP 直出的最终显示值（sRGB 编码域）",
                    ["frames"] = kUrpWarmupFrames + " 暖启动 + 1 取帧",
                },
                ["zrBridge"] = new JObj
                {
                    ["rt"] = "ARGB32 Linear 读写",
                    ["semantic"] = "共享纹理原始字节直通（UNORM 外部视图按线性采样 + 生产 ZRender/Blit " +
                                   "材质直通贴屏——与生产桥显示路径同构；等价于原生共享纹理字节）",
                    ["externalTexture"] = "BGRA32 linear:true（与 ZRenderPipeline.cs:243 同参）",
                    ["frames"] = kNativeFrames + " 帧（末帧返回后 readable=上一帧，帧首 fence 已等完）",
                    ["sharedTargetSize"] = kWidth + "x" + kHeight + "（探针定尺寸，与 URP 帧同分辨率）",
                },
            };
        }

        private static List<object> StagesToList()
        {
            var list = new List<object>();
            foreach (Stage stage in s_stages)
            {
                list.Add(new JObj
                {
                    ["name"] = stage.Name,
                    ["status"] = stage.Status,
                    ["detail"] = stage.Detail,
                });
            }

            return list;
        }

        // git HEAD 只读快照（env-probe.js 同法：不调外部进程；loose 优先，packed-refs 兜底）
        private static JObj ReadGitHead(string repo)
        {
            try
            {
                string headRaw = File.ReadAllText(Path.Combine(repo, ".git", "HEAD")).Trim();
                if (!headRaw.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    return new JObj
                    {
                        ["ref"] = "(detached)",
                        ["sha"] = headRaw,
                        ["short"] = headRaw.Substring(0, Math.Min(7, headRaw.Length)),
                    };
                }

                string reference = headRaw.Substring(5).Trim();
                string loose = Path.Combine(repo, ".git", reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    string sha = File.ReadAllText(loose).Trim();
                    return new JObj
                    {
                        ["ref"] = reference,
                        ["sha"] = sha,
                        ["short"] = sha.Substring(0, Math.Min(7, sha.Length)),
                    };
                }

                foreach (string line in File.ReadAllLines(Path.Combine(repo, ".git", "packed-refs")))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] parts = trimmed.Split(' ');
                    if (parts.Length >= 2 && parts[1] == reference)
                    {
                        return new JObj
                        {
                            ["ref"] = reference,
                            ["sha"] = parts[0],
                            ["short"] = parts[0].Substring(0, Math.Min(7, parts[0].Length)),
                        };
                    }
                }

                return new JObj { ["status"] = "MISSING", ["reason"] = "找不到 ref 指向的提交: " + reference };
            }
            catch (Exception error)
            {
                return new JObj { ["status"] = "MISSING", ["reason"] = error.Message };
            }
        }

        // ---- 极简 JSON 序列化（保序；数字不变文化；null 显式）=======================

        private sealed class JObj : List<KeyValuePair<string, object>>
        {
            public object this[string key]
            {
                set => Add(key, value);
            }

            public object Get(string key)
            {
                foreach (KeyValuePair<string, object> pair in this)
                {
                    if (pair.Key == key)
                    {
                        return pair.Value;
                    }
                }

                return null;
            }

            /// <summary>Upsert：同名键原地覆盖（保序）——占位先写 null、后补真值的用法依赖此语义。</summary>
            public void Add(string key, object value)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (this[i].Key == key)
                    {
                        this[i] = new KeyValuePair<string, object>(key, value);
                        return;
                    }
                }

                Add(new KeyValuePair<string, object>(key, value));
            }
        }

        private static List<object> ToObjList(float[] values)
        {
            var list = new List<object>();
            foreach (float value in values)
            {
                list.Add(value);
            }

            return list;
        }

        private static List<object> UintList(uint[] values)
        {
            var list = new List<object>();
            foreach (uint value in values)
            {
                list.Add((long)value);
            }

            return list;
        }

        private static JObj CamObj(List<object> position, float fovY, float nearPlane, float farPlane)
        {
            return new JObj
            {
                ["position"] = position,
                ["rotationEuler"] = new List<object> { 0f, 0f, 0f },
                ["fovY"] = fovY,
                ["near"] = nearPlane,
                ["far"] = farPlane,
                ["clear"] = "SolidColor",
                ["backgroundColor"] = new List<object>
                {
                    kNativeClearColorR, kNativeClearColorG, kNativeClearColorB,
                },
            };
        }

        private static JObj ObjectObj(string name, string primitive, string material,
            List<object> baseColor, object smoothness)
        {
            var obj = new JObj
            {
                ["name"] = name,
                ["primitive"] = primitive,
                ["material"] = material,
                ["baseColor"] = baseColor,
            };
            if (smoothness != null)
            {
                obj["smoothness"] = smoothness;
            }

            return obj;
        }

        private static JObj LightObj()
        {
            return new JObj
            {
                ["travelDirection"] = new List<object> { -0.5, -1.0, -0.3 },
                ["color"] = new List<object> { 1.0, 0.96, 0.9 },
                ["intensity"] = 1.2,
                ["shadows"] = "None（探针口径）",
            };
        }

        private static string ToJson(object root)
        {
            var sb = new StringBuilder();
            WriteValue(sb, root, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string text:
                    WriteString(sb, text);
                    return;
                case bool flag:
                    sb.Append(flag ? "true" : "false");
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case float f:
                    WriteDouble(sb, f);
                    return;
                case double d:
                    WriteDouble(sb, d);
                    return;
                case JObj obj:
                    WriteObject(sb, obj, indent);
                    return;
                case List<object> list:
                    WriteList(sb, list, indent);
                    return;
                default:
                    WriteString(sb, value.ToString());
                    return;
            }
        }

        private static void WriteObject(StringBuilder sb, JObj obj, int indent)
        {
            if (obj.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{\n");
            int childIndent = indent + 1;
            for (int i = 0; i < obj.Count; i++)
            {
                AppendIndent(sb, childIndent);
                WriteString(sb, obj[i].Key);
                sb.Append(": ");
                WriteValue(sb, obj[i].Value, childIndent);
                if (i < obj.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            AppendIndent(sb, indent);
            sb.Append('}');
        }

        private static void WriteList(StringBuilder sb, List<object> list, int indent)
        {
            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            bool simple = true;
            foreach (object item in list)
            {
                if (item is JObj || item is List<object>)
                {
                    simple = false;
                    break;
                }
            }

            if (simple)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    WriteValue(sb, list[i], indent);
                    if (i < list.Count - 1)
                    {
                        sb.Append(", ");
                    }
                }

                sb.Append(']');
                return;
            }

            sb.Append("[\n");
            int childIndent = indent + 1;
            for (int i = 0; i < list.Count; i++)
            {
                AppendIndent(sb, childIndent);
                WriteValue(sb, list[i], childIndent);
                if (i < list.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            AppendIndent(sb, indent);
            sb.Append(']');
        }

        private static void AppendIndent(StringBuilder sb, int indent)
        {
            for (int i = 0; i < indent; i++)
            {
                sb.Append("  ");
            }
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        private static void WriteDouble(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null"); // JSON 无 NaN；缺值语义由周边 status/reason 表达
                return;
            }

            sb.Append(Math.Round(value, 4).ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static double Round(double value)
        {
            return Math.Round(value, 4);
        }

        private static double ToDouble(object value)
        {
            return value is double d ? d : 0.0;
        }
    }
}
#endif
