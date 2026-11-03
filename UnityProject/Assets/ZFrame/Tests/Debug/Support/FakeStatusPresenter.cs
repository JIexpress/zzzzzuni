using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Concrete presenter over <see cref="FakeStatusView"/>: subscribes <see cref="FakeStatusEvent"/> inside its Attach scope.</summary>
    public sealed class FakeStatusPresenter : UIPresenter<FakeStatusView>
    {
        public int AttachCalls { get; private set; }

        public int DetachCalls { get; private set; }

        /// <summary>Args forwarded to the view at attach time (tests assert the injection reached the view).</summary>
        public object AttachArgs { get; private set; }

        public FakeStatusPresenter(FakeStatusView view)
            : base(view)
        {
        }

        protected override void OnAttach(object args)
        {
            AttachCalls++;
            AttachArgs = args;
            View.SetStatus($"attached:{(args as string ?? string.Empty)}");
            EventBus.Subscribe<FakeStatusEvent>(OnStatusEvent);
        }

        protected override void OnDetach()
        {
            DetachCalls++;
        }

        private void OnStatusEvent(FakeStatusEvent evt)
        {
            View.SetStatus(evt.Text);
        }
    }
}
