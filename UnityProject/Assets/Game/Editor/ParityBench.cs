// =============================================================================
// ParityBench.cs —— URP 侧对等闸门基准场景（TASK-B-R5-04，Unity 批处理）
//
// 用法（仓库根，勿带 -quit——本方法自带 EditorApplication.Exit）：
//   "C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode
//     -projectPath <UnityProject> -logFile TempLogs/parity_urp.log
//     -executeMethod Game.EditorTools.ParityBench.RunFromCommandLine
//
// 产出（仓库 Docs/Parity/）：
//   parity_urp.png   1280×720 PNG（RT 渲染 → ReadPixels → EncodeToPNG）
//   metrics_urp.txt  帧耗（Stopwatch 包 camera.Render 的 CPU 墙钟，20 帧均值）+
//                    DrawCall（ProfilerRecorder "Draw Calls Count"，不可靠时注明
//                    人工 Profiler 窗口读取步骤）
//
// 基准场景契约（与 Native/tests/zrhi_d3d12_window.cpp 的 --parity 段注释严格
// 一致，修改须两侧同步）：
//   分辨率 1280×720；相机 pos(0,1.6,-4) 朝 +Z 看原点（fovY 60°，near 0.3 far 60）
//   地面：20×20 平面 y=0，URP Lit 灰色 (0.5,0.5,0.5) smoothness 0.1
//   Cube A：pos(0,0.5,0) 1m³，URP Lit 暖白 (0.9,0.85,0.75) smoothness 0.5
//   Cube B：pos(1.8,0.5,0.8) 1m³，无光照 Unlit 纯色 (0.9,0.3,0.25)（描边省略，
//           差异清单见 parity_report.md；投影阴影开）
//   方向光：行进方向 (-0.5,-1,-0.3)/|·|（LookRotation），色 (1,0.96,0.9)，强度 1.2
//   环境：RenderSettings Flat ambient 0.12（对齐 ZRender 常数环境项）
//   清屏色 (0.03,0.03,0.045)（相机 Solid Color）；阴影开（Soft）；后处理关
//   （相机 renderPostProcessing=false，场景无 Volume）；固定一帧（无动画）
//   管线档：URP_Medium（QualitySettings 名义档 + GraphicsSettings 默认管线）
// =============================================================================
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch; // Debug 与 UnityEngine.Debug 二义，用别名
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Profiling; // ProfilerRecorder/ProfilerCategory（2022.3 在 Unity.Profiling）

namespace Game.EditorTools
{
    public static class ParityBench
    {
        private const int kWidth = 1280;
        private const int kHeight = 720;
        private const int kBenchFrames = 20; // 帧耗计时帧数（1 帧暖启动 + 20 帧计时）
        private const int kDrawCallProbeFrames = 5; // DrawCall 计数采样帧数
        private const string kOutDir = "Docs/Parity";
        private const string kPngPath = kOutDir + "/parity_urp.png";
        private const string kMetricsPath = kOutDir + "/metrics_urp.txt";

        /// <summary>Entry point: build the contract scene, render, write PNG +
        /// metrics, exit 0/1（自带 Exit，批处理命令勿再加 -quit）。</summary>
        public static void RunFromCommandLine()
        {
            int exitCode;
            try
            {
                exitCode = Run();
            }
            catch (Exception e)
            {
                Debug.Log($"[ParityBench] EXCEPTION:\n{e}");
                exitCode = 1;
            }

            Debug.Log($"[ParityBench] done, exit={exitCode}");
            EditorApplication.Exit(exitCode);
        }

        private static int Run()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outDir = Path.Combine(repoRoot, kOutDir);
            Directory.CreateDirectory(outDir);

            // ---- 0. 管线前提：URP 激活 + Medium 档 --------------------------------
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Debug.Log("[ParityBench] FAIL: currentRenderPipeline is not URP");
                return 1;
            }

            int mediumLevel = Array.IndexOf(QualitySettings.names, "Medium");
            if (mediumLevel < 0)
            {
                Debug.Log("[ParityBench] FAIL: QualitySettings has no 'Medium' level: "
                          + string.Join(",", QualitySettings.names));
                return 1;
            }
            QualitySettings.SetQualityLevel(mediumLevel);

            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (litShader == null || unlitShader == null)
            {
                Debug.Log("[ParityBench] FAIL: URP Lit/Unlit shader not found");
                return 1;
            }

            // ---- 1. 临时场景（契约：不落盘，退出即弃）-----------------------------
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.12f, 0.12f, 0.12f, 1f); // 契约环境

            // 地面：Unity Plane 原生 10×10 → scale 2 = 20×20 @y=0
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(2f, 1f, 2f);
            var matGround = new Material(litShader) { name = "MAT_ParityGround" };
            matGround.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            matGround.SetFloat("_Smoothness", 0.1f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = matGround;

            // Cube A：URP Lit 暖白
            var cubeA = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeA.name = "CubeA";
            cubeA.transform.position = new Vector3(0f, 0.5f, 0f);
            var matA = new Material(litShader) { name = "MAT_ParityCubeA" };
            matA.color = new Color(0.9f, 0.85f, 0.75f, 1f);
            matA.SetFloat("_Smoothness", 0.5f);
            cubeA.GetComponent<MeshRenderer>().sharedMaterial = matA;

            // Cube B：无光照 Unlit 纯色（卡通材质的 URP 侧近似；描边省略——报告注明）
            var cubeB = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeB.name = "CubeB";
            cubeB.transform.position = new Vector3(1.8f, 0.5f, 0.8f);
            var matB = new Material(unlitShader) { name = "MAT_ParityCubeB" };
            matB.color = new Color(0.9f, 0.3f, 0.25f, 1f);
            var meshB = cubeB.GetComponent<MeshRenderer>();
            meshB.sharedMaterial = matB;
            meshB.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // 契约：投影

            // 方向光（契约：行进方向 (-0.5,-1,-0.3)/|·|，色 (1,0.96,0.9)，强度 1.2）
            var lightGo = new GameObject("DirLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f, 1f);
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            lightGo.transform.rotation =
                Quaternion.LookRotation(new Vector3(-0.5f, -1f, -0.3f).normalized);

            // 相机（契约：pos(0,1.6,-4) 朝 +Z，fov 60，near 0.3 far 60，后处理关）
            var camGo = new GameObject("ParityCam");
            var cam = camGo.AddComponent<Camera>();
            camGo.transform.position = new Vector3(0f, 1.6f, -4f);
            camGo.transform.rotation = Quaternion.identity; // Unity 相机默认朝 +Z
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 60f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.03f, 0.03f, 0.045f, 1f);
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = false; // 契约：后处理关（场景亦无 Volume）

            // ---- 2. RT 渲染 + 指标 ------------------------------------------------
            var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            cam.Render(); // 暖启动（shader/PSO 编译、culling 初始化不入计时）

            var sw = new Stopwatch();
            double sumMs = 0.0, minMs = double.MaxValue, maxMs = 0.0;
            for (int i = 0; i < kBenchFrames; ++i)
            {
                sw.Restart();
                cam.Render();
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                sumMs += ms;
                minMs = Math.Min(minMs, ms);
                maxMs = Math.Max(maxMs, ms);
            }
            double avgMs = sumMs / kBenchFrames;

            // DrawCall：ProfilerRecorder（Rendering/Draw Calls Count）。批处理下该
            // 计数器若取不到（0/异常），指标如实标注并给出人工 Profiler 读取步骤。
            long drawCalls = -1;
            string drawCallMethod = "ProfilerRecorder(Render/Draw Calls Count, last frame)";
            try
            {
                using (var rec = new ProfilerRecorder(
                    ProfilerCategory.Render, "Draw Calls Count", 8))
                {
                    rec.Start();
                    for (int i = 0; i < kDrawCallProbeFrames; ++i)
                    {
                        cam.Render();
                    }
                    drawCalls = rec.LastValue;
                    rec.Stop();
                }
            }
            catch (Exception e)
            {
                Debug.Log($"[ParityBench] ProfilerRecorder unavailable: {e.Message}");
            }
            if (drawCalls <= 0)
            {
                drawCallMethod = "UNAVAILABLE in batchmode — 人工步骤：编辑器打开同场景"
                    + "（契约参数见本文件头），Window > Analysis > Profiler > Render 行"
                    + " 'Draw Calls Count' 或 Game 视图 Stats 面板 DrawCalls 读取";
                drawCalls = -1;
            }

            // ---- 3. ReadPixels → PNG ----------------------------------------------
            RenderTexture.active = rt;
            var tex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            string pngPath = Path.Combine(outDir, "parity_urp.png");
            File.WriteAllBytes(pngPath, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();

            // ---- 4. 指标文件 -------------------------------------------------------
            var sb = new StringBuilder();
            sb.AppendLine("URP parity metrics (TASK-B-R5-04)");
            sb.AppendLine($"unity: {Application.unityVersion} | pipeline asset: {urp.name} "
                          + $"(quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]})");
            sb.AppendLine($"resolution: {kWidth}x{kHeight} (RT ARGB32, msaa 1x)");
            sb.AppendLine("frames: fixed scene, no animation, shadows on (Soft), "
                          + "post processing off");
            sb.AppendLine($"avg_frame_ms: {avgMs:F4}  (口径: Stopwatch 包 camera.Render() "
                          + $"单调用 CPU 墙钟, {kBenchFrames} 帧均值, 暖启动 1 帧不计; "
                          + "批处理编辑器路径, 非独立运行时 GPU 基准)");
            sb.AppendLine($"frame_ms_min: {minMs:F4}");
            sb.AppendLine($"frame_ms_max: {maxMs:F4}");
            sb.AppendLine($"draw_calls: {(drawCalls >= 0 ? drawCalls.ToString() : "N/A")}  "
                          + $"(口径: {drawCallMethod})");
            sb.AppendLine($"screenshot: Docs/Parity/parity_urp.png (PNG, EncodeToPNG)");
            sb.AppendLine($"urp_asset: HDR={urp.supportsHDR} msaa={urp.msaaSampleCount} "
                          + $"shadowDistance={urp.shadowDistance} "
                          + $"cascades={urp.shadowCascadeCount}");
            string metricsPath = Path.Combine(outDir, "metrics_urp.txt");
            sb.Replace("\r\n", "\n"); // 文件规范：LF 换行（AppendLine 在 Windows 产 CRLF）
            File.WriteAllText(metricsPath, sb.ToString(), new UTF8Encoding(false));

            bool ok = File.Exists(pngPath) && new FileInfo(pngPath).Length > 0;
            Debug.Log($"[ParityBench] png={pngPath} ({(ok ? "OK" : "FAILED")}) "
                      + $"avgFrameMs={avgMs:F3} drawCalls={drawCalls}");
            Debug.Log($"[ParityBench] metrics:\n{sb}");
            return ok ? 0 : 1;
        }
    }
}
#endif
