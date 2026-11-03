namespace ZFrame.View
{
    /// <summary>Open/close animation of a panel (TASK-A-M3-01 keeps this deliberately minimal: None or a simple alpha fade).</summary>
    public enum PanelAnim
    {
        /// <summary>No animation — lifecycle calls happen immediately.</summary>
        None,

        /// <summary>Simple CanvasGroup alpha fade (in on show, out before hide). Runtime only; skipped in edit mode so tests stay deterministic.</summary>
        Fade
    }
}
