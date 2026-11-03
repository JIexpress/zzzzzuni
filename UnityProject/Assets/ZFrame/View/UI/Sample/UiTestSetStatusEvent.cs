namespace ZFrame.View
{
    /// <summary>Request to change the sample panel's status text — lets tests / other systems drive the presenter → view path over the EventBus.</summary>
    public struct UiTestSetStatusEvent
    {
        /// <summary>New status text.</summary>
        public string Status;
    }
}
