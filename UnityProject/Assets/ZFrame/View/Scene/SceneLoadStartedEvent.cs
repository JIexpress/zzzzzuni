namespace ZFrame.View
{
    /// <summary>Raised on the EventBus when an asynchronous scene load starts (before the first progress tick).</summary>
    public struct SceneLoadStartedEvent
    {
        /// <summary>Scene being loaded (build-settings scene name).</summary>
        public string SceneName { get; }

        /// <summary>Builds the event.</summary>
        public SceneLoadStartedEvent(string sceneName)
        {
            SceneName = sceneName;
        }
    }
}
