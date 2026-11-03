using System;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ZFrame.View
{
    /// <summary>
    /// Editor-period switch between the two coexisting SRP assets
    /// (TASK-A-M2-03 design point 3; the design decision "运行期禁止跨管线
    /// 热切" is enforced here):
    ///
    ///  - "URP"     → Assets/Settings/Pipeline/URP_Medium.asset (tier matrix default)
    ///  - "ZRender" → Assets/Settings/ZRenderPipeline.asset (Track B, pre-existing)
    ///
    /// <see cref="SelectToEditor"/> is an EDITOR-ONLY operation (writes the
    /// Graphics settings default pipeline + SaveAssets). Calling it in play
    /// mode throws — the player can never hot-swap across pipelines. In
    /// player builds the whole editor branch is compiled out and the call
    /// always throws.
    ///
    /// Note on precedence: with the tier matrix active, the QualitySettings
    /// level binding (URP_Low/Medium/High per level) overrides the Graphics
    /// DEFAULT pipeline for the effective
    /// <see cref="GraphicsSettings.currentRenderPipeline"/>. The selector
    /// therefore warns when the current level pins a pipeline; switching the
    /// default is still persisted for sessions/machines without the binding.
    /// </summary>
    public static class RenderPipelineSelector
    {
        /// <summary>Key selecting the URP tier-matrix default asset.</summary>
        public const string KeyUrp = "URP";

        /// <summary>Key selecting the Track B ZRender pipeline asset.</summary>
        public const string KeyZRender = "ZRender";

        /// <summary>Editor period asset paths per selector key.</summary>
        public const string UrpAssetPath = "Assets/Settings/Pipeline/URP_Medium.asset";
        public const string ZRenderAssetPath = "Assets/Settings/ZRenderPipeline.asset";

        /// <summary>
        /// Enumerates the pipeline the graphics settings currently resolve to:
        /// "URP", "ZRender", "BuiltIn" or the raw asset name as fallback.
        /// </summary>
        public static string CurrentKey
        {
            get
            {
                RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
                if (asset == null)
                {
                    return "BuiltIn";
                }

                string name = asset.name;
                if (name.StartsWith(KeyUrp + "_", StringComparison.Ordinal))
                {
                    return KeyUrp;
                }

                if (name.StartsWith(KeyZRender, StringComparison.Ordinal))
                {
                    return KeyZRender;
                }

                return name;
            }
        }

        /// <summary>
        /// Pure guard predicate: cross-pipeline selection is allowed ONLY in
        /// the editor's edit (non-playing) mode. Exposed for unit tests — the
        /// throwing path is the guard below (SelectToEditor, line ~100).
        /// </summary>
        public static bool IsEditorSwitchAllowed(bool isPlaying)
        {
            return !isPlaying;
        }

        /// <summary>
        /// Editor-period switch to the pipeline registered under
        /// <paramref name="key"/> ("URP" / "ZRender"). Persists the default
        /// pipeline asset. Throws <see cref="InvalidOperationException"/> in
        /// play mode (runtime cross-pipeline switching is forbidden by
        /// design) and <see cref="ArgumentException"/> for unknown keys.
        /// </summary>
        public static void SelectToEditor(string key)
        {
            // RUNTIME GUARD — forbidden by the R-design: no hot pipeline swap
            // outside editor edit mode. Do not relax this check.
            if (!IsEditorSwitchAllowed(Application.isPlaying))
            {
                throw new InvalidOperationException(
                    "RenderPipelineSelector: 运行期禁止跨管线切换（仅编辑期）— key=" + key);
            }

#if UNITY_EDITOR
            string path = key switch
            {
                KeyUrp => UrpAssetPath,
                KeyZRender => ZRenderAssetPath,
                _ => throw new ArgumentException(
                    $"RenderPipelineSelector: unknown key '{key}' (expected '{KeyUrp}' or '{KeyZRender}').", nameof(key)),
            };

            var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
            if (asset == null)
            {
                throw new InvalidOperationException($"RenderPipelineSelector: pipeline asset missing at {path}.");
            }

            GraphicsSettings.defaultRenderPipeline = asset;
            AssetDatabase.SaveAssets();

            // Quality-level bindings override the default — surface that
            // honestly instead of silently leaving the editor unchanged.
            if (GraphicsSettings.currentRenderPipeline != asset)
            {
                UnityEngine.Debug.LogWarning(
                    $"[RenderPipelineSelector] default pipeline set to '{asset.name}', but the active quality level " +
                    $"'{QualitySettings.names[QualitySettings.GetQualityLevel()]}' pins " +
                    $"'{GraphicsSettings.currentRenderPipeline?.name}' — quality binding wins (tier matrix is active).");
            }
            else
            {
                UnityEngine.Debug.Log($"[RenderPipelineSelector] default pipeline -> '{asset.name}'");
            }
#else
            throw new InvalidOperationException(
                "RenderPipelineSelector: editor-period switching does not exist in player builds.");
#endif
        }
    }
}
