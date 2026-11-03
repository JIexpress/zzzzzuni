namespace ZFrame.Gameplay
{
    /// <summary>
    /// Tri-state result of one behaviour tree tick (TASK-A-R8-03, teaching
    /// reference: course M14-72). The whole point of behaviour trees over
    /// truth tables: <see cref="BTStatus.Running"/> means "keep ticking me,
    /// I am not done" — composites carry that state across frames.
    /// </summary>
    public enum BTStatus
    {
        /// <summary>The node finished this piece of behaviour successfully.</summary>
        Success = 0,

        /// <summary>The node failed; parents decide what that means.</summary>
        Failure = 1,

        /// <summary>The node needs more ticks; the tree must resume it later.</summary>
        Running = 2,
    }
}
