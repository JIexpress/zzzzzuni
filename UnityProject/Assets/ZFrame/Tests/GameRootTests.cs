using System;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>
    /// EditMode tests for the GameRoot boot flow. The heavy lifting lives in
    /// the static <see cref="GameRoot.Bootstrap"/>/<see cref="GameRoot.Shutdown"/>,
    /// so most cases need no GameObject at all; two cases exercise the real
    /// MonoBehaviour path (Awake dedup + driver wiring).
    /// </summary>
    public class GameRootTests
    {
        [SetUp]
        public void SetUp()
        {
            // Static state must be pristine regardless of test order.
            if (GameRoot.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(GameRoot.Instance.gameObject);
            }

            GameRoot.Shutdown();
            Services.Clear();
            ZLog.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            if (GameRoot.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(GameRoot.Instance.gameObject);
            }

            GameRoot.Shutdown();
            Services.Clear();
            ZLog.Reset();
        }

        [Test]
        public void Bootstrap_RegistersCoreServices()
        {
            GameRoot.Bootstrap();

            Assert.IsTrue(GameRoot.IsBootstrapped);
            Assert.IsInstanceOf<GameTime>(Services.Get<IGameTime>(), "IGameTime must resolve after Bootstrap");
            Assert.IsNotNull(Services.Get<Scheduler>(), "Scheduler must resolve after Bootstrap");
            Assert.IsNotNull(Services.Get<TickQueue>(), "TickQueue must resolve after Bootstrap");
        }

        [Test]
        public void Bootstrap_IsIdempotent_DoesNotDoubleRegister()
        {
            GameRoot.Bootstrap();
            Assert.DoesNotThrow(() => GameRoot.Bootstrap(),
                "second Bootstrap must be a no-op, not a duplicate-registration exception");
        }

        [Test]
        public void Bootstrap_SchedulerIsWiredToGameTimeClock()
        {
            GameRoot.Bootstrap();
            var time = (GameTime)Services.Get<IGameTime>();
            time.Advance(3f);

            var scheduler = Services.Get<Scheduler>();
            int fired = 0;
            scheduler.Delay(1f, () => fired++);

            // Deadline must be TotalUnscaled(3) + 1 = 4: proves the injected clock is live.
            scheduler.Tick(3.5f);
            Assert.AreEqual(0, fired);
            scheduler.Tick(4f);
            Assert.AreEqual(1, fired);
        }

        [Test]
        public void Shutdown_ClearsServicesAndBootstrappedFlag()
        {
            GameRoot.Bootstrap();
            GameRoot.Shutdown();

            Assert.IsFalse(GameRoot.IsBootstrapped);
            Assert.Throws<InvalidOperationException>(
                () => Services.Get<IGameTime>(), "services must be gone after Shutdown");
        }

        [Test]
        public void Bootstrap_AgainAfterShutdown_RebuildsFreshInstances()
        {
            GameRoot.Bootstrap();
            var first = Services.Get<Scheduler>();
            GameRoot.Shutdown();
            GameRoot.Bootstrap();
            var second = Services.Get<Scheduler>();

            Assert.AreNotSame(first, second, "re-bootstrap must build fresh loop objects");
        }

        [Test]
        public void Initialize_BootstrapsAndWiresDriver()
        {
            var go = new GameObject("rootA");
            var root = go.AddComponent<GameRoot>();
            // EditMode: Awake never runs for plain MonoBehaviours — call the
            // documented seam directly (Play Mode Awake does the same).
            root.Initialize();

            Assert.IsNotNull(GameRoot.Instance, "static Instance is set by Initialize");
            Assert.IsTrue(GameRoot.IsBootstrapped);
            Assert.IsNotNull(Services.Get<IGameTime>(), "Initialize path registers services too");
            Assert.IsNotNull(go.GetComponentInChildren<GameLoopDriver>(),
                "driver child is created and wired");
        }

        [Test]
        public void SecondRootInstance_IsDeduplicated()
        {
            var goA = new GameObject("rootA");
            goA.AddComponent<GameRoot>().Initialize();
            var first = GameRoot.Instance;

            var goB = new GameObject("rootB");
            goB.AddComponent<GameRoot>().Initialize();

            Assert.AreSame(first, GameRoot.Instance, "first instance survives");
            Assert.IsTrue(goB == null, "duplicate GameObject is destroyed in EditMode");
        }
    }
}
