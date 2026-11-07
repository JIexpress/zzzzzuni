#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Game.View.Assembly;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot generator for the modular-assembly demo scene
    /// <c>Assets/Scenes/SCN_Assembly.unity</c> (TASK-A-TECH-02): ground,
    /// aim camera, three part kinds from <see cref="AssemblyPartFactory"/>
    /// (two structural cubes, one thruster, one wheel — procedural
    /// primitives, zero imported assets) and the
    /// <see cref="AssemblyDragController"/> host. Idempotent: the scene is
    /// rebuilt from scratch on every run, so the committed artifact always
    /// matches this script (same contract as <see cref="GameSceneSetup"/>).
    ///
    /// Build settings keep [SCN_Boot, SCN_Empty, SCN_Game] first and append
    /// SCN_Assembly; any other entry keeps its relative position.
    ///
    /// Run via menu (ZFrame/Setup/Generate SCN_Assembly) or batch:
    ///   -executeMethod Game.View.Editor.AssemblySceneSetup.SetupFromCommandLine
    /// </summary>
    public static class AssemblySceneSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string BootScenePath = ScenesDir + "/SCN_Boot.unity";
        private const string EmptyScenePath = ScenesDir + "/SCN_Empty.unity";
        private const string GameScenePath = ScenesDir + "/SCN_Game.unity";
        private const string AssemblyScenePath = ScenesDir + "/SCN_Assembly.unity";

        /// <summary>Rebuilds SCN_Assembly and pins the build-settings order.</summary>
        [MenuItem("ZFrame/Setup/Generate SCN_Assembly")]
        public static void Setup()
        {
            Directory.CreateDirectory(ScenesDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildContent();
            EditorSceneManager.SaveScene(scene, AssemblyScenePath);
            ApplyBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AssemblySceneSetup] created {AssemblyScenePath}");
        }

        /// <summary>Batch entry: rebuild the scene, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            Setup();
            EditorApplication.Exit(0);
        }

        private static void BuildContent()
        {
            Camera camera = BuildCamera();
            BuildLight();
            BuildGround();

            // Layout: cube A centred, cube B to the left, thruster to the
            // right, wheel in front — all resting on the ground plane.
            AssemblyPartFactory.CreateCube("Part_CubeA", new Vector3(0f, 0.5f, 0f),
                new Color(0.72f, 0.72f, 0.75f, 1f));
            AssemblyPartFactory.CreateCube("Part_CubeB", new Vector3(-2.5f, 0.5f, 0f),
                new Color(0.45f, 0.6f, 0.85f, 1f));
            AssemblyPartFactory.CreateThruster("Part_Thruster", new Vector3(2.5f, 0.45f, 0f));
            AssemblyPartFactory.CreateWheel("Part_Wheel", new Vector3(0f, 0.4f, 3f));

            BuildDragRig(camera);
        }

        private static Camera BuildCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            go.transform.position = new Vector3(0f, 2.8f, -5.2f);
            go.transform.rotation = Quaternion.Euler(14f, 0f, 0f);

            Camera camera = go.GetComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
            return camera;
        }

        private static void BuildLight()
        {
            var go = new GameObject("Directional Light", typeof(Light));
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = go.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
        }

        private static void BuildGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(3f, 1f, 3f); // 30 m x 30 m
        }

        private static void BuildDragRig(Camera camera)
        {
            var go = new GameObject("AssemblyRig");
            AssemblyDragController controller = go.AddComponent<AssemblyDragController>();

            // Serialized field wiring through SerializedObject (private by
            // convention; no public field).
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_camera").objectReferenceValue = camera;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ApplyBuildSettings()
        {
            var enabled = new Dictionary<string, bool>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (!string.IsNullOrEmpty(scene.path))
                {
                    enabled[scene.path] = scene.enabled;
                }
            }

            enabled[AssemblyScenePath] = true;

            var ordered = new List<EditorBuildSettingsScene>();
            foreach (string path in new[] { BootScenePath, EmptyScenePath, GameScenePath, AssemblyScenePath })
            {
                if (enabled.TryGetValue(path, out bool isEnabled) && File.Exists(path))
                {
                    ordered.Add(new EditorBuildSettingsScene(path, isEnabled));
                    enabled.Remove(path);
                }
            }

            // Anything else (future scenes) keeps its relative order after ours.
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (!string.IsNullOrEmpty(scene.path) && enabled.Remove(scene.path))
                {
                    ordered.Add(scene);
                }
            }

            EditorBuildSettings.scenes = ordered.ToArray();
        }
    }
}
#endif
