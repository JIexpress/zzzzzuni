using System;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// One entry of the <see cref="TaskBoard"/> (TASK-A-AI-02): an immutable
    /// value record of WHAT to do (kind, e.g. "pickup"/"repair"/"cover"),
    /// WHERE (plane position) and UNTIL WHEN (expiry in the caller's clock
    /// domain — <see cref="float.PositiveInfinity"/> means never expires).
    /// While claimed it additionally carries WHO holds it and WHEN the claim
    /// was taken; the board is the only writer of those two fields, so a
    /// handed-out record never mutates under its reader.
    ///
    /// Pure C#: engine-free like the rest of <c>Game.Domain.AI</c>; the View
    /// maps <see cref="Position"/> onto a world anchor exactly as it does for
    /// <c>NpcUtilityAgent.Position</c>.
    /// </summary>
    public sealed class CoordinationTask
    {
        /// <summary>Creates a task record. Null/empty id or kind throws (identity fields).</summary>
        public CoordinationTask(
            string id,
            string kind,
            Vec2 position,
            float expiresAt,
            CoordinationTaskStatus status,
            string claimedBy,
            float claimedAt)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("CoordinationTask: id must not be empty.", nameof(id));
            }

            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("CoordinationTask: kind must not be empty.", nameof(kind));
            }

            Id = id;
            Kind = kind;
            Position = position;
            ExpiresAt = expiresAt;
            Status = status;
            ClaimedBy = claimedBy ?? string.Empty;
            ClaimedAt = claimedAt;
        }

        /// <summary>Stable identity (board key, rankings, GOAP atoms, logs).</summary>
        public string Id { get; }

        /// <summary>Task type tag (e.g. "pickup", "repair", "cover").</summary>
        public string Kind { get; }

        /// <summary>Where the work happens (plane position).</summary>
        public Vec2 Position { get; }

        /// <summary>Absolute expiry instant in the owner clock; <see cref="float.PositiveInfinity"/> = never.</summary>
        public float ExpiresAt { get; }

        /// <summary>Open (claimable) or Claimed (held by exactly one NPC).</summary>
        public CoordinationTaskStatus Status { get; }

        /// <summary>Id of the holding NPC, or empty while <see cref="CoordinationTaskStatus.Open"/>.</summary>
        public string ClaimedBy { get; }

        /// <summary>Claim instant in the owner clock, or negative while open (no claim yet).</summary>
        public float ClaimedAt { get; }

        /// <summary>
        /// True when <paramref name="now"/> has reached <see cref="ExpiresAt"/>
        /// (到期即失效: at exactly the expiry instant the task is already gone
        /// for claiming purposes). Expiry is absolute-clock, caller-pumped —
        /// never a wall clock.
        /// </summary>
        public bool IsExpired(float now)
        {
            return now >= ExpiresAt;
        }

        /// <summary>Debug form: "task-1[pickup @ (3, 4) open]" / "... claimed by npc-a".</summary>
        public override string ToString()
        {
            string holder = Status == CoordinationTaskStatus.Claimed
                ? " claimed by " + ClaimedBy
                : " open";
            return Id + "[" + Kind + " @ " + Position + holder + "]";
        }
    }
}
