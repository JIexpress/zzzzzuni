using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>
    /// Composition root of ZFrame. Place exactly one GameRoot in the boot
    /// scene (SCN_Boot); it survives scene loads, builds the shared loop
    /// objects in a fixed order and registers them in <see cref="Services"/>.
    ///
    /// The actual boot sequence lives in the testable static
    /// <see cref="Bootstrap"/> (pure C#, no MonoBehaviour lifecycle needed):
    ///   ZLog reset → GameTime → Scheduler → TickQueue → Services registration.
    /// <see cref="Awake"/> merely deduplicates, marks DontDestroyOnLoad and
    /// calls Bootstrap, then spawns the <see cref="GameLoopDriver"/> child.
    /// <see cref="OnDestroy"/> tears down in reverse order via
    /// <see cref="Shutdown"/>.
    /// </summary>
    public sealed class GameRoot : MonoBehaviour
    {
        private static GameRoot _instance;

        /// <summary>Live root instance, if a scene hosts one. Read-only convenience.</summary>
        public static GameRoot Instance => _instance;

        /// <summary>True between <see cref="Bootstrap"/> and <see cref="Shutdown"/>.</summary>
        public static bool IsBootstrapped { get; private set; }

        /// <summary>
        /// Lazily ensures a root exists in the active scene (defensive path
        /// when a scene was loaded without a boot scene before it).
        /// </summary>
        public static GameRoot EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var go = new GameObject("GameRoot");
            return go.AddComponent<GameRoot>();
        }

        private void Awake()
        {
            Initialize();
        }

        /// <summary>
        /// The real boot body: dedup, DontDestroyOnLoad, Bootstrap, driver
        /// wiring. Awake calls this in Play Mode; in EditMode tests Awake
        /// never runs (plain MonoBehaviours do not tick in the editor), so
        /// tests call this directly — that is the documented seam that keeps
        /// the whole flow testable without a play session.
        /// </summary>
        public void Initialize()
        {
            if (_instance != null && _instance != this)
            {
                // Duplicate root (scene re-load etc.): kill the newcomer.
                // DestroyImmediate in EditMode: deferred Destroy does not run there.
                Debug.LogWarning("GameRoot: duplicate instance destroyed.", this);
                if (Application.isPlaying)
                {
                    Destroy(gameObject);
                }
                else
                {
                    DestroyImmediate(gameObject);
                }
                return;
            }

            _instance = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            Bootstrap();

            var driverGo = new GameObject("GameLoopDriver");
            driverGo.transform.SetParent(transform, false);
            var driver = driverGo.AddComponent<GameLoopDriver>();
            driver.Init(Services.Get<GameTime>(), Services.Get<TickQueue>(), Services.Get<Scheduler>());
        }

        private void OnDestroy()
        {
            if (_instance != this)
            {
                return; // we were the destroyed duplicate; nothing owns
            }

            Shutdown();
            _instance = null;
        }

        /// <summary>
        /// Builds the shared services in the canonical order. Idempotent:
        /// a second call while bootstrapped is a no-op (this is what makes
        /// duplicate GameRoot destruction safe). Directly callable from
        /// EditMode tests — no GameObject required.
        /// </summary>
        public static void Bootstrap()
        {
            if (IsBootstrapped)
            {
                return;
            }

            // 1. Logging first: everything below may log.
            ZLog.Reset();

            // 2. Clock, then scheduler wired to the clock.
            var time = new GameTime();
            var scheduler = new Scheduler(() => time.TotalUnscaled);

            // 3. Central tick registry.
            var queue = new TickQueue();

            // 4. EventBus is fully static — nothing to build or register.

            // 5. Publish to the service locator (reverse of this order in Shutdown).
            Services.Register<IGameTime>(time);
            Services.Register<GameTime>(time);
            Services.Register<Scheduler>(scheduler);
            Services.Register<TickQueue>(queue);
            Services.Register<ITickQueue>(queue);

            IsBootstrapped = true;
        }

        /// <summary>
        /// Tears down in reverse boot order and clears every registration.
        /// Safe to call even when not bootstrapped (also used by tests).
        /// </summary>
        public static void Shutdown()
        {
            if (IsBootstrapped && Services.TryGet(out Scheduler scheduler))
            {
                scheduler.Clear(); // pending gameplay timers must not survive a boot cycle
            }

            if (IsBootstrapped && Services.TryGet(out TickQueue queue))
            {
                queue.Clear();
            }

            ZLog.Reset();
            Services.Clear(); // wipes IGameTime/GameTime/Scheduler/TickQueue and anything else
            IsBootstrapped = false;
        }
    }
}
