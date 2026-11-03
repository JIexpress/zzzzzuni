using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Data;
using ZFrame.Data.Tests;

namespace ZFrame.Data.Addressables.Tests
{
    /// <summary>
    /// EditMode tests for the Addressables backend (TASK-A-R8-02). The real
    /// UnityEngine.Addressables static API is isolated behind
    /// <see cref="IAddressablesAdapter"/> — a counting fake stands in here so
    /// the provider's reference counting / reuse / release logic is testable
    /// without an initialized Addressables runtime. The real chain is covered
    /// by one PlayMode smoke test (AddressablesSmokeTests).
    /// </summary>
    public sealed class AddressablesProviderTests
    {
        /// <summary>Counting fake: parks or synchronously completes loads, counts every call.</summary>
        private sealed class FakeAdapter : IAddressablesAdapter
        {
            public readonly Dictionary<string, UnityEngine.Object> Store = new Dictionary<string, UnityEngine.Object>();
            public readonly Dictionary<string, int> LoadCallsPerKey = new Dictionary<string, int>();
            public readonly List<int> ReleasedHandleIds = new List<int>();
            public int TotalLoadCalls;
            public int TotalReleaseCalls;

            /// <summary>When false, LoadAsync parks the task until Resolve/Fail (in-flight sharing tests).</summary>
            public bool CompleteSynchronously = true;

            private readonly Dictionary<string, TaskCompletionSource<AddressablesLoadResult>> _parked =
                new Dictionary<string, TaskCompletionSource<AddressablesLoadResult>>();
            private int _nextHandleId;

            public Task<AddressablesLoadResult> LoadAsync<T>(string key) where T : UnityEngine.Object
            {
                TotalLoadCalls++;
                LoadCallsPerKey.TryGetValue(key, out var calls);
                LoadCallsPerKey[key] = calls + 1;

                // Deliberately NO RunContinuationsAsynchronously: completions run
                // inline on the test's main thread, so blocking GetResult() works
                // in EditMode (mirrors FakeProvider in ResourceServiceTests).
                var completion = new TaskCompletionSource<AddressablesLoadResult>();
                if (!CompleteSynchronously)
                {
                    _parked[key] = completion;
                    return completion.Task;
                }

                if (Store.TryGetValue(key, out var asset))
                {
                    completion.TrySetResult(new AddressablesLoadResult(asset, new FakeHandle(_nextHandleId++)));
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"FakeAdapter: no asset stored for key '{key}'."));
                }

                return completion.Task;
            }

            /// <summary>Completes a parked load with the stored asset (no-op when the store lacks the key).</summary>
            public void Resolve(string key)
            {
                if (_parked.TryGetValue(key, out var parked) && Store.TryGetValue(key, out var asset))
                {
                    _parked.Remove(key);
                    parked.TrySetResult(new AddressablesLoadResult(asset, new FakeHandle(_nextHandleId++)));
                }
            }

            /// <summary>Faults a parked load (failed underlying load).</summary>
            public void Fail(string key, string message)
            {
                if (_parked.TryGetValue(key, out var parked))
                {
                    _parked.Remove(key);
                    parked.TrySetException(new InvalidOperationException(message));
                }
            }

            public void Release(IAddressablesHandle handle)
            {
                TotalReleaseCalls++;
                ReleasedHandleIds.Add(((FakeHandle)handle).Id);
            }

            public bool Exists(string key)
            {
                return Store.ContainsKey(key);
            }

            public int ParkedCount => _parked.Count;
        }

        private sealed class FakeHandle : IAddressablesHandle
        {
            public readonly int Id;

            public FakeHandle(int id)
            {
                Id = id;
            }
        }

        private FakeAdapter _fake;

        [SetUp]
        public void SetUp()
        {
            ZFrameData.Shutdown(); // clean slate even after a failed prior test
            _fake = new FakeAdapter();
            _fake.Store["k"] = ScriptableObject.CreateInstance<TestConfigData>();
        }

        [TearDown]
        public void TearDown()
        {
            ZFrameData.Shutdown();
        }

        private static T Complete<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }

        // ---------- reference counting (FakeAdapter path) ----------

        [Test]
        public void Reuse_SecondLoad_MakesNoUnderlyingCall()
        {
            var provider = new AddressablesProvider(_fake);

            var first = Complete(provider.LoadAsync<TestConfigData>("k"));
            var second = Complete(provider.LoadAsync<TestConfigData>("k"));

            Assert.AreSame(first, second, "same key must resolve to the same asset instance");
            Assert.AreEqual(1, _fake.TotalLoadCalls, "second load must reuse the tracked handle");
            Assert.AreEqual(1, _fake.LoadCallsPerKey["k"]);
            Assert.AreEqual(2, provider.GetRefCount("k"));
            Assert.AreEqual(1, provider.TrackedKeyCount);
        }

        [Test]
        public void Release_OnlyAtZero_ReleasesUnderlyingHandleExactlyOnce()
        {
            var provider = new AddressablesProvider(_fake);
            Complete(provider.LoadAsync<TestConfigData>("k"));
            Complete(provider.LoadAsync<TestConfigData>("k"));

            provider.Release("k");

            Assert.AreEqual(0, _fake.TotalReleaseCalls, "must not release the handle while leases remain");
            Assert.AreEqual(1, provider.GetRefCount("k"));
            Assert.AreEqual(1, provider.TrackedKeyCount, "asset stays tracked while referenced");

            provider.Release("k");

            Assert.AreEqual(1, _fake.TotalReleaseCalls, "zero leases must release the handle exactly once");
            Assert.AreEqual(1, _fake.ReleasedHandleIds.Count);
            Assert.AreEqual(0, provider.GetRefCount("k"));
            Assert.AreEqual(0, provider.TrackedKeyCount);
        }

        [Test]
        public void Reload_AfterFullRelease_GoesUnderlyingAgain()
        {
            var provider = new AddressablesProvider(_fake);
            Complete(provider.LoadAsync<TestConfigData>("k"));
            provider.Release("k");

            Complete(provider.LoadAsync<TestConfigData>("k"));

            Assert.AreEqual(2, _fake.TotalLoadCalls, "reload after zero must hit the adapter again");
            Assert.AreEqual(1, provider.GetRefCount("k"));
        }

        [Test]
        public void ConcurrentInFlight_SharesOneUnderlyingCall()
        {
            _fake.CompleteSynchronously = false;
            var provider = new AddressablesProvider(_fake);

            var first = provider.LoadAsync<TestConfigData>("k");
            var second = provider.LoadAsync<TestConfigData>("k");

            Assert.AreEqual(1, _fake.TotalLoadCalls,
                "a concurrent second load must share the in-flight one, not start another");
            Assert.AreEqual(1, _fake.ParkedCount);

            _fake.Resolve("k");

            Assert.AreSame(Complete(first), Complete(second), "both loads complete with the same asset");
            Assert.AreEqual(2, provider.GetRefCount("k"));
            Assert.AreEqual(1, provider.TrackedKeyCount);

            provider.Release("k");
            provider.Release("k");
            Assert.AreEqual(1, _fake.TotalReleaseCalls);
        }

        [Test]
        public void FailedLoad_FaultsCaller_DropsEntry_NoHandleLeak()
        {
            var provider = new AddressablesProvider(_fake);

            var ex = Assert.Throws<InvalidOperationException>(
                () => Complete(provider.LoadAsync<TestConfigData>("missing")));

            StringAssert.Contains("missing", ex.Message);
            Assert.AreEqual(0, provider.TrackedKeyCount, "a failed load must not leave a dangling entry");
            Assert.AreEqual(0, _fake.TotalReleaseCalls);
        }

        [Test]
        public void Release_UnknownKey_IsTolerated()
        {
            var provider = new AddressablesProvider(_fake);

            Assert.DoesNotThrow(() => provider.Release("nope"));
            Assert.AreEqual(0, _fake.TotalReleaseCalls);
        }

        // ---------- IAssetProvider contract ----------

        [Test]
        public void Exists_DelegatesToAdapter()
        {
            var provider = new AddressablesProvider(_fake);

            Assert.IsTrue(provider.Exists("k"));
            Assert.IsFalse(provider.Exists("unknown_zframe"));
        }

        [Test]
        public void NullOrEmptyKey_Throws()
        {
            var provider = new AddressablesProvider(_fake);

            Assert.Throws<ArgumentException>(() => Complete(provider.LoadAsync<TestConfigData>(null)));
            Assert.Throws<ArgumentException>(() => Complete(provider.LoadAsync<TestConfigData>(string.Empty)));
            Assert.Throws<ArgumentException>(() => provider.Release(null));
        }

        // ---------- the switch (ZFrameData.Init providerMode) ----------

        [Test]
        public void Init_Default_RegistersResourcesProvider()
        {
            ZFrameData.Init();

            Assert.IsTrue(ZFrameData.IsInitialized);
            Assert.IsInstanceOf<ResourcesProvider>(((ResourceService)Res.Service).Provider,
                "default Init must keep the Resources backend (zero breakage)");
        }

        [Test]
        public void UseAddressables_Init_RegistersAddressablesProvider()
        {
            AddressablesBootstrap.UseAddressables();

            ZFrameData.Init(providerMode: AssetProviderMode.Addressables);

            Assert.IsTrue(ZFrameData.IsInitialized);
            var provider = ((ResourceService)Res.Service).Provider;
            Assert.IsInstanceOf<AddressablesProvider>(provider,
                "the switch must register the Addressables backend through the factory");
            Assert.IsNotInstanceOf<ResourcesProvider>(provider);
        }

        [Test]
        public void Init_ExplicitProvider_WinsOverMode()
        {
            var explicitProvider = new AddressablesProvider(_fake);

            ZFrameData.Init(explicitProvider, AssetProviderMode.Addressables);

            Assert.AreSame(explicitProvider, ((ResourceService)Res.Service).Provider,
                "a passed-in provider must be used verbatim, whatever the mode says");
        }

        [Test]
        public void RegisterProviderFactory_NullFactory_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => ZFrameData.RegisterProviderFactory(AssetProviderMode.Addressables, null));
        }

        [Test]
        public void Init_IsIdempotent_KeepsFirstBackend()
        {
            ZFrameData.Init();
            var first = Res.Service;

            AddressablesBootstrap.UseAddressables();
            ZFrameData.Init(providerMode: AssetProviderMode.Addressables); // no-op: already registered

            Assert.AreSame(first, Res.Service, "Init must not re-register over a live service");
            Assert.IsInstanceOf<ResourcesProvider>(((ResourceService)Res.Service).Provider);
        }
    }
}
