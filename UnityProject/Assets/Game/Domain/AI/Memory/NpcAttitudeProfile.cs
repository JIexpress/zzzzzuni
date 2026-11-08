using System;
using Game.Domain.AI.Utility;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// Per-NPC attitude aggregation coefficients (TASK-A-AI-01) — "聚合系数每
    /// NPC 可配". Two parts:
    ///
    /// - <see cref="HalfLifeSeconds"/>: the decay half-life of a memory's
    ///   weight (pure, testable — see <see cref="NpcAttitudeMath.DecayWeight"/>).
    /// - One gain per (event type × attitude dimension): how strongly one unit
    ///   of that episode moves that dimension. Zero gain = the episode type
    ///   never moves that dimension (e.g. gifts never scare).
    ///
    /// CROSS-SYSTEM LINK (个性×态度联动, "与个性表同源"):
    /// <see cref="ForPersonality"/> derives a profile from a CFG_NpcPersonality
    /// row — brave NPCs (high <c>BraveryWeight</c>) get a LOW fear gain and a
    /// HIGH anger gain (they shrug off and fight back), timid NPCs the
    /// reverse; trust gains scale with <c>SocialWeight</c>. The personality
    /// table itself is NOT modified (A-TECH-04 semantics untouched) — attitude
    /// is a NEW dimension layered on top, sourced from the same row. A null
    /// row yields the neutral profile. Profiles are also freely constructible,
    /// so an NPC's temperament can be tuned per-instance without any table.
    /// </summary>
    public sealed class NpcAttitudeProfile
    {
        /// <summary>Default half-life (seconds of owner-clock) for neutral profiles.</summary>
        public const float DefaultHalfLifeSeconds = 20f;

        /// <summary>Base fear gain for one unit of <see cref="MemoryEventType.AttackedBy"/> threat.</summary>
        public const float BaseAttackedByFear = 0.6f;

        /// <summary>Base anger gain for one unit of <see cref="MemoryEventType.AttackedBy"/> threat.</summary>
        public const float BaseAttackedByAnger = 0.8f;

        /// <summary>Base fear gain for one unit of <see cref="MemoryEventType.SawDeath"/> threat.</summary>
        public const float BaseSawDeathFear = 0.5f;

        /// <summary>Base trust gain for one unit of <see cref="MemoryEventType.GiftedFrom"/> goodwill.</summary>
        public const float BaseGiftedFromTrust = 0.5f;

        /// <summary>Base trust gain for one unit of <see cref="MemoryEventType.PlayerNear"/> goodwill (信任微增).</summary>
        public const float BasePlayerNearTrust = 0.1f;

        private readonly float _halfLifeSeconds;
        private readonly float _attackedByFear;
        private readonly float _attackedByAnger;
        private readonly float _sawDeathFear;
        private readonly float _giftedFromTrust;
        private readonly float _playerNearTrust;

        /// <summary>
        /// Creates a profile. All gains must be finite and >= 0 (they only ever
        /// amplify); the half-life must be > 0. Fail fast on nonsense.
        /// </summary>
        public NpcAttitudeProfile(
            float halfLifeSeconds,
            float attackedByFear,
            float attackedByAnger,
            float sawDeathFear,
            float giftedFromTrust,
            float playerNearTrust)
        {
            if (halfLifeSeconds <= 0f || float.IsNaN(halfLifeSeconds) || float.IsInfinity(halfLifeSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds), "NpcAttitudeProfile: half-life must be > 0.");
            }

            _halfLifeSeconds = halfLifeSeconds;
            _attackedByFear = RequireNonNegative(attackedByFear, nameof(attackedByFear));
            _attackedByAnger = RequireNonNegative(attackedByAnger, nameof(attackedByAnger));
            _sawDeathFear = RequireNonNegative(sawDeathFear, nameof(sawDeathFear));
            _giftedFromTrust = RequireNonNegative(giftedFromTrust, nameof(giftedFromTrust));
            _playerNearTrust = RequireNonNegative(playerNearTrust, nameof(playerNearTrust));
        }

        /// <summary>The neutral profile (no personality link): base gains, default half-life.</summary>
        public static NpcAttitudeProfile Neutral()
        {
            return new NpcAttitudeProfile(
                DefaultHalfLifeSeconds,
                BaseAttackedByFear,
                BaseAttackedByAnger,
                BaseSawDeathFear,
                BaseGiftedFromTrust,
                BasePlayerNearTrust);
        }

        /// <summary>
        /// Derives a profile from a CFG_NpcPersonality row (同源联动):
        /// fear gains divide by <c>BraveryWeight</c> (brave ⇒ fearless), the
        /// attacked-by anger gain multiplies by it (brave ⇒ retaliatory), and
        /// trust gains scale with <c>SocialWeight</c>. Null row = neutral.
        /// Defensive floor 0.1 keeps a pathological weight from zeroing a gain.
        /// </summary>
        public static NpcAttitudeProfile ForPersonality(NPCPersonalityDef personality, float halfLifeSeconds = DefaultHalfLifeSeconds)
        {
            if (personality == null)
            {
                return Neutral();
            }

            float bravery = SafeWeight(personality.BraveryWeight);
            float social = SafeWeight(personality.SocialWeight);
            return new NpcAttitudeProfile(
                halfLifeSeconds,
                BaseAttackedByFear / bravery,
                BaseAttackedByAnger * bravery,
                BaseSawDeathFear / bravery,
                BaseGiftedFromTrust * social,
                BasePlayerNearTrust * social);
        }

        /// <summary>Decay half-life in owner-clock seconds (pure parameter of the aggregation).</summary>
        public float HalfLifeSeconds => _halfLifeSeconds;

        /// <summary>Fear gain for one unit of <paramref name="type"/> threat (0 = never scares).</summary>
        public float FearGain(MemoryEventType type)
        {
            switch (type)
            {
                case MemoryEventType.AttackedBy: return _attackedByFear;
                case MemoryEventType.SawDeath: return _sawDeathFear;
                default: return 0f;
            }
        }

        /// <summary>Anger gain for one unit of <paramref name="type"/> threat (0 = never enrages).</summary>
        public float AngerGain(MemoryEventType type)
        {
            switch (type)
            {
                case MemoryEventType.AttackedBy: return _attackedByAnger;
                default: return 0f;
            }
        }

        /// <summary>Trust gain for one unit of <paramref name="type"/> goodwill (0 = never builds trust).</summary>
        public float TrustGain(MemoryEventType type)
        {
            switch (type)
            {
                case MemoryEventType.GiftedFrom: return _giftedFromTrust;
                case MemoryEventType.PlayerNear: return _playerNearTrust;
                default: return 0f;
            }
        }

        private static float SafeWeight(float weight)
        {
            if (float.IsNaN(weight) || weight < 0.1f)
            {
                return 0.1f;
            }

            return weight;
        }

        private static float RequireNonNegative(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(parameterName, "NpcAttitudeProfile: gains must be finite and >= 0.");
            }

            return value;
        }
    }
}
