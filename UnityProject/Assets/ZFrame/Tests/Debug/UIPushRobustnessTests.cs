using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Data;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Robustness of <see cref="UIManager.Push{TView}"/> (TASK-H-01 D3):
    ///  - a panel is only on the stack AFTER its fade-in, so the duplicate check must
    ///    also see the "still opening" state (in-flight), otherwise the same panel can
    ///    be opened twice while the fade runs;
    ///  - a Push that fails half-way must give back the prefab reference it acquired and
    ///    must not leave an orphan instance on the layer canvas.
    ///
    /// The "still opening" window is reproduced re-entrantly: the resource layer calls
    /// back into Push while the first Push is between the duplicate check and the stack
    /// insert. In play mode that same window is the fade-in (EditMode fades are instant,
    /// see <see cref="UIAnimRunner"/>), so this stays deterministic without a frame pump.
    /// </summary>
    public sealed class UIPushRobustnessTests
    {
        private UIRoot _root;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
        }

        [TearDown]
        public void TearDown()
        {
            UITestEnv.TearDown();
        }

        [Test]
        public void Push_WhileTheSamePanelIsStillOpening_RejectsTheSecondPush()
        {
            GameObject prefab = Resources.Load<GameObject>("ui_test_panel");
            Assert.IsNotNull(prefab, "the Setup-generated prefab must be committed under Assets/UI/Resources");

            var resources = new SpyResourceService();
            resources.Prefabs["opening_panel"] = prefab;
            var ui = new UIManager(resources, _root);

            var nestedResults = new List<bool>();
            bool reentered = false;
            resources.OnLoad = _ =>
            {
                if (reentered)
                {
                    return;
                }

                reentered = true;
                nestedResults.Add(UITestEnv.Push<TestPanelView>(
                    ui, PanelConfig.Create("opening_panel", UIConsts.UILayer.Panel)));
            };

            Assert.IsTrue(UITestEnv.Push<TestPanelView>(
                ui, PanelConfig.Create("opening_panel", UIConsts.UILayer.Panel)));

            Assert.AreEqual(1, nestedResults.Count, "the second Push must be attempted exactly once");
            Assert.IsFalse(nestedResults[0],
                "a panel that is still opening (prefab load / fade-in) must reject the second Push of the same panel");
            Assert.AreEqual(1, ui.StackCount, "the rejected push must not add a second record");
            Assert.AreEqual(1, resources.LoadCount, "the rejected push must not even load the prefab again");
            Assert.AreEqual(1, resources.GetRefCount("opening_panel"),
                "exactly one acquired reference may exist for the single live panel");
        }

        [Test]
        public void Push_WhenThePrefabHasNoViewComponent_ReleasesTheAcquiredReference()
        {
            var resources = new SpyResourceService();
            var brokenPrefab = new GameObject("broken_panel", typeof(RectTransform));
            resources.Prefabs["broken_panel"] = brokenPrefab;

            GameObject validPrefab = Resources.Load<GameObject>("ui_test_panel");
            Assert.IsNotNull(validPrefab);
            resources.Prefabs["good_panel"] = validPrefab;

            var ui = new UIManager(resources, _root);
            Canvas canvas = _root.GetLayerCanvas(UIConsts.UILayer.Panel);
            int childrenBefore = canvas.transform.childCount;

            try
            {
                Assert.Throws<InvalidOperationException>(() => UITestEnv.Push<TestPanelView>(
                    ui, PanelConfig.Create("broken_panel", UIConsts.UILayer.Panel)));

                Assert.AreEqual(1, resources.LoadCount, "the prefab reference was acquired before the failure");
                CollectionAssert.Contains(resources.ReleasedKeys, "broken_panel",
                    "a Push that fails after acquiring the prefab must release it");
                Assert.AreEqual(0, resources.GetRefCount("broken_panel"),
                    "the reference count must return to its baseline (no leak)");
                Assert.AreEqual(0, ui.StackCount, "a failed push must not grow the stack");
                Assert.AreEqual(childrenBefore, canvas.transform.childCount,
                    "the half-created instance must not stay on the layer canvas");

                // The in-flight mark of the failed push must be gone, otherwise this panel
                // type could never be opened again.
                Assert.IsTrue(UITestEnv.Push<TestPanelView>(
                    ui, PanelConfig.Create("good_panel", UIConsts.UILayer.Panel)),
                    "the same view type must be pushable after a failed push");
                Assert.AreEqual(1, ui.StackCount);
                Assert.AreEqual(1, resources.GetRefCount("good_panel"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(brokenPrefab);
            }
        }

        /// <summary>
        /// IResourceService spy: reference-counts like the real service, records releases
        /// and can call back into the caller from inside <see cref="LoadAsync{T}"/>.
        /// </summary>
        private sealed class SpyResourceService : IResourceService
        {
            public readonly Dictionary<string, GameObject> Prefabs =
                new Dictionary<string, GameObject>(StringComparer.Ordinal);

            public readonly List<string> ReleasedKeys = new List<string>();

            private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Invoked synchronously inside every successful load (re-entrancy hook).</summary>
            public Action<string> OnLoad;

            public int LoadCount { get; private set; }

            public Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
            {
                if (!Prefabs.TryGetValue(key, out GameObject prefab) || !(prefab is T typed))
                {
                    throw new KeyNotFoundException($"[SpyResourceService] no prefab registered for key '{key}'.");
                }

                LoadCount++;
                _refCounts.TryGetValue(key, out int count);
                _refCounts[key] = count + 1;

                OnLoad?.Invoke(key);
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
                throw new NotSupportedException("[SpyResourceService] InstantiateAsync is not used by UIManager.");
            }

            public void ReleaseInstance(GameObject instance)
            {
                throw new NotSupportedException("[SpyResourceService] ReleaseInstance is not used by UIManager.");
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
