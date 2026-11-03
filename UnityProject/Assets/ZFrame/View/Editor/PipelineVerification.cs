#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Acceptance verification for TASK-A-M2-03, run via Unity batch mode:
    ///   -executeMethod ZFrame.View.PipelineVerification.ValidateFromCommandLine
    ///
    /// Checks (report → repo TempLogs/pipeline_report.txt, exit 0/1):
    ///  1. three URP assets + renderers exist and match the 差异表
    ///     (Low: HDR off/MSAA 1x/阴影 20/无软阴影; Medium: HDR on/MSAA 2x/阴影 40;
    ///      High: HDR on/MSAA 4x/阴影 60/软阴影 High);
    ///  2. volume profiles match the grade matrix; tier table resolves;
    ///  3. QualitySettings has exactly Low/Medium/High and
    ///     GraphicsSettings.asset default pipeline → URP_Medium;
    ///  4. live SetTier round-robin: activeQualityLevel, current
    ///     RenderPipelineAsset instance name, manager tier, grade profile,
    ///     EventBus TierChangedEvent;
    ///  5. material pink check — no built-in shader/material references
    ///     left under Assets (plus an idempotent TestCube → URP Lit migration
    ///     pass so committed test assets stay URP-clean);
    ///  6. restores the Medium tier before exiting.
    /// </summary>
    public static class PipelineVerification
    {
        private const string PipelineDir = "Assets/Settings/Pipeline";
        private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
        private static readonly Regex BuiltinMaterialRef = new Regex(
            @"-\s*\{fileID: \d+, guid: 0000000000000000f000000000000000", RegexOptions.Compiled);
        private static readonly Regex BuiltinShaderRef = new Regex(
            @"m_Shader: \{fileID: \d+, guid: 0000000000000000f000000000000000", RegexOptions.Compiled);

        /// <summary>Entry point: run all checks, write the report file, exit 0/1.</summary>
        public static void ValidateFromCommandLine()
        {
            var violations = new List<string>();
            var notes = new List<string>();

            EnsureTestCubeUrpMaterial(violations, notes);
            VerifyAssets(violations, notes);
            VerifyQualitySettings(violations, notes);
            VerifyGraphicsDefault(violations, notes);
            VerifyLiveTierSwitch(violations, notes);
            VerifyNoBuiltInMaterials(violations, notes);

            // Leave the project on the default tier.
            PipelineManager.SetTier(TierMapper.DefaultTier);

            string report = BuildReport(violations, notes);
            string reportPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "TempLogs", "pipeline_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
            Debug.Log($"[PipelineVerification] report written to {reportPath}\n{report}");

            EditorApplication.Exit(violations.Count == 0 ? 0 : 1);
        }

        // ---- 1..3 asset/settings presence ----------------------------------

        private static void VerifyAssets(List<string> violations, List<string> notes)
        {
            var assets = new UniversalRenderPipelineAsset[(int)QualityTier.Count];
            for (int i = 0; i < (int)QualityTier.Count; i++)
            {
                var tier = (QualityTier)i;
                string name = TierMapper.ToPipelineAssetKey(tier);
                string assetPath = $"{PipelineDir}/{name}.asset";
                assets[i] = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
                if (assets[i] == null)
                {
                    violations.Add($"missing URP asset {assetPath}");
                    continue;
                }

                if (!PipelineSetup.ParamsMatch(assets[i], tier))
                {
                    violations.Add($"{name} does not match the parameter matrix");
                }
                else
                {
                    notes.Add($"{name}: HDR={assets[i].supportsHDR} MSAA={assets[i].msaaSampleCount} " +
                              $"shadowDistance={assets[i].shadowDistance} cascades={assets[i].shadowCascadeCount} " +
                              $"softShadows={assets[i].supportsSoftShadows}");
                }

                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>($"{PipelineDir}/{name}_Renderer.asset");
                if (renderer == null)
                {
                    violations.Add($"missing renderer {PipelineDir}/{name}_Renderer.asset");
                }
                else if (renderer.renderingMode != RenderingMode.Forward)
                {
                    violations.Add($"{name}_Renderer renderingMode={renderer.renderingMode}, expected Forward");
                }
                else
                {
                    notes.Add($"{name}_Renderer: Forward, postProcessData={(renderer.postProcessData != null ? "ok" : "NULL")}");
                }

                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>($"{PipelineDir}/{TierMapper.ToVolumeProfileKey(tier)}.asset");
                if (profile == null)
                {
                    violations.Add($"missing VolumeProfile {PipelineDir}/{TierMapper.ToVolumeProfileKey(tier)}.asset");
                }
                else if (!PipelineSetup.ProfileMatches(profile, tier))
                {
                    violations.Add($"{TierMapper.ToVolumeProfileKey(tier)} does not match the grade matrix");
                }
                else
                {
                    notes.Add($"{TierMapper.ToVolumeProfileKey(tier)}: components={profile.components.Count}");
                }
            }

            var table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            if (table == null)
            {
                violations.Add($"missing tier table Resources/{PipelineManager.TableResourcePath}");
            }
            else if (table.TierCount != (int)QualityTier.Count)
            {
                violations.Add($"tier table covers {table.TierCount} tiers, expected {(int)QualityTier.Count}");
            }
            else
            {
                for (int i = 0; i < (int)QualityTier.Count; i++)
                {
                    var bound = table.GetPipelineAsset((QualityTier)i);
                    string expected = TierMapper.ToPipelineAssetKey((QualityTier)i);
                    if (bound == null || bound.name != expected)
                    {
                        violations.Add($"tier table row {i} binds '{bound?.name}', expected '{expected}'");
                    }
                }

                notes.Add("CFG_PipelineTiers: 3 rows bound to URP_Low/Medium/High");
            }
        }

        private static void VerifyQualitySettings(List<string> violations, List<string> notes)
        {
            string[] names = QualitySettings.names;
            if (names.Length != (int)QualityTier.Count)
            {
                violations.Add($"QualitySettings has {names.Length} levels, expected {(int)QualityTier.Count}");
                return;
            }

            for (int i = 0; i < names.Length; i++)
            {
                string expected = ((QualityTier)i).ToString();
                if (names[i] != expected)
                {
                    violations.Add($"QualitySettings level {i} is '{names[i]}', expected '{expected}'");
                }
            }

            notes.Add($"QualitySettings levels: [{string.Join(", ", names)}]");
        }

        private static void VerifyGraphicsDefault(List<string> violations, List<string> notes)
        {
            string mediumGuid = AssetDatabase.AssetPathToGUID(
                $"{PipelineDir}/{TierMapper.ToPipelineAssetKey(QualityTier.Medium)}.asset");
            string content = File.ReadAllText(GraphicsSettingsPath);
            Match match = Regex.Match(content, @"m_CustomRenderPipeline: \{fileID: (\d+), guid: ([0-9a-f]{32})");
            if (!match.Success)
            {
                violations.Add("GraphicsSettings.asset has no m_CustomRenderPipeline entry");
            }
            else if (match.Groups[2].Value != mediumGuid)
            {
                violations.Add($"GraphicsSettings default pipeline guid={match.Groups[2].Value}, expected {mediumGuid} (URP_Medium)");
            }
            else
            {
                notes.Add("GraphicsSettings default pipeline -> URP_Medium");
            }
        }

        // ---- 4 live tier switch ---------------------------------------------

        private static void VerifyLiveTierSwitch(List<string> violations, List<string> notes)
        {
            int events = 0;
            IDisposable subscription = EventBus.Subscribe<TierChangedEvent>(_ => events++);

            try
            {
                for (int i = 0; i < (int)QualityTier.Count; i++)
                {
                    var tier = (QualityTier)i;
                    string expectedAsset = TierMapper.ToPipelineAssetKey(tier);
                    string expectedProfile = TierMapper.ToVolumeProfileKey(tier);

                    PipelineManager.SetTier(tier);

                    if (QualitySettings.GetQualityLevel() != i)
                    {
                        violations.Add(
                            $"SetTier({tier}): activeQualityLevel={QualitySettings.GetQualityLevel()}, expected {i}");
                    }

                    var current = GraphicsSettings.currentRenderPipeline;
                    if (current == null || current.name != expectedAsset)
                    {
                        violations.Add(
                            $"SetTier({tier}): currentRenderPipeline='{current?.name}', expected '{expectedAsset}'");
                    }

                    if (PipelineManager.CurrentTier != tier)
                    {
                        violations.Add($"SetTier({tier}): manager tier={PipelineManager.CurrentTier}");
                    }

                    var manager = PipelineManager.Instance;
                    if (manager != null && manager.CurrentGradeProfile != null &&
                        manager.CurrentGradeProfile.name != expectedProfile)
                    {
                        violations.Add(
                            $"SetTier({tier}): grade profile='{manager.CurrentGradeProfile.name}', expected '{expectedProfile}'");
                    }

                    notes.Add($"SetTier({tier}): level={QualitySettings.GetQualityLevel()} " +
                              $"pipeline={current?.name} profile={manager?.CurrentGradeProfile?.name}");
                }

                if (events < (int)QualityTier.Count)
                {
                    violations.Add($"TierChangedEvent fired {events} times across 3 SetTier calls, expected >= 3");
                }
                else
                {
                    notes.Add($"EventBus TierChangedEvent fired {events} times");
                }
            }
            finally
            {
                subscription.Dispose();
            }
        }

        // ---- 5 material migration + pink check -------------------------------

        /// <summary>
        /// Idempotent migration: TestCube.prefab must not carry the built-in
        /// default material (Standard shader → magenta under URP). Assigns a
        /// committed URP Lit material MAT_TestCube. Any future material assets
        /// must reference URP shaders — the pink scan below enforces that.
        /// </summary>
        private static void EnsureTestCubeUrpMaterial(List<string> violations, List<string> notes)
        {
            const string prefabPath = "Assets/ZFrame/Tests/Resources/TestCube.prefab";
            const string materialPath = "Assets/ZFrame/Tests/Resources/MAT_TestCube.asset";
            if (!File.Exists(prefabPath))
            {
                return; // not this project's concern
            }

            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null)
            {
                violations.Add("URP Lit shader not found — package import broken?");
                return;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null || mat.shader != litShader)
            {
                if (mat != null)
                {
                    mat.shader = litShader;
                    EditorUtility.SetDirty(mat);
                }
                else
                {
                    mat = new Material(litShader) { name = "MAT_TestCube" };
                    AssetDatabase.CreateAsset(mat, materialPath);
                }

                AssetDatabase.SaveAssets();
                notes.Add("MAT_TestCube (URP Lit) regenerated");
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var renderer = root.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == null ||
                    renderer.sharedMaterial.name != "MAT_TestCube")
                {
                    renderer.sharedMaterial = mat;
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    notes.Add("TestCube.prefab: MeshRenderer material -> MAT_TestCube (URP Lit)");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Magenta-risk scan: any .mat whose shader is a built-in (f000…)
        /// shader, or any .prefab/.unity renderer still referencing a built-in
        /// material. Every hit fails verification.
        /// </summary>
        private static void VerifyNoBuiltInMaterials(List<string> violations, List<string> notes)
        {
            var hits = new List<string>();
            foreach (string file in Directory.GetFiles("Assets", "*.mat", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (BuiltinShaderRef.IsMatch(line))
                    {
                        hits.Add($"{file}: built-in shader ({line.Trim()})");
                    }
                }
            }

            foreach (string extension in new[] { "*.prefab", "*.unity" })
            {
                foreach (string file in Directory.GetFiles("Assets", extension, SearchOption.AllDirectories))
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        if (BuiltinMaterialRef.IsMatch(line))
                        {
                            hits.Add($"{file}: built-in material reference ({line.Trim()})");
                        }
                    }
                }
            }

            if (hits.Count > 0)
            {
                violations.Add($"built-in material/shader references remain ({hits.Count}):");
                violations.AddRange(hits);
            }
            else
            {
                notes.Add("pink check: 0 built-in shader/material references under Assets");
            }
        }

        // ---- report -----------------------------------------------------------

        private static string BuildReport(List<string> violations, List<string> notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Pipeline tier matrix verification @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Unity {Application.unityVersion} | URP package active pipeline: " +
                          $"{GraphicsSettings.currentRenderPipeline?.name ?? "BuiltIn"}");
            sb.AppendLine($"violations: {violations.Count}");
            sb.AppendLine(violations.Count == 0 ? "result: PASS" : "result: FAIL");
            sb.AppendLine(new string('-', 60));
            foreach (string note in notes)
            {
                sb.AppendLine($"[ok] {note}");
            }

            foreach (string violation in violations)
            {
                sb.AppendLine($"[FAIL] {violation}");
            }

            return sb.ToString();
        }
    }
}
#endif
