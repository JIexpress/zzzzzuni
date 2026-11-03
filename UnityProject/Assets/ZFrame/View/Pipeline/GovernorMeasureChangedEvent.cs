namespace ZFrame.View
{
    /// <summary>
    /// EventBus payload raised by <see cref="FrameGovernorHost"/> whenever the
    /// governor moves one rung on the measure ladder (TASK-A-R8-01 design
    /// point 1, event shape <c>GovernorMeasureChanged{measure, from, to}</c>).
    /// Fired only on actual level changes — never inside the hysteresis
    /// deadband and never when the governor is disabled.
    /// </summary>
    public readonly struct GovernorMeasureChangedEvent
    {
        /// <summary>Kind of the now-active rung (convenience equal to <see cref="To"/>.Kind).</summary>
        public readonly GovernorMeasureKind Measure;

        /// <summary>Rung that was active before the change (default at ladder level 0).</summary>
        public readonly GovernorMeasure From;

        /// <summary>Rung that is active now.</summary>
        public readonly GovernorMeasure To;

        public GovernorMeasureChangedEvent(GovernorMeasure from, GovernorMeasure to)
        {
            From = from;
            To = to;
            Measure = to.Kind;
        }

        public override string ToString()
        {
            return $"GovernorMeasureChanged({From} -> {To})";
        }
    }
}
