namespace ZFrame.Gameplay
{
    /// <summary>
    /// Player movement command published by the View input bridge
    /// (TASK-A-M4-01) and consumed by gameplay code. Direction space is
    /// screen/input space (x = right, y = forward) — the consumer decides how
    /// to map it onto world axes, so the logic layer never touches InputSystem
    /// or UnityEngine.
    ///
    /// A released stick/keyboard fires this with <see cref="Vec2.Zero"/>.
    /// </summary>
    public readonly struct MoveInputEvent
    {
        /// <summary>Current 2D input vector (already dead-zoned by InputSystem).</summary>
        public readonly Vec2 Value;

        /// <summary>Creates the command.</summary>
        public MoveInputEvent(Vec2 value)
        {
            Value = value;
        }
    }

    /// <summary>Interact button (E / gamepad South) pressed this frame. Fired by the View input bridge.</summary>
    public readonly struct InteractInputEvent
    {
    }

    /// <summary>Pause button (Esc / gamepad Start) pressed this frame. Fired by the View input bridge.</summary>
    public readonly struct PauseInputEvent
    {
    }
}
