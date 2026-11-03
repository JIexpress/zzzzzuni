using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Loop pump for the grade Volume crossfade (TASK-A-R9-08 briefing design
    /// point 2). <see cref="PipelineManager"/> owns the two stacked grade
    /// Volumes and the pure <see cref="VolumeCrossfadeState"/>; this host is
    /// the ITickable adapter that advances the fade once per frame on the
    /// central game loop — the AudioService/FrameGovernorHost mounting
    /// pattern: self-bootstraps via <see cref="RuntimeInitializeOnLoadMethod"/>,
    /// its Tick runs on the <see cref="ITickQueue"/> at
    /// <see cref="TickStage.View"/> — never Update: the frame order stays
    /// GameLoopDriver-owned. EditMode tests — where MonoBehaviour callbacks
    /// are not driven — use the documented <see cref="Initialize"/> seam
    /// (same as AudioService) and call <see cref="Tick"/> directly.
    ///
    /// Shutdown semantics (briefing design point 2): once this host stops,
    /// nothing advances the fade anymore — a fade frozen mid-blend would
    /// leave the grade at half weights forever. <see cref="Shutdown"/>
    /// therefore lands the fade on its target at full weight
    /// (<see cref="PipelineManager.AbortGradeCrossfade"/>: 恢复目标 profile
    /// 全权重) before unregistering.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VolumeCrossfadeHost : MonoBehaviour, ITickable
    {
        private const string LogChannel = "Pipeline";

        private static VolumeCrossfadeHost _instance;

        private bool _initialized;

        /// <summary>The self-bootstrapped instance (null before boot).</summary>
        public static VolumeCrossfadeHost Instance => _instance;

        /// <summary>
        /// Self-bootstrap before the first scene loads. Mounting only wires
        /// the pump — the crossfade itself lives on the manager and stays
        /// idle until a tier switch or <c>CrossfadeToProfile</c> starts one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~VolumeCrossfadeRoot");
            DontDestroyOnLoad(host);
            host.AddComponent<VolumeCrossfadeHost>().Initialize();
        }

        /// <summary>
        /// Wires loop registration. Idempotent; callable from EditMode tests
        /// (same seam as AudioService.Initialize).
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _instance = this;
            _initialized = true;

            // Loop guarantee: same seam as AudioService/FrameGovernorHost.
            // Bind the RETURNED root, not the static Instance — in EditMode
            // test sequences the static can still reference a destroyed
            // GameRoot while EnsureInstance() has just built a fresh one.
            GameRoot root = GameRoot.EnsureInstance();
            if (!GameRoot.IsBootstrapped)
            {
                root.Initialize();
            }

            Services.Get<ITickQueue>().Register(this, TickStage.View);
            ZLog.Info(LogChannel, "volume crossfade host registered at the View tick stage.");
        }

        /// <summary>
        /// Game-loop tick (View stage, driven by GameLoopDriver via
        /// ITickQueue): advances the manager's grade crossfade. No-op when the
        /// manager has not mounted yet (boot order of
        /// RuntimeInitializeOnLoad callbacks is undefined).
        /// </summary>
        public void Tick(float dt)
        {
            PipelineManager manager = PipelineManager.Instance;
            if (manager != null)
            {
                manager.TickGradeCrossfade(dt);
            }
        }

        /// <summary>
        /// Deterministic teardown seam (also invoked by
        /// <see cref="OnDestroy"/>): lands a running fade on its target at
        /// full weight (nothing would keep ticking it), unregisters from the
        /// game loop. Mirrors the AudioService Initialize/Shutdown pattern so
        /// EditMode tests exercise the exact path the engine runs at object
        /// teardown. Idempotent.
        /// </summary>
        public void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            _initialized = false;

            if (Services.TryGet(out ITickQueue queue))
            {
                queue.Unregister(this);
            }

            PipelineManager manager = PipelineManager.Instance;
            if (manager != null)
            {
                manager.AbortGradeCrossfade(); // 恢复目标 profile 全权重
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
