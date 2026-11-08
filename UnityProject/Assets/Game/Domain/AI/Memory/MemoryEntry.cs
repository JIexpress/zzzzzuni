using System;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// One remembered episode (TASK-A-AI-01) — the atom the memory store keeps
    /// and the attitude aggregator consumes. Immutable value type:
    ///
    /// - <see cref="Type"/>: what kind of episode this was (<see cref="MemoryEventType"/>).
    /// - <see cref="Valence"/>: how good/bad it was for the NPC, clamped domain
    ///   −1..1 (−1 = worst, e.g. a full-strength attack received; +1 = best,
    ///   e.g. a gift). The per-dimension attitude gains in
    ///   <see cref="NpcAttitudeProfile"/> scale it further.
    /// - <see cref="Timestamp"/>: when it happened, in the owner's clock
    ///   domain (agent clock — the caller-pumped monotonic seconds, never a
    ///   wall clock; this is what makes the half-life decay testable).
    /// - <see cref="Source"/>: who it is attributed to (<see cref="EntityRef"/>).
    ///
    /// The constructor validates the valence domain (fail fast): NaN or an
    /// out-of-range valence would silently poison every downstream aggregate.
    /// </summary>
    public readonly struct MemoryEntry
    {
        /// <summary>What kind of episode this was.</summary>
        public MemoryEventType Type { get; }

        /// <summary>How good/bad it was for the NPC (−1..1; −1 worst, +1 best).</summary>
        public float Valence { get; }

        /// <summary>When it happened (owner clock domain, seconds).</summary>
        public float Timestamp { get; }

        /// <summary>Who it is attributed to.</summary>
        public EntityRef Source { get; }

        /// <summary>
        /// Creates one entry. Throws when <paramref name="valence"/> is NaN or
        /// outside −1..1, or when <paramref name="timestamp"/> is not finite.
        /// </summary>
        public MemoryEntry(MemoryEventType type, float valence, float timestamp, EntityRef source)
        {
            if (float.IsNaN(valence) || valence < -1f || valence > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(valence), "MemoryEntry: valence must be in -1..1.");
            }

            if (float.IsNaN(timestamp) || float.IsInfinity(timestamp))
            {
                throw new ArgumentOutOfRangeException(nameof(timestamp), "MemoryEntry: timestamp must be finite.");
            }

            Type = type;
            Valence = valence;
            Timestamp = timestamp;
            Source = source;
        }
    }
}
