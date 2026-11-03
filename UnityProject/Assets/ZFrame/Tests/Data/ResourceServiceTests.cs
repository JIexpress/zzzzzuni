using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// EditMode tests for the resource layer. Two tracks per the task brief:
    /// a FakeProvider for exact call/counter assertions, and the real
    /// ResourcesProvider against the committed test assets
    /// (Assets/ZFrame/Tests/Resources/TestAsset.asset + TestCube.prefab).
    /// </summary>
    public class ResourceServiceTests
    {
        /// <summary>Deterministic in-memory provider that counts every call.</summary>
        private sealed class FakeProvider : IAssetProvider
        {
            public int LoadCalls;
            public int ReleaseCalls;
            public readonly Dictionary<string, UnityEngine.Object> Store = new Dictionary<string, UnityEngine.Object>();

            public Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
            {
                LoadCalls++;
                if (Store.TryGetValue(key, out var asset) && asset is T typed)
                {
                    return Task.FromResult(typed);
                }

                throw new InvalidOperationException($"FakeProvider: no '{typeof(T).Name}' stored for key '{key}'.");
            }

            public void Release(string key)
            {
                ReleaseCalls++;
            }

            public bool Exists(string key)
            {
                return Store.ContainsKey(key);
            }
        }

        [SetUp]
        public void SetUp()
        {
            ZFrameData.Shutdown(); // guarantee a clean slate even if a prior test failed mid-way
        }

        /// <summary>FakeProvider preloaded with one TestConfigData under key "k".</summary>
        private static FakeProvider MakeFakeProvider(string key = "k")
        {
            var fake = new FakeProvider();
            fake.Store[key] = ScriptableObject.CreateInstance<TestConfigData>();
            return fake;
        }

        [TearDown]
        public void TearDown()
        {
            // Prevent pollution of other test suites (task brief item 4).
            ZFrameData.Shutdown();
            Services.Clear();
            PoolRegistry.ClearAll();
        }

        private static T Complete<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }

        // ---------- ResourcesProvider (real Resources path) ----------

        [Test]
        public void Provider_Exists_TrueForCommittedAsset()
        {
            var provider = new ResourcesProvider();
            Assert.IsTrue(provider.Exists("TestAsset"));
            Assert.IsTrue(provider.Exists("TestCube"));
        }

        [Test]
        public void Provider_Exists_FalseForUnknownKey()
        {
            var provider = new ResourcesProvider();
            Assert.IsFalse(provider.Exists("No/Such/Asset_zframe"));
        }

        [Test]
        public void Provider_LoadAsync_ReturnsTypedAssetWithPayload()
        {
            var provider = new ResourcesProvider();

            var config = Complete(provider.LoadAsync<TestConfigData>("TestAsset"));

            Assert.IsNotNull(config);
            Assert.IsInstanceOf<TestConfigData>(config);
            Assert.AreEqual(42, config.value);
        }

        [Test]
        public void Provider_LoadAsync_UnknownKey_Throws()
        {
            var provider = new ResourcesProvider();
            Assert.Throws<InvalidOperationException>(
                () => Complete(provider.LoadAsync<GameObject>("No/Such/Asset_zframe")));
        }

        // ---------- Reference counting (FakeProvider path) ----------

        [Test]
        public void RefCount_SecondLoad_ReusesObjectWithoutProviderCall()
        {
            var fake = MakeFakeProvider();
            var service = new ResourceService(fake);

            var first = Complete(service.LoadAsync<TestConfigData>("k"));
            var second = Complete(service.LoadAsync<TestConfigData>("k"));

            Assert.AreSame(first, second);
            Assert.AreEqual(1, fake.LoadCalls, "second Load must be served from cache");
            Assert.AreEqual(2, service.GetRefCount("k"));
            Assert.AreEqual(1, service.CachedAssetCount);
        }

        [Test]
        public void RefCount_ReleaseAboveZero_DoesNotReleaseProvider()
        {
            var fake = MakeFakeProvider();
            var service = new ResourceService(fake);
            Complete(service.LoadAsync<TestConfigData>("k"));
            Complete(service.LoadAsync<TestConfigData>("k"));

            service.Release("k");

            Assert.AreEqual(0, fake.ReleaseCalls, "must not release while count > 0");
            Assert.AreEqual(1, service.GetRefCount("k"));
            Assert.AreEqual(1, service.CachedAssetCount, "asset stays cached while referenced");

            service.Release("k");

            Assert.AreEqual(1, fake.ReleaseCalls, "count zero must release exactly once");
            Assert.AreEqual(0, service.GetRefCount("k"));
            Assert.AreEqual(0, service.CachedAssetCount);
        }

        [Test]
        public void RefCount_AfterZero_ReloadGoesThroughProviderAgain()
        {
            var fake = MakeFakeProvider();
            var service = new ResourceService(fake);
            Complete(service.LoadAsync<TestConfigData>("k"));
            service.Release("k");

            Complete(service.LoadAsync<TestConfigData>("k"));

            Assert.AreEqual(2, fake.LoadCalls, "reload after zero must hit the provider again");
            Assert.AreEqual(1, service.GetRefCount("k"));
        }

        // ---------- Pool integration (real Resources path) ----------

        [Test]
        public void Pool_InstantiateTwice_AfterRelease_ReusesSameGameObject()
        {
            ZFrameData.Init();

            var go1 = Complete(Res.InstantiateAsync("TestCube"));
            var marker1 = go1.GetComponent<TestComponent>();
            Assert.IsNotNull(marker1, "spawned instance must carry TestComponent");
            Assert.AreEqual(TestComponent.Marker, marker1.marker);
            Assert.AreEqual(1, Res.Service.LiveInstanceCount);

            Res.ReleaseInstance(go1);
            Assert.AreEqual(0, Res.Service.LiveInstanceCount);

            var go2 = Complete(Res.InstantiateAsync("TestCube"));

            Assert.AreEqual(go1.GetInstanceID(), go2.GetInstanceID(),
                "second InstantiateAsync must reuse the pooled instance");
            Assert.IsTrue(go2.activeSelf, "pooled instance must come back active");

            Res.ReleaseInstance(go2);
        }

        [Test]
        public void Pool_ReleaseInstance_OfUntrackedObject_DestroysIt()
        {
            ZFrameData.Init();
            var stranger = new GameObject("Stranger");

            Res.ReleaseInstance(stranger);

            Assert.IsTrue(stranger == null, "non-pooled instance must be destroyed, not leaked");
        }

        [Test]
        public void Instantiate_HoldsAssetRef_PerLiveInstance()
        {
            ZFrameData.Init();

            var go = Complete(Res.InstantiateAsync("TestCube"));
            Assert.AreEqual(1, Res.Service.GetRefCount("TestCube"),
                "one live instance = one asset reference");

            Res.ReleaseInstance(go);
            Assert.AreEqual(0, Res.Service.GetRefCount("TestCube"),
                "releasing the instance must return its asset reference");
        }

        // ---------- Facade and lifecycle ----------

        [Test]
        public void Res_BeforeInit_ThrowsWithClearHint()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => Res.LoadAsync<GameObject>("TestCube"));

            StringAssert.Contains("ZFrameData.Init", ex.Message);
        }

        [Test]
        public void ZFrameData_Init_IsIdempotent_AndKeepsSameInstance()
        {
            ZFrameData.Init();
            var first = Res.Service;

            Assert.DoesNotThrow(() => ZFrameData.Init());
            Assert.AreSame(first, Res.Service);
            Assert.IsTrue(ZFrameData.IsInitialized);

            ZFrameData.Shutdown();
            Assert.IsFalse(ZFrameData.IsInitialized);
        }

        [Test]
        public void UnloadAllUnused_ClearsZeroRefLeftoversAndDoesNotThrow()
        {
            ZFrameData.Init();
            // Defensive branch: normal flows never leave zero-ref entries;
            // load+release to zero then call the scene-switch hook.
            var cfg = Complete(Res.LoadAsync<TestConfigData>("TestAsset"));
            Res.Release("TestAsset");
            Assert.AreEqual(0, Res.Service.CachedAssetCount);

            Assert.DoesNotThrow(Res.UnloadAllUnused);
            Assert.IsNotNull(cfg);
        }
    }
}
