using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Data;
using ZFrame.Data.Addressables;
using ZFrame.Data.Tests;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// PlayMode smoke of the Addressables backend (TASK-A-R8-02) — the one
    /// REAL UnityEngine.Addressables chain in the suite: switch
    /// (<c>UseAddressables</c> + <c>ZFrameData.Init(providerMode: Addressables)</c>)
    /// → Addressables.InitializeAsync (asset-database play mode over the
    /// committed settings asset, see AddressablesRuntimeAdapter) →
    /// LoadAssetAsync → ResourceService cache reuse → release → reload.
    /// EditMode covers the reference-counting logic via the fake adapter;
    /// this case proves the switch and the load against the real package.
    ///
    /// Batch discipline (same as BootChainSmokeTests): NO wall-clock waits —
    /// every wait is a frame loop with a realtime timeout ceiling. The test
    /// restores the Resources backend in its body so test order cannot
    /// poison the boot-chain cases.
    /// </summary>
    public sealed class AddressablesSmokeTests
    {
        [UnityTest]
        public IEnumerator Addressables_SwitchRealChain_LoadTestAssetAndCube()
        {
            // -- switch: install + register the Addressables backend ---------
            AddressablesBootstrap.UseAddressables();
            ZFrameData.Shutdown();
            ZFrameData.Init(providerMode: AssetProviderMode.Addressables);

            Assert.IsTrue(ZFrameData.IsInitialized, "ZFrameData did not initialize in Addressables mode");
            var service = (ResourceService)Res.Service;
            Assert.IsInstanceOf<AddressablesProvider>(service.Provider,
                "Res.Service must run on the Addressables backend after the switch");

            // -- real chain: load the SO payload through Addressables --------
            var configTask = Res.LoadAsync<TestConfigData>("TestAsset");
            yield return WaitFor(configTask, "Addressables load TestAsset");
            Assert.IsNotNull(configTask.Result, "TestAsset did not load");
            Assert.AreEqual(42, configTask.Result.value, "TestAsset payload did not round-trip");

            // cache reuse: second load is served synchronously, same instance
            var againTask = Res.LoadAsync<TestConfigData>("TestAsset");
            Assert.IsTrue(againTask.IsCompleted, "cached key must complete synchronously");
            Assert.AreSame(configTask.Result, againTask.Result, "second load must reuse the cached asset");

            // -- real chain: load a GameObject asset through Addressables ----
            var cubeTask = Res.LoadAsync<GameObject>("TestCube");
            yield return WaitFor(cubeTask, "Addressables load TestCube");
            Assert.IsNotNull(cubeTask.Result, "TestCube did not load");
            StringAssert.StartsWith("TestCube", cubeTask.Result.name, "wrong prefab loaded");

            // -- release to zero, then reload goes through Addressables again
            Res.Release("TestAsset");
            Res.Release("TestAsset");
            Assert.AreEqual(0, service.GetRefCount("TestAsset"), "count must be back to zero");

            var reloadTask = Res.LoadAsync<TestConfigData>("TestAsset");
            yield return WaitFor(reloadTask, "Addressables reload TestAsset");
            Assert.AreEqual(42, reloadTask.Result.value, "reload after full release failed");
            Res.Release("TestAsset");

            // -- restore the default backend so other tests are unaffected ---
            ZFrameData.Shutdown();
            ZFrameData.Init();
            Assert.IsInstanceOf<ResourcesProvider>(((ResourceService)Res.Service).Provider,
                "the default backend must be restored for subsequent tests");
        }

        /// <summary>Frame-loop wait with a realtime timeout ceiling; fails on faulted tasks.</summary>
        private static IEnumerator WaitFor(Task task, string what, float timeoutSeconds = 30f)
        {
            float start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail($"timeout ({timeoutSeconds:F0}s) waiting for: {what}");
                }

                yield return null;
            }

            if (task.IsFaulted)
            {
                Assert.Fail($"{what} faulted: {task.Exception?.GetBaseException().Message}");
            }
        }
    }
}
