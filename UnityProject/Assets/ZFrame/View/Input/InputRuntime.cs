using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Single boot owner of the Track A input stack (TASK-A-M3-03, extended by
    /// A-M4-01): the committed <c>ZFrameControls.inputactions</c> clone, its
    /// <see cref="RebindService"/> (overrides loaded from
    /// <c>input_bindings.json</c> at boot), the
    /// <see cref="PlayerInputGate"/> that ties the Player map to
    /// <see cref="InputBlocker"/>, and the <see cref="PlayerInputBridge"/> that
    /// turns Move/Interact/Pause into EventBus command events.
    ///
    /// Auto-boots after scene load (same self-mount discipline as
    /// <see cref="UIRoot"/>/<see cref="SettingsEntry"/>); callers that need
    /// bindings earlier call <see cref="EnsureCreated"/> — it is idempotent
    /// and returns false (input disabled, one ZLog error) when the asset is
    /// missing, so a broken asset can never break boot.
    /// </summary>
    public static class InputRuntime
    {
        private static ZFrameControls _controls;
        private static RebindService _bindings;
        private static PlayerInputGate _gate;
        private static PlayerInputBridge _bridge;

        /// <summary>The shared controls wrapper (null before boot / when the asset is missing).</summary>
        public static ZFrameControls Controls => _controls;

        /// <summary>The shared rebind service (null before boot / when the asset is missing).</summary>
        public static RebindService Bindings => _bindings;

        /// <summary>The shared InputBlocker gate (null before boot / when the asset is missing).</summary>
        public static PlayerInputGate Gate => _gate;

        /// <summary>The shared gameplay command bridge (null before boot / when the asset is missing).</summary>
        public static PlayerInputBridge Bridge => _bridge;

        /// <summary>Boots the input stack after the first scene is loaded (runtime only).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            EnsureCreated();
        }

        /// <summary>
        /// Creates the stack once. Returns false when the committed asset is
        /// not in Resources — callers then run without input (no exception).
        /// </summary>
        public static bool EnsureCreated()
        {
            if (_controls != null)
            {
                return true;
            }

            ZFrameControls controls = ZFrameControls.FromResources();
            if (controls == null)
            {
                return false;
            }

            var bindings = new RebindService(controls);
            bindings.Load(); // idempotent: missing/corrupt file keeps the asset defaults

            _controls = controls;
            _bindings = bindings;
            _gate = new PlayerInputGate(controls);
            _bridge = new PlayerInputBridge(controls);
            return true;
        }

        /// <summary>
        /// Tears the stack down (bridge → gate → bindings → asset). Used by
        /// tests and by callers that want a clean re-boot; runtime normally
        /// lets the process own it.
        /// </summary>
        public static void Shutdown()
        {
            _bridge?.Dispose();
            _bridge = null;
            _gate?.Dispose();
            _gate = null;
            _bindings = null;
            _controls?.Dispose();
            _controls = null;
        }
    }
}
