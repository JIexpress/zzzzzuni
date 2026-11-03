namespace Game.Domain.Interaction
{
    /// <summary>
    /// Anything the player can aim at and activate (door, pickup, dialog
    /// node, ...). Implementations live in the pure-C# Domain layer and are
    /// driven by the View through <see cref="InteractionRegistry"/> only —
    /// the View adapts engine objects to this interface, never the reverse.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>Current prompt/identity. Recomputed on access so it tracks state (open/closed, exhausted).</summary>
        InteractInfo Info { get; }

        /// <summary>False when the actor may not interact right now (locked, already consumed, finished).</summary>
        bool CanInteract(ActorId actor);

        /// <summary>Performs the interaction. Implementations must no-op when <see cref="CanInteract"/> is false.</summary>
        void Interact(ActorId actor);
    }
}
