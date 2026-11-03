using System;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Door template: a two-state toggle with an optional lock (TASK-A-M4-01).
    /// Pure logic — the View subscribes to <see cref="DoorStateChanged"/> and
    /// plays the open/close animation; the entity itself only flips a bool.
    ///
    /// TASK-A-R9-07: the template optionally carries the door's position on
    /// the 2D gameplay plane so <see cref="DoorStateChanged"/> can report where
    /// the door swung. The Domain never derives a position itself — the View
    /// that builds the entity hands one in; positionless doors (tests, abstract
    /// setups) keep reporting <see cref="Vec2.Zero"/>.
    /// </summary>
    public sealed class DoorEntity : IInteractable
    {
        private readonly string _id;
        private readonly string _openPrompt;
        private readonly string _closePrompt;
        private readonly Vec2 _position;

        /// <summary>Creates a door.</summary>
        /// <param name="id">Stable identity, e.g. "door-main".</param>
        /// <param name="startsOpen">Initial open state.</param>
        /// <param name="locked">When true <see cref="CanInteract"/> refuses until unlocked.</param>
        /// <param name="openPrompt">Prompt shown while closed.</param>
        /// <param name="closePrompt">Prompt shown while open.</param>
        /// <param name="position">Door position on the 2D plane (zero = unknown).</param>
        public DoorEntity(
            string id,
            bool startsOpen = false,
            bool locked = false,
            string openPrompt = "按 E 开门",
            string closePrompt = "按 E 关门",
            Vec2 position = default)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("DoorEntity: id must not be empty.", nameof(id));
            }

            _id = id;
            _openPrompt = openPrompt;
            _closePrompt = closePrompt;
            _position = position;
            IsOpen = startsOpen;
            IsLocked = locked;
        }

        /// <summary>Raised after every successful toggle.</summary>
        public event Action<DoorStateChanged> DoorStateChanged;

        /// <summary>Stable identity.</summary>
        public string Id => _id;

        /// <summary>Door position on the 2D gameplay plane (<see cref="Vec2.Zero"/> when unknown).</summary>
        public Vec2 Position => _position;

        /// <summary>True while the door is open.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Locked doors refuse interaction (and keep their prompt).</summary>
        public bool IsLocked { get; set; }

        /// <inheritdoc />
        public InteractInfo Info => new InteractInfo(
            _id,
            IsLocked ? "门已锁" : (IsOpen ? _closePrompt : _openPrompt),
            "Door");

        /// <inheritdoc />
        public bool CanInteract(ActorId actor)
        {
            return !IsLocked;
        }

        /// <inheritdoc />
        public void Interact(ActorId actor)
        {
            if (!CanInteract(actor))
            {
                return;
            }

            Toggle();
        }

        /// <summary>Flips the open state and raises <see cref="DoorStateChanged"/>. No-op when locked.</summary>
        public bool Toggle()
        {
            if (IsLocked)
            {
                return false;
            }

            IsOpen = !IsOpen;
            var evt = new DoorStateChanged(_id, IsOpen, _position);
            DoorStateChanged?.Invoke(evt);
            EventBus.Fire(evt);
            return true;
        }
    }
}
