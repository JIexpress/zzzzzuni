using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Sample presenter (TASK-A-M3-01): PURE C# — no UnityEngine import. It
    /// subscribes <see cref="UiTestButtonClickedEvent"/> (fired by the view's
    /// button) and <see cref="UiTestSetStatusEvent"/> inside the Attach scope,
    /// and drives the view through <see cref="TestPanelView.SetStatus"/> /
    /// typed <see cref="UIPresenter{TView}.View"/>. Detach (via
    /// <see cref="UIManager.Pop"/>) bulk-unsubscribes both through the
    /// <see cref="EventBus.EventScope"/>.
    /// </summary>
    public sealed class TestPanelPresenter : UIPresenter<TestPanelView>
    {
        private int _clickCount;

        /// <summary>Creates the presenter for the injected view (registered in <see cref="UIBinding.EnsureDefaults"/>).</summary>
        public TestPanelPresenter(TestPanelView view)
            : base(view)
        {
        }

        protected override void OnAttach(object args)
        {
            View.SetStatus("UI_TestPanel ready");
            EventBus.Subscribe<UiTestButtonClickedEvent>(OnButtonClicked);
            EventBus.Subscribe<UiTestSetStatusEvent>(OnSetStatus);
        }

        private void OnButtonClicked(UiTestButtonClickedEvent evt)
        {
            _clickCount = evt.ClickCount;
            View.SetStatus($"clicked x{evt.ClickCount}");
        }

        private void OnSetStatus(UiTestSetStatusEvent evt)
        {
            View.SetStatus(evt.Status);
        }
    }
}
