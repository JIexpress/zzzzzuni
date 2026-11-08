using System;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// An NPC's current disposition toward the world (TASK-A-AI-01) — three
    /// independent scalars in 0..1:
    ///
    /// - <see cref="Fear"/>: how threatened the NPC feels (drives fleeing).
    /// - <see cref="Anger"/>: how aggressive the NPC feels (drives fighting back).
    /// - <see cref="Trust"/>: how much goodwill the NPC holds (social layer).
    ///
    /// The constructor clamps every dimension into 0..1: an attitude is a
    /// bounded emotional state, and bounding keeps the downstream utility
    /// modulation inside its designed range no matter how many episodes pile
    /// up. Attitudes are NOT stored — they are re-derived from memory by
    /// <see cref="NpcAttitudeMath.Evaluate"/> every tick, which is what makes
    /// "no new events → attitude recedes with the half-life → behaviour
    /// returns to baseline" (调制可逆) fall out of the math for free.
    /// </summary>
    public readonly struct NpcAttitude : IEquatable<NpcAttitude>
    {
        /// <summary>The all-zero attitude (no disposition; behaviour baseline).</summary>
        public static NpcAttitude Neutral => new NpcAttitude(0f, 0f, 0f);

        /// <summary>0..1 — how threatened the NPC feels.</summary>
        public float Fear { get; }

        /// <summary>0..1 — how aggressive the NPC feels.</summary>
        public float Anger { get; }

        /// <summary>0..1 — how much goodwill the NPC holds.</summary>
        public float Trust { get; }

        /// <summary>Creates an attitude; every dimension is clamped into 0..1.</summary>
        public NpcAttitude(float fear, float anger, float trust)
        {
            Fear = Clamp01(fear);
            Anger = Clamp01(anger);
            Trust = Clamp01(trust);
        }

        /// <summary>True when every dimension matches exactly (tests use this).</summary>
        public bool Equals(NpcAttitude other)
        {
            return Fear.Equals(other.Fear) && Anger.Equals(other.Anger) && Trust.Equals(other.Trust);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is NpcAttitude other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Fear.GetHashCode();
                hash = (hash * 397) ^ Anger.GetHashCode();
                hash = (hash * 397) ^ Trust.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "NpcAttitude(Fear=" + Fear.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                   + ", Anger=" + Anger.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                   + ", Trust=" + Trust.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value))
            {
                return 0f;
            }

            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
