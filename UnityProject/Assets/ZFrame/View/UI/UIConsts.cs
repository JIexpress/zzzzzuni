namespace ZFrame.View
{
    /// <summary>
    /// UI framework constants (TASK-A-M3-01): the eight fixed render layers.
    ///
    /// Each layer owns ONE independent Canvas under <see cref="UIRoot"/> —
    /// render order is the layer's numeric value used as the canvas
    /// <see cref="Canvas.sortingOrder"/>, so stacking between layers is
    /// deterministic and never depends on sibling order. Game code references
    /// layers exclusively through <see cref="UILayer"/>, never raw ints.
    /// </summary>
    public static class UIConsts
    {
        /// <summary>
        /// Fixed UI layers. The enum value doubles as the layer canvas
        /// sorting order (Background lowest, Top highest).
        /// </summary>
        public enum UILayer
        {
            /// <summary>Full-screen backdrops / world behind UI.</summary>
            Background = 0,

            /// <summary>Always-on HUD elements.</summary>
            HUD = 100,

            /// <summary>Standard full panels (menus, settings).</summary>
            Panel = 200,

            /// <summary>Modal popups above panels.</summary>
            Popup = 300,

            /// <summary>Transient non-blocking toasts.</summary>
            Toast = 400,

            /// <summary>Loading screens — above everything gameplay related.</summary>
            Loading = 500,

            /// <summary>Developer/debug overlay.</summary>
            Debug = 900,

            /// <summary>Escape hatch on top of all other layers.</summary>
            Top = 1000
        }

        /// <summary>All layers in ascending render order. Length is 8 by design (UIRoot builds exactly one canvas per entry).</summary>
        public static readonly UILayer[] AllLayers =
        {
            UILayer.Background,
            UILayer.HUD,
            UILayer.Panel,
            UILayer.Popup,
            UILayer.Toast,
            UILayer.Loading,
            UILayer.Debug,
            UILayer.Top
        };

        /// <summary>Number of layers (8) — UIRoot asserts this many canvases exist.</summary>
        public const int LayerCount = 8;

        /// <summary>Render order (canvas sortingOrder) of <paramref name="layer"/> — the enum value itself.</summary>
        public static int SortOrder(UILayer layer)
        {
            return (int)layer;
        }
    }
}
