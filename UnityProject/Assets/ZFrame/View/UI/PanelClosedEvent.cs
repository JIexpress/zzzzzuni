namespace ZFrame.View
{
    /// <summary>Published on <see cref="ZFrame.Core.EventBus"/> after a panel left the stack (hidden; destroyed or cached per its config).</summary>
    public struct PanelClosedEvent
    {
        /// <summary>Prefab key of the closed panel (null for pure test views).</summary>
        public string PrefabKey;

        /// <summary>Concrete view type of the closed panel.</summary>
        public System.Type ViewType;

        /// <summary>Layer the panel was on.</summary>
        public UIConsts.UILayer Layer;
    }
}
