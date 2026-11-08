#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Game.Domain.AI.Utility;
using Game.View.Npc;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot generator for the utility-AI slice scene
    /// <c>Assets/Scenes/SCN_Utility.unity</c> (TASK-A-TECH-01/04): ground, a
    /// static player, a rest point, three drops, four waypoint markers, a
    /// TOOLBOX (tool source for the GOAP repair chain) and a BROKEN DEVICE
    /// (repair target), plus FIVE utility NPCs that all spawn at the SAME
    /// point (0, 1, 0) with the SAME start state — they differ ONLY by their
    /// CFG_NpcPersonality row (balanced / sociable / curious / brave /
    /// lethargic), so identical worlds plus identical brains still produce
    /// pairwise-different behaviour purely from the personality weights.
    /// Idempotent by the GameSceneSetup convention: the scene is rebuilt from
    /// scratch on every run, so the committed artifact always matches this
    /// script. Build settings keep [SCN_Boot, SCN_Empty, SCN_Game] in front
    /// and append SCN_Utility as the fourth entry.
    ///
    /// Run via menu (ZFrame/Setup/Generate SCN_Utility) or batch:
    ///   -executeMethod Game.View.Editor.UtilitySceneSetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as
    /// GameSceneSetup). Requires Assets/Configs/CFG_NpcPersonality.asset
    /// (generate first via NpcPersonalityConfigSetup).
    /// </summary>
    public static class UtilitySceneSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string UtilityScenePath = ScenesDir + "/SCN_Utility.unity";
        private const string BootScenePath = ScenesDir + "/SCN_Boot.unity";
        private const string EmptyScenePath = ScenesDir + "/SCN_Empty.unity";
        private const string GameScenePath = ScenesDir + "/SCN_Game.unity";
        private const string PersonalityTablePath = "Assets/Configs/CFG_NpcPersonality.asset";

        private static readonly Vector3 SpawnPoint = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 RestPoint = new Vector3(-6f, 0.2f, -4f);
        private static readonly Vector3 PlayerPosition = new Vector3(7f, 1f, 4.5f);
        private static readonly Vector3 ToolboxPosition = new Vector3(-3f, 0.35f, 3f);
        private static readonly Vector3 DevicePosition = new Vector3(4f, 0.3f, -3f);
        private static readonly Vector3[] Waypoints =
        {
            new Vector3(6f, 0f, -6f),
            new Vector3(6f, 0f, 6f),
            new Vector3(-6f, 0f, 6f),
            new Vector3(-6f, 0f, -6f),
        };
        private static readonly Vector3[] DropPositions =
        {
            new Vector3(3f, 0.25f, 3f),
            new Vector3(4f, 0.25f, 3.5f),
            new Vector3(3.5f, 0.25f, 2.5f),
        };

        /// <summary>The five demo personalities — same order as CFG_NpcPersonality (assertion anchors).</summary>
        private static readonly string[] PersonalityIds =
        {
            "balanced",
            "sociable",
            "curious",
            "brave",
            "lethargic",
        };

        /// <summary>Rebuilds SCN_Utility and pins the build-settings order.</summary>
        [MenuItem("ZFrame/Setup/Generate SCN_Utility")]
        public static void Setup()
        {
            NpcPersonalityTable personalityTable =
                AssetDatabase.LoadAssetAtPath<NpcPersonalityTable>(PersonalityTablePath);
            if (personalityTable == null)
            {
                throw new System.InvalidOperationException(
                    $"[UtilitySceneSetup] {PersonalityTablePath} missing — run "
                    + "Game.View.Editor.NpcPersonalityConfigSetup.SetupFromCommandLine first.");
            }

            Directory.CreateDirectory(ScenesDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildContent(personalityTable);
            EditorSceneManager.SaveScene(scene, UtilityScenePath);
            ApplyBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UtilitySceneSetup] created {UtilityScenePath}");
        }

        /// <summary>Batch entry: rebuild the scene, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            Setup();
            EditorApplication.Exit(0);
        }

        private static void BuildContent(NpcPersonalityTable personalityTable)
        {
            BuildCamera();
            BuildLight();
            BuildGround();
            Transform player = BuildPlayer();
            BuildRestPoint();
            Transform toolbox = BuildToolbox();
            Transform device = BuildBrokenDevice();
            Transform[] drops = BuildDrops();
            BuildWaypointMarkers();
            BuildNpcs(personalityTable, player, toolbox, device, drops);
        }

        private static void BuildCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            go.transform.position = new Vector3(0f, 13f, -9.5f);
            go.transform.rotation = Quaternion.Euler(52f, 0f, 0f);

            Camera camera = go.GetComponent<Camera>();
            camera.fieldOfView = 60f;
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
            player.transform.position = PlayerPosition; // static: the follow target
            return player.transform;
        }

        private static void BuildRestPoint()
        {
            GameObject rest = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rest.name = "RestPoint";
            rest.transform.position = RestPoint;
            rest.transform.localScale = new Vector3(1.6f, 0.4f, 1.6f);
        }

        private static Transform BuildToolbox()
        {
            // GOAP 修理链第一步的目标：取工具（TASK-A-TECH-04）。
            GameObject toolbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
            toolbox.name = "Toolbox";
            toolbox.transform.position = ToolboxPosition;
            toolbox.transform.localScale = new Vector3(0.7f, 0.7f, 0.7f);
            return toolbox.transform;
        }

        private static Transform BuildBrokenDevice()
        {
            // GOAP 修理链第二步的目标：损坏装置（修复它 = 全局事实翻转）。
            GameObject device = GameObject.CreatePrimitive(PrimitiveType.Cube);
            device.name = "BrokenDevice";
            device.transform.position = DevicePosition;
            device.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            device.GetComponent<Renderer>().material.color = new Color(0.85f, 0.2f, 0.15f);
            return device.transform;
        }

        private static Transform[] BuildDrops()
        {
            var root = new GameObject("UtilityDrops");
            var drops = new Transform[DropPositions.Length];
            for (int i = 0; i < DropPositions.Length; i++)
            {
                GameObject drop = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                drop.name = $"Drop_{i + 1}";
                drop.transform.position = DropPositions[i];
                drop.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
                drop.transform.SetParent(root.transform, false);
                drops[i] = drop.transform;
            }

            return drops;
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

        private static void BuildNpcs(
            NpcPersonalityTable personalityTable,
            Transform player,
            Transform toolbox,
            Transform device,
            Transform[] drops)
        {
            var root = new GameObject("UtilityNPCs");
            // 五个 NPC：同起点、同起始状态、同世界——唯一差异是 CFG_NpcPersonality 行。
            for (int i = 0; i < PersonalityIds.Length; i++)
            {
                char suffix = (char)('A' + i);
                BuildNpc(root.transform, $"NPC_{suffix}", PersonalityIds[i], personalityTable, 0f,
                    player, toolbox, device, drops);
            }
        }

        private static void BuildNpc(
            Transform parent,
            string npcId,
            string personalityId,
            NpcPersonalityTable personalityTable,
            float startFatigue,
            Transform player,
            Transform toolbox,
            Transform device,
            Transform[] drops)
        {
            GameObject npc = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            npc.name = npcId;
            npc.transform.position = SpawnPoint; // the SAME start point for all five
            npc.transform.localScale = new Vector3(0.9f, 0.95f, 0.9f);
            npc.transform.SetParent(parent, false);

            var view = npc.AddComponent<NpcUtilityView>();
            // Stable NPC id uses the hyphen form ("npc-a") — the same identity the
            // serialized default and the PlayMode assertions key on; GameObject
            // names keep the readable underscore caps ("NPC_A").
            SetString(view, "_npcId", npcId.ToLowerInvariant().Replace('_', '-'));
            SetReference(view, "_player", player);
            SetVector3(view, "_restPoint", RestPoint);
            SetVector3Array(view, "_waypoints", Waypoints);
            SetTransformArray(view, "_drops", drops);
            SetReference(view, "_toolbox", toolbox);
            SetReference(view, "_device", device);
            SetFloat(view, "_startFatigue", startFatigue);
            SetFloat(view, "_fatiguePerSecond", 0.02f);
            SetEnum(view, "_bias", (int)NpcUtilityView.NpcBias.Balanced);
            SetReference(view, "_personalityTable", personalityTable);
            SetString(view, "_personalityId", personalityId);
            SetReference(view, "_renderer", npc.GetComponent<Renderer>());
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

            enabled[UtilityScenePath] = true;

            var ordered = new List<EditorBuildSettingsScene>();
            foreach (string path in new[] { BootScenePath, EmptyScenePath, GameScenePath, UtilityScenePath })
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
                    $"[UtilitySceneSetup] {target.GetType().Name} has no serialized field '{field}'.");
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
