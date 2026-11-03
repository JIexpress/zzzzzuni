namespace ZFrame.View
{
    /// <summary>
    /// Result of one <see cref="FrameGovernor.Tick"/>: whether the active
    /// ladder level moved and between which rungs. The host turns a changed
    /// decision into a <see cref="GovernorMeasureChangedEvent"/> on the
    /// EventBus plus the corresponding settings application; unchanged
    /// decisions are pure no-ops. A struct (not an event) so the pure core
    /// stays free of global state.
    /// </summary>
    public readonly struct GovernorDecision
    {
        /// <summary>True when the active ladder level changed this tick.</summary>
        public readonly bool Changed;

        /// <summary>Ladder level before the change (0 = no measure).</summary>
        public readonly int FromIndex;

        /// <summary>Ladder level after the change (0 = no measure; max = TargetFps give-up rung).</summary>
        public readonly int ToIndex;

        /// <summary>Rung that was active before (default struct at level 0).</summary>
        public readonly GovernorMeasure From;

        /// <summary>Rung that is active now (default struct at level 0).</summary>
        public readonly GovernorMeasure To;

        private GovernorDecision(bool changed, int fromIndex, int toIndex, GovernorMeasure from, GovernorMeasure to)
        {
            Changed = changed;
            FromIndex = fromIndex;
            ToIndex = toIndex;
            From = from;
            To = to;
        }

        public static GovernorDecision None(int index)
        {
            return new GovernorDecision(false, index, index, default, default);
        }

        public static GovernorDecision Change(int fromIndex, int toIndex, GovernorMeasure from, GovernorMeasure to)
        {
            return new GovernorDecision(true, fromIndex, toIndex, from, to);
        }

        public override string ToString()
        {
            return Changed ? $"GovernorDecision({FromIndex} -> {ToIndex}: {From} -> {To})" : $"GovernorDecision(none @{FromIndex})";
        }
    }
}
