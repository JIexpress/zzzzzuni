#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// One-shot project setup for the scene system (TASK-A-M2-01):
    /// generates the committed empty scene <c>Assets/Scenes/SCN_Empty.unity</c>
    /// (built-in camera + light, no gameplay content) and pins the build
    /// settings scene list to [SCN_Boot, SCN_Empty] — the loader's smoke-test
    /// target sits at index 1. Idempotent: safe to re-run. Run via menu
    /// (ZFrame/Setup/Generate SCN_Empty + Build Settings) or batch:
    ///   -executeMethod ZFrame.View.SceneProjectSetup.SetupFromCommandLine
    /// Editor-only code inside the ZFrame.View assembly, guarded by
    /// #if UNITY_EDITOR (same pattern as Data/Editor/ConfigAssetSetup.cs).
    /// </summary>
    public static class SceneProjectSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string BootScenePath = ScenesDir + "/SCN_Boot.unity";
        private const string EmptyScenePath = ScenesDir + "/SCN_Empty.unity";

        [MenuItem("ZFrame/Setup/Generate SCN_Empty + Build Settings")]
        public static void Setup()
        {
            Directory.CreateDirectory(ScenesDir);

            if (!File.Exists(EmptyScenePath))
            {
                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, EmptyScenePath);
                Debug.Log($"[SceneProjectSetup] created {EmptyScenePath}");
            }

            var scenes = new List<EditorBuildSettingsScene>();
            if (File.Exists(BootScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(BootScenePath, true));
            }

            scenes.RemoveAll(existing =>
                string.Equals(existing.path, EmptyScenePath, System.StringComparison.Ordinal));
            scenes.Add(new EditorBuildSettingsScene(EmptyScenePath, true));

            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.Refresh();
            Debug.Log(
                $"[SceneProjectSetup] build settings: {string.Join(", ", ToPathList(scenes))}");
        }

        /// <summary>Batch entry: runs the setup and exits 0 (keeps automation exit codes trustworthy).</summary>
        public static void SetupFromCommandLine()
        {
            Setup();
            EditorApplication.Exit(0);
        }

        private static IEnumerable<string> ToPathList(List<EditorBuildSettingsScene> scenes)
        {
            foreach (var scene in scenes)
            {
                yield return scene.path;
            }
        }
    }
}
#endif
