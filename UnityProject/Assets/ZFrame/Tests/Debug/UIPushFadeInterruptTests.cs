using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// TASK-R10-04 (audit A-8): the UIManager consumer side of the fade contract.
    /// An interrupted fade-in (host loss → Cancelled; panel instance destroyed →
    /// Failed) must (1) settle the push instead of hanging it, (2) roll the push
    /// back per the H-01 D3 convention — presenter detached, instance destroyed,
    /// acquired prefab reference released, stack and events untouched — and
    /// (3) clear the in-flight mark so the same panel is pushable again immediately
    /// (before the fix the panel stayed in-flight forever = UI soft lock).
    ///
    /// EditMode cannot run the fade coroutine, so the fades are parked through the
    /// <see cref="UIAnimRunner"/> test seam (HoldFadesForTests) and interrupted via
    /// AbortActive — the same settle point OnDisable/destroy uses — while the push
    /// itself runs the real production await path.
    /// </summary>
    public sealed class UIPushFadeInterruptTests
    {
        private UIRoot _root;
        private UIAnimRunner _runner;
        private RefCountResourceService _resources;
        private UIManager _ui;
        private IDisposable _openedSubscription;
        private int _canvasChildrenBefore;
        private int _panelOpenedCount;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
            _runner = _root.GetComponent<UIAnimRunner>();
            _resources = new RefCountResourceService();
            _resources.Prefabs["fade_panel"] = Resources.Load<GameObject>("ui_test_panel");
            Assert.IsNotNull(_resources.Prefabs["fade_panel"], "the committed ui_test_panel prefab must be loadable");
            _ui = new UIManager(_resources, _root);
            _canvasChildrenBefore = _root.GetLayerCanvas(UIConsts.UILayer.Popup).transform.childCount;
            _panelOpenedCount = 0;
            _openedSubscription = EventBus.Subscribe<PanelOpenedEvent>(_ => _panelOpenedCount++);
        }

        [TearDown]
        public void TearDown()
        {
            _openedSubscription?.Dispose();
            UITestEnv.TearDown();
        }

        [Test]
        public void Push_FadeCancelled_RollsBack_ReleasesResources_AllowsRepush()
        {
            var config = PanelConfig.Create("fade_panel", UIConsts.UILayer.Popup, animIn: PanelAnim.Fade);
            _runner.HoldFadesForTests = true;

            Task<bool> push = _ui.Push<TestPanelView>(config);
            Assert.IsFalse(push.IsCompleted, "precondition: the push is parked inside the fade-in");
            Assert.AreEqual(0, _ui.StackCount, "an in-flight panel is not on the stack yet");

            // The audit's soft-lock window: while the fade is in flight a second Push
            // of the same panel is rejected — and after the interrupt below it must
            // be possible again (in-flight cleared).
            Assert.IsFalse(UITestEnv.Push<TestPanelView>(_ui, config), "in-flight duplicate push is rejected");

            Assert.AreEqual(1, _runner.AbortActive(), "the interrupt settles the parked fade (Cancelled)");

            Assert.IsTrue(push.IsCompleted, "the await must settle — the push must not hang (audit A-8)");
            Assert.IsTrue(push.IsFaulted, "documented semantic: an interrupted fade rolls the push back");
            Assert.AreEqual(typeof(InvalidOperationException), push.Exception.GetBaseException().GetType(),
                "the rollback surfaces as InvalidOperationException (Push's XML doc)");
            Assert.That(push.Exception.GetBaseException().Message, Does.Contain("Cancelled"),
                "the outcome reaches the caller (Cancelled = host loss)");

            Assert.AreEqual(0, _ui.StackCount, "the rolled-back panel never entered the stack");
            Assert.AreEqual(0, _panelOpenedCount, "no PanelOpenedEvent may fire for a rolled-back push");
            CollectionAssert.Contains(_resources.ReleasedKeys, "fade_panel",
                "H-01 D3: the acquired prefab reference is given back");
            Assert.AreEqual(0, _resources.GetRefCount("fade_panel"), "the reference count returns to baseline (no leak)");
            Assert.AreEqual(_canvasChildrenBefore, _root.GetLayerCanvas(UIConsts.UILayer.Popup).transform.childCount,
                "the half-faded instance must not stay on the layer canvas");

            _runner.HoldFadesForTests = false; // back to normal instant fades
            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, config),
                "the in-flight mark is cleared: the same panel is pushable again");
            Assert.AreEqual(1, _ui.StackCount);
        }

        [Test]
        public void Push_FadeFailedByInstanceDestroy_RollsBack_AllowsRepush()
        {
            var config = PanelConfig.Create("fade_panel", UIConsts.UILayer.Popup, animIn: PanelAnim.Fade);
            _runner.HoldFadesForTests = true;

            Task<bool> push = _ui.Push<TestPanelView>(config);
            Assert.IsFalse(push.IsCompleted, "precondition: parked inside the fade-in");

            Transform canvas = _root.GetLayerCanvas(UIConsts.UILayer.Popup).transform;
            Assert.Greater(canvas.childCount, _canvasChildrenBefore, "precondition: the instance is on the canvas");
            UnityEngine.Object.DestroyImmediate(canvas.GetChild(canvas.childCount - 1).gameObject); // audit trigger 2

            Assert.AreEqual(1, _runner.AbortActive());

            Assert.IsTrue(push.IsCompleted, "the await must settle — the push must not hang (audit A-8)");
            Assert.IsTrue(push.IsFaulted, "documented semantic: an interrupted fade rolls the push back");
            Assert.AreEqual(typeof(InvalidOperationException), push.Exception.GetBaseException().GetType());
            Assert.That(push.Exception.GetBaseException().Message, Does.Contain("Failed"),
                "the outcome reaches the caller (Failed = target destroyed)");

            Assert.AreEqual(0, _ui.StackCount, "the rolled-back panel never entered the stack");
            Assert.AreEqual(0, _panelOpenedCount, "no PanelOpenedEvent may fire for a rolled-back push");
            Assert.AreEqual(0, _resources.GetRefCount("fade_panel"), "the acquired reference is released despite the destroy");
            Assert.AreEqual(_canvasChildrenBefore, canvas.childCount, "no orphan instance stays on the canvas");

            _runner.HoldFadesForTests = false;
            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, config), "the same panel is pushable again");
        }

        [Test]
        public void Push_FadeCompletes_PanelCommitted_WithAlphaOne()
        {
            var config = PanelConfig.Create("fade_panel", UIConsts.UILayer.Popup, animIn: PanelAnim.Fade);

            Assert.IsTrue(UITestEnv.Push<TestPanelView>(_ui, config), "a completed fade commits the panel");

            Assert.AreEqual(1, _ui.StackCount);
            Assert.AreEqual(1, _panelOpenedCount);
            Assert.That(_ui.TryGetView(out TestPanelView view));
            CanvasGroup group = view.GetComponent<CanvasGroup>();
            Assert.IsNotNull(group, "the fade path ensures a CanvasGroup on the instance");
            Assert.AreEqual(1f, group.alpha, "the instant edit-mode fade leaves alpha at 1");
        }

        /// <summary>
        /// IResourceService spy with real reference counting (the shared
        /// FakeResourceService's counters stay at 0 on purpose), so the rollback
        /// assertions can prove the acquired prefab reference is given back.
        /// </summary>
        private sealed class RefCountResourceService : IResourceService
        {
            public readonly Dictionary<string, GameObject> Prefabs =
                new Dictionary<string, GameObject>(StringComparer.Ordinal);

            public readonly List<string> ReleasedKeys = new List<string>();

            private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            public Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
            {
                if (!Prefabs.TryGetValue(key, out GameObject prefab) || !(prefab is T typed))
                {
                    throw new KeyNotFoundException($"[RefCountResourceService] no prefab registered for key '{key}'.");
                }

                _refCounts.TryGetValue(key, out int count);
                _refCounts[key] = count + 1;
                return Task.FromResult(typed);
            }

            public void Release(string key)
            {
                ReleasedKeys.Add(key);
                if (_refCounts.TryGetValue(key, out int count) && count > 0)
                {
                    _refCounts[key] = count - 1;
                }
            }

            public int GetRefCount(string key)
            {
                return _refCounts.TryGetValue(key, out int count) ? count : 0;
            }

            public Task<GameObject> InstantiateAsync(string key, Transform parent = null)
            {
                throw new NotSupportedException("[RefCountResourceService] InstantiateAsync is not used by UIManager.");
            }

            public void ReleaseInstance(GameObject instance)
            {
                throw new NotSupportedException("[RefCountResourceService] ReleaseInstance is not used by UIManager.");
            }

            public void UnloadAllUnused()
            {
            }

            public int PendingLoadCount => 0;

            public int CachedAssetCount => Prefabs.Count;

            public int LiveInstanceCount => 0;

            public void Clear()
            {
                _refCounts.Clear();
            }
        }
    }
}
