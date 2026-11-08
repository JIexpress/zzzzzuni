using System;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// Trait tags an action can declare so per-NPC personality weights
    /// (CFG_NpcPersonality) modulate its desirability (TASK-A-TECH-04). A
    /// trait is a dimension of temperament — fatigue (self-care), social
    /// (company), curiosity (investigation/collecting), bravery (roaming) —
    /// and maps to exactly one weight column of
    /// <see cref="NPCPersonalityDef"/>.
    ///
    /// Untagged actions are personality-neutral (weight 1.0): duties such as
    /// the GOAP-driven Repair keep their pull regardless of temperament, so
    /// personality shapes PREFERENCES while obligations stay obligations.
    /// </summary>
    public static class UtilityTraits
    {
        /// <summary>Reactions to one's own tiredness (Rest desire).</summary>
        public const string Fatigue = "fatigue";

        /// <summary>Desire for company (Follow desire).</summary>
        public const string Social = "social";

        /// <summary>Desire to investigate and collect (Pickup desire).</summary>
        public const string Curiosity = "curiosity";

        /// <summary>Desire to roam far (Patrol desire).</summary>
        public const string Bravery = "bravery";

        /// <summary>True when <paramref name="trait"/> is a known trait (wiring guard for tests/tooling).</summary>
        public static bool IsKnown(string trait)
        {
            return trait == Fatigue || trait == Social || trait == Curiosity || trait == Bravery;
        }
    }
}
