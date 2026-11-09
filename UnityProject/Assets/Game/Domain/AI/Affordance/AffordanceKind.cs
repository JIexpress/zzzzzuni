namespace Game.Domain.AI.Affordances
{
    /// <summary>
    /// Kinds of world interaction a utility NPC can perceive (TASK-A-AI-03).
    /// Deliberately four: every extra kind needs a matching need-weight
    /// source in <see cref="AffordanceAction"/> and an art/placement story in
    /// the scene slice (A-AI-04) — a longer enum would only add dead options.
    /// </summary>
    public enum AffordanceKind
    {
        /// <summary>A place to sleep/sit down (bed, campfire). Need source: fatigue.</summary>
        Rest,

        /// <summary>A job to do (broken device, workbench). Need source: a pending duty.</summary>
        Work,

        /// <summary>A place to be with someone (tavern table, chat spot). Need source: trust.</summary>
        Social,

        /// <summary>A place to eat (food pot). Need source: baseline (no hunger key yet).</summary>
        Eat,
    }
}
