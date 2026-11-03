using System;

namespace ZFrame.View
{
    /// <summary>
    /// Hysteresis tuning of <see cref="FrameGovernor"/> (TASK-A-R8-01 design
    /// point 1). The frame-time deadband is
    /// <c>[TargetMs * (1 - UpThreshold), TargetMs * (1 + DownThreshold)]</c>:
    /// a sample strictly below the band accumulates the UP timer, strictly
    /// above the band the DOWN timer, inside the band both timers reset.
    /// <see cref="UpWindowSeconds"/> (N) must be strictly greater than
    /// <see cref="DownWindowSeconds"/> (M) so that upgrading is slower than
    /// downgrading — the anti-oscillation rule enforced by the constructor.
    /// </summary>
    public readonly struct GovernorTuning
    {
        /// <summary>
        /// Upper bound for the per-tick timer accumulation in seconds. A single
        /// huge tick (level load hitch, breakpoint) must not by itself satisfy
        /// a "sustained for M seconds" window — very low frame rates simply
        /// accumulate the clamped value over several ticks instead.
        /// </summary>
        public const float MaxDeltaSeconds = 0.5f;

        /// <summary>Fraction below target counting as "comfortable" (briefing: +10% headroom).</summary>
        public readonly float UpThreshold;

        /// <summary>Fraction above target counting as "over budget" (briefing: -10% over).</summary>
        public readonly float DownThreshold;

        /// <summary>N — sustained comfort seconds required before stepping UP one rung. Must be &gt; <see cref="DownWindowSeconds"/>.</summary>
        public readonly float UpWindowSeconds;

        /// <summary>M — sustained overload seconds before stepping DOWN one rung.</summary>
        public readonly float DownWindowSeconds;

        /// <summary>Briefing defaults: ±10% thresholds, N = 5s &gt; M = 2s.</summary>
        public static GovernorTuning Default => new GovernorTuning(0.10f, 0.10f, 5f, 2f);

        /// <summary>
        /// Validates the anti-oscillation invariant (N &gt; M) and threshold
        /// ranges; throws <see cref="ArgumentException"/> on violation so a
        /// misconfigured governor cannot be constructed.
        /// </summary>
        public GovernorTuning(float upThreshold, float downThreshold, float upWindowSeconds, float downWindowSeconds)
        {
            if (upThreshold <= 0f || upThreshold >= 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(upThreshold), upThreshold, "must be in (0, 1)");
            }

            if (downThreshold <= 0f || downThreshold >= 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(downThreshold), downThreshold, "must be in (0, 1)");
            }

            if (upWindowSeconds <= 0f || downWindowSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(upWindowSeconds), upWindowSeconds, "windows must be positive");
            }

            if (upWindowSeconds <= downWindowSeconds)
            {
                throw new ArgumentException(
                    $"upWindowSeconds ({upWindowSeconds:0.##}) must be greater than downWindowSeconds ({downWindowSeconds:0.##}) — N > M is the anti-oscillation rule");
            }

            UpThreshold = upThreshold;
            DownThreshold = downThreshold;
            UpWindowSeconds = upWindowSeconds;
            DownWindowSeconds = downWindowSeconds;
        }

        public override string ToString()
        {
            return $"GovernorTuning(up ±{UpThreshold * 100f:0}%, N={UpWindowSeconds:0.##}s, M={DownWindowSeconds:0.##}s)";
        }
    }
}
