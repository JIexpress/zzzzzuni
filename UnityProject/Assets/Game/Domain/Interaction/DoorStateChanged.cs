using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>Raised after a <see cref="DoorEntity"/> toggles. View layers animate/sound from this.</summary>
    public readonly struct DoorStateChanged
    {
        /// <summary>Door identity.</summary>
        public readonly string DoorId;

        /// <summary>True = open.</summary>
        public readonly bool IsOpen;

        /// <summary>
        /// Door position on the 2D gameplay plane (TASK-A-R9-07 minimal event
        /// extension, ItemPickedUp precedent: presentation layers turn it into
        /// a spark/puff without owning a position lookup). <see cref="Vec2.Zero"/>
        /// when the door was created without a position.
        /// </summary>
        public readonly Vec2 Position;

        /// <summary>Creates the event (positionless form; <see cref="Position"/> becomes <see cref="Vec2.Zero"/>).</summary>
        public DoorStateChanged(string doorId, bool isOpen)
        {
            DoorId = doorId;
            IsOpen = isOpen;
            Position = Vec2.Zero;
        }

        /// <summary>Creates the event with the door's plane position.</summary>
        public DoorStateChanged(string doorId, bool isOpen, Vec2 position)
        {
            DoorId = doorId;
            IsOpen = isOpen;
            Position = position;
        }
    }
}
