namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// Kind of episode an NPC remembers (TASK-A-AI-01). One enum value per
    /// salient social stimulus the memory system can aggregate into an
    /// attitude; each maps to per-dimension attitude gains in
    /// <see cref="NpcAttitudeProfile"/>:
    ///
    /// - <see cref="AttackedBy"/>: violence received from the source — fear
    ///   and anger rise (the brief's "AttackedBy→Fear+Anger↑").
    /// - <see cref="GiftedFrom"/>: benefit received from the source — trust
    ///   rises ("GiftedFrom→Trust↑").
    /// - <see cref="SawDeath"/>: witnessed violence (e.g. the behaviour-tree
    ///   enemy's swing fired as <c>EnemyAttackRequested</c>) — fear rises.
    /// - <see cref="PlayerNear"/>: friendly player activity perceived in
    ///   sight (e.g. <c>ItemPickedUp</c> landing inside the NPC's sight
    ///   radius) — trust rises by a small gain (信任微增).
    /// </summary>
    public enum MemoryEventType
    {
        /// <summary>Violence received from the source (valence negative).</summary>
        AttackedBy = 0,

        /// <summary>Benefit received from the source (valence positive).</summary>
        GiftedFrom = 1,

        /// <summary>Violence witnessed near the NPC (valence negative).</summary>
        SawDeath = 2,

        /// <summary>Friendly player activity perceived nearby (valence positive).</summary>
        PlayerNear = 3,
    }
}
