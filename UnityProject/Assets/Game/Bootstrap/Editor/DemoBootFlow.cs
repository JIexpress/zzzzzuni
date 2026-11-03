#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using Game.View.Editor;
using Game.View.UI;
using UnityEditor;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Data.Config;
using ZFrame.Data.Save;
using ZFrame.View;

namespace Game.Bootstrap.Editor
{
    /// <summary>
    /// Automated "fake manual" smoke of the boot chain for the demo document
    /// (TASK-A-M4-02). Runs in EDIT mode via batch:
    ///
    ///   Unity.exe -batchmode -projectPath ... \
    ///     -executeMethod Game.Bootstrap.Editor.DemoBootFlow.RunFromCommandLine
    ///
    /// It replays the GameBootstrap.Awake sequence step by step (GameRoot
    /// bootstrap → ZFrameData.Init → services check → input/UI stacks →
    /// main menu push with the real binding) — the parts of Awake that need a
    /// live play session (MonoBehaviour Awake) are simulated explicitly; the
    /// REAL end-to-end chain is covered by the ZFrame.PlayMode.Tests smoke.
    /// Lives in Game.Bootstrap (not Game.View) because it drives GameBootstrap
    /// flow objects — the dependency edge is Bootstrap → View.
    ///
    /// Output: <c>Docs/Parity/demo_boot.png</c> — a 1280×720 render of the
    /// main menu exactly as the boot chain leaves it (continue button visible
    /// iff slot 0 has a save), plus <c>Docs/Parity/demo_boot_log.txt</c>.
    /// Exit 0 on success, 1 on any failure (self-exits; do NOT add -quit).
    /// </summary>
    public static class DemoBootFlow
    {
        private const int kWidth = 1280;
        private const int kHeight = 720;
        private const string kOutDir = "Docs/Parity";
        private const string kPngPath = kOutDir + "/demo_boot.png";

        /// <summary>Entry point: run the simulated boot chain and capture the menu. Self-exits.</summary>
        public static void RunFromCommandLine()
        {
            int exitCode;
            try
            {
                exitCode = Run();
            }
            catch (Exception error)
            {
                Debug.Log($"[DemoBootFlow] EXCEPTION:\n{error}");
                exitCode = 1;
            }

            Debug.Log($"[DemoBootFlow] done, exit={exitCode}");
            EditorApplication.Exit(exitCode);
        }

        private static int Run()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string pngPath = Path.Combine(repoRoot, kPngPath);
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath));

            // ---- step 0: make sure the boot assets exist ------------------------
            BootSceneSetup.Setup();
            Debug.Log("[DemoBootFlow] step 0: boot assets verified (prefabs + SCN_Boot).");

            // ---- step 1: framework boot (the GameRoot half of Awake) -------------
            GameRoot.Bootstrap();
            ZFrameData.Init();
            Services.Get<IGameTime>();
            Services.Get<IResourceService>();
            Services.Get<IConfigService>();
            Services.Get<ISaveSystem>();
            Debug.Log("[DemoBootFlow] step 1: services ready: "
                      + string.Join(", ", Services.GetAllRegistered()));

            // ---- step 2: input + UI stacks ---------------------------------------
            InputRuntime.EnsureCreated();
            UIRoot root = UIRoot.EnsureCreated();
            var ui = new UIManager(Services.Get<IResourceService>(), root);
            UIBinding.Register<MainMenuView>(view => new MainMenuPresenter((MainMenuView)view));
            Debug.Log("[DemoBootFlow] step 2: input runtime + UIRoot + UIManager up.");

            // ---- step 3: push the main menu (real binding, real prefab) ----------
            bool hasSave = Services.Get<ISaveSystem>().GetSlotInfo(0) != null;
            var push = ui.Push<MainMenuView>(
                GameBootstrap.MainMenuConfig,
                MainMenuArgs.Create(null, null, null, null, hasSave));
            if (!push.IsCompleted || !push.Result)
            {
                Debug.Log($"[DemoBootFlow] FAIL: main menu push did not complete (status={push.Status}, result={push.Result}).");
                return 1;
            }

            Debug.Log($"[DemoBootFlow] step 3: main menu pushed (stack={ui.StackCount}, continue visible={hasSave}).");

            // ---- step 4: render the menu through a capture camera ----------------
            // UGUI canvases default to Screen Space - Overlay, which a camera
            // render cannot see — switch every layer canvas to Screen Space -
            // Camera so the capture shows exactly what a player would see.
            var camGo = new GameObject("DemoCaptureCam");
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.06f, 0.07f, 0.1f, 1f);

            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                Canvas canvas = root.GetLayerCanvas(layer);
                if (canvas == null)
                {
                    continue;
                }

                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 10f;
            }

            var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            File.WriteAllBytes(pngPath, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();

            bool ok = File.Exists(pngPath) && new FileInfo(pngPath).Length > 0;

            var report = new StringBuilder();
            report.AppendLine("DemoBootFlow (TASK-A-M4-02) — 编辑态启动链模拟");
            report.AppendLine($"unity: {Application.unityVersion}");
            report.AppendLine("steps: GameRoot.Bootstrap -> ZFrameData.Init -> services check -> input/UI -> Push(MainMenuView)");
            report.AppendLine($"main menu: stack={ui.StackCount}, continue_button={(hasSave ? "visible (slot 0 has save)" : "hidden (no save)")}");
            report.AppendLine($"screenshot: {kPngPath} ({(ok ? "OK" : "FAILED")})");
            report.AppendLine("note: 编辑态模拟（Awake 手动逐步执行）；真实端到端链路由 ZFrame.PlayMode.Tests 冒烟覆盖");
            report.Replace("\r\n", "\n");
            File.WriteAllText(Path.Combine(repoRoot, kOutDir, "demo_boot_log.txt"), report.ToString(), new UTF8Encoding(false));

            Debug.Log($"[DemoBootFlow] png={pngPath} ({(ok ? "OK" : "FAILED")})");
            return ok ? 0 : 1;
        }
    }
}
#endif
