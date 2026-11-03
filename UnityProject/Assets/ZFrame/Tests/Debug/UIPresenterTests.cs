using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Debug.Tests.Support;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Presenter tests (TASK-A-M3-01) — PURE C# fixture: no Unity objects are
    /// constructed, the fake view is a plain class. Covers constructor
    /// injection, EventBus-driven view updates, Attach idempotency and the
    /// EventScope bulk-unsubscribe on Detach.
    /// </summary>
    public sealed class UIPresenterTests
    {
        private FakeStatusView _view;
        private FakeStatusPresenter _presenter;

        [SetUp]
        public void SetUp()
        {
            _view = new FakeStatusView();
            _presenter = new FakeStatusPresenter(_view);
        }

        [TearDown]
        public void TearDown()
        {
            _presenter.Detach(); // safety net; every test leaves the bus clean
        }

        [Test]
        public void Attach_GreetsView_AndCountsSubscriber()
        {
            Assert.IsFalse(_presenter.IsAttached);
            Assert.AreEqual(0, EventBus.SubscriberCount<FakeStatusEvent>());

            _presenter.Attach(null);

            Assert.IsTrue(_presenter.IsAttached);
            Assert.AreEqual("attached:", _view.Status, "attach must reach the view (constructor-injected)");
            Assert.AreEqual(1, EventBus.SubscriberCount<FakeStatusEvent>());
            Assert.AreEqual(1, _presenter.AttachCalls);
        }

        [Test]
        public void Attach_ForwardsArgs()
        {
            _presenter.Attach("hero");

            Assert.AreEqual("attached:hero", _view.Status);
            Assert.AreEqual("hero", _presenter.AttachArgs);
        }

        [Test]
        public void EventBusEvent_DrivesViewUpdate_WhileAttached()
        {
            _presenter.Attach(null);

            EventBus.Fire(new FakeStatusEvent { Text = "hp=42" });

            Assert.AreEqual("hp=42", _view.Status);
            CollectionAssert.AreEqual(new[] { "attached:", "hp=42" }, _view.StatusHistory);
        }

        [Test]
        public void Detach_Unsubscribes_EventsNoLongerReachTheView()
        {
            _presenter.Attach(null);
            Assert.AreEqual(1, EventBus.SubscriberCount<FakeStatusEvent>());

            _presenter.Detach();

            Assert.IsFalse(_presenter.IsAttached);
            Assert.AreEqual(0, EventBus.SubscriberCount<FakeStatusEvent>(), "EventScope disposal must unsubscribe OnAttach's subscriptions");
            Assert.AreEqual(1, _presenter.DetachCalls);

            int historyLength = _view.StatusHistory.Count;
            EventBus.Fire(new FakeStatusEvent { Text = "after detach" });
            Assert.AreEqual(historyLength, _view.StatusHistory.Count, "a detached presenter must not receive events");
        }

        [Test]
        public void AttachDetach_AreIdempotent()
        {
            _presenter.Attach(null);
            _presenter.Attach(null);
            Assert.AreEqual(1, _presenter.AttachCalls);
            Assert.AreEqual(1, EventBus.SubscriberCount<FakeStatusEvent>());

            _presenter.Detach();
            _presenter.Detach();
            Assert.AreEqual(1, _presenter.DetachCalls);
            Assert.AreEqual(0, EventBus.SubscriberCount<FakeStatusEvent>());
        }
    }
}
