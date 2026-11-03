using System;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// TASK-A-M3-03 bridge between the engine-free <see cref="InputBlocker"/>
    /// counter and the Player action map — the integration point A-M2-01 left
    /// open. While <see cref="InputBlocker.Active"/> is true the Player map is
    /// DISABLED (scene loading, modal panels: no gameplay input at all); when
    /// the counter returns to zero the map is re-enabled. The UI map is
    /// ALWAYS enabled (菜单/面板导航不能跟着玩法一起被冻结).
    ///
    /// The gate is a plain C# object (not a MonoBehaviour) so EditMode tests
    /// can assert the enabled state transitions directly — it reacts to
    /// <see cref="InputBlocker.Changed"/> and also exposes
    /// <see cref="Sync"/> for callers that prefer polling.
    /// </summary>
    public sealed class PlayerInputGate : IDisposable
    {
        private readonly ZFrameControls _controls;
        private bool _disposed;

        /// <summary>Subscribes to <see cref="InputBlocker"/> and applies the CURRENT state immediately.</summary>
        public PlayerInputGate(ZFrameControls controls)
        {
            _controls = controls ?? throw new ArgumentNullException(nameof(controls));
            InputBlocker.Changed += Sync;
            Sync();
        }

        /// <summary>True while the Player map is enabled (i.e. gameplay input is live).</summary>
        public bool IsPlayerEnabled => !_disposed && _controls.Player.enabled;

        /// <summary>True while the UI map is enabled (always true for a live gate).</summary>
        public bool IsUiEnabled => !_disposed && _controls.UI.enabled;

        /// <summary>
        /// Re-reads <see cref="InputBlocker.Active"/> and applies it: blocked →
        /// Player.Disable(), otherwise Player.Enable(). UI stays enabled in
        /// both cases. Idempotent — safe to call every frame.
        /// </summary>
        public void Sync()
        {
            if (_disposed || _controls.Asset == null)
            {
                return; // asset torn down (scene unload) — the static counter must not touch it
            }

            _controls.UI.Enable();
            if (InputBlocker.Active)
            {
                _controls.Player.Disable();
            }
            else
            {
                _controls.Player.Enable();
            }
        }

        /// <summary>Unsubscribes from the static counter and disables both maps (the asset itself is owned by <see cref="ZFrameControls"/>).</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            InputBlocker.Changed -= Sync;
            if (_controls.Asset != null)
            {
                _controls.Asset.Disable();
            }
        }
    }
}
