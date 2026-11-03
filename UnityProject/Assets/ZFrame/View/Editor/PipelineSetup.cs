#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ZFrame.View
{
    /// <summary>
    /// One-shot pipeline setup (TASK-A-M2-03): generates the committed tier
    /// matrix under <c>Assets/Settings/Pipeline/</c>, binds the QualitySettings
    /// levels and points the Graphics default pipeline at URP_Medium.
    /// Idempotent: re-running keeps valid assets and only regenerates
    /// missing/incomplete ones. Run via menu (ZFrame/Setup/Generate URP
    /// Pipeline Assets) or batch:
    ///   -executeMethod ZFrame.View.PipelineSetup.SetupFromCommandLine
    ///
    /// Generated inventory (差异表 — Low: HDR off/MSAA off/阴影 20/无软阴影;
    /// Medium: HDR on/MSAA 2x/阴影 40; High: HDR on/MSAA 4x/阴影 60/软阴影):
    ///   URP_Low/Medium/High.asset           — UniversalRenderPipelineAsset
    ///   URP_Low/Medium/High_Renderer.asset  — UniversalRendererData (Forward)
    ///   Vol_Low/Medium/High.asset           — VolumeProfile (Low: empty;
    ///                                         Medium: weak Bloom; High: Bloom + ColorAdjustments)
    ///   Resources/CFG_PipelineTiers.asset   — PipelineTierTable (Resources-resolvable)
    ///
    /// ProjectSettings are written by script (no manual editor clicking).
    /// Unity exposes no API to author quality levels, so both
    /// ProjectSettings/QualitySettings.asset (3 levels bound to the tier
    /// assets) and the m_CustomRenderPipeline line of
    /// ProjectSettings/GraphicsSettings.asset are patched as raw YAML —
    /// deterministic rewrites of the existing 2022.3 file layout, performed
    /// without touching the in-memory settings (so the session cannot
    /// clobber the files on exit). A fresh verification session re-reads
    /// them from disk.
    /// </summary>
    public static class PipelineSetup
    {
        private const string PipelineDir = "Assets/Settings/Pipeline";
        private const string ResourcesDir = PipelineDir + "/Resources";
        private const string QualitySettingsPath = "ProjectSettings/QualitySettings.asset";
        private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";

        /// <summary>Cascade counts per tier (not in the 差异表; documented here).</summary>
        private static readonly int[] TierCascades = { 1, 2, 2 };

        [MenuItem("ZFrame/Setup/Generate URP Pipeline Assets")]
        public static void Setup()
        {
            EnsureAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[PipelineSetup] tier matrix ensured (see PipelineSetup.EnsureAll).");
        }

        /// <summary>Batch entry: ensure everything, patch ProjectSettings, exit 0.</summary>
        public static void SetupFromCommandLine()
        {
            EnsureAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorApplication.Exit(0);
        }

        /// <summary>Full idempotent ensure: assets → table → ProjectSettings patches.</summary>
        public static void EnsureAll()
        {
            Directory.CreateDirectory(PipelineDir);
            Directory.CreateDirectory(ResourcesDir);

            var assets = new UniversalRenderPipelineAsset[(int)QualityTier.Count];
            var profiles = new VolumeProfile[(int)QualityTier.Count];

            for (int i = 0; i < (int)QualityTier.Count; i++)
            {
                var tier = (QualityTier)i;
                assets[i] = EnsureTierAssets(tier);
                profiles[i] = EnsureVolumeProfile(tier);
            }

            EnsureTierTable(assets, profiles);
            PatchGraphicsSettingsYaml(AssetDatabase.AssetPathToGUID(
                $"{PipelineDir}/{TierMapper.ToPipelineAssetKey(QualityTier.Medium)}.asset"));
            WriteQualitySettingsYaml();
        }

        // ---- URP asset + renderer ------------------------------------------

        /// <summary>差异表 check against the loaded asset (public getters + serialized soft shadows).</summary>
        public static bool ParamsMatch(UniversalRenderPipelineAsset asset, QualityTier tier)
        {
            int tierIndex = (int)tier;
            return asset.supportsHDR == (tier != QualityTier.Low)
                && asset.msaaSampleCount == (tier == QualityTier.Low ? 1 : tier == QualityTier.Medium ? 2 : 4)
                && Mathf.Approximately(asset.shadowDistance, 20f + 20f * tierIndex)
                && asset.shadowCascadeCount == TierCascades[tierIndex]
                && asset.supportsSoftShadows == (tier == QualityTier.High)
                && (tier != QualityTier.High
                    || GetSerializedSoftShadowQuality(asset) == SoftShadowQuality.High);
        }

        private static UniversalRenderPipelineAsset EnsureTierAssets(QualityTier tier)
        {
            string name = TierMapper.ToPipelineAssetKey(tier);
            string rendererPath = $"{PipelineDir}/{name}_Renderer.asset";
            string assetPath = $"{PipelineDir}/{name}.asset";

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                rendererData.name = $"{name}_Renderer";
                AssetDatabase.CreateAsset(rendererData, rendererPath);
            }

            // Forward renderer explicitly (差异表) + package PostProcessData so
            // Volume post-processing (Bloom/ColorAdjustments) actually renders.
            rendererData.renderingMode = RenderingMode.Forward;
            if (rendererData.postProcessData == null)
            {
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            }

            EditorUtility.SetDirty(rendererData);

            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
            if (asset == null)
            {
                asset = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            if (!ParamsMatch(asset, tier))
            {
                ApplyTierParams(asset, tier);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(assetPath);
                var reloaded = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
                if (reloaded == null || !ParamsMatch(reloaded, tier))
                {
                    throw new InvalidOperationException(
                        $"[PipelineSetup] {assetPath} failed parameter validation after (re)generation.");
                }

                asset = reloaded;
            }

            return asset;
        }

        private static void ApplyTierParams(UniversalRenderPipelineAsset asset, QualityTier tier)
        {
            int tierIndex = (int)tier;
            asset.supportsHDR = tier != QualityTier.Low;
            asset.msaaSampleCount = tier == QualityTier.Low ? 1 : tier == QualityTier.Medium ? 2 : 4;
            asset.shadowDistance = 20f + 20f * tierIndex;
            asset.shadowCascadeCount = TierCascades[tierIndex];

            // Soft shadows only have an internal setter on the asset — the
            // serialized fields are the public contract here (14.0.11).
            // SoftShadowQuality has no Disabled member: soft shadows are
            // switched by m_SoftShadowsSupported, the quality value only
            // matters when supported.
            var so = new SerializedObject(asset);
            so.FindProperty("m_SoftShadowsSupported").boolValue = tier == QualityTier.High;
            so.FindProperty("m_SoftShadowQuality").enumValueIndex = tier == QualityTier.High
                ? (int)SoftShadowQuality.High
                : (int)SoftShadowQuality.Low;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static SoftShadowQuality GetSerializedSoftShadowQuality(UniversalRenderPipelineAsset asset)
        {
            using (var so = new SerializedObject(asset))
            {
                return (SoftShadowQuality)so.FindProperty("m_SoftShadowQuality").enumValueIndex;
            }
        }

        // ---- volume profiles -------------------------------------------------

        private static VolumeProfile EnsureVolumeProfile(QualityTier tier)
        {
            string name = TierMapper.ToVolumeProfileKey(tier);
            string path = $"{PipelineDir}/{name}.asset";

            var existing = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (existing != null && ProfileMatches(existing, tier))
            {
                return existing;
            }

            if (existing != null)
            {
                Debug.LogWarning($"[PipelineSetup] existing {path} does not match the 差异表 — regenerating.");
                AssetDatabase.DeleteAsset(path);
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = name;
            AssetDatabase.CreateAsset(profile, path);

            if (tier >= QualityTier.Medium)
            {
                Bloom bloom = profile.Add<Bloom>(true);
                bloom.threshold.overrideState = true;
                bloom.threshold.value = 0.9f;
                bloom.intensity.overrideState = true;
                bloom.intensity.value = tier == QualityTier.High ? 2.5f : 1.0f; // Medium = 弱 Bloom
                bloom.scatter.overrideState = true;
                bloom.scatter.value = 0.5f;
            }

            if (tier == QualityTier.High)
            {
                ColorAdjustments adjustments = profile.Add<ColorAdjustments>(true);
                adjustments.postExposure.overrideState = true;
                adjustments.postExposure.value = 0.1f;
                adjustments.saturation.overrideState = true;
                adjustments.saturation.value = 8f;
            }

            // Persist every component INTO the profile asset (sub-asset), then
            // save — otherwise the components array serializes dangling.
            foreach (VolumeComponent component in profile.components)
            {
                component.name = component.GetType().Name;
                AssetDatabase.AddObjectToAsset(component, profile);
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            VolumeProfile reloaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (reloaded == null || !ProfileMatches(reloaded, tier))
            {
                throw new InvalidOperationException($"[PipelineSetup] {path} failed validation after generation.");
            }

            return reloaded;
        }

        /// <summary>差异表 check: Low empty; Medium Bloom; High Bloom + ColorAdjustments.</summary>
        public static bool ProfileMatches(VolumeProfile profile, QualityTier tier)
        {
            return tier switch
            {
                QualityTier.Low => !profile.Has<Bloom>() && !profile.Has<ColorAdjustments>(),
                QualityTier.Medium => profile.Has<Bloom>() && !profile.Has<ColorAdjustments>(),
                QualityTier.High => profile.Has<Bloom>() && profile.Has<ColorAdjustments>(),
                _ => false,
            };
        }

        // ---- tier table -------------------------------------------------------

        private static void EnsureTierTable(UniversalRenderPipelineAsset[] assets, VolumeProfile[] profiles)
        {
            string path = $"{ResourcesDir}/CFG_PipelineTiers.asset";
            var table = AssetDatabase.LoadAssetAtPath<PipelineTierTable>(path);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<PipelineTierTable>();
                AssetDatabase.CreateAsset(table, path);
            }

            var so = new SerializedObject(table);
            SerializedProperty pipelines = so.FindProperty("_pipelineAssets");
            SerializedProperty volumes = so.FindProperty("_volumeProfiles");
            pipelines.arraySize = assets.Length;
            volumes.arraySize = profiles.Length;
            for (int i = 0; i < assets.Length; i++)
            {
                pipelines.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
                volumes.GetArrayElementAtIndex(i).objectReferenceValue = profiles[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);

            var reloaded = AssetDatabase.LoadAssetAtPath<PipelineTierTable>(path);
            if (reloaded == null || reloaded.TierCount != (int)QualityTier.Count)
            {
                throw new InvalidOperationException("[PipelineSetup] CFG_PipelineTiers.asset failed validation.");
            }
        }

        // ---- project settings (raw YAML patches) -------------------------------

        /// <summary>
        /// Points m_CustomRenderPipeline of GraphicsSettings.asset at the
        /// URP_Medium asset (differs from the quality-level binding: this is
        /// the DEFAULT pipeline for sessions/levels without a binding).
        /// </summary>
        public static void PatchGraphicsSettingsYaml(string mediumGuid)
        {
            if (string.IsNullOrEmpty(mediumGuid))
            {
                throw new InvalidOperationException("[PipelineSetup] URP_Medium GUID missing.");
            }

            string[] lines = File.ReadAllLines(GraphicsSettingsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("  m_CustomRenderPipeline:", StringComparison.Ordinal))
                {
                    lines[i] = $"  m_CustomRenderPipeline: {{fileID: 11400000, guid: {mediumGuid}, type: 2}}";
                    File.WriteAllLines(GraphicsSettingsPath, lines, new UTF8Encoding(false));
                    Debug.Log("[PipelineSetup] GraphicsSettings default pipeline -> URP_Medium.");
                    return;
                }
            }

            throw new InvalidOperationException(
                "[PipelineSetup] m_CustomRenderPipeline line not found in GraphicsSettings.asset — layout drift?");
        }

        /// <summary>
        /// Rewrites ProjectSettings/QualitySettings.asset to exactly three
        /// levels (Low/Medium/High) bound to the generated URP assets.
        /// </summary>
        private static void WriteQualitySettingsYaml()
        {
            string[] guids = new string[(int)QualityTier.Count];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = $"{PipelineDir}/{TierMapper.ToPipelineAssetKey((QualityTier)i)}.asset";
                guids[i] = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guids[i]))
                {
                    throw new InvalidOperationException($"[PipelineSetup] no GUID for {path}.");
                }
            }

            var sb = new StringBuilder();
            sb.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!47 &1\nQualitySettings:\n");
            sb.Append("  m_ObjectHideFlags: 0\n  serializedVersion: 5\n");
            sb.Append("  m_CurrentQuality: 1\n  m_QualitySettings:\n");
            for (int i = 0; i < guids.Length; i++)
            {
                string tierName = ((QualityTier)i).ToString();
                sb.Append("  - serializedVersion: 3\n");
                sb.Append($"    name: {tierName}\n");
                sb.Append("    pixelLightCount: 2\n");
                sb.Append("    shadows: 2\n");
                sb.Append("    shadowResolution: 2\n");
                sb.Append("    shadowProjection: 1\n");
                sb.Append($"    shadowCascades: {TierCascades[i]}\n");
                sb.Append("    shadowDistance: 150\n");
                sb.Append("    shadowNearPlaneOffset: 3\n");
                sb.Append("    shadowCascade2Split: {x: 0.33333334, y: 0.2, z: 0.46666667}\n");
                sb.Append("    shadowCascade4Split: {x: 0.06666667, y: 0.2, z: 0.46666667}\n");
                sb.Append("    shadowmaskMode: 1\n");
                sb.Append("    skinWeights: 2\n");
                sb.Append("    globalTextureMipmapLimit: 0\n");
                sb.Append("    textureMipmapLimitSettings: []\n");
                sb.Append("    anisotropicTextures: 1\n");
                sb.Append("    antiAliasing: 0\n");
                sb.Append("    softParticles: 0\n");
                sb.Append("    softVegetation: 1\n");
                sb.Append("    realtimeReflectionProbes: 0\n");
                sb.Append("    billboardsFaceCameraPosition: 1\n");
                sb.Append("    useLegacyDetailDistribution: 0\n");
                sb.Append("    vSyncCount: 1\n");
                sb.Append("    realtimeGICPUUsage: 25\n");
                sb.Append("    lodBias: 1\n");
                sb.Append("    maximumLODLevel: 0\n");
                sb.Append("    enableLODCrossFade: 1\n");
                sb.Append("    streamingMipmapsActive: 0\n");
                sb.Append("    streamingMipmapsAddAllCameras: 1\n");
                sb.Append("    streamingMipmapsMemoryBudget: 512\n");
                sb.Append("    streamingMipmapsRenderersPerFrame: 512\n");
                sb.Append("    streamingMipmapsMaxLevelReduction: 2\n");
                sb.Append("    streamingMipmapsMaxFileIORequests: 1024\n");
                sb.Append("    particleRaycastBudget: 64\n");
                sb.Append("    asyncUploadTimeSlice: 2\n");
                sb.Append("    asyncUploadBufferSize: 16\n");
                sb.Append("    asyncUploadPersistentBuffer: 1\n");
                sb.Append("    resolutionScalingFixedDPIFactor: 1\n");
                sb.Append($"    customRenderPipeline: {{fileID: 11400000, guid: {guids[i]}, type: 2}}\n");
                sb.Append("    terrainQualityOverrides: 0\n");
                sb.Append("    terrainPixelError: 1\n");
                sb.Append("    terrainDetailDensityScale: 1\n");
                sb.Append("    terrainBasemapDistance: 1000\n");
                sb.Append("    terrainDetailDistance: 80\n");
                sb.Append("    terrainTreeDistance: 5000\n");
                sb.Append("    terrainBillboardStart: 50\n");
                sb.Append("    terrainFadeLength: 5\n");
                sb.Append("    terrainMaxTrees: 50\n");
                sb.Append("    excludedTargetPlatforms: []\n");
            }

            sb.Append("  m_TextureMipmapLimitGroupNames: []\n  m_PerPlatformDefaultQuality:\n");
            foreach (string platform in new[]
                     {
                         "Android", "EmbeddedLinux", "GameCoreScarlett", "GameCoreXboxOne",
                         "LinuxHeadlessSimulation", "Nintendo Switch", "PS4", "PS5", "QNX", "Server",
                         "Stadia", "Standalone", "VisionOS", "WebGL", "Windows Store Apps", "XboxOne",
                         "iPhone", "tvOS",
                     })
            {
                sb.Append($"    {platform}: 1\n");
            }

            File.WriteAllText(QualitySettingsPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[PipelineSetup] QualitySettings rewritten: Low/Medium/High bound to URP tier assets.");
        }
    }
}
#endif
