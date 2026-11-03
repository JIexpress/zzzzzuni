namespace ZFrame.View
{
    /// <summary>Raised on the EventBus when an asynchronous scene load has fully activated (operation done).</summary>
    public struct SceneLoadedEvent
    {
        /// <summary>Scene that finished loading.</summary>
        public string SceneName { get; }

        /// <summary>Builds the event.</summary>
        public SceneLoadedEvent(string sceneName)
        {
            SceneName = sceneName;
        }
    }
}
