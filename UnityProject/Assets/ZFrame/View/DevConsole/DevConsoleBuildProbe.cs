#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ZFrame.View.DevConsole
{
    /// <summary>
    /// TASK-A-DEV-04（H1）取证用构建入口：同一份工程分别编出
    /// **非开发包**（<see cref="BuildOptions.None"/>，真正的发布产物形态）
    /// 和**开发包**（<see cref="BuildOptions.Development"/>，对照组）。
    ///
    /// 只在 Editor 下编译（`#if UNITY_EDITOR`），发布包 DLL 里连类型都不存在。
    /// 产物落在 Build/DevConsoleProbe/{release,dev}/，不碰 Build/Windows64 与版本号。
    ///
    /// 用法（必须通过 Git Bash 启动 Unity，见 HANDOFF）：
    ///   Unity.exe -batchmode -projectPath UnityProject \
    ///     -executeMethod ZFrame.View.DevConsole.DevConsoleBuildProbe.BuildNonDevelopment \
    ///     -logFile TempLogs/dev04_build_release.log
    /// </summary>
    internal static class DevConsoleBuildProbe
    {
        private const string LogTag = "[ZBF-BUILD]";

        /// <summary>非开发包：发布包形态，用来证明"符号不在、端口不监听"。</summary>
        public static void BuildNonDevelopment()
        {
            Build("release", BuildOptions.None);
        }

        /// <summary>开发包：对照组，用来证明 grep 方法不是假阴性。</summary>
        public static void BuildDevelopment()
        {
            Build("dev", BuildOptions.Development);
        }

        private static void Build(string name, BuildOptions options)
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outputPath = Path.Combine(repoRoot, "Build", "DevConsoleProbe", name, "ZFrame.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError(LogTag + " no enabled scenes");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log(LogTag + " building " + name + " options=" + options + " -> " + outputPath);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = options,
            });

            Debug.Log(LogTag + " " + name + " result=" + report.summary.result
                + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
#endif
