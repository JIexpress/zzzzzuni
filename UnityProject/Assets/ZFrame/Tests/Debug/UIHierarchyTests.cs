using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.Debug.Tests.Support;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// UIRoot structure and layer parenting (TASK-A-M3-01): eight independent
    /// layer canvases with fixed sorting orders, EventSystem guarantee,
    /// EnsureCreated idempotency, and Push parenting Component-view panels onto
    /// the canvas of their configured layer.
    ///
    /// TEST TRADE-OFF (recorded in the task log): MonoBehaviours from TEST
    /// assemblies cannot be added as components (Unity's AddComponent silently
    /// returns null — verified by a batch diagnostic), so the Component-path
    /// tests here use the committed <c>ui_test_panel</c> prefab (TestPanelView
    /// from the runtime ZFrame.View assembly) registered under arbitrary keys
    /// in the FakeResourceService, instead of hand-built stub views.
    /// </summary>
    public sealed class UIHierarchyTests
    {
        private UIRoot _root;
        private FakeResourceService _resources;
        private UIManager _ui;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
            _resources = new FakeResourceService();
            GameObject testPanelPrefab = Resources.Load<GameObject>("ui_test_panel");
            Assert.IsNotNull(testPanelPrefab, "the Setup-generated prefab must be committed under Assets/UI/Resources");
            _resources.Prefabs["hud_panel"] = testPanelPrefab;
            _resources.Prefabs["popup_panel"] = testPanelPrefab;
            _resources.Prefabs["empty_panel"] = new GameObject("empty_panel", typeof(RectTransform));
            _ui = new UIManager(_resources, _root);
        }

        [TearDown]
        public void TearDown()
        {
            UITestEnv.TearDown();
        }

        [Test]
        public void UIRoot_HasEightLayerCanvases_WithFixedSortOrders()
        {
            UIRoot root = UIRoot.EnsureCreated();

            Assert.AreEqual(UIConsts.LayerCount, root.LayerCanvasCount);
            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                Canvas canvas = root.GetLayerCanvas(layer);
                Assert.IsNotNull(canvas, $"layer {layer} must own its canvas");
                Assert.AreEqual(UIConsts.SortOrder(layer), canvas.sortingOrder);
                Assert.IsNotNull(canvas.GetComponent<CanvasScaler>(), $"layer {layer} needs a CanvasScaler");
                Assert.IsNotNull(canvas.GetComponent<GraphicRaycaster>(), $"layer {layer} needs a GraphicRaycaster");
            }

            Assert.IsNotNull(root.GetComponent<UIAnimRunner>(), "root hosts the fade runner");
        }

        [Test]
        public void UIRoot_EnsuresAnEventSystem()
        {
            UIRoot.EnsureCreated();

            EventSystem eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
            Assert.IsNotNull(eventSystem, "UIRoot must guarantee an EventSystem");
            Assert.IsNotNull(eventSystem.GetComponent<StandaloneInputModule>());
        }

        [Test]
        public void EnsureCreated_IsIdempotent_AndAdoptsSingleRoot()
        {
            UIRoot first = UIRoot.EnsureCreated();
            UIRoot second = UIRoot.EnsureCreated();

            Assert.AreSame(first, second);
            Assert.AreEqual(1, UnityEngine.Object.FindObjectsOfType<UIRoot>(true).Length);
        }

        [Test]
        public void Push_ParentsPanelOntoConfiguredLayerCanvas()
        {
            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, PanelConfig.Create("popup_panel", UIConsts.UILayer.Popup)));

            Assert.That(_ui.TryGetView(out TestPanelView panel), "the pushed view must be reachable");
            Assert.AreSame(_root.GetLayerCanvas(UIConsts.UILayer.Popup).transform, panel.transform.parent);
        }

        [Test]
        public void Push_ComponentView_LoadsPrefabThroughResourceService_AndReleasesOnPop()
        {
            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, PanelConfig.Create("hud_panel", UIConsts.UILayer.HUD)));
            Assert.AreEqual(1, _resources.LoadCount, "prefab must be loaded by key through IResourceService");
            Assert.That(_ui.TryGetView(out TestPanelView view));

            Assert.IsTrue(_ui.Pop());
            CollectionAssert.Contains(_resources.ReleasedKeys, "hud_panel");
            Assert.IsTrue(view == null, "a cachePolicy None panel must be destroyed on pop (edit mode: immediately)");
        }

        [Test]
        public void Push_BlockingPanelAbove_ComponentPanel_DisablesItsRaycaster()
        {
            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, PanelConfig.Create("hud_panel", UIConsts.UILayer.HUD)));
            Assert.That(_ui.TryGetView(out TestPanelView hud));
            GraphicRaycaster hudRaycaster = hud.gameObject.AddComponent<GraphicRaycaster>();

            Assert.IsTrue(UITestEnv.Push<FakeAltView>(_ui, PanelConfig.Create(
                null, UIConsts.UILayer.Popup, blockMask: true)));

            Assert.IsFalse(hudRaycaster.enabled, "paused panels must stop consuming raycasts");
        }

        [Test]
        public void Push_PrefabWithoutViewComponent_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => UITestEnv.Push<TestPanelView>(
                _ui, PanelConfig.Create("empty_panel", UIConsts.UILayer.Panel)));
        }
    }
}
