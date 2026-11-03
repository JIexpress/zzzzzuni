namespace ZFrame.View
{
    /// <summary>
    /// The kind of one degradation measure on the FrameGovernor ladder
    /// (TASK-A-R8-01 design point 1). The ladder downgrade order is fixed:
    /// <see cref="RenderScale"/> steps first (cheapest visually), then
    /// <see cref="Shadows"/>, then <see cref="PostFx"/>, and
    /// <see cref="TargetFps"/> is the terminal "target given up" warning.
    /// </summary>
    public enum GovernorMeasureKind
    {
        /// <summary>Ladder level 0 — no governor intervention (user settings untouched).</summary>
        None = 0,

        /// <summary>Dynamic resolution: active URP asset <c>renderScale</c> reduction.</summary>
        RenderScale = 1,

        /// <summary>PipelineManager quality tier downgrade (shadow band, 高→中→关).</summary>
        Shadows = 2,

        /// <summary>Grade volume weight reduction (post-processing band).</summary>
        PostFx = 3,

        /// <summary>Terminal level: every measure is applied and the target frame rate is given up on (warning only).</summary>
        TargetFps = 4,
    }
}
