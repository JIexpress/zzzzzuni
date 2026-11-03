#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.View.Enemy;
using Game.View.Interaction;
using Game.View.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot generator for the M4 interaction slice scene
    /// <c>Assets/Scenes/SCN_Game.unity</c> (TASK-A-M4-01): ground, player
    /// capsule with a <see cref="PlayerMotor"/>, a door / a pickup / an NPC
    /// carrying <see cref="InteractableProxy"/>, one patrolling
    /// <see cref="EnemyView"/>, the aim camera and the
    /// <see cref="InteractionScanner"/> + <see cref="InteractionPromptHud"/>
    /// host. Idempotent: the scene is rebuilt from scratch on every run, so the
    /// committed artifact always matches this script.
    ///
    /// TASK-A-R9-10 adds the real-model stage: the four character FBX and six
    /// environment prop FBX from <c>Assets/Art/Models/</c> are placed from
    /// <see cref="ModelStageLayout"/> (ring of characters around the activity
    /// area, props as dressing), rescaled from their measured bounds (the FBX
    /// import leaves Blender's ×100 node scales in place — characters measure
    /// 180-208 m at scale 1), settled on the ground, and given the project's
    /// MAT_ materials. The pickup moved next to the Kenney character so the
    /// R9-06 pickup→spark chain blooms beside a real model.
    ///
    /// Build settings are re-ordered to [SCN_Boot, SCN_Empty, SCN_Game] —
    /// SCN_Game is third, exactly as the brief requires — while any other
    /// entry keeps its relative position.
    ///
    /// Run via menu (ZFrame/Setup/Generate SCN_Game) or batch:
    ///   -executeMethod Game.View.Editor.GameSceneSetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as
    /// ZFrame.View.SceneProjectSetup).
    /// </summary>
    public static class GameSceneSetup
    {
        private const string ScenesDir = "Assets/Scenes";
        private const string BootScenePath = ScenesDir + "/SCN_Boot.unity";
        private const string EmptyScenePath = ScenesDir + "/SCN_Empty.unity";
        private const string GameScenePath = ScenesDir + "/SCN_Game.unity";

        /// <summary>Rebuilds SCN_Game and pins the build-settings order.</summary>
        [MenuItem("ZFrame/Setup/Generate SCN_Game")]
        public static void Setup()
        {
            Directory.CreateDirectory(ScenesDir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildContent();
            EditorSceneManager.SaveScene(scene, GameScenePath);
            ApplyBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GameSceneSetup] created {GameScenePath}; build settings: {DescribeBuildSettings()}");
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
            Transform player = BuildPlayer(camera);
            BuildDoor();
            BuildPickup();
            BuildNpc();
            BuildEnemy(player);
            BuildModelStage();
            BuildScanner(camera, player);
        }

        private static Camera BuildCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            go.transform.position = new Vector3(0f, 3.2f, -4.2f);
            go.transform.rotation = Quaternion.Euler(10f, 0f, 0f);

            var camera = go.GetComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
            return camera;
        }

        private static void BuildLight()
        {
            var go = new GameObject("Directional Light", typeof(Light));
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = go.GetComponent<Light>();
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

        private static Transform BuildPlayer(Camera camera)
        {
            GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.transform.position = new Vector3(0f, 1f, 0f);

            // CharacterController replaces the primitive collider: one collider
            // keeps the aim ray from hitting the player's own body.
            var capsuleCollider = player.GetComponent<CapsuleCollider>();
            if (capsuleCollider != null)
            {
                UnityEngine.Object.DestroyImmediate(capsuleCollider);
            }

            var controller = player.AddComponent<CharacterController>();
            controller.height = 2f;
            controller.radius = 0.5f;
            controller.center = Vector3.zero;

            var motor = player.AddComponent<PlayerMotor>();
            SetReference(motor, "_camera", camera.transform);

            var aim = new GameObject("AimOrigin");
            aim.transform.SetParent(player.transform, false);
            aim.transform.localPosition = new Vector3(0f, 0.4f, 0.3f);

            return player.transform;
        }

        private static void BuildDoor()
        {
            GameObject door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.position = new Vector3(0f, 1f, 3.2f);
            door.transform.localScale = new Vector3(1.4f, 2f, 0.2f);

            var proxy = door.AddComponent<InteractableProxy>();
            SetEnum(proxy, "_kind", (int)InteractableKind.Door);
            SetString(proxy, "_id", "door-main");
            SetString(proxy, "_prompt", "按 E 开门");
            SetString(proxy, "_alternatePrompt", "按 E 关门");
        }

        private static void BuildPickup()
        {
            GameObject pickup = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pickup.name = "Pickup";
            // TASK-A-R9-10: parked beside the Kenney character so the pickup
            // spark blooms next to a real model (ModelStageLayout.PickupPosition).
            pickup.transform.position = ModelStageLayout.PickupPosition;
            pickup.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);

            var proxy = pickup.AddComponent<InteractableProxy>();
            SetEnum(proxy, "_kind", (int)InteractableKind.Pickup);
            SetString(proxy, "_id", "pickup-gold-1");
            SetString(proxy, "_prompt", "按 E 拾取");
            SetString(proxy, "_itemId", "gold");
            SetInt(proxy, "_amount", 3);
        }

        private static void BuildNpc()
        {
            GameObject npc = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            npc.name = "NPC";
            npc.transform.position = new Vector3(-2.2f, 1f, 2.2f);
            npc.transform.localScale = new Vector3(0.9f, 0.95f, 0.9f);

            var proxy = npc.AddComponent<InteractableProxy>();
            SetEnum(proxy, "_kind", (int)InteractableKind.Dialog);
            SetString(proxy, "_id", "npc-elder");
            SetString(proxy, "_prompt", "按 E 对话");
            SetString(proxy, "_speaker", "旅人");
            SetStringArray(proxy, "_lines", new[]
            {
                "旅人，你终于来了。",
                "门后面有你要找的东西。",
                "小心巡逻的守卫。",
            });
        }

        private static void BuildEnemy(Transform player)
        {
            GameObject enemy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            enemy.name = "Enemy";
            enemy.transform.position = new Vector3(3f, 1f, 6f);
            enemy.transform.localScale = new Vector3(0.9f, 0.95f, 0.9f);

            var view = enemy.AddComponent<EnemyView>();
            SetString(view, "_enemyId", "enemy-1");
            SetReference(view, "_player", player);
            SetReference(view, "_renderer", enemy.GetComponent<Renderer>());
            SetVector3Array(view, "_waypoints", new[]
            {
                new Vector3(3f, 1f, 6f),
                new Vector3(3f, 1f, 3f),
                new Vector3(0.5f, 1f, 5f),
            });
        }

        /// <summary>
        /// TASK-A-R9-10: instantiates the ten real models from
        /// <see cref="ModelStageLayout"/> — 4 characters in a ring facing the
        /// centre, 6 environment props as dressing — rescaled from measured
        /// bounds, settled on the ground, materials from
        /// <see cref="ModelAssetSetup.GetOrCreateMaterial"/>.
        /// </summary>
        private static void BuildModelStage()
        {
            foreach (ModelStageLayout.Placement placement in ModelStageLayout.All)
            {
                string path = $"Assets/Art/Models/{placement.Model}.fbx";
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                {
                    Debug.LogError($"[GameSceneSetup] model asset missing: {path} (run ModelAssetSetup / check the import)");
                    continue;
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                instance.name = placement.Model;
                instance.transform.position = placement.Position;
                instance.transform.rotation = Quaternion.Euler(0f,
                    placement.YawDeg + ModelStageLayout.ForwardYawOffset(placement.Model), 0f);

                // The import leaves Blender's ×100 node scales in place (the
                // probe measured characters at 180-208 m at scale 1), so the
                // plan's target size is reached by dividing the MEASURED
                // bounds — never by trusting the file.
                Bounds original = MeasureBounds(instance);
                float originalSize = placement.TargetHeight > 0f
                    ? original.size.y
                    : Mathf.Max(original.size.x, original.size.z);
                float target = placement.TargetHeight > 0f ? placement.TargetHeight : placement.TargetLength;
                float scale = ModelStageLayout.ScaleForHeight(originalSize, target);
                instance.transform.localScale = new Vector3(scale, scale, scale);

                Bounds scaled = MeasureBounds(instance);
                if (placement.SitOnGround)
                {
                    // Settle: bottom rests on y=0; ground-inset props (road)
                    // sink so only ~2 cm of the top pokes above the grass.
                    float offset = placement.SinkTopAboveGround ? 0.02f - scaled.max.y : -scaled.min.y;
                    instance.transform.position += new Vector3(0f, offset, 0f);
                }

                EnsureModelMaterials(instance, placement.Model);
                DisableHigherLods(instance);
                StripStrayStageComponents(instance);
            }
        }

        /// <summary>
        /// Combined world bounds of every renderer under <paramref name="root"/>.
        /// Skinned renderers use their sharedMesh bounds pushed through the
        /// skinning transform — Unity's live skinned AABB is inflated by the
        /// ×100 rig scales and would mis-scale the characters.
        /// </summary>
        private static Bounds MeasureBounds(GameObject root)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var skinned = renderer as SkinnedMeshRenderer;
                Bounds part = skinned != null && skinned.sharedMesh != null
                    ? TransformBounds(skinned.sharedMesh.bounds, skinned.localToWorldMatrix)
                    : renderer.bounds;
                if (!any)
                {
                    bounds = part;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(part);
                }
            }

            return bounds;
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var extents = bounds.extents;
            var axes = new[]
            {
                matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f)),
                matrix.MultiplyVector(new Vector3(0f, extents.y, 0f)),
                matrix.MultiplyVector(new Vector3(0f, 0f, extents.z)),
            };

            var result = new Bounds(center, Vector3.zero);
            foreach (Vector3 axis in axes)
            {
                result.Encapsulate(center + axis);
                result.Encapsulate(center - axis);
            }

            return result;
        }

        /// <summary>
        /// Authoritative material pass: every renderer slot of a placed model
        /// gets the model's single MAT_ material (the project-owned URP Lit
        /// asset from <see cref="ModelAssetSetup.GetOrCreateMaterial"/>).
        ///
        /// Why always, not only on null/error: the FBX remap table is set
        /// (ModelAssetSetup), but Unity's external-material resolution still
        /// falls back to the FBX's own embedded materials (they carry the
        /// author colours — the 2026-10-31 diagnose proved it), so the only
        /// deterministic statement "this model renders with MAT_x, never
        /// pink" is the explicit assignment here. Idempotent by construction.
        /// </summary>
        private static void EnsureModelMaterials(GameObject instance, string model)
        {
            Material material = ModelAssetSetup.GetOrCreateMaterial(model);
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (!ReferenceEquals(materials[i], material))
                    {
                        materials[i] = material;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        /// <summary>
        /// The ENV_* FBX ship LOD0/LOD1/LOD2 as three sibling renderers (no
        /// LODGroup was built at import); all three would overlap and
        /// z-fight. Keep LOD0, switch the higher levels off.
        /// </summary>
        private static void DisableHigherLods(GameObject instance)
        {
            foreach (Transform node in instance.GetComponentsInChildren<Transform>(true))
            {
                if (node.name.EndsWith("_LOD1", StringComparison.Ordinal)
                    || node.name.EndsWith("_LOD2", StringComparison.Ordinal))
                {
                    node.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Strips anything an FBX must never bring into a scene: cameras,
        /// lights and audio listeners (belt and suspenders — the import flags
        /// are off since the probe found Blender Camera/Lamp nodes in two
        /// characters, but a re-import with flags on must not leak renderers).
        /// </summary>
        private static void StripStrayStageComponents(GameObject instance)
        {
            foreach (Camera camera in instance.GetComponentsInChildren<Camera>(true))
            {
                UnityEngine.Object.DestroyImmediate(camera);
            }

            foreach (Light light in instance.GetComponentsInChildren<Light>(true))
            {
                UnityEngine.Object.DestroyImmediate(light);
            }

            foreach (AudioListener listener in instance.GetComponentsInChildren<AudioListener>(true))
            {
                UnityEngine.Object.DestroyImmediate(listener);
            }
        }

        private static void BuildScanner(Camera camera, Transform player)
        {
            var go = new GameObject("InteractionSystem");
            var scanner = go.AddComponent<InteractionScanner>();
            SetReference(scanner, "_camera", camera);
            SetReference(scanner, "_rayOrigin", player.Find("AimOrigin"));
            SetFloat(scanner, "_maxDistance", 2f);

            go.AddComponent<InteractionPromptHud>();
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

            enabled[GameScenePath] = true;

            var ordered = new List<EditorBuildSettingsScene>();
            foreach (string path in new[] { BootScenePath, EmptyScenePath, GameScenePath })
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

        private static string DescribeBuildSettings()
        {
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                paths.Add($"[{paths.Count}] {scene.path}");
            }

            return string.Join(", ", paths);
        }

        private static SerializedObject Open(Component target, string field)
        {
            var serialized = new SerializedObject(target);
            if (serialized.FindProperty(field) == null)
            {
                throw new System.InvalidOperationException(
                    $"[GameSceneSetup] {target.GetType().Name} has no serialized field '{field}'.");
            }

            return serialized;
        }

        private static void SetReference(Component target, string field, UnityEngine.Object value)
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

        private static void SetInt(Component target, string field, int value)
        {
            var serialized = Open(target, field);
            serialized.FindProperty(field).intValue = value;
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

        private static void SetStringArray(Component target, string field, string[] values)
        {
            var serialized = Open(target, field);
            SerializedProperty property = serialized.FindProperty(field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).stringValue = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetVector3Array(Component target, string field, Vector3[] values)
        {
            var serialized = Open(target, field);
            SerializedProperty property = serialized.FindProperty(field);
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).vector3Value = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
