namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// Lifecycle of a <see cref="CoordinationTask"/> ON the board
    /// (TASK-A-AI-02). Completed tasks leave the board entirely (the owner
    /// learns completion from <see cref="TaskBoard.Complete"/>'s return
    /// value), so a task on the board is either <see cref="Open"/> (claimable
    /// by any NPC) or <see cref="Claimed"/> (held by exactly one NPC — the
    /// claim mutex).
    /// </summary>
    public enum CoordinationTaskStatus
    {
        /// <summary>Nobody holds the task; any NPC may claim it.</summary>
        Open,

        /// <summary>Exactly one NPC holds the task (<see cref="CoordinationTask.ClaimedBy"/>).</summary>
        Claimed
    }
}
