namespace Game.Domain.Interaction
{
    /// <summary>
    /// Raised after an undo restores a pickup (TASK-A-R9-12) — the mirror of
    /// <see cref="ItemPickedUp"/> for the revert path. Presentation layers use
    /// it to keep counters honest after an undo; the gameplay spark binding
    /// deliberately does NOT subscribe (visuals do not travel back in time —
    /// the spark of the original pickup stays spent, see GameplayFxBindings).
    /// </summary>
    public readonly struct ItemReturned
    {
        /// <summary>Whose undo returned it.</summary>
        public readonly ActorId Actor;

        /// <summary>Item identity.</summary>
        public readonly string ItemId;

        /// <summary>Amount taken back out of the inventory.</summary>
        public readonly int Amount;

        /// <summary>Inventory total for <see cref="ItemId"/> after the removal.</summary>
        public readonly int TotalCount;

        /// <summary>Creates the event.</summary>
        public ItemReturned(ActorId actor, string itemId, int amount, int totalCount)
        {
            Actor = actor;
            ItemId = itemId;
            Amount = amount;
            TotalCount = totalCount;
        }
    }
}
