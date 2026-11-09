#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Editor
{
    /// <summary>
    /// 【实验工具 · 默认禁用】URP 色彩管线迁移（Gamma → Linear + Tonemapping）。
    ///
    /// ⚠ 2026-11-09 实测结论（TASK-A-R9-19）：**这不是一个开关级迁移**。
    /// 单独执行本工具会让 4 个用例变红，并使画面基准整体位移 +9 灰阶：
    ///   1. `ZRenderFrameSyncTests` / `ZRenderPipelineTextureLifecycleTests` —
    ///      Unity 在 Linear 下给 ZRender 的 BGRA8 共享纹理建 sRGB SRV，
    ///      与原生 UNORM 资源不兼容（dxgi-fmt 91 vs 87）；
    ///   2. `GameplayFxPixelEvidencePlayModeTests` — 加色粒子在 Linear 下变暗
    ///      （门限 0.0040，实测 0.0003），特效强度需要重标定；
    ///   3. `PostFxPixelEvidencePlayModeTests` — 两档都加 Tonemapping 后
    ///      High/Medium 差异被压缩到门限以下（0.1469 vs 0.1590）。
    ///
    /// 另一个机制点：切 Linear 时 Unity 会把 `m_LightsUseLinearIntensity`
    /// 从 0 翻到 1，全场景灯光需要按线性强度重新标定——这是"切完变黑"的根因，
    /// 不是渲染坏了。
    ///
    /// 因此本工具加了环境变量闸门，只有显式设置 `ZF_COLOR_MIGRATION_OK=1`
    /// 才会执行，避免被误用。完整迁移步骤见 `Docs/Logs/TASK-A-R9-19.md`。
    ///
    /// 批处理（自退出）：
    ///   set ZF_COLOR_MIGRATION_OK=1
    ///   -executeMethod Game.Editor.ColorPipelineSetup.MigrateFromCommandLine
    /// </summary>
    public static class ColorPipelineSetup
    {
        /// <summary>显式授权开关：没有它，工具拒绝执行。</summary>
        public const string AllowEnvVar = "ZF_COLOR_MIGRATION_OK";

        private const string MediumProfilePath = "Assets/Settings/Pipeline/Vol_Medium.asset";
        private const string HighProfilePath = "Assets/Settings/Pipeline/Vol_High.asset";
        private const string LowProfilePath = "Assets/Settings/Pipeline/Vol_Low.asset";

        /// <summary>批处理入口：迁移 + 落盘报告 + 退出码（2 = 未授权）。</summary>
        public static void MigrateFromCommandLine()
        {
            int exitCode;
            try
            {
                exitCode = Run();
            }
            catch (Exception error)
            {
                Debug.LogError($"[ColorPipelineSetup] EXCEPTION:\n{error}");
                exitCode = 1;
            }

            EditorApplication.Exit(exitCode);
        }

        [MenuItem("ZFrame/Setup/Color Pipeline Migration (experimental — read TASK-A-R9-19)")]
        public static void MigrateFromMenu()
        {
            int code = Run();
            Debug.Log($"[ColorPipelineSetup] menu run done, exit={code}");
        }

        /// <summary>执行迁移并返回退出码（0 = 校验通过，2 = 未授权）。</summary>
        public static int Run()
        {
            if (Environment.GetEnvironmentVariable(AllowEnvVar) != "1")
            {
                Debug.LogError(
                    $"[ColorPipelineSetup] 拒绝执行：本工具是实验性的，单独开启会让 4 个用例变红。" +
                    $"确认已读完 Docs/Logs/TASK-A-R9-19.md 后，设置 {AllowEnvVar}=1 再跑。");
                return 2;
            }

            var report = new StringBuilder();
            report.AppendLine($"Color pipeline migration @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"unity: {Application.unityVersion}");

            ColorSpace before = PlayerSettings.colorSpace;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            report.AppendLine($"color space: {before} -> {PlayerSettings.colorSpace}");
            report.AppendLine($"lightsUseLinearIntensity: {GraphicsSettings.lightsUseLinearIntensity}");

            report.AppendLine(EnsureTonemapping(MediumProfilePath));
            report.AppendLine(EnsureTonemapping(HighProfilePath));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            bool ok = PlayerSettings.colorSpace == ColorSpace.Linear;
            report.AppendLine($"verify colorSpace==Linear: {ok}");
            ok &= VerifyTonemapping(MediumProfilePath, report);
            ok &= VerifyTonemapping(HighProfilePath, report);

            VolumeProfile low = AssetDatabase.LoadAssetAtPath<VolumeProfile>(LowProfilePath);
            if (low != null)
            {
                bool lowHasTonemap = low.TryGet(out Tonemapping _);
                report.AppendLine($"verify Vol_Low has NO tonemapping (by design): {!lowHasTonemap}");
                ok &= !lowHasTonemap;
            }

            report.AppendLine($"RESULT: {(ok ? "PASS" : "FAIL")}");

            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "color_pipeline_setup.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ColorPipelineSetup]\n{report}");

            return ok ? 0 : 1;
        }

        private static string EnsureTonemapping(string profilePath)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (profile == null)
            {
                return $"  {profilePath}: MISSING (skipped)";
            }

            bool created = false;
            if (!profile.TryGet(out Tonemapping tonemapping))
            {
                tonemapping = profile.Add<Tonemapping>();
                created = true;
            }

            tonemapping.active = true;
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.Neutral;
            EditorUtility.SetDirty(profile);

            return $"  {profilePath}: {(created ? "ADDED" : "updated")} Tonemapping "
                   + $"(mode={tonemapping.mode.value}, override={tonemapping.mode.overrideState})";
        }

        private static bool VerifyTonemapping(string profilePath, StringBuilder report)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            bool has = profile != null
                       && profile.TryGet(out Tonemapping tonemapping)
                       && tonemapping.mode.overrideState
                       && tonemapping.mode.value == TonemappingMode.Neutral;
            report.AppendLine($"verify {profilePath} tonemapping=Neutral+override: {has}");
            return has;
        }
    }
}
#endif
