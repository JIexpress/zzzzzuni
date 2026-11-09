#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.View.Animation;
using Game.View.Interaction;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// TASK-A-ART-01: imports the Mixamo "Vanguard" humanoid
    /// (<c>Assets/Art/Models/Humanoid/CHR_Soldier.fbx</c>) as a Humanoid rig
    /// with its own Idle/Walk/Run takes, generates a locomotion controller from
    /// those takes (same parameters/states as the Kenney plan, so the
    /// <see cref="NpcAnimatorDriver"/> contract is unchanged) and patches the
    /// NPC body in <c>SCN_Game</c> without rebuilding the scene (the committed
    /// scene also carries content added after generation — A-CONT-01's grass
    /// ground material — which a rebuild would silently drop).
    ///
    /// Why a generated setup instead of a hand-written .meta: the importer is
    /// the only supported way to create the humanoid Avatar sub-asset and to
    /// pin per-clip loop flags, and this project's convention is idempotent
    /// batch generation (see AnimRigSetup / ModelAssetSetup).
    ///
    /// Scale: the FBX is measured once at scale 1 and every placed instance is
    /// rescaled from the MEASURED bounds (ModelStageLayout.ScaleForHeight), so
    /// the import unit (metres vs Blender's ×100) never leaks into the scene.
    /// Feet settle at world y = 0.
    ///
    /// Material: the FBX's embedded Vanguard materials import as URP/Lit (no
    /// texture maps ship with the Blender export, but the shaders are valid), so
    /// they are kept — the brief's "material first" rule. Any slot whose shader
    /// is missing or not URP falls back to the project's URP/Lit
    /// <c>MAT_CHR_Human</c> (the "不许粉红" gate).
    ///
    /// Batch entry points:
    ///   -executeMethod Game.View.Editor.HumanoidCharacterSetup.SetupFromCommandLine
    ///   -executeMethod Game.View.Editor.HumanoidCharacterSetup.PatchGameSceneFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard.
    /// </summary>
    public static class HumanoidCharacterSetup
    {
        private const string ModelsRoot = "Assets/Art/Models";
        private const string HumanoidDir = ModelsRoot + "/Humanoid";
        private const string ModelPath = HumanoidDir + "/CHR_Soldier.fbx";
        private const string ControllerPath = HumanoidDir + "/CHR_SoldierLocomotion.controller";
        private const string FallbackMaterialPath = ModelsRoot + "/MAT_CHR_Human.mat";
        private const string BodyMaterialPath = HumanoidDir + "/MAT_CHR_SoldierBody.mat";
        private const string VisorMaterialPath = HumanoidDir + "/MAT_CHR_SoldierVisor.mat";
        private const string GameScenePath = "Assets/Scenes/SCN_Game.unity";
        private const string ReportPath = "TempLogs/humanoid_setup.txt";
        private const string MaterialReportPath = "TempLogs/soldier_materials.txt";

        /// <summary>Height of the SCN_Game NPC body (metres, the capsule's measured 1.9).</summary>
        public const float NpcHeightMeters = 1.9f;

        /// <summary>Height used by the SCN_Anim utility NPCs (matches AnimSceneSetup).</summary>
        public const float AnimNpcHeightMeters = 1.75f;

        private static readonly string[] ClipStates = { "Idle", "Walk", "Run" };

        [MenuItem("ZFrame/Setup/Generate Humanoid Character")]
        public static void Setup()
        {
            string report = Run();
            WriteReport(report);
        }

        /// <summary>Batch entry: import + controller, then exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            string report = Run();
            WriteReport(report);
            EditorApplication.Exit(0);
        }

        /// <summary>Batch entry: swap the SCN_Game NPC body to the humanoid, then exit 0.</summary>
        public static void PatchGameSceneFromCommandLine()
        {
            string report;
            int exit = 0;
            try
            {
                report = PatchGameScene();
            }
            catch (Exception error)
            {
                report = $"[HumanoidCharacterSetup] PATCH FAILED:\n{error}";
                exit = 1;
            }

            WriteReport(report);
            Debug.Log(report);
            EditorApplication.Exit(exit);
        }

        /// <summary>
        /// Batch entry: creates/refreshes the two soldier materials and rebinds
        /// them on the SCN_Game NPC's SkinnedMeshRenderers, then exits.
        /// </summary>
        public static void ApplySoldierMaterialsFromCommandLine()
        {
            string report;
            int exit = 0;
            try
            {
                report = ApplySoldierMaterials();
            }
            catch (Exception error)
            {
                report = $"[HumanoidCharacterSetup] MATERIALS FAILED:\n{error}";
                exit = 1;
            }

            WriteMaterialReport(report);
            Debug.Log(report);
            EditorApplication.Exit(exit);
        }

        /// <summary>
        /// TASK-A-ART-01 follow-up: the Blender FBX shipped no texture maps, so
        /// the embedded Vanguard materials render as flat 0.91 grey-white. This
        /// replaces them with two authored URP/Lit materials (armoured blue-grey
        /// body, dark semi-glossy visor) at the renderer level of the SCN_Game
        /// prefab instance — the model, rig, controller and scene structure are
        /// untouched, only the material slots change.
        /// </summary>
        public static string ApplySoldierMaterials()
        {
            var report = new StringBuilder();
            report.AppendLine($"CHR_Soldier materials @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            Material body = EnsureSoldierMaterial(
                BodyMaterialPath, "MAT_CHR_SoldierBody", new Color(0.30f, 0.34f, 0.40f, 1f), 0.55f, 0.55f);
            Material visor = EnsureSoldierMaterial(
                VisorMaterialPath, "MAT_CHR_SoldierVisor", new Color(0.08f, 0.10f, 0.14f, 1f), 0.80f, 0.85f);
            report.AppendLine($"=== body : {DescribeMaterial(body)}");
            report.AppendLine($"=== visor: {DescribeMaterial(visor)}");

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
            GameObject npc = GameObject.Find("NPC");
            if (npc == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] SCN_Game has no 'NPC' object.");
            }

            Transform model = npc.transform.Find("CHR_Soldier");
            if (model == null)
            {
                throw new InvalidOperationException(
                    "[HumanoidCharacterSetup] NPC has no 'CHR_Soldier' child — run PatchGameScene first.");
            }

            int bound = 0;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                Material target = renderer.name == "vanguard_Mesh" ? body
                    : renderer.name == "vanguard_visor" ? visor
                    : null;
                if (target == null)
                {
                    continue;
                }

                renderer.sharedMaterials = new[] { target };
                // Batch mode has no Undo stack; without this the override is
                // dropped the next time the prefab instance is serialized.
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                report.AppendLine($"=== '{renderer.name}' -> {target.name}");
                bound++;
            }

            if (bound != 2)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] expected 2 soldier renderers, bound {bound}.");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, GameScenePath);
            report.AppendLine("=== saved SCN_Game");
            return report.ToString();
        }

        /// <summary>
        /// Loads (or creates) a URP/Lit material and pins the scalar/colour
        /// inputs. Every texture slot is explicitly cleared: the FBX has no
        /// texture maps, and a dangling reference would reintroduce the pink /
        /// missing-texture path the brief forbids.
        /// </summary>
        private static Material EnsureSoldierMaterial(
            string path, string name, Color baseColor, float metallic, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] URP/Lit shader not found.");
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            material.SetColor("_BaseColor", baseColor);
            material.SetColor("_Color", baseColor);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Glossiness", smoothness);
            foreach (string slot in new[]
                     {
                         "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_SpecGlossMap",
                         "_OcclusionMap", "_EmissionMap", "_DetailMask", "_DetailAlbedoMap",
                         "_DetailNormalMap", "_ParallaxMap",
                     })
            {
                material.SetTexture(slot, null);
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static string DescribeMaterial(Material material)
        {
            return $"{material.name}({material.shader?.name}) " +
                   $"base={material.GetColor("_BaseColor").ToString("F2")} " +
                   $"metallic={material.GetFloat("_Metallic"):F2} smoothness={material.GetFloat("_Smoothness"):F2} " +
                   $"baseMap={(material.GetTexture("_BaseMap") == null ? "none" : "SET")}";
        }

        private static void WriteMaterialReport(string text)
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", MaterialReportPath));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }

        /// <summary>Diagnostic entry: prints the humanoid pose/orientation in both scenes.</summary>
        public static void ProbeScenesFromCommandLine()
        {
            var report = new StringBuilder();
            report.AppendLine($"Humanoid scene probe @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (string path in new[] { "Assets/Scenes/SCN_Anim.unity", GameScenePath })
            {
                UnityEngine.SceneManagement.Scene scene =
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                report.AppendLine($"== {path}");
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (node.name != "Character" && node.name != "CHR_Soldier")
                        {
                            continue;
                        }

                        report.AppendLine($"  '{node.name}' localEuler={node.localEulerAngles:F1} " +
                                          $"worldEuler={node.eulerAngles:F1} parent='{node.parent?.name}'");
                    }

                    foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null
                            && skinned.sharedMesh.name.StartsWith("vanguard", StringComparison.Ordinal))
                        {
                            report.AppendLine($"  SKIN '{skinned.name}' worldSize={skinned.bounds.size:F3} " +
                                              $"center={skinned.bounds.center:F3}");
                        }
                    }
                }
            }

            WriteReport(report.ToString());
            EditorApplication.Exit(0);
        }

        /// <summary>Diagnostic entry: captures SCN_Anim from its own camera.</summary>
        public static void CaptureAnimSceneFromCommandLine()
        {
            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.OpenScene("Assets/Scenes/SCN_Anim.unity", OpenSceneMode.Single);
            Camera camera = Camera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] SCN_Anim has no main camera.");
            }

            const int width = 1280;
            const int height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = camera.targetTexture;
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            camera.targetTexture = previous;

            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "art01_scn_anim.png"));
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            Debug.Log($"[HumanoidCharacterSetup] captured {path}");
            EditorApplication.Exit(0);
        }

        // ---- 1. import settings ------------------------------------------------

        /// <summary>Imports/verifies the FBX; returns the import report.</summary>
        public static string Run()
        {
            AssetDatabase.Refresh();
            EnsureFolder(HumanoidDir);
            if (!File.Exists(ModelPath))
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] missing model at {ModelPath} — copy the Blender export first.");
            }

            var report = new StringBuilder();
            report.AppendLine($"Humanoid character setup @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"unity: {Application.unityVersion}");

            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null)
            {
                throw new InvalidOperationException($"[HumanoidCharacterSetup] no ModelImporter at {ModelPath}");
            }

            report.AppendLine(ConfigureImporter(importer));

            Avatar avatar = FindAvatar(ModelPath);
            if (avatar == null)
            {
                throw new InvalidOperationException(
                    "[HumanoidCharacterSetup] humanoid import produced NO avatar — the rig mapping failed.");
            }

            if (!avatar.isHuman || !avatar.isValid)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] avatar '{avatar.name}' isHuman={avatar.isHuman} isValid={avatar.isValid} " +
                    "— Humanoid retargeting / IK would not work.");
            }

            report.AppendLine($"=== avatar: '{avatar.name}' isHuman={avatar.isHuman} isValid={avatar.isValid}");

            Dictionary<string, AnimationClip> clips = VerifyClips(report);
            EnsureController(ControllerPath, clips, report);
            report.AppendLine(DescribeModelSize());
            report.AppendLine(DescribeMaterials(importer));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report.ToString();
        }

        private static string ConfigureImporter(ModelImporter importer)
        {
            var report = new StringBuilder();
            bool changed = false;

            if (importer.animationType != ModelImporterAnimationType.Human)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                changed = true;
            }

            if (!importer.importAnimation)
            {
                importer.importAnimation = true;
                changed = true;
            }

            if (importer.optimizeGameObjects)
            {
                // NpcAnimatorDriver's IK reads real bone transforms
                // (GetBoneTransform(Head/LeftFoot)); optimized hierarchies hide them.
                importer.optimizeGameObjects = false;
                changed = true;
            }

            if (importer.importCameras || importer.importLights)
            {
                importer.importCameras = false;
                importer.importLights = false;
                changed = true;
            }

            if (importer.materialLocation != ModelImporterMaterialLocation.InPrefab)
            {
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                changed = true;
            }

            if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                changed = true;
            }

            // Clip plan: only the three locomotion takes, clean state names,
            // looping. Blender's NLA export can emit duplicate takes
            // ("Character|Idle" + "Idle.001"); the undotted one wins.
            ModelImporterClipAnimation[] defaults = importer.defaultClipAnimations;
            report.AppendLine($"=== imported takes: {DescribeClips(defaults)}");

            var picked = new Dictionary<string, ModelImporterClipAnimation>();
            var fallbackPicks = new Dictionary<string, ModelImporterClipAnimation>();
            foreach (ModelImporterClipAnimation clip in defaults)
            {
                string state = StateOf(clip.name);
                if (state == null)
                {
                    continue;
                }

                ModelImporterClipAnimation copy = clip;
                copy.name = state;
                copy.loopTime = true;
                copy.loopPose = true;
                copy.lockRootRotation = false;
                copy.lockRootHeightY = false;
                copy.lockRootPositionXZ = false;

                Dictionary<string, ModelImporterClipAnimation> target =
                    clip.name.IndexOf('.') >= 0 ? fallbackPicks : picked;
                if (!target.ContainsKey(state))
                {
                    target[state] = copy;
                }
            }

            foreach (KeyValuePair<string, ModelImporterClipAnimation> entry in fallbackPicks)
            {
                if (!picked.ContainsKey(entry.Key))
                {
                    picked[entry.Key] = entry.Value;
                }
            }

            if (picked.Count != ClipStates.Length)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] expected Idle/Walk/Run takes, found {picked.Count} " +
                    $"(takes: {DescribeClips(defaults)}). Extend the setup rather than guessing.");
            }

            var ordered = new ModelImporterClipAnimation[ClipStates.Length];
            for (int i = 0; i < ClipStates.Length; i++)
            {
                ordered[i] = picked[ClipStates[i]];
            }

            if (!SameClipPlan(importer.clipAnimations, ordered))
            {
                importer.clipAnimations = ordered;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }

            report.AppendLine($"=== importer: animationType=Human importAnimation=true optimizeGameObjects=false " +
                              $"importCameras/Lights=false clips=[{string.Join(", ", ClipStates)}] loop=true" +
                              (changed ? " (reimported)" : " (unchanged)"));
            return report.ToString();
        }

        private static bool SameClipPlan(ModelImporterClipAnimation[] actual, ModelImporterClipAnimation[] wanted)
        {
            if (actual == null || actual.Length != wanted.Length)
            {
                return false;
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i].name != wanted[i].name || actual[i].loopTime != wanted[i].loopTime)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribeClips(ModelImporterClipAnimation[] clips)
        {
            var parts = new List<string>();
            foreach (ModelImporterClipAnimation clip in clips)
            {
                parts.Add($"'{clip.name}'({clip.firstFrame:F0}-{clip.lastFrame:F0}, loop={clip.loopTime})");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "<none>";
        }

        /// <summary>"Character|Idle" → "Idle"; returns null for non-locomotion takes.</summary>
        private static string StateOf(string takeName)
        {
            string tail = takeName ?? string.Empty;
            int bar = tail.LastIndexOf('|');
            if (bar >= 0)
            {
                tail = tail.Substring(bar + 1);
            }

            foreach (string state in ClipStates)
            {
                if (tail.StartsWith(state, StringComparison.OrdinalIgnoreCase))
                {
                    return state;
                }
            }

            return null;
        }

        private static Dictionary<string, AnimationClip> VerifyClips(StringBuilder report)
        {
            var clips = new Dictionary<string, AnimationClip>();
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                if (asset is AnimationClip clip && Array.IndexOf(ClipStates, clip.name) >= 0)
                {
                    clips[clip.name] = clip;
                }
            }

            foreach (string state in ClipStates)
            {
                if (!clips.TryGetValue(state, out AnimationClip clip))
                {
                    throw new InvalidOperationException($"[HumanoidCharacterSetup] clip '{state}' is missing after import.");
                }

                if (clip.length <= 0f)
                {
                    throw new InvalidOperationException($"[HumanoidCharacterSetup] clip '{state}' has zero length.");
                }

                if (!clip.isLooping)
                {
                    throw new InvalidOperationException($"[HumanoidCharacterSetup] clip '{state}' did not keep its loop flag.");
                }
            }

            report.AppendLine($"=== clips: {string.Join(", ", Array.ConvertAll(ClipStates, s => $"'{s}' {clips[s].length:F3}s"))}");
            return clips;
        }

        private static string DescribeModelSize()
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (asset == null)
            {
                return "=== size: model asset missing";
            }

            GameObject instance = UnityEngine.Object.Instantiate(asset);
            try
            {
                StripStrayRenderers(instance);
                Bounds raw = MeasureBounds(instance);
                float scaleNpc = ModelStageLayout.ScaleForHeight(raw.size.y, NpcHeightMeters);
                float scaleAnim = ModelStageLayout.ScaleForHeight(raw.size.y, AnimNpcHeightMeters);
                var lines = new StringBuilder();
                lines.AppendLine($"=== size at scale 1: ({raw.size.x:F3}, {raw.size.y:F3}, {raw.size.z:F3}) " +
                                 $"minY={raw.min.y:F3} maxY={raw.max.y:F3}");
                lines.AppendLine($"=== scale: SCN_Game NPC target {NpcHeightMeters:F2} m -> {scaleNpc:F6}; " +
                                 $"SCN_Anim target {AnimNpcHeightMeters:F2} m -> {scaleAnim:F6}");
                lines.AppendLine("=== hierarchy / renderers (scale-1 probe):");
                DescribeNode(instance.transform, 0, lines);
                lines.AppendLine($"=== materials: {DescribeMaterialAssignments(instance)}");
                return lines.ToString().TrimEnd('\n');
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static string DescribeMaterials(ModelImporter importer)
        {
            var names = new List<string>();
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath))
            {
                if (asset is Material material)
                {
                    names.Add($"'{material.name}'({(material.shader != null ? material.shader.name : "no-shader")})");
                }
            }

            string embedded = names.Count > 0 ? string.Join(", ", names) : "<none>";
            return $"=== embedded materials: {embedded}\n" +
                   $"=== material decision: keep the model's own URP/Lit materials; " +
                   $"per-slot fallback to {FallbackMaterialPath} only where the shader is missing or not URP.";
        }

        private static void DescribeNode(Transform node, int depth, StringBuilder lines)
        {
            if (depth <= 3)
            {
                lines.AppendLine($"{new string(' ', 2 + depth * 2)}node '{node.name}' localScale={node.localScale:F4} " +
                                 $"lossyScale={node.lossyScale:F4} pos={node.localPosition:F3}");
                for (int i = 0; i < node.childCount; i++)
                {
                    DescribeNode(node.GetChild(i), depth + 1, lines);
                }
            }

            var skinned = node.GetComponent<SkinnedMeshRenderer>();
            var mesh = node.GetComponent<MeshRenderer>();
            if (skinned != null)
            {
                lines.AppendLine($"{new string(' ', 2 + depth * 2)}SKIN '{node.name}' mesh='{skinned.sharedMesh?.name}' " +
                                 $"meshBounds={skinned.sharedMesh?.bounds.size:F3} " +
                                 $"worldSize={skinned.bounds.size:F3} worldCenter={skinned.bounds.center:F3} " +
                                 $"mats=[{DescribeRendererMaterials(skinned)}]");
            }

            if (mesh != null && mesh.GetComponent<MeshFilter>() != null)
            {
                lines.AppendLine($"{new string(' ', 2 + depth * 2)}MESH '{node.name}' " +
                                 $"worldSize={mesh.bounds.size:F3} worldCenter={mesh.bounds.center:F3} " +
                                 $"mats=[{DescribeRendererMaterials(mesh)}]");
            }
        }

        private static string DescribeRendererMaterials(Renderer renderer)
        {
            var names = new List<string>();
            foreach (Material material in renderer.sharedMaterials)
            {
                names.Add(material == null ? "NULL" : $"{material.name}({material.shader?.name})");
            }

            return string.Join(", ", names);
        }

        // ---- 2. controller -----------------------------------------------------

        /// <summary>
        /// Generates the Soldier locomotion controller from its OWN clips.
        /// Same parameters (Speed/Grounded/Jump), states (Idle/Run/Jump) and six
        /// transitions as <see cref="KenneyLocomotionPlan"/>, so the
        /// brain→animator contract is identical. The source has no Jump take, so
        /// the Jump state reuses Idle (the SCN_Game NPC never jumps; the
        /// driver-driven NPCs keep the Kenney controller with a real Jump clip).
        /// </summary>
        private static void EnsureController(string assetPath, Dictionary<string, AnimationClip> clips, StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null)
            {
                // 2022.3 loses transitions when a controller is rebuilt in place
                // (AnimRigSetup's verified pitfall) — delete + create.
                AssetDatabase.DeleteAsset(assetPath);
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
            while (controller.layers.Length > 0)
            {
                controller.RemoveLayer(controller.layers.Length - 1);
            }

            controller.AddParameter(KenneyLocomotionPlan.SpeedParam, AnimatorControllerParameterType.Float);
            controller.AddParameter(KenneyLocomotionPlan.GroundedParam, AnimatorControllerParameterType.Bool);
            controller.AddParameter(KenneyLocomotionPlan.JumpParam, AnimatorControllerParameterType.Trigger);

            var layer = new AnimatorControllerLayer
            {
                name = "BaseLayer",
                defaultWeight = 1f,
                iKPass = true,
                stateMachine = new AnimatorStateMachine
                {
                    name = "Locomotion",
                    hideFlags = HideFlags.HideInHierarchy,
                },
            };
            AssetDatabase.AddObjectToAsset(layer.stateMachine, controller);
            controller.AddLayer(layer);

            AnimatorStateMachine root = layer.stateMachine;
            AnimatorState idle = MakeState(root, "Idle", clips["Idle"], new Vector3(200f, 0f, 0f), 1f, null);
            AnimatorState run = MakeState(root, "Run", clips["Run"], new Vector3(400f, 0f, 0f),
                1f / KenneyLocomotionPlan.RunReferenceSpeed, KenneyLocomotionPlan.SpeedParam);
            AnimatorState jump = MakeState(root, "Jump", clips["Idle"], new Vector3(600f, 0f, 0f), 1f, null);
            root.defaultState = idle;

            AnimatorStateTransition idleToRun = idle.AddTransition(run, false);
            ConfigureTransition(idleToRun, "IdleToRun", KenneyLocomotionPlan.IdleToRunBlend);
            idleToRun.AddCondition(AnimatorConditionMode.Greater, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                KenneyLocomotionPlan.SpeedParam);

            AnimatorStateTransition runToIdle = run.AddTransition(idle, false);
            ConfigureTransition(runToIdle, "RunToIdle", KenneyLocomotionPlan.RunToIdleBlend);
            runToIdle.AddCondition(AnimatorConditionMode.Less, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                KenneyLocomotionPlan.SpeedParam);

            AnimatorStateTransition idleToJump = idle.AddTransition(jump, false);
            ConfigureTransition(idleToJump, "IdleToJump", KenneyLocomotionPlan.IdleToJumpBlend);
            idleToJump.AddCondition(AnimatorConditionMode.If, 0f, KenneyLocomotionPlan.JumpParam);

            AnimatorStateTransition runToJump = run.AddTransition(jump, false);
            ConfigureTransition(runToJump, "RunToJump", KenneyLocomotionPlan.RunToJumpBlend);
            runToJump.AddCondition(AnimatorConditionMode.If, 0f, KenneyLocomotionPlan.JumpParam);

            AnimatorStateTransition jumpToRun = jump.AddTransition(run, false);
            ConfigureTransition(jumpToRun, "JumpToRun", KenneyLocomotionPlan.JumpToRunBlend);
            jumpToRun.hasExitTime = true;
            jumpToRun.exitTime = KenneyLocomotionPlan.JumpExitTime;
            jumpToRun.AddCondition(AnimatorConditionMode.Greater, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                KenneyLocomotionPlan.SpeedParam);
            jumpToRun.AddCondition(AnimatorConditionMode.If, 0f, KenneyLocomotionPlan.GroundedParam);

            AnimatorStateTransition jumpToIdle = jump.AddTransition(idle, false);
            ConfigureTransition(jumpToIdle, "JumpToIdle", KenneyLocomotionPlan.JumpToIdleBlend);
            jumpToIdle.hasExitTime = true;
            jumpToIdle.exitTime = KenneyLocomotionPlan.JumpExitTime;
            jumpToIdle.AddCondition(AnimatorConditionMode.If, 0f, KenneyLocomotionPlan.GroundedParam);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // Falsifiable guard: reload and assert the graph survived.
            var persisted = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (persisted == null || persisted.layers.Length != 1 || persisted.layers[0].stateMachine.states.Length != 3)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] controller did not persist its 3 states.");
            }

            int transitions = 0;
            foreach (ChildAnimatorState child in persisted.layers[0].stateMachine.states)
            {
                foreach (AnimatorStateTransition transition in child.state.transitions)
                {
                    transitions++;
                    if (transition.destinationState == null || transition.conditions.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"[HumanoidCharacterSetup] transition under '{child.state.name}' lost its destination/conditions.");
                    }
                }
            }

            if (transitions != 6)
            {
                throw new InvalidOperationException($"[HumanoidCharacterSetup] expected 6 transitions, persisted {transitions}.");
            }

            report.AppendLine($"=== controller: {assetPath} (3 states / 6 transitions / IK pass, clips=own Idle+Run)");
        }

        private static AnimatorState MakeState(AnimatorStateMachine root, string name, AnimationClip motion,
            Vector3 position, float speed, string speedParameter)
        {
            AnimatorState state = root.AddState(name, position);
            state.motion = motion;
            state.speed = speed;
            state.speedParameterActive = !string.IsNullOrEmpty(speedParameter);
            state.speedParameter = speedParameter ?? string.Empty;
            state.writeDefaultValues = true;
            return state;
        }

        private static void ConfigureTransition(AnimatorStateTransition transition, string name, float duration)
        {
            transition.name = name;
            transition.duration = Mathf.Max(0.01f, duration);
            transition.hasExitTime = false;
            transition.canTransitionToSelf = false;
            transition.interruptionSource = TransitionInterruptionSource.None;
        }

        // ---- 3. placement helpers (shared with AnimSceneSetup) -----------------

        /// <summary>
        /// Removes non-skinned debris the FBX carries (the Blender scene's
        /// 'Icosphere' guide at ×100). A humanoid body is skinned; a plain
        /// MeshRenderer here is export junk and would corrupt the measured
        /// height used for scaling.
        /// </summary>
        public static void StripStrayRenderers(GameObject root)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>() == null)
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(renderer.gameObject);
            }
        }

        /// <summary>
        /// Keeps the model's own materials when they are URP shaders (they are —
        /// VanguardBodyMat/Vanguard_VisorMat import as URP/Lit) and falls back
        /// to <c>MAT_CHR_Human</c> per slot only when a shader is missing or not
        /// URP — the "no pink" gate.
        /// </summary>
        private static void EnsureUrpMaterials(GameObject model)
        {
            Material fallback = AssetDatabase.LoadAssetAtPath<Material>(FallbackMaterialPath);
            if (fallback == null)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] fallback material missing at {FallbackMaterialPath}");
            }

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null || material.shader == null
                        || !material.shader.name.StartsWith("Universal Render Pipeline", StringComparison.Ordinal))
                    {
                        materials[i] = fallback;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        private static string DescribeMaterialAssignments(GameObject model)
        {
            var parts = new List<string>();
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    string color = material != null && material.HasProperty("_BaseColor")
                        ? material.GetColor("_BaseColor").ToString("F2")
                        : "n/a";
                    parts.Add($"'{renderer.name}'={(material == null ? "NULL" : $"{material.name}({material.shader?.name}, base={color})")}");
                }
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// Instantiates the humanoid under <paramref name="shell"/>, scales it to
        /// <paramref name="targetHeight"/> from the MEASURED bounds, settles the
        /// feet on world y = 0, assigns the URP fallback material and binds the
        /// animator/controller. Returns the model root.
        /// </summary>
        public static GameObject AttachHumanoid(Transform shell, float targetHeight, bool disableShellRenderer)
        {
            if (shell == null)
            {
                throw new ArgumentNullException(nameof(shell));
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (prefab == null)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] model missing at {ModelPath} — run the setup first.");
            }

            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            model.name = "CHR_Soldier";
            model.transform.SetParent(shell, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            // The Blender export carries a stray 'Icosphere' guide mesh at
            // localScale 100 — it inflates the measured AABB by a full metre
            // and would both mis-scale and mis-settle the character.
            StripStrayRenderers(model);

            Bounds original = MeasureBounds(model);
            float scale = ModelStageLayout.ScaleForHeight(original.size.y, targetHeight);
            model.transform.localScale = Vector3.one * scale;

            Bounds scaled = MeasureBounds(model);
            float shellScaleY = Mathf.Max(0.0001f, shell.lossyScale.y);
            model.transform.localPosition += new Vector3(0f, -scaled.min.y / shellScaleY, 0f);

            EnsureUrpMaterials(model);

            var animator = model.GetComponent<Animator>();
            if (animator == null)
            {
                animator = model.AddComponent<Animator>();
            }

            animator.avatar = FindAvatar(ModelPath) ?? animator.avatar;
            animator.runtimeAnimatorController =
                AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (disableShellRenderer)
            {
                // Keep the capsule collider (gameplay/interaction) but stop the
                // placeholder body from drawing over the humanoid.
                var shellRenderer = shell.GetComponent<MeshRenderer>();
                if (shellRenderer != null)
                {
                    shellRenderer.enabled = false;
                }
            }

            return model;
        }

        /// <summary>
        /// Adds a 180° world-Y yaw to the armature so the body's VISUAL forward
        /// becomes +Z while the model root's transform.forward stays the
        /// movement direction (the locomotion driver/tests equate the two).
        /// The armature's native rotation already carries Blender's Z-up→Y-up
        /// X180, so this COMPOSES with it instead of replacing it — replacing
        /// would lay the character on its side. Call it AFTER the animator
        /// controller is assigned: binding a controller rebinds the rig.
        /// Call it once per instance (it is topology-changing, not idempotent).
        /// </summary>
        public static void AlignVisualForward(GameObject model)
        {
            Transform armature = model.transform.Find("Character");
            if (armature == null)
            {
                throw new InvalidOperationException(
                    "[HumanoidCharacterSetup] expected an armature node named 'Character' for the forward flip.");
            }

            armature.localRotation = Quaternion.Euler(0f, 180f, 0f) * armature.localRotation;
            // Batch mode has no Undo stack: without this the child override
            // is silently dropped when the prefab instance is re-serialized.
            PrefabUtility.RecordPrefabInstancePropertyModifications(armature);
            Debug.Log($"[HumanoidCharacterSetup] '{model.name}' armature flipped -> localEuler={armature.localEulerAngles:F1}");
        }

        public static Avatar FindAvatar(string path)
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is Avatar avatar)
                {
                    return avatar;
                }
            }

            return null;
        }

        /// <summary>Combined world bounds of every renderer (skinned meshes use their shared mesh bounds).</summary>
        public static Bounds MeasureBounds(GameObject root)
        {
            var bounds = new Bounds();
            bool any = false;
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
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            Vector3 extents = bounds.extents;
            var result = new Bounds(center, Vector3.zero);
            foreach (Vector3 axis in new[]
                     {
                         matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f)),
                         matrix.MultiplyVector(new Vector3(0f, extents.y, 0f)),
                         matrix.MultiplyVector(new Vector3(0f, 0f, extents.z)),
                     })
            {
                result.Encapsulate(center + axis);
                result.Encapsulate(center - axis);
            }

            return result;
        }

        // ---- 4. SCN_Game patch -------------------------------------------------

        /// <summary>
        /// Swaps the SCN_Game "NPC" capsule body for the humanoid. The scene is
        /// edited in place (not regenerated) so post-generation content — the
        /// grass ground material — survives. Idempotent: a previous
        /// <c>CHR_Soldier</c> child is replaced, never stacked.
        /// </summary>
        public static string PatchGameScene()
        {
            var report = new StringBuilder();
            report.AppendLine($"SCN_Game NPC patch @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
            GameObject npc = GameObject.Find("NPC");
            if (npc == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] SCN_Game has no 'NPC' object.");
            }

            // Measured capsule height BEFORE the swap (the size the brief asks
            // us to match); the capsule collider stays untouched.
            Bounds capsuleBounds = MeasureBounds(npc);
            report.AppendLine($"=== capsule NPC bounds: size=({capsuleBounds.size.x:F3}, {capsuleBounds.size.y:F3}, " +
                              $"{capsuleBounds.size.z:F3}) minY={capsuleBounds.min.y:F3} pos={npc.transform.position:F3}");

            Transform previous = npc.transform.Find("CHR_Soldier");
            if (previous != null)
            {
                UnityEngine.Object.DestroyImmediate(previous.gameObject);
                report.AppendLine("=== removed previous CHR_Soldier child");
            }

            float target = Mathf.Max(1.5f, capsuleBounds.size.y);
            GameObject model = AttachHumanoid(npc.transform, target, disableShellRenderer: true);

            Bounds landed = MeasureBounds(model);
            report.AppendLine($"=== CHR_Soldier: target={target:F3} m scale={model.transform.localScale.x:F6} " +
                              $"finalSize=({landed.size.x:F3}, {landed.size.y:F3}, {landed.size.z:F3}) " +
                              $"minY={landed.min.y:F3} pos={model.transform.position:F3}");

            if (landed.min.y < -0.01f || landed.min.y > 0.02f)
            {
                throw new InvalidOperationException(
                    $"[HumanoidCharacterSetup] feet are not on y=0 (minY={landed.min.y:F3}).");
            }

            SkinnedMeshRenderer skinned = model.GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinned == null || skinned.sharedMesh == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] the new NPC body has no SkinnedMeshRenderer.");
            }

            report.AppendLine($"=== NPC renderer: SkinnedMeshRenderer mesh='{skinned.sharedMesh.name}' " +
                              $"avatar={(model.GetComponent<Animator>().avatar != null ? model.GetComponent<Animator>().avatar.name : "none")} " +
                              $"controller='{model.GetComponent<Animator>().runtimeAnimatorController?.name}'");

            var proxy = npc.GetComponent<InteractableProxy>();
            if (proxy == null)
            {
                throw new InvalidOperationException("[HumanoidCharacterSetup] the NPC lost its InteractableProxy.");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, GameScenePath);
            report.AppendLine("=== saved SCN_Game");
            return report.ToString();
        }

        // ---- report ----------------------------------------------------------

        private static void WriteReport(string text)
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ReportPath));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            Debug.Log($"[HumanoidCharacterSetup] report -> {path}\n{text}");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                EnsureFolder(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
#endif
