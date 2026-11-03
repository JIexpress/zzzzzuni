namespace ZFrame.View
{
    /// <summary>
    /// EventBus payload raised by <see cref="PipelineManager.SetTier"/> after a
    /// quality tier change has been applied. Subscribers use this to react
    /// without polling (e.g. settings UI, render-scale dependent systems).
    /// </summary>
    public readonly struct TierChangedEvent
    {
        /// <summary>Tier that was active before the change.</summary>
        public readonly QualityTier From;

        /// <summary>Tier that is active now.</summary>
        public readonly QualityTier To;

        public TierChangedEvent(QualityTier from, QualityTier to)
        {
            From = from;
            To = to;
        }

        public override string ToString()
        {
            return $"TierChangedEvent({From} -> {To})";
        }
    }
}
