using System;
using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// The ordered measure ladder of the FrameGovernor (TASK-A-R8-01 design
    /// point 1). Built once per enable from the user baseline and immutable
    /// afterwards; <see cref="StateAtIndex"/> derives the accumulated
    /// <see cref="GovernorState"/> for any level so both the core and the host
    /// share one definition of "what should be applied right now".
    ///
    /// Run generation (downgrade order, briefing-fixed):
    ///  1. RenderScale — maxScale−step down to minScale (0.95…0.75 by 0.05),
    ///     skipping every scale not strictly below the user's original scale
    ///     (the governor only ever subtracts quality);
    ///  2. Shadows — one rung per tier below the user tier (高→中→关: High
    ///     yields two rungs, Medium one, Low none), Value = tiers below user;
    ///  3. PostFx — one rung per weight below the user's original weight;
    ///  4. TargetFps — the terminal give-up warning rung, always last.
    ///
    /// Pure C#: no UnityEngine usage — fully EditMode-testable.
    /// </summary>
    public sealed class GovernorLadder
    {
        /// <summary>Briefing bounds of the RenderScale band.</summary>
        public const float MinRenderScale = 0.75f;
        public const float MaxRenderScale = 1.0f;

        /// <summary>Briefing RenderScale step.</summary>
        public const float RenderScaleStep = 0.05f;

        /// <summary>Briefing default PostFx weight rungs (half, then off).</summary>
        public static readonly float[] DefaultPostFxWeights = { 0.5f, 0f };

        private const float Epsilon = 0.0001f;

        private readonly GovernorMeasure[] _measures;

        /// <summary>Normalized user tier captured when the ladder was built (the restore target).</summary>
        public QualityTier UserTier { get; }

        /// <summary>User's renderScale captured when the ladder was built (the restore target).</summary>
        public float OriginalRenderScale { get; }

        /// <summary>User's grade volume weight captured when the ladder was built (the restore target).</summary>
        public float OriginalPostFxWeight { get; }

        /// <summary>Rungs of this ladder in downgrade order (never empty — the TargetFps rung always exists).</summary>
        public IReadOnlyList<GovernorMeasure> Measures => _measures;

        /// <summary>Rung count. Ladder level <c>i</c> (1-based in <see cref="FrameGovernor"/>) = <c>_measures[i - 1]</c>.</summary>
        public int Count => _measures.Length;

        private GovernorLadder(GovernorMeasure[] measures, QualityTier userTier, float originalRenderScale, float originalPostFxWeight)
        {
            _measures = measures;
            UserTier = userTier;
            OriginalRenderScale = originalRenderScale;
            OriginalPostFxWeight = originalPostFxWeight;
        }

        /// <summary>
        /// Builds the ladder for one governance session. Out-of-range
        /// <paramref name="userTier"/> falls back to Medium (same rule as
        /// <see cref="TierMapper.Normalize"/>).
        /// </summary>
        public static GovernorLadder Create(
            QualityTier userTier,
            float originalRenderScale = MaxRenderScale,
            float originalPostFxWeight = 1f,
            float minRenderScale = MinRenderScale,
            float maxRenderScale = MaxRenderScale,
            float renderScaleStep = RenderScaleStep,
            IReadOnlyList<float> postFxWeights = null)
        {
            userTier = TierMapper.Normalize(userTier);

            if (originalRenderScale <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(originalRenderScale), originalRenderScale, "must be positive");
            }

            if (originalPostFxWeight < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(originalPostFxWeight), originalPostFxWeight, "must be >= 0");
            }

            if (minRenderScale <= 0f || minRenderScale > maxRenderScale || renderScaleStep <= 0f || renderScaleStep > maxRenderScale - minRenderScale + Epsilon)
            {
                throw new ArgumentOutOfRangeException(nameof(minRenderScale),
                    $"invalid RenderScale band: min={minRenderScale} max={maxRenderScale} step={renderScaleStep}");
            }

            var rungs = new List<GovernorMeasure>();

            // 1. RenderScale band — descending from maxScale-step to minScale,
            //    only scales strictly below what the user already runs.
            int steps = (int)(((maxRenderScale - minRenderScale) + Epsilon) / renderScaleStep);
            for (int k = 1; k <= steps; k++)
            {
                float scale = (float)Math.Round(maxRenderScale - k * renderScaleStep, 4);
                if (scale < minRenderScale - Epsilon || scale > originalRenderScale - Epsilon)
                {
                    continue;
                }

                rungs.Add(new GovernorMeasure(GovernorMeasureKind.RenderScale, scale, $"RenderScale {scale:0.00}"));
            }

            // 2. Shadows band — one rung per tier below the user tier.
            for (int s = 1; s <= (int)userTier; s++)
            {
                var tier = (QualityTier)((int)userTier - s);
                rungs.Add(new GovernorMeasure(GovernorMeasureKind.Shadows, s, $"Shadows → {tier}"));
            }

            // 3. PostFx band — only weights strictly below the user's weight.
            IReadOnlyList<float> weights = postFxWeights ?? DefaultPostFxWeights;
            for (int w = 0; w < weights.Count; w++)
            {
                float weight = weights[w];
                if (weight < 0f || weight >= 1f)
                {
                    throw new ArgumentOutOfRangeException(nameof(postFxWeights), weight, "weights must be in [0, 1)");
                }

                if (weight > originalPostFxWeight - Epsilon)
                {
                    continue;
                }

                rungs.Add(new GovernorMeasure(GovernorMeasureKind.PostFx, weight, $"PostFx weight {weight:0.00}"));
            }

            // 4. Terminal give-up warning rung.
            rungs.Add(new GovernorMeasure(GovernorMeasureKind.TargetFps, 0f, "TargetFps give-up"));

            return new GovernorLadder(rungs.ToArray(), userTier, originalRenderScale, originalPostFxWeight);
        }

        /// <summary>The measure rung at ladder level <paramref name="level"/> (0 = the None measure).</summary>
        public GovernorMeasure MeasureAtLevel(int level)
        {
            return level > 0 && level <= _measures.Length ? _measures[level - 1] : default;
        }

        /// <summary>
        /// Accumulated desired state at ladder level <paramref name="level"/>
        /// (0 returns the pure user baseline; levels beyond
        /// <see cref="Count"/> clamp to the last rung — the give-up level).
        /// </summary>
        public GovernorState StateAtIndex(int level)
        {
            float scale = OriginalRenderScale;
            int shadowSteps = 0;
            float weight = OriginalPostFxWeight;
            bool giveUp = false;

            int last = Math.Min(Math.Max(level, 0), _measures.Length);
            for (int i = 0; i < last; i++)
            {
                GovernorMeasure rung = _measures[i];
                switch (rung.Kind)
                {
                    case GovernorMeasureKind.RenderScale:
                        scale = rung.Value;
                        break;
                    case GovernorMeasureKind.Shadows:
                        shadowSteps = (int)rung.Value;
                        break;
                    case GovernorMeasureKind.PostFx:
                        weight = rung.Value;
                        break;
                    case GovernorMeasureKind.TargetFps:
                        giveUp = true;
                        break;
                }
            }

            return new GovernorState(scale, shadowSteps, weight, giveUp);
        }
    }
}
