namespace ZFrame.View
{
    /// <summary>Fired by the sample panel's button (TestPanelView) — the presenter listens and updates the status text.</summary>
    public struct UiTestButtonClickedEvent
    {
        /// <summary>How many times the button was clicked in this panel's lifetime (set by the view).</summary>
        public int ClickCount;
    }
}
