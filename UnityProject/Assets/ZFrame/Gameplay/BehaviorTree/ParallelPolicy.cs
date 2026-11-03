namespace ZFrame.Gameplay
{
    /// <summary>
    /// Completion policy of <see cref="Parallel"/> (TASK-A-R8-03).
    /// </summary>
    public enum ParallelPolicy
    {
        /// <summary>Succeeds only when EVERY child succeeds; the first Failure aborts the whole parallel (AND semantics).</summary>
        RequireAll = 0,

        /// <summary>Succeeds as soon as ANY child succeeds; only fails when every child failed (OR semantics).</summary>
        RequireOne = 1,
    }
}
