namespace Game.View.Interaction
{
    /// <summary>
    /// Which Domain template an <see cref="InteractableProxy"/> builds for its
    /// scene object (TASK-A-M4-01). Serialized as an int so scene assets stay
    /// stable when entries are appended.
    /// </summary>
    public enum InteractableKind
    {
        /// <summary><c>DoorEntity</c> — toggles open/closed.</summary>
        Door = 0,

        /// <summary><c>PickupEntity</c> — one-shot into the shared inventory.</summary>
        Pickup = 1,

        /// <summary><c>DialogEntity</c> — advances through a line table.</summary>
        Dialog = 2,
    }
}
