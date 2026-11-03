using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.View
{
    /// <summary>
    /// Serialised tier matrix (TASK-A-M2-03 design point 2): one row per
    /// <see cref="QualityTier"/>, binding the URP pipeline asset and the grade
    /// VolumeProfile the <see cref="PipelineManager"/> activates for that tier.
    ///
    /// Authored by PipelineSetup into
    /// Assets/Settings/Pipeline/Resources/CFG_PipelineTiers.asset so runtime
    /// code can resolve it via <c>Resources.Load</c> (works in the editor AND
    /// in player builds; AssetDatabase paths are not runtime-safe).
    /// Array index == (int)tier — the canonical order is enforced by
    /// PipelineSetup and asserted by the batch validation.
    /// </summary>
    public sealed class PipelineTierTable : ScriptableObject
    {
        [SerializeField] private RenderPipelineAsset[] _pipelineAssets;
        [SerializeField] private VolumeProfile[] _volumeProfiles;

        /// <summary>Number of tiers this table covers (expected: 3).</summary>
        public int TierCount => Mathf.Min(
            _pipelineAssets?.Length ?? 0,
            _volumeProfiles?.Length ?? 0);

        /// <summary>URP asset bound to <paramref name="tier"/> (null out of range / unassigned).</summary>
        public RenderPipelineAsset GetPipelineAsset(QualityTier tier)
        {
            int index = (int)TierMapper.Normalize(tier);
            if (_pipelineAssets == null || index >= _pipelineAssets.Length)
            {
                return null;
            }

            return _pipelineAssets[index];
        }

        /// <summary>Grade VolumeProfile bound to <paramref name="tier"/> (null out of range / unassigned).</summary>
        public VolumeProfile GetVolumeProfile(QualityTier tier)
        {
            int index = (int)TierMapper.Normalize(tier);
            if (_volumeProfiles == null || index >= _volumeProfiles.Length)
            {
                return null;
            }

            return _volumeProfiles[index];
        }
    }
}
