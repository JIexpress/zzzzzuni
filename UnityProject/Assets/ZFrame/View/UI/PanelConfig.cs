using System;

namespace ZFrame.View
{
    /// <summary>
    /// Data-driven description of one panel (TASK-A-M3-01): which prefab to
    /// load, which layer it renders on, whether opening it blocks the panels
    /// below, how the instance is cached and how it animates. Plain C# class
    /// + static factories instead of a ScriptableObject — panels are described
    /// in code and stay unit-testable without assets.
    /// </summary>
    public sealed class PanelConfig
    {
        /// <summary>Resource key of the panel prefab (loaded through <see cref="ZFrame.Data.IResourceService"/>). Null for pure-C# test views.</summary>
        public string PrefabKey { get; set; }

        /// <summary>Render layer — decides which layer canvas the panel is parented to.</summary>
        public UIConsts.UILayer Layer { get; set; }

        /// <summary>True = opening this panel blocks the input of (and pauses) the panels below it on the stack.</summary>
        public bool BlockMask { get; set; }

        /// <summary>Instance cache behaviour after Pop.</summary>
        public PanelCachePolicy CachePolicy { get; set; }

        /// <summary>Open animation.</summary>
        public PanelAnim AnimIn { get; set; }

        /// <summary>Close animation.</summary>
        public PanelAnim AnimOut { get; set; }

        /// <summary>Create a config with explicit values (missing behaviour fields default to the inert ones).</summary>
        public static PanelConfig Create(
            string prefabKey,
            UIConsts.UILayer layer,
            bool blockMask = false,
            PanelCachePolicy cachePolicy = PanelCachePolicy.None,
            PanelAnim animIn = PanelAnim.None,
            PanelAnim animOut = PanelAnim.None)
        {
            return new PanelConfig
            {
                PrefabKey = prefabKey,
                Layer = layer,
                BlockMask = blockMask,
                CachePolicy = cachePolicy,
                AnimIn = animIn,
                AnimOut = animOut
            };
        }
    }
}
