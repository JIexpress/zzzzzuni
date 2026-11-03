namespace ZFrame.View
{
    /// <summary>How <see cref="UIManager"/> keeps a panel instance after Pop.</summary>
    public enum PanelCachePolicy
    {
        /// <summary>Destroy the view (and release the prefab reference) on Pop — full lifecycle including OnDestroy.</summary>
        None,

        /// <summary>Keep the instantiated view alive (hidden) after Pop; the next Push with the same prefab key reuses it (OnDestroy is skipped until the cache is cleared).</summary>
        Once
    }
}
