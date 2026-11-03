using System;
using UnityEngine;
using UnityEngine.InputSystem;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace ZFrame.View
{
    /// <summary>
    /// TASK-A-M4-01 bridge between the Player action map and the engine-free
    /// command events: Move/Interact/Pause are translated into
    /// <see cref="MoveInputEvent"/> / <see cref="InteractInputEvent"/> /
    /// <see cref="PauseInputEvent"/> on the <see cref="EventBus"/>.
    ///
    /// This class is the ONLY place where gameplay input meets the logic
    /// layer — Domain and Gameplay never see an <c>InputAction</c> (the
    /// architecture validator's purity scan enforces it). Because the Player
    /// map is disabled by <see cref="PlayerInputGate"/> while
    /// <see cref="InputBlocker"/> is active, no event is published during
    /// scene loads or modal panels: the gate does the filtering, the bridge
    /// stays dumb.
    ///
    /// TASK-A-AND-04: on-screen controls (touch stick / interact / pause) do
    /// not open a second path — they call <see cref="ApplyVirtualMove"/> /
    /// <see cref="ApplyVirtualInteract"/> / <see cref="ApplyVirtualPause"/>,
    /// which run through the very same <c>Forward*</c> bodies the action
    /// callbacks use (and honour the same gate).
    ///
    /// Lifetime: created and owned by <see cref="InputRuntime"/>; callers that
    /// build a private stack must dispose it (the action subscriptions are
    /// per-asset, not static).
    /// </summary>
    public sealed class PlayerInputBridge : IDisposable
    {
        private readonly ZFrameControls _controls;
        private bool _disposed;

        /// <summary>Subscribes to the Player map. The maps themselves are enabled by <see cref="PlayerInputGate"/>.</summary>
        public PlayerInputBridge(ZFrameControls controls)
        {
            _controls = controls ?? throw new ArgumentNullException(nameof(controls));

            _controls.Move.performed += OnMove;
            _controls.Move.canceled += OnMove;
            _controls.Interact.performed += OnInteract;
            _controls.Pause.performed += OnPause;
        }

        /// <summary>True while the bridge forwards input.</summary>
        public bool IsActive => !_disposed;

        /// <summary>Last movement vector forwarded (diagnostics/tests).</summary>
        public Vec2 LastMove { get; private set; }

        /// <summary>Number of <see cref="InteractInputEvent"/> published (diagnostics/tests).</summary>
        public int InteractCount { get; private set; }

        /// <summary>Number of <see cref="PauseInputEvent"/> published (diagnostics/tests).</summary>
        public int PauseCount { get; private set; }

        /// <summary>Unsubscribes from every action. Idempotent.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _controls.Move.performed -= OnMove;
            _controls.Move.canceled -= OnMove;
            _controls.Interact.performed -= OnInteract;
            _controls.Pause.performed -= OnPause;
        }

        /// <summary>
        /// TASK-A-AND-04: feeds a value produced OUTSIDE an InputAction (the
        /// on-screen stick) through the SAME forward path as a real Move
        /// action, so gameplay still sees exactly one producer and one event.
        ///
        /// Gating: while <see cref="PlayerInputGate"/> has the Player map
        /// disabled (scene load / modal panel) a non-zero value is swallowed —
        /// the touch UI must not be a hole in <see cref="InputBlocker"/>. The
        /// ZERO vector is always forwarded: a release that happens while input
        /// is blocked must still reset the last published direction, otherwise
        /// the motor would resume with a stale vector after the block lifts.
        /// </summary>
        public void ApplyVirtualMove(Vec2 value)
        {
            if (_disposed)
            {
                return;
            }

            if (value.SqrMagnitude > 0f && !_controls.Move.enabled)
            {
                return;
            }

            ForwardMove(value);
        }

        /// <summary>
        /// TASK-A-AND-04: on-screen interact press, forwarded exactly like a
        /// real Interact action (respects the same gate).
        /// </summary>
        public void ApplyVirtualInteract()
        {
            if (_disposed || !_controls.Interact.enabled)
            {
                return;
            }

            ForwardInteract();
        }

        /// <summary>
        /// TASK-A-AND-04: on-screen pause press, forwarded exactly like a real
        /// Pause action (respects the same gate).
        /// </summary>
        public void ApplyVirtualPause()
        {
            if (_disposed || !_controls.Pause.enabled)
            {
                return;
            }

            ForwardPause();
        }

        private void OnMove(InputAction.CallbackContext context)
        {
            if (_disposed)
            {
                return;
            }

            Vector2 value = context.ReadValue<Vector2>();
            ForwardMove(new Vec2(value.x, value.y));
        }

        private void OnInteract(InputAction.CallbackContext context)
        {
            if (_disposed)
            {
                return;
            }

            ForwardInteract();
        }

        private void OnPause(InputAction.CallbackContext context)
        {
            if (_disposed)
            {
                return;
            }

            ForwardPause();
        }

        private void ForwardMove(Vec2 value)
        {
            LastMove = value;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (value.SqrMagnitude <= 0f)
            {
                // TASK-A-AND-04 device diagnosis: a release must reach the consumers.
                ZFrame.View.Diagnostics.DeviceFileLogger.Step(
                    $"[bridge] move ZERO fired (subscribers={EventBus.SubscriberCount<MoveInputEvent>()})");
            }
#endif
            EventBus.Fire(new MoveInputEvent(value));
        }

        private void ForwardInteract()
        {
            InteractCount++;
            EventBus.Fire(default(InteractInputEvent));
        }

        private void ForwardPause()
        {
            PauseCount++;
            EventBus.Fire(default(PauseInputEvent));
        }
    }
}
