using NUnit.Framework;
using ZFrame.Debug.Tests.Support;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Lifecycle guarantees over pure fakes (TASK-A-M3-01): exact global call
    /// order (OnInit → OnShow → (OnPause/OnResume)* → OnHide → OnDestroy),
    /// args plumbing through Push, the cachePolicy Once round-trip and the
    /// cache eviction tail (OnDestroy deferred until eviction).
    /// </summary>
    public sealed class UILifecycleTests
    {
        private UIRoot _root;
        private UIManager _ui;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
            _ui = new UIManager(new FakeResourceService(), _root);
        }

        [TearDown]
        public void TearDown()
        {
            UITestEnv.TearDown();
        }

        [Test]
        public void PushBlockPop_ExactLifecycleOrder()
        {
            UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD));
            FakeUIView hud;
            _ui.TryGetView(out hud);

            UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(
                null, UIConsts.UILayer.Popup, blockMask: true));
            FakeAltView popup;
            _ui.TryGetView(out popup);

            Assert.IsTrue(_ui.Pop()); // popup
            Assert.IsTrue(_ui.Pop()); // hud

            CollectionAssert.AreEqual(
                new[]
                {
                    $"{hud.Id}.OnInit",
                    $"{hud.Id}.OnShow",
                    $"{popup.Id}.OnInit",
                    $"{popup.Id}.OnShow",
                    $"{hud.Id}.OnPause",     // blocking popup pushed
                    $"{popup.Id}.OnHide",    // popup popped
                    $"{popup.Id}.OnDestroy",
                    $"{hud.Id}.OnResume",
                    $"{hud.Id}.OnHide",      // hud popped
                    $"{hud.Id}.OnDestroy"
                },
                UIRecordLog.Entries,
                "the full lifecycle must be OnInit→OnShow→(Pause/Resume)*→OnHide→OnDestroy");
        }

        [Test]
        public void Push_ForwardsArgsIntoOnInit()
        {
            var args = new object();
            UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Panel), args);

            FakeUIView view;
            _ui.TryGetView(out view);
            Assert.AreSame(args, view.LastInitArgs);
        }

        [Test]
        public void CachePolicyOnce_PopKeepsInstance_ReuseSkipsDestroy()
        {
            var config = PanelConfig.Create("cached_panel", UIConsts.UILayer.Panel, cachePolicy: PanelCachePolicy.Once);
            UITestEnv.Push<FakeUIView>(_ui, config);
            FakeUIView first;
            _ui.TryGetView(out first);

            Assert.IsTrue(_ui.Pop());
            Assert.IsFalse(first.Destroyed, "cached panels must NOT be destroyed on pop");
            Assert.AreEqual(1, _ui.CachedPanelCount);

            UITestEnv.Push<FakeUIView>(_ui, config);
            FakeUIView second;
            _ui.TryGetView(out second);
            Assert.AreSame(first, second, "the cached instance must be reused");
            Assert.IsFalse(second.Destroyed);

            Assert.IsTrue(_ui.Pop());
            Assert.IsFalse(second.Destroyed, "still cached — no destroy yet");
            Assert.AreEqual(1, _ui.EvictPanelCache());
            Assert.IsTrue(second.Destroyed, "eviction is the deferred OnDestroy of cached panels");
            Assert.AreEqual(0, _ui.CachedPanelCount);
        }

        [Test]
        public void Push_CachedInstance_GetsFreshOnInitArgs()
        {
            var config = PanelConfig.Create("cached_panel", UIConsts.UILayer.Panel, cachePolicy: PanelCachePolicy.Once);
            var firstArgs = new object();
            var secondArgs = new object();
            UITestEnv.Push<FakeUIView>(_ui, config, firstArgs);
            _ui.Pop();
            UITestEnv.Push<FakeUIView>(_ui, config, secondArgs);

            FakeUIView view;
            _ui.TryGetView(out view);
            Assert.AreSame(secondArgs, view.LastInitArgs, "reuse must run OnInit with the new push args");
        }

        [Test]
        public void CachePolicyNone_DestroysOnPop_AndReleasesResourceKey()
        {
            var resources = new FakeResourceService();
            var ui = new UIManager(resources, _root);
            var config = PanelConfig.Create("one_shot", UIConsts.UILayer.Panel);

            Assert.IsTrue(UITestEnv.Push<FakeUIView>(ui, config));
            Assert.IsTrue(ui.Pop());

            CollectionAssert.Contains(resources.ReleasedKeys, "one_shot", "destroy path must release the prefab key");
        }

        [Test]
        public void EvictPanelCache_IgnoresPanelsStillOnStack()
        {
            var config = PanelConfig.Create("cached_panel", UIConsts.UILayer.Panel, cachePolicy: PanelCachePolicy.Once);
            UITestEnv.Push<FakeUIView>(_ui, config);
            Assert.IsTrue(_ui.Pop()); // panel now sits in the cache, off the stack

            UITestEnv.Push<FakeUIView>(_ui, config); // reused from cache — back on the stack

            FakeUIView view;
            _ui.TryGetView(out view);
            Assert.AreEqual(0, _ui.EvictPanelCache(), "a live (stacked) panel must not be evicted even though it is cached");
            Assert.AreEqual(1, _ui.CachedPanelCount);
            Assert.IsFalse(view.Destroyed);
        }
    }
}
