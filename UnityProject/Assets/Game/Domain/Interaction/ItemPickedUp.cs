using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>Raised after a pickup lands in the inventory.</summary>
    public readonly struct ItemPickedUp
    {
        /// <summary>Who picked it up.</summary>
        public readonly ActorId Actor;

        /// <summary>Item identity.</summary>
        public readonly string ItemId;

        /// <summary>Amount added by this pickup.</summary>
        public readonly int Amount;

        /// <summary>Inventory total for <see cref="ItemId"/> after the add.</summary>
        public readonly int TotalCount;

        /// <summary>
        /// World position of the pickup on the 2D gameplay plane (TASK-A-R9-06
        /// minimal event extension: presentation layers turn it into a hit
        /// spark without owning a position lookup). <see cref="Vec2.Zero"/>
        /// when the pickup was created without a position (4-arg constructor).
        /// </summary>
        public readonly Vec2 Position;

        /// <summary>Creates the event (positionless form; <see cref="Position"/> becomes <see cref="Vec2.Zero"/>).</summary>
        public ItemPickedUp(ActorId actor, string itemId, int amount, int totalCount)
        {
            Actor = actor;
            ItemId = itemId;
            Amount = amount;
            TotalCount = totalCount;
            Position = Vec2.Zero;
        }

        /// <summary>Creates the event with the pickup's plane position.</summary>
        public ItemPickedUp(ActorId actor, string itemId, int amount, int totalCount, Vec2 position)
        {
            Actor = actor;
            ItemId = itemId;
            Amount = amount;
            TotalCount = totalCount;
            Position = position;
        }
    }
}
