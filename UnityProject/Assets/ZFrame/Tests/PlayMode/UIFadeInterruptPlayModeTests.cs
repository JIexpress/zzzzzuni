using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Data;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-R10-04 (audit A-8) — the one path EditMode structurally cannot see: the
    /// REAL fade coroutine. A push parks inside the fade-in (AnimIn=Fade), the UIRoot
    /// GameObject is deactivated mid-fade (the audit's trigger) and the push must
    /// settle instead of hanging forever. With the pre-fix code the await never
    /// completed, the in-flight mark never cleared and the panel could never be
    /// opened again (UI soft lock). Frame-loop waits carry a realtime ceiling so a
    /// regression degrades into a clean assertion failure, not a batch hang
    /// (BootChainSmokeTests discipline).
    /// </summary>
    public sealed class UIFadeInterruptPlayModeTests
    {
        private const float SettleBudgetSeconds = 10f;

        [UnityTest]
        public IEnumerator Push_FadeInterruptedByRootDeactivation_SettlesWithRollback_AndAllowsRepush()
        {
            UIRoot root = UIRoot.EnsureCreated();
            var resources = new SinglePrefabResources();
            resources.Prefabs["ui_test_panel"] = Resources.Load<GameObject>("ui_test_panel");
            Assert.IsNotNull(resources.Prefabs["ui_test_panel"], "the committed ui_test_panel prefab must be loadable");
            var ui = new UIManager(resources, root);

            Transform canvas = root.GetLayerCanvas(UIConsts.UILayer.Popup).transform;
            int childrenBefore = canvas.childCount;

            Task<bool> push = ui.Push<TestPanelView>(TestPanelView.DefaultConfig); // blocking Popup, AnimIn=Fade
            yield return null; // exactly one frame: the coroutine is mid-flight, well inside the 0.15s window

            Assert.IsFalse(push.IsCompleted, "precondition: the push is parked inside the real fade coroutine");
            Assert.AreEqual(0, ui.StackCount, "an in-flight panel is not on the stack yet");

            root.gameObject.SetActive(false); // the audit trigger: host lost mid-fade → OnDisable → AbortActive

            float deadline = Time.realtimeSinceStartup + SettleBudgetSeconds;
            while (!push.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null; // continuations pump through the sync context; pre-fix this loop never ended
            }

            Assert.IsTrue(push.IsCompleted, "the interrupted fade must settle within the budget (pre-fix: hang forever)");
            Assert.IsTrue(push.IsFaulted, "documented semantic: an interrupted fade rolls the push back");
            Assert.AreEqual(typeof(InvalidOperationException), push.Exception.GetBaseException().GetType());
            Assert.AreEqual(0, ui.StackCount, "rollback leaves the stack empty");
            Assert.AreEqual(0, resources.GetRefCount("ui_test_panel"), "rollback releases the acquired prefab reference");

            yield return null; // Object.Destroy is deferred in play mode — cross a frame before counting children
            Assert.AreEqual(childrenBefore, canvas.childCount, "rollback destroys the half-open instance");

            // The in-flight mark is gone: the very same panel opens normally again.
            root.gameObject.SetActive(true);
            Task<bool> reopen = ui.Push<TestPanelView>(TestPanelView.DefaultConfig);
            deadline = Time.realtimeSinceStartup + SettleBudgetSeconds;
            while (!reopen.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null; // the reopened fade runs its full 0.15s
            }

            Assert.IsTrue(reopen.IsCompleted, "the reopened push must settle");
            Assert.IsTrue(reopen.Result, "the same panel must be pushable after the interrupted push (in-flight cleared)");
            Assert.AreEqual(1, ui.StackCount);

            ui.PopAll(UIConsts.UILayer.Popup); // leave the shared root as we found it
        }

        /// <summary>IResourceService fake with real reference counting over one committed prefab.</summary>
        private sealed class SinglePrefabResources : IResourceService
        {
            public readonly Dictionary<string, GameObject> Prefabs =
                new Dictionary<string, GameObject>(StringComparer.Ordinal);

            public readonly List<string> ReleasedKeys = new List<string>();

            private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            public Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
            {
                if (!Prefabs.TryGetValue(key, out GameObject prefab) || !(prefab is T typed))
                {
                    throw new KeyNotFoundException($"[SinglePrefabResources] no prefab registered for key '{key}'.");
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
                throw new NotSupportedException("[SinglePrefabResources] InstantiateAsync is not used by UIManager.");
            }

            public void ReleaseInstance(GameObject instance)
            {
                throw new NotSupportedException("[SinglePrefabResources] ReleaseInstance is not used by UIManager.");
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
