namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Event used to drive presenters from tests over the EventBus.</summary>
    public struct FakeStatusEvent
    {
        /// <summary>Text carried to the presenter.</summary>
        public string Text;
    }
}
