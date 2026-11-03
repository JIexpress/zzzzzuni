namespace ZFrame.View
{
    /// <summary>Published on <see cref="ZFrame.Core.EventBus"/> after a panel finished opening (OnInit/Attach/OnShow done, pushed on the stack).</summary>
    public struct PanelOpenedEvent
    {
        /// <summary>Prefab key of the opened panel (null for pure test views).</summary>
        public string PrefabKey;

        /// <summary>Concrete view type of the opened panel.</summary>
        public System.Type ViewType;

        /// <summary>Layer the panel was pushed onto.</summary>
        public UIConsts.UILayer Layer;
    }
}
