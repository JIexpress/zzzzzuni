namespace ZFrame.View
{
    /// <summary>
    /// Final outcome of one fade (TASK-R10-04, audit A-8). Exactly one value is
    /// delivered to the awaiter per fade — the single-completion guarantee lives in
    /// <see cref="UIAnimRunner"/> / <see cref="FadeSession"/>.
    /// </summary>
    public enum UIAnimOutcome
    {
        /// <summary>The fade ran to its end (alpha reached the target value).</summary>
        Completed,

        /// <summary>The fade was interrupted from outside: its host (the UIRoot) was deactivated or destroyed while it ran; the fade target itself is fine.</summary>
        Cancelled,

        /// <summary>The fade cannot continue: its target object was destroyed mid-fade.</summary>
        Failed
    }
}
