#if UNITY_EDITOR
using System.IO;
using Game.View.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Bootstrap.Editor
{
    /// <summary>
    /// One-shot setup of the boot scene (TASK-A-M4-02): SCN_Boot carries a
    /// "GameBootstrap" GameObject with the <see cref="GameBootstrap"/> component
    /// — the entry of the whole vertical slice. It deliberately does NOT carry
    /// a GameRoot component: the framework itself guarantees a GameRoot
    /// instance (AudioService self-mounts via EnsureInstance at
    /// BeforeSceneLoad), and GameRoot's duplicate handling destroys its whole
    /// GameObject — which would take a co-located GameBootstrap down with it.
    /// Chained with the UI prefab half (<see cref="GameUiSetup"/>, Game.View
    /// side; this assembly may reference it — the dependency edge is
    /// Bootstrap → View).
    ///
    /// Idempotent: the scene is patched only when the bootstrap object is
    /// missing or incomplete. Batch entry:
    ///   -executeMethod Game.Bootstrap.Editor.BootSceneSetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as GameSceneSetup).
    /// </summary>
    public static class BootSceneSetup
    {
        private const string BootScenePath = "Assets/Scenes/SCN_Boot.unity";

        [MenuItem("ZFrame/Setup/Generate Game Boot Scene (SCN_Boot)")]
        public static void Setup()
        {
            GameUiSetup.EnsureMainMenuPrefab();
            GameUiSetup.EnsurePausePrefab();
            EnsureBootScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>Batch entry: ensure prefabs + boot scene, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            Setup();
            EditorApplication.Exit(0);
        }

        private static void EnsureBootScene()
        {
            if (!File.Exists(BootScenePath))
            {
                Directory.CreateDirectory("Assets/Scenes");
                var created = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(created, BootScenePath);
            }

            var scene = EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);
            GameObject existing = GameObject.Find("GameBootstrap");
            if (existing != null && existing.GetComponent<GameBootstrap>() != null)
            {
                Debug.Log("[BootSceneSetup] SCN_Boot already carries GameBootstrap — no change.");
                return;
            }

            // A legacy combined "GameRoot" object (GameRoot + GameBootstrap on
            // one GO) must not survive: see class doc for the dedup hazard.
            GameObject legacy = GameObject.Find("GameRoot");
            if (legacy != null && legacy.GetComponent<GameBootstrap>() != null)
            {
                Object.DestroyImmediate(legacy);
                Debug.Log("[BootSceneSetup] removed legacy combined GameRoot/GameBootstrap object.");
            }

            var host = new GameObject("GameBootstrap");
            host.AddComponent<GameBootstrap>();
            EditorSceneManager.SaveScene(scene, BootScenePath);
            Debug.Log("[BootSceneSetup] SCN_Boot: GameBootstrap assembled.");
        }
    }
}
#endif
