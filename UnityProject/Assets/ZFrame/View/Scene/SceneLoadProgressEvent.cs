namespace ZFrame.View
{
    /// <summary>
    /// Raised on the EventBus whenever the displayed (eased) load progress
    /// changes. <see cref="Normalized"/> is in [0,1] and monotonic for one
    /// load; 1.0 is delivered right before <see cref="SceneLoadedEvent"/>.
    /// </summary>
    public struct SceneLoadProgressEvent
    {
        /// <summary>Scene being loaded.</summary>
        public string SceneName { get; }

        /// <summary>Displayed progress 0..1 (eased, non-regressing).</summary>
        public float Normalized { get; }

        /// <summary>Builds the event.</summary>
        public SceneLoadProgressEvent(string sceneName, float normalized)
        {
            SceneName = sceneName;
            Normalized = normalized;
        }
    }
}
