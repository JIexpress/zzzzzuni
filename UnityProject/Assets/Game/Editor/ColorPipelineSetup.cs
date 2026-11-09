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
    /// URP 色彩管线迁移（Gamma → Linear + HDR 两档 Tonemapping）。
    ///
    /// **状态：已落地**（TASK-A-R9-19，2026-11-09）。工程当前就是
    /// `m_ActiveColorSpace: 1` + Medium/High 两档 Tonemapping(Neutral)。
    /// 本工具保留为**幂等重放/核对**入口：重复跑不会叠加组件，只把
    /// 色彩空间与 Tonemapping 摆回目标状态。
    ///
    /// 落地时踩过的三件事（别再踩）：
    ///   1. 切换后**第一次运行不可信**：变体未编译完，首张截图全黑、首轮
    ///      粒子像素用例假红。任何对照都必须复跑一次再判定。
    ///   2. Unity 会自动把 `m_LightsUseLinearIntensity` 0→1，并重新序列化
    ///      材质的 sRGB 颜色——迁移是一个资产级提交，不是只改 ProjectSettings。
    ///   3. ZRender 的共享纹理桥在 Linear 下必须用 UNORM 视图
    ///      （`CreateExternalTexture(linear:true)`），否则建 sRGB SRV 与
    ///      UNORM 资源冲突（dxgi 91 vs 87）。见 TASK-A-R9-19 §6。
    ///
    /// 闸门：仍需 `ZF_COLOR_MIGRATION_OK=1` 才执行，防止在未读完交接的环境里
    /// 误跑（效果是幂等的，但会触发一次全量资产重导入）。
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
                    $"[ColorPipelineSetup] 拒绝执行：迁移是资产级操作（会触发全量重导入）。" +
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
