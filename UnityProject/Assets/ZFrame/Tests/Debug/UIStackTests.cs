using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Debug.Tests.Support;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Panel stack semantics over pure fakes (TASK-A-M3-01): pause/resume of the
    /// panels below, duplicate-push rejection, cross-layer independence,
    /// PopAll(layer) and the PanelOpened/PanelClosed bus events. Lifecycle
    /// ordering with UGUI objects is covered by the stub/prefab fixtures.
    /// </summary>
    public sealed class UIStackTests
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
        public void Push_BlockingPanel_PausesBelow_Pop_ResumesIt()
        {
            Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD)));
            Assert.IsTrue(UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(
                null, UIConsts.UILayer.Popup, blockMask: true)));

            FakeUIView hud;
            _ui.TryGetView(out hud);
            Assert.AreEqual(1, hud.PauseCount, "blocking Popup over HUD must pause the HUD");
            Assert.AreEqual(0, hud.ResumeCount);

            Assert.IsTrue(_ui.Pop());

            Assert.AreEqual(1, hud.ResumeCount, "after Pop the HUD must be resumed");
        }

        [Test]
        public void Push_NonBlockingPanel_DoesNotPauseBelow()
        {
            Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD)));
            Assert.IsTrue(UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Toast)));

            FakeUIView hud;
            _ui.TryGetView(out hud);
            Assert.AreEqual(0, hud.PauseCount, "a non-blocking Toast must leave the HUD running");
        }

        [Test]
        public void Push_DuplicateViewType_IsRejected()
        {
            var config = PanelConfig.Create(null, UIConsts.UILayer.Panel);
            Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, config));
            int logLengthBefore = UIRecordLog.Entries.Count;

            Assert.IsFalse(UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Popup)));
            Assert.AreEqual(1, _ui.StackCount, "rejected push must not grow the stack");
            Assert.AreEqual(logLengthBefore, UIRecordLog.Entries.Count, "rejected push must not touch any view");
        }

        [Test]
        public void Push_DuplicatePrefabKey_AcrossTypes_IsRejected()
        {
            Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create("same_key", UIConsts.UILayer.Panel)));
            Assert.IsFalse(UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create("same_key", UIConsts.UILayer.Popup)));
            Assert.AreEqual(1, _ui.StackCount);
        }

        [Test]
        public void Stack_CrossLayerPanels_DoNotInterfere()
        {
            Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD)));
            Assert.IsTrue(UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Panel)));
            Assert.IsTrue(UITestEnv.Push<FakeThirdView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Toast)));

            FakeAltView panel;
            _ui.TryGetView(out panel);
            Assert.AreEqual(0, panel.PauseCount, "non-blocking layers must not pause each other");
            Assert.AreEqual(3, _ui.StackCount);
        }

        [Test]
        public void Pop_EmptyStack_ReturnsFalse()
        {
            Assert.IsFalse(_ui.Pop());
        }

        [Test]
        public void Pop_PopsTopmost_ByDefault()
        {
            UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD));
            UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Panel));

            Assert.IsTrue(_ui.Pop());

            Assert.IsTrue(_ui.IsOnStack<FakeUIView>());
            Assert.IsFalse(_ui.IsOnStack<FakeAltView>());
            Assert.IsTrue(UIRecordLog.Entries.Contains("2.OnDestroy"));
        }

        [Test]
        public void PopWithConfig_PopsMatchingPanel_NotNecessarilyTop()
        {
            var hudConfig = PanelConfig.Create("hud", UIConsts.UILayer.HUD);
            var popupConfig = PanelConfig.Create("popup", UIConsts.UILayer.Popup, blockMask: true);
            UITestEnv.Push<FakeUIView>(_ui, hudConfig);
            UITestEnv.Push<FakeAltView>(_ui, popupConfig);

            Assert.IsTrue(_ui.Pop(hudConfig));

            Assert.IsFalse(_ui.IsOnStack<FakeUIView>());
            Assert.IsTrue(_ui.IsOnStack<FakeAltView>());
            Assert.IsFalse(_ui.Pop(hudConfig), "second Pop with the same config must report nothing left");
        }

        [Test]
        public void PopAll_PopsOnlyGivenLayer()
        {
            UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD));
            UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Popup, blockMask: true));
            UITestEnv.Push<FakeThirdView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Toast));

            int popped = _ui.PopAll(UIConsts.UILayer.Toast);

            Assert.AreEqual(1, popped);
            Assert.AreEqual(2, _ui.StackCount);
            Assert.IsFalse(_ui.IsOnStack<FakeThirdView>(), "the Toast layer must be emptied");
            Assert.IsTrue(_ui.IsOnStack<FakeUIView>(), "other layers must be untouched");
            Assert.IsTrue(_ui.IsOnStack<FakeAltView>(), "other layers must be untouched");
        }

        [Test]
        public void PopAll_ThenRemainingBottomPanel_IsResumed()
        {
            UITestEnv.Push<FakeUIView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.HUD));
            UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(null, UIConsts.UILayer.Popup, blockMask: true));

            FakeUIView hud;
            _ui.TryGetView(out hud);
            Assert.AreEqual(1, hud.PauseCount);

            _ui.PopAll(UIConsts.UILayer.Popup);

            Assert.AreEqual(1, hud.ResumeCount);
            Assert.AreEqual(1, _ui.StackCount);
        }

        [Test]
        public void PushFireAndPopFire_BusEvents_WithLayerAndKey()
        {
            UIConsts.UILayer? openedLayer = null;
            UIConsts.UILayer? closedLayer = null;
            string openedKey = null;
            var opened = EventBus.Subscribe<PanelOpenedEvent>(e => { openedLayer = e.Layer; openedKey = e.PrefabKey; });
            var closed = EventBus.Subscribe<PanelClosedEvent>(e => closedLayer = e.Layer);
            try
            {
                var config = PanelConfig.Create("popup_key", UIConsts.UILayer.Popup);
                Assert.IsTrue(UITestEnv.Push<FakeUIView>(_ui, config));
                Assert.AreEqual(UIConsts.UILayer.Popup, openedLayer);
                Assert.AreEqual("popup_key", openedKey);

                Assert.IsTrue(_ui.Pop());
                Assert.AreEqual(UIConsts.UILayer.Popup, closedLayer);
            }
            finally
            {
                opened.Dispose();
                closed.Dispose();
            }
        }
    }
}
