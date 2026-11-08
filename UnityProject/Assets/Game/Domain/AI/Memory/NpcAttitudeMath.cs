using System;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// The pure math of the attitude system (TASK-A-AI-01) — every function is
    /// a total, deterministic float→float (or entries→attitude) mapping with no
    /// clock, no state and no randomness, mirroring the
    /// <c>Game.Domain.AI.Utility.UtilityCurves</c> discipline so each formula
    /// is directly assertable in EditMode tests:
    ///
    /// - <see cref="DecayWeight"/>: exponential forgetting with a half-life —
    ///   1 at age 0, 0.5 at one half-life, 0.25 at two (半衰期纯函数).
    /// - <see cref="Evaluate"/>: memory + profile → <see cref="NpcAttitude"/>
    ///   (per-type gains × decay weights, negative valence = threat driving
    ///   Fear/Anger, positive = goodwill driving Trust, each dimension clamped).
    /// - <see cref="FleeDesire"/> / <see cref="CounterDesire"/>: the attitude →
    ///   utility MODULATION formulas. Fear amplifies fleeing and suppresses
    ///   attacking; anger amplifies attacking (恐惧→逃跑增益、攻击抑制).
    /// </summary>
    public static class NpcAttitudeMath
    {
        /// <summary>How strongly fear (0..1) multiplies the flee desire at maximum (fear 1 ⇒ ×3).</summary>
        public const float FearFleeBoost = 2f;

        /// <summary>How strongly anger (0..1) multiplies the attack desire at maximum (anger 1 ⇒ ×2.5).</summary>
        public const float AngerAttackBoost = 1.5f;

        /// <summary>How strongly fear (0..1) suppresses the attack desire at maximum (fear 1 ⇒ ×0.2).</summary>
        public const float FearAttackSuppress = 0.8f;

        /// <summary>How strongly trust (0..1) boosts the follow desire at maximum (trust 1 ⇒ ×1.5).</summary>
        public const float TrustFollowBoost = 0.5f;

        /// <summary>
        /// Exponential decay with a half-life: <c>0.5^(age / halfLife)</c>.
        /// Negative ages (future-stamped entries) count as age 0 (full weight);
        /// a non-positive half-life throws — an immortal or instant memory is
        /// nonsense. Pure and hand-checkable: age == halfLife ⇒ exactly 0.5.
        /// </summary>
        /// <param name="ageSeconds">How long ago the episode happened (seconds).</param>
        /// <param name="halfLifeSeconds">Time for the weight to halve (must be > 0).</param>
        public static float DecayWeight(float ageSeconds, float halfLifeSeconds)
        {
            if (halfLifeSeconds <= 0f || float.IsNaN(halfLifeSeconds) || float.IsInfinity(halfLifeSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds), "NpcAttitudeMath.DecayWeight: half-life must be > 0.");
            }

            if (ageSeconds <= 0f)
            {
                return 1f;
            }

            return (float)Math.Pow(0.5, ageSeconds / halfLifeSeconds);
        }

        /// <summary>
        /// Aggregates a memory into a current attitude (纯聚合): for every
        /// entry, its decayed valence splits into
        /// <c>threat = max(0, −valence)</c> and
        /// <c>goodwill = max(0, valence)</c>, then
        /// <c>Fear += decay × threat × profile.FearGain(type)</c>,
        /// <c>Anger += decay × threat × profile.AngerGain(type)</c>,
        /// <c>Trust += decay × goodwill × profile.TrustGain(type)</c>.
        /// The <see cref="NpcAttitude"/> constructor clamps each dimension to
        /// 0..1. Same memory + same profile + same now ⇒ same attitude.
        /// </summary>
        /// <param name="memory">The episodic memory to read (entries are not modified).</param>
        /// <param name="profile">Per-NPC aggregation coefficients.</param>
        /// <param name="now">Current time in the owner clock domain (seconds).</param>
        public static NpcAttitude Evaluate(NpcMemory memory, NpcAttitudeProfile profile, float now)
        {
            if (memory == null)
            {
                throw new ArgumentNullException(nameof(memory));
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            float halfLife = profile.HalfLifeSeconds;
            float fear = 0f;
            float anger = 0f;
            float trust = 0f;
            var entries = memory.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                MemoryEntry entry = entries[i];
                float decay = DecayWeight(now - entry.Timestamp, halfLife);
                float threat = entry.Valence < 0f ? -entry.Valence : 0f;
                float goodwill = entry.Valence > 0f ? entry.Valence : 0f;
                fear += decay * threat * profile.FearGain(entry.Type);
                anger += decay * threat * profile.AngerGain(entry.Type);
                trust += decay * goodwill * profile.TrustGain(entry.Type);
            }

            return new NpcAttitude(fear, anger, trust);
        }

        /// <summary>
        /// The flee-desire MODULATION (恐惧→逃跑增益): desire scales with fear
        /// itself (no fear ⇒ no flight — baseline recovery for free) and is
        /// further amplified by it (<c>1 + FearFleeBoost × fear</c>).
        /// Pure: fear 0 ⇒ 0; fear 1, proximity 1 ⇒ 3.
        /// </summary>
        /// <param name="fear">Current fear, 0..1.</param>
        /// <param name="proximity">Threat proximity 0..1 (1 = at the NPC).</param>
        public static float FleeDesire(float fear, float proximity)
        {
            return proximity * fear * (1f + (FearFleeBoost * fear));
        }

        /// <summary>
        /// The attack-desire MODULATION (攻击类乘抑制): scales with anger (no
        /// anger ⇒ no aggression), amplified by anger
        /// (<c>1 + AngerAttackBoost × anger</c>) and SUPPRESSED by fear
        /// (<c>1 − FearAttackSuppress × fear</c>) — a scared NPC bites its
        /// tongue even when angry. Pure: anger 0 ⇒ 0; anger 1, fear 0 ⇒ 2.5;
        /// anger 1, fear 1 ⇒ 0.5.
        /// </summary>
        /// <param name="anger">Current anger, 0..1.</param>
        /// <param name="fear">Current fear, 0..1.</param>
        /// <param name="proximity">Threat proximity 0..1 (1 = at the NPC).</param>
        public static float CounterDesire(float anger, float fear, float proximity)
        {
            float angerGain = 1f + (AngerAttackBoost * anger);
            float fearSuppress = 1f - (FearAttackSuppress * fear);
            return proximity * anger * angerGain * fearSuppress;
        }

        /// <summary>
        /// The follow-desire MODULATION (信任增益): goodwill earned through
        /// gifts/friendly presence makes company more desirable —
        /// <c>1 + TrustFollowBoost × trust</c> (trust 0 ⇒ identity ×1, so the
        /// pre-attitude Follow desire is bit-identical). Pure.
        /// </summary>
        /// <param name="trust">Current trust, 0..1.</param>
        public static float FollowTrustGain(float trust)
        {
            return 1f + (TrustFollowBoost * trust);
        }
    }
}
