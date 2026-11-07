using System;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// Pure score curves for utility actions (TASK-A-TECH-01). Every function
    /// here is a total, deterministic mapping float → float with no clock, no
    /// state and no randomness — the testable core of utility AI: monotonicity
    /// and endpoint behaviour are asserted directly in EditMode tests, so the
    /// shaped desire curves the demo actions compose stay provable.
    /// </summary>
    public static class UtilityCurves
    {
        /// <summary>Clamps <paramref name="value"/> into 0..1.</summary>
        public static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }

        /// <summary>
        /// Linear ramp: 0 at or below <paramref name="low"/>, 1 at or above
        /// <paramref name="high"/>, linear in between. The classic "desire
        /// grows with need" curve (fatigue → rest desire).
        /// </summary>
        public static float Ramp(float value, float low, float high)
        {
            if (high <= low)
            {
                throw new ArgumentOutOfRangeException(nameof(high), "UtilityCurves.Ramp: high must be > low.");
            }

            if (value <= low)
            {
                return 0f;
            }

            if (value >= high)
            {
                return 1f;
            }

            return (value - low) / (high - low);
        }

        /// <summary>
        /// Diminishing returns: <paramref name="value"/>/<paramref name="saturation"/>
        /// clamped to 0..1 — desire rises fast while the resource is scarce and
        /// flattens once it is plentiful (inventory → pickup desire decay).
        /// </summary>
        public static float Diminishing(float value, float saturation)
        {
            if (saturation <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(saturation), "UtilityCurves.Diminishing: saturation must be > 0.");
            }

            return Clamp01(value / saturation);
        }

        /// <summary>Linear fall: 1 at or below <paramref name="high"/>, 0 at or above <paramref name="low"/> (inverse of a ramp).</summary>
        public static float Fall(float value, float high, float low)
        {
            return 1f - Ramp(value, high, low);
        }
    }
}
