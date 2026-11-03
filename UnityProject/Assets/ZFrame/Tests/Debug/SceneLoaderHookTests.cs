using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode coverage for the SceneLoader before-unload hook seam
    /// (<see cref="SceneLoader.NotifyBeforeSceneUnload"/>) and payload
    /// null-safety. The FULL async sequence (started → progress → gated
    /// activation → loaded, with OnBeforeSceneUnload raised before the old
    /// scene unloads) needs a real SceneManager pump and is PlayMode smoke
    /// scope — noted in the construction report of TASK-A-M2-01.
    /// </summary>
    public sealed class SceneLoaderHookTests
    {
        private StubResourceService _resourceService;

        [SetUp]
        public void SetUp()
        {
            InputBlocker.Reset();
            Services.Clear(); // isolate from any other fixture's registrations
            _resourceService = new StubResourceService();
            Services.Register<IResourceService>(_resourceService);
        }

        [TearDown]
        public void TearDown()
        {
            Services.Clear();
            InputBlocker.Reset();
        }

        [Test]
        public void NotifyBeforeSceneUnload_RaisesSubscriber()
        {
            int calls = 0;
            System.Action handler = () => calls++;
            SceneLoader.OnBeforeSceneUnload += handler;
            try
            {
                SceneLoader.NotifyBeforeSceneUnload();

                Assert.AreEqual(1, calls);
            }
            finally
            {
                SceneLoader.OnBeforeSceneUnload -= handler;
            }
        }

        [Test]
        public void NotifyBeforeSceneUnload_CallsResourceServiceUnloadAllUnused_AfterEvent()
        {
            int unloadCallsAtEventTime = -1;
            System.Action handler = () =>
                unloadCallsAtEventTime = _resourceService.UnloadAllUnusedCalls;
            SceneLoader.OnBeforeSceneUnload += handler;
            try
            {
                SceneLoader.NotifyBeforeSceneUnload();

                Assert.AreEqual(0, unloadCallsAtEventTime,
                    "event subscribers must run BEFORE the built-in resource cleanup");
                Assert.AreEqual(1, _resourceService.UnloadAllUnusedCalls,
                    "NotifyBeforeSceneUnload must call IResourceService.UnloadAllUnused exactly once");
            }
            finally
            {
                SceneLoader.OnBeforeSceneUnload -= handler;
            }
        }

        [Test]
        public void Facade_ForwardsBeforeUnloadHook()
        {
            int calls = 0;
            System.Action handler = () => calls++;
            SceneLoaderFacade.OnBeforeSceneUnload += handler;
            try
            {
                SceneLoader.NotifyBeforeSceneUnload();
                Assert.AreEqual(1, calls);

                SceneLoader.NotifyBeforeSceneUnload();
                Assert.AreEqual(2, calls);
            }
            finally
            {
                SceneLoaderFacade.OnBeforeSceneUnload -= handler;
            }

            SceneLoader.NotifyBeforeSceneUnload();
            Assert.AreEqual(2, calls, "handler must be detached after remove");
        }

        [Test]
        public void ConsumePayload_NoSceneLoadedYet_IsNullSafe()
        {
            Assert.IsNull(SceneLoader.ConsumePayload("anything"));
            Assert.IsFalse(SceneLoader.TryPeekPayload("anything", out string value));
            Assert.IsNull(value);
        }

        [Test]
        public void Facade_LoadSceneAsync_RejectsEmptyName()
        {
            Assert.Throws<System.ArgumentException>(
                () => SceneLoaderFacade.LoadSceneAsync(null));
            Assert.Throws<System.ArgumentException>(
                () => SceneLoaderFacade.LoadSceneAsync(""));
        }

        /// <summary>Minimal IResourceService stub recording UnloadAllUnused calls.</summary>
        private sealed class StubResourceService : IResourceService
        {
            public int UnloadAllUnusedCalls { get; private set; }

            public Task<T> LoadAsync<T>(string key) where T : Object
            {
                throw new System.NotSupportedException("stub");
            }

            public void Release(string key)
            {
            }

            public Task<GameObject> InstantiateAsync(string key, Transform parent = null)
            {
                throw new System.NotSupportedException("stub");
            }

            public void ReleaseInstance(GameObject instance)
            {
            }

            public void UnloadAllUnused()
            {
                UnloadAllUnusedCalls++;
            }

            public int PendingLoadCount => 0;

            public int CachedAssetCount => 0;

            public int LiveInstanceCount => 0;

            public int GetRefCount(string key) => 0;

            public void Clear()
            {
            }
        }
    }
}
