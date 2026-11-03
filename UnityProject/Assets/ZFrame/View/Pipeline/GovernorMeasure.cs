namespace ZFrame.View
{
    /// <summary>
    /// One concrete rung of the FrameGovernor ladder (TASK-A-R8-01 design
    /// point 1). <see cref="Kind"/> selects the knob, <see cref="Value"/> its
    /// target and <see cref="Label"/> a human-readable form for logs/HUD:
    /// RenderScale steps carry the scale itself (0.95 … 0.75), Shadows steps
    /// carry the number of tiers below the user tier (1 = one notch down),
    /// PostFx steps carry the desired grade volume weight, and the TargetFps
    /// rung carries 0 (it is a warning, not a knob).
    /// </summary>
    public readonly struct GovernorMeasure
    {
        /// <summary>Which knob this rung turns.</summary>
        public readonly GovernorMeasureKind Kind;

        /// <summary>Knob target (unit depends on <see cref="Kind"/> — see type doc).</summary>
        public readonly float Value;

        /// <summary>Human-readable form (null for the None measure).</summary>
        public readonly string Label;

        public GovernorMeasure(GovernorMeasureKind kind, float value, string label)
        {
            Kind = kind;
            Value = value;
            Label = label;
        }

        public override string ToString()
        {
            return Label ?? Kind.ToString();
        }
    }
}
