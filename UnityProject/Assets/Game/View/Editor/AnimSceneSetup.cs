#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.View.Animation;
using Game.View.Npc;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot generator for the humanoid-animation demo scene
    /// <c>Assets/Scenes/SCN_Anim.unity</c> (TASK-A-ANIM-01): ground, a static
    /// player (the look-at target), a rest point, four waypoint markers and
    /// TWO utility NPCs carrying the REAL Kenney skinned model with a
    /// generated <c>CHR_KenneyLocomotion</c> animator controller plus
    /// <see cref="NpcAnimatorDriver"/> — A "Restful" (fatigue 0.95, walks to
    /// the rest point, then idles, then follows) and C "Wayfarer" (patrols
    /// the square forever, so Run and the smooth turning stay visible).
    /// Same brain code as SCN_Utility, different BODIES: the animation state
    /// is the brain state's function through measured displacement, and the
    /// head looks at the player via OnAnimatorIK.
    ///
    /// Idempotent by the GameSceneSetup convention: the scene is rebuilt from
    /// scratch on every run. SCN_Utility and its tests are untouched — this
    /// is a standalone add-alongside demo. Build settings keep
    /// [SCN_Boot, SCN_Empty, SCN_Game, SCN_Utility] in front and append
    /// SCN_Anim as the fifth entry.
    ///
    /// Scale note: the FBX import leaves Blender's ×100 node scales in place
    /// (A-R9-10 probe: characters measure ~180-208 m at scale 1). This scene
    /// does NOT touch the shared model import (SCN_Game owns that); each
    /// placed model instance is measured and rescaled to
    /// <see cref="CharacterHeightMeters"/> via
    /// <see cref="ModelStageLayout.ScaleForHeight"/> inside a parent node so
    /// the view/label logic (which expects unscaled local units) stays exact.
    ///
    /// Run via menu (ZFrame/Setup/Generate SCN_Anim) or batch:
    ///   -executeMethod Game.View.Editor.AnimSceneSetup.SetupFromCommandLine
    /// </summary>
    public static class AnimSceneSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string AnimScenePath = ScenesDir + "/SCN_Anim.unity";
        private const string BootScenePath = ScenesDir + "/SCN_Boot.unity";
        private const string EmptyScenePath = ScenesDir + "/SCN_Empty.unity";
        private const string GameScenePath = ScenesDir + "/SCN_Game.unity";
        private const string UtilityScenePath = ScenesDir + "/SCN_Utility.unity";
        private const string ControllerPath = "Assets/Art/Models/CHR_KenneyLocomotion.controller";

        /// <summary>Target on-screen height of the characters (metres).</summary>
        public const float CharacterHeightMeters = 1.75f;

        private static readonly Vector3 PlayerPosition = new Vector3(5f, 1f, 3f);
        private static readonly Vector3 RestPoint = new Vector3(-2.5f, 0.2f, -2f);
        private static readonly Vector3[] Waypoints =
        {
            new Vector3(5f, 0f, -5f),
            new Vector3(5f, 0f, 5f),
            new Vector3(-5f, 0f, 5f),
            new Vector3(-5f, 0f, -5f),
        };

        /// <summary>Rebuilds SCN_Anim and pins the build-settings order.</summary>
        [MenuItem("ZFrame/Setup/Generate SCN_Anim")]
        public static void Setup()
        {
            // The controller is REGENERATED (delete+create — see AnimRigSetup)
            // before the scene so the animator reference always binds to the
            // artifact this run produced; one batch command = consistent pair.
            AnimRigSetup.Run();
            Directory.CreateDirectory(ScenesDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildContent();
            EditorSceneManager.SaveScene(scene, AnimScenePath);
            ApplyBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AnimSceneSetup] created {AnimScenePath}");
        }

        /// <summary>Batch entry: rebuild the scene, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            Setup();
            EditorApplication.Exit(0);
        }

        private static void BuildContent()
        {
            BuildCamera();
            BuildLight();
            BuildGround();
            Transform player = BuildPlayer();
            BuildRestPoint();
            BuildWaypointMarkers();
            BuildNpcs(player);
        }

        private static void BuildCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            go.transform.position = new Vector3(0f, 7f, -11f);
            go.transform.rotation = Quaternion.Euler(38f, 0f, 0f);

            Camera camera = go.GetComponent<Camera>();
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
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
            ground.transform.localScale = new Vector3(2f, 1f, 2f); // 20 m x 20 m
        }

        private static Transform BuildPlayer()
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.transform.position = PlayerPosition; // static: the look-at target
            return player.transform;
        }

        private static void BuildRestPoint()
        {
            GameObject rest = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rest.name = "RestPoint";
            rest.transform.position = RestPoint;
            rest.transform.localScale = new Vector3(1.6f, 0.4f, 1.6f);
        }

        private static void BuildWaypointMarkers()
        {
            var root = new GameObject("WaypointMarkers");
            for (int i = 0; i < Waypoints.Length; i++)
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = $"WP_{i + 1}";
                marker.transform.position = new Vector3(Waypoints[i].x, 0.1f, Waypoints[i].z);
                marker.transform.localScale = new Vector3(0.8f, 0.1f, 0.8f);
                marker.transform.SetParent(root.transform, false);
            }
        }

        private static void BuildNpcs(Transform player)
        {
            var root = new GameObject("AnimNPCs");
            BuildNpc(root.transform, "NPC_A", "npc-anim-a", NpcUtilityView.NpcBias.Restful, 0.95f,
                waypoints: null, player: player);
            BuildNpc(root.transform, "NPC_C", "npc-anim-c", NpcUtilityView.NpcBias.Wayfarer, 0f,
                waypoints: Waypoints, player: player);
        }

        private static void BuildNpc(
            Transform parent,
            string objectName,
            string npcId,
            NpcUtilityView.NpcBias bias,
            float startFatigue,
            Vector3[] waypoints,
            Transform player)
        {
            // The view shell lives on an UNSCALED parent (NpcUtilityView maps
            // the agent's plane position onto its transform and drops a label
            // at local +1.4 m); the skinned model is a measured child below.
            var shell = new GameObject(objectName);
            shell.transform.position = Vector3.zero; // same start point for both NPCs
            shell.transform.SetParent(parent, false);

            // TASK-A-ART-01: the body is the Mixamo Vanguard humanoid. Scale is
            // measured from the imported bounds and the feet settle on y=0
            // (HumanoidCharacterSetup.AttachHumanoid — the same helper the
            // SCN_Game NPC uses). The Kenney-named controller stays: its clips
            // are Humanoid, so they retarget onto the Mixamo avatar by muscle
            // space and the NpcAnimatorDriver / state-machine contract is
            // untouched (NpcAnimatorPlayModeTests pins the controller name).
            GameObject model = HumanoidCharacterSetup.AttachHumanoid(
                shell.transform, CharacterHeightMeters, disableShellRenderer: false);
            var animator = model.GetComponent<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null)
            {
                throw new System.InvalidOperationException(
                    $"[AnimSceneSetup] controller missing at {ControllerPath} — run AnimRigSetup first.");
            }

            animator.runtimeAnimatorController = controller;
            // Binding a controller rebinds the rig — flip the visual forward
            // AFTER it (the driver treats transform.forward as the movement
            // direction; the Vanguard body natively faces −Z).
            HumanoidCharacterSetup.AlignVisualForward(model);
            animator.applyRootMotion = false; // the View owns the transform — clips are in-place loops
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // batch/PlayMode tests have no culling surprises

            var view = shell.AddComponent<NpcUtilityView>();
            SetString(view, "_npcId", npcId);
            SetReference(view, "_player", player);
            SetVector3(view, "_restPoint", RestPoint);
            SetVector3Array(view, "_waypoints", waypoints ?? new Vector3[0]);
            SetTransformArray(view, "_drops", new Transform[0]);
            SetFloat(view, "_startFatigue", startFatigue);
            SetFloat(view, "_fatiguePerSecond", 0.02f);
            SetEnum(view, "_bias", (int)bias);
            SetReference(view, "_renderer", model.GetComponentInChildren<Renderer>());

            // The driver MUST sit on the same GameObject as the Animator —
            // OnAnimatorIK is only invoked there.
            var driver = model.AddComponent<NpcAnimatorDriver>();
            SetReference(driver, "_npcView", view);
            SetReference(driver, "_animator", animator);
            SetReference(driver, "_lookTarget", player);
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

            enabled[AnimScenePath] = true;

            var ordered = new List<EditorBuildSettingsScene>();
            foreach (string path in new[] { BootScenePath, EmptyScenePath, GameScenePath, UtilityScenePath, AnimScenePath })
            {
                if (enabled.TryGetValue(path, out bool isEnabled) && File.Exists(path))
                {
                    ordered.Add(new EditorBuildSettingsScene(path, isEnabled));
                    enabled.Remove(path);
                }
            }

            // Anything else keeps its relative order after ours.
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (!string.IsNullOrEmpty(scene.path) && enabled.Remove(scene.path))
                {
                    ordered.Add(scene);
                }
            }

            EditorBuildSettings.scenes = ordered.ToArray();
        }

        private static SerializedObject Open(Component target, string field)
        {
            var serialized = new SerializedObject(target);
            if (serialized.FindProperty(field) == null)
            {
                throw new System.InvalidOperationException(
                    $"[AnimSceneSetup] {target.GetType().Name} has no serialized field '{field}'.");
            }

            return serialized;
        }

        private static void SetReference(Component target, string field, Object value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetString(Component target, string field, string value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(Component target, string field, float value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetEnum(Component target, string field, int value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).enumValueIndex = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetVector3(Component target, string field, Vector3 value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).vector3Value = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetVector3Array(Component target, string field, Vector3[] values)
        {
            SerializedProperty property = Open(target, field).FindProperty(field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).vector3Value = values[i];
            }

            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetTransformArray(Component target, string field, Transform[] values)
        {
            SerializedProperty property = Open(target, field).FindProperty(field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
