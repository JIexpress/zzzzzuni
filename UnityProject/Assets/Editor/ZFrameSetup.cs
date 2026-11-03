using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ZFrame.EditorSetup
{
    /// <summary>
    /// One-shot project scaffold executed via Unity batch mode (-executeMethod).
    /// Creates the ZFrame directory skeleton, saves the empty boot scene and
    /// registers it in Build Settings. Task A-M0-01.
    /// </summary>
    public static class ZFrameSetup
    {
        private static readonly string[] _directories =
        {
            "Assets/ZFrame/Core",
            "Assets/ZFrame/Data",
            "Assets/ZFrame/Gameplay",
            "Assets/ZFrame/View",
            "Assets/ZFrame/Editor",
            "Assets/ZFrame/Tests",
            "Assets/Game/Domain",
            "Assets/Game/View",
            "Assets/Game/Bootstrap",
            "Assets/Art/Models",
            "Assets/Art/Models/Raw",
            "Assets/Art/Materials",
            "Assets/Art/Textures",
            "Assets/Art/Animations",
            "Assets/Audio",
            "Assets/Prefabs",
            "Assets/Scenes",
            "Assets/UI",
            "Assets/Configs",
            "Assets/Settings",
            "Assets/Plugins",
        };

        private const string BootScenePath = "Assets/Scenes/SCN_Boot.unity";

        /// <summary>
        /// Entry point for -executeMethod. Idempotent: re-running only recreates
        /// missing directories and re-saves an identical boot scene.
        /// </summary>
        public static void Setup()
        {
            CreateDirectories();
            CreateBootScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ZFrameSetup] Scaffold complete: directories, SCN_Boot, BuildSettings entry.");
        }

        private static void CreateDirectories()
        {
            foreach (string directory in _directories)
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void CreateBootScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, BootScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootScenePath, true),
            };
        }
    }
}
