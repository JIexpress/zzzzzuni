#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.View.Editor
{
    /// <summary>
    /// TASK-A-R9-10: import-time preparation for the ten real models under
    /// <c>Assets/Art/Models/</c> (4 characters + 6 environment props, sources
    /// and licences in that folder's SOURCES.md).
    ///
    /// Two batch entry points:
    /// - <see cref="ProbeFromCommandLine"/>: first-import probe. Imports every
    ///   FBX and dumps hierarchy / renderers / materials / mesh bounds /
    ///   animation clips into <c>TempLogs/model_probe.txt</c> — the evidence
    ///   the scene builder (<see cref="GameSceneSetup"/>) and the material
    ///   plan are derived from, and the "original size" column of the task
    ///   report.
    /// - <see cref="SetupFromCommandLine"/>: import settings + materials.
    ///   Blender left stray Camera/Lamp nodes in two FBX (they would render in
    ///   play mode), so those import flags go off; every FBX carries embedded
    ///   materials (already URP Lit by 2022.3 defaults, but texture-less and
    ///   unlabelled), and each of them is remapped onto ONE per-model MAT_
    ///   asset so the ten models stop looking like white blobs and the project
    ///   owns named, tunable materials. Idempotent (LoadOrCreate + same
    ///   values every run).
    ///
    /// Probe findings that shaped this (2026-10-31, TempLogs/model_probe.txt):
    /// * The importer does NOT apply the file's unit conversion at instantiate
    ///   time for these files — characters measure ~180-208 m at scale 1
    ///   (Blender's ×100 node scales survive import). The scene builder
    ///   therefore measures real bounds and rescales (see GameSceneSetup).
    /// * CHR_Human / CHR_DarkElf contain "Camera"/"Lamp" nodes; ENV_* meshes
    ///   come as LOD0/LOD1/LOD2 sibling renderers (no LODGroup).
    ///
    /// Editor-only by the #if UNITY_EDITOR guard (GameSceneSetup pattern).
    /// </summary>
    public static class ModelAssetSetup
    {
        private const string ModelsDir = "Assets/Art/Models";

        /// <summary>The ten models the brief staged, in stable order.</summary>
        public static readonly string[] ModelFiles =
        {
            "CHR_Kenney_CharacterMedium.fbx",
            "CHR_Robot.fbx",
            "CHR_Human.fbx",
            "CHR_DarkElf.fbx",
            "ENV_Tree_01.fbx",
            "ENV_Tree_02.fbx",
            "ENV_Rock.fbx",
            "ENV_Barrel.fbx",
            "ENV_Fence.fbx",
            "ENV_Road.fbx",
        };

        /// <summary>
        /// Base colours for the material plan (sRGB). One per model — the
        /// "reasonable solid colour" the brief asks for when no textures ship
        /// with the FBX (the ENV_* FBX reference a colour-sheet texture that
        /// was not staged; the characters have plain white/grey slots).
        /// </summary>
        private static readonly Dictionary<string, Color> BaseColor = new Dictionary<string, Color>
        {
            // Characters: cloth/skin-ish shades, deliberately distinct.
            { "CHR_Kenney_CharacterMedium", new Color(0.90f, 0.62f, 0.35f) }, // warm cloth
            { "CHR_Robot",                  new Color(0.62f, 0.68f, 0.74f) }, // steel grey-blue
            { "CHR_Human",                  new Color(0.82f, 0.65f, 0.52f) }, // skin/cloth
            { "CHR_DarkElf",                new Color(0.45f, 0.30f, 0.42f) }, // dark elf robe
            // Props: foliage greens (muted olive so the single colour also
            // reads as a trunk), stone grey, wood brown, asphalt grey.
            { "ENV_Tree_01",                new Color(0.35f, 0.52f, 0.28f) },
            { "ENV_Tree_02",                new Color(0.40f, 0.50f, 0.30f) },
            { "ENV_Rock",                   new Color(0.55f, 0.54f, 0.52f) },
            { "ENV_Barrel",                 new Color(0.55f, 0.38f, 0.20f) },
            { "ENV_Fence",                  new Color(0.60f, 0.47f, 0.30f) },
            { "ENV_Road",                   new Color(0.45f, 0.44f, 0.43f) },
        };

        /// <summary>
        /// Probe entry: imports all ten FBX and dumps what Unity made of them.
        /// Batch: -executeMethod Game.View.Editor.ModelAssetSetup.ProbeFromCommandLine
        /// </summary>
        public static void ProbeFromCommandLine()
        {
            string report = Probe();
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "model_probe.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Debug.Log($"[ModelAssetSetup] probe written to {path}\n{report}");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Setup entry: import flags + MAT_ materials + FBX remap.
        /// Batch: -executeMethod Game.View.Editor.ModelAssetSetup.SetupFromCommandLine
        /// </summary>
        public static void SetupFromCommandLine()
        {
            string report = Setup();
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "model_setup.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Debug.Log($"[ModelAssetSetup] setup report written to {path}\n{report}");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// The URP Lit material this project uses for <paramref name="model"/>
        /// (FBX stem). Created on first call, reused afterwards — idempotent,
        /// and the SAME instance the scene builder assigns to the placed
        /// objects. Public so <see cref="GameSceneSetup"/> and the PlayMode
        /// pink-check agree on one truth.
        /// </summary>
        public static Material GetOrCreateMaterial(string model)
        {
            if (!BaseColor.TryGetValue(model, out Color color))
            {
                throw new ArgumentException($"[ModelAssetSetup] no colour plan for '{model}'.", nameof(model));
            }

            string materialPath = $"{ModelsDir}/MAT_{model}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material != null)
            {
                return material;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "[ModelAssetSetup] URP/Lit shader not found — is the URP package installed?");
            }

            material = new Material(shader) { name = $"MAT_{model}" };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.25f); // keep low-poly highlights tame
            AssetDatabase.CreateAsset(material, materialPath);
            return material;
        }

        /// <summary>The plan for all ten models (probe/setup/scene agree on it).</summary>
        public static IReadOnlyList<string> ModelList => ModelFiles;

        /// <summary>
        /// Diagnostic entry: for each FBX prints the importer material state
        /// (location, external object map) and what an instantiate actually
        /// resolves per renderer slot (name + shader + _BaseColor). Batch:
        /// -executeMethod Game.View.Editor.ModelAssetSetup.DiagnoseFromCommandLine
        /// </summary>
        public static void DiagnoseFromCommandLine()
        {
            var report = new StringBuilder();
            report.AppendLine($"Material diagnose @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (string file in ModelFiles)
            {
                string path = $"{ModelsDir}/{file}";
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                report.AppendLine($"=== {file}: location={importer?.materialLocation} " +
                                  $"externalMap={(importer != null ? importer.GetExternalObjectMap().Count : -1)}");
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                {
                    continue;
                }

                GameObject instance = UnityEngine.Object.Instantiate(asset);
                try
                {
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (Material material in renderer.sharedMaterials)
                        {
                            string color = material != null && material.HasProperty("_BaseColor")
                                ? material.GetColor("_BaseColor").ToString("F2")
                                : "n/a";
                            report.AppendLine($"  '{renderer.name}' slot: " +
                                              $"{(material != null ? $"{material.name} ({material.shader?.name}) base={color}" : "NULL")}");
                        }
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            string reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "TempLogs", "model_diagnose.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ModelAssetSetup] diagnose written to {reportPath}\n{report}");
            EditorApplication.Exit(0);
        }

        // ---- probe -----------------------------------------------------------

        private static string Probe()
        {
            AssetDatabase.Refresh();
            var report = new StringBuilder();
            report.AppendLine($"Model probe @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (string file in ModelFiles)
            {
                string path = $"{ModelsDir}/{file}";
                if (!File.Exists(path))
                {
                    report.AppendLine($"=== {file}: MISSING");
                    continue;
                }

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                report.AppendLine($"=== {file}");
                if (importer != null)
                {
                    int embedded = 0;
                    foreach (UnityEngine.Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (obj is Material)
                        {
                            embedded++;
                        }
                    }

                    report.AppendLine(
                        $"  importer: materialLocation={importer.materialLocation} " +
                        $"embeddedMats={embedded} importAnim={importer.importAnimation} " +
                        $"fileScale={importer.fileScale} globalScale={importer.globalScale} " +
                        $"useFileUnits={importer.useFileUnits} clips={importer.clipAnimations.Length}");
                }

                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null)
                {
                    report.AppendLine("  asset: NOT A GAMEOBJECT (import failed?)");
                    continue;
                }

                GameObject instance = UnityEngine.Object.Instantiate(asset);
                try
                {
                    DescribeHierarchy(instance.transform, 0, report);
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh mesh = ResolveMesh(renderer);
                        string meshInfo = mesh != null
                            ? $"mesh='{mesh.name}' submeshes={mesh.subMeshCount} localSize={mesh.bounds.size:F3}"
                            : "mesh=null";
                        var materialNames = new List<string>();
                        foreach (Material material in renderer.sharedMaterials)
                        {
                            materialNames.Add(material == null
                                ? "NULL"
                                : $"'{material.name}'({material.shader?.name ?? "no-shader"})");
                        }

                        report.AppendLine(
                            $"  renderer '{renderer.name}' type={renderer.GetType().Name} {meshInfo} " +
                            $"worldSize={renderer.bounds.size:F3} mats=[{string.Join(", ", materialNames)}]");
                    }

                    Animator animator = instance.GetComponentInChildren<Animator>();
                    report.AppendLine($"  animator={(animator != null ? animator.runtimeAnimatorController?.name ?? "none" : "none")}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            return report.ToString();
        }

        private static void DescribeHierarchy(Transform node, int depth, StringBuilder report)
        {
            report.AppendLine($"{new string(' ', 2 + depth * 2)}node '{node.name}' pos={node.localPosition:F3} rotE={node.localEulerAngles:F0} scale={node.localScale:F3}");
            for (int i = 0; i < node.childCount && depth < 3; i++)
            {
                DescribeHierarchy(node.GetChild(i), depth + 1, report);
            }
        }

        private static Mesh ResolveMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                return skinned.sharedMesh;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // ---- setup -----------------------------------------------------------

        private static string Setup()
        {
            AssetDatabase.Refresh();
            var report = new StringBuilder();
            report.AppendLine($"Model material setup @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (string file in ModelFiles)
            {
                string path = $"{ModelsDir}/{file}";
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!File.Exists(path))
                {
                    report.AppendLine($"=== {file}: MISSING");
                    continue;
                }

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    report.AppendLine($"=== {file}: no ModelImporter");
                    continue;
                }

                bool changed = false;
                if (importer.importCameras || importer.importLights)
                {
                    // Blender exported its scene cameras/lamps inside two FBX;
                    // stray Camera components would render (and a stray Light
                    // would relight) every scene that instantiates the asset.
                    importer.importCameras = false;
                    importer.importLights = false;
                    changed = true;
                }

                var embedded = new List<Material>();
                foreach (UnityEngine.Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (obj is Material material)
                    {
                        embedded.Add(material);
                    }
                }

                report.Append($"=== {file}: embeddedSlots={embedded.Count} mats=[");
                for (int i = 0; i < embedded.Count; i++)
                {
                    report.Append((i > 0 ? ", " : string.Empty) + embedded[i].name);
                }

                report.AppendLine($"] location={importer.materialLocation} " +
                                  $"importCameras={importer.importCameras} importLights={importer.importLights}");

                if (importer.materialLocation != ModelImporterMaterialLocation.InPrefab)
                {
                    // MATERIAL DECISION (2026-10-31, after a live experiment):
                    // switching to External + AddRemap did NOT take — Unity's
                    // resolution fell back to auto-extracting every embedded
                    // material into Art/Models/Materials/ (14 stray .mat).
                    // The embedded materials are already URP Lit with the
                    // authors' colours (no pink, nothing to fix at asset
                    // level); the SCENE look is owned by the per-model MAT_
                    // assets, which GameSceneSetup assigns to every slot of
                    // every placed instance. Embedded it is.
                    importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                    changed = true;
                }

                if (importer.GetExternalObjectMap().Count > 0)
                {
                    // Stale remap entries (e.g. pointing at the deleted
                    // extraction folder) silently suppress embedded material
                    // creation on reimport — clear them so the FBX keeps its
                    // own author-coloured URP materials as the asset-level
                    // fallback. (2022.3 has no ClearExternalObjects on
                    // ModelImporter; RemoveRemap per key is the equivalent.)
                    foreach (AssetImporter.SourceAssetIdentifier key in importer.GetExternalObjectMap().Keys)
                    {
                        importer.RemoveRemap(key);
                    }

                    changed = true;
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                    report.AppendLine("  reimported (flags/location changed)");
                }
            }

            AssetDatabase.SaveAssets();
            return report.ToString();
        }
    }
}
#endif
