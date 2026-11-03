using System;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Pickup template: one-shot collection into an <see cref="Inventory"/>
    /// (TASK-A-M4-01). After a successful interact the entity is spent —
    /// <see cref="CanInteract"/> turns false so the prompt disappears even
    /// while the object is still the nearest target (the View decides whether
    /// to hide/destroy the prop from <see cref="ItemPickedUp"/>).
    ///
    /// TASK-A-R9-06: the template optionally carries the pickup's position on
    /// the 2D gameplay plane so <see cref="ItemPickedUp"/> can report where the
    /// item was taken. The Domain never derives a position itself — the View
    /// that builds the entity hands one in; positionless pickups (tests,
    /// abstract setups) keep reporting <see cref="Vec2.Zero"/>.
    /// </summary>
    public sealed class PickupEntity : IInteractable
    {
        private readonly string _id;
        private readonly string _itemId;
        private readonly int _amount;
        private readonly string _prompt;
        private readonly Inventory _inventory;
        private readonly Vec2 _position;

        /// <summary>Creates a pickup bound to a shared inventory.</summary>
        /// <param name="id">Stable identity, e.g. "pickup-gold-1".</param>
        /// <param name="itemId">Inventory key, e.g. "gold".</param>
        /// <param name="amount">Stack size granted on pickup (&gt; 0).</param>
        /// <param name="inventory">Destination inventory (must not be null).</param>
        /// <param name="prompt">Player-facing prompt.</param>
        /// <param name="position">Pickup position on the 2D plane (zero = unknown).</param>
        public PickupEntity(
            string id,
            string itemId,
            int amount,
            Inventory inventory,
            string prompt = "按 E 拾取",
            Vec2 position = default)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("PickupEntity: id must not be empty.", nameof(id));
            }

            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("PickupEntity: itemId must not be empty.", nameof(itemId));
            }

            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "PickupEntity: amount must be positive.");
            }

            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _id = id;
            _itemId = itemId;
            _amount = amount;
            _prompt = prompt;
            _position = position;
        }

        /// <summary>Raised after the item lands in the inventory.</summary>
        public event Action<ItemPickedUp> ItemPickedUp;

        /// <summary>Stable identity.</summary>
        public string Id => _id;

        /// <summary>Inventory key granted by this pickup.</summary>
        public string ItemId => _itemId;

        /// <summary>Stack size granted by this pickup.</summary>
        public int Amount => _amount;

        /// <summary>True once collected — the entity is spent.</summary>
        public bool IsPickedUp { get; private set; }

        /// <inheritdoc />
        public InteractInfo Info => new InteractInfo(_id, IsPickedUp ? string.Empty : _prompt, "Pickup");

        /// <inheritdoc />
        public bool CanInteract(ActorId actor)
        {
            return !IsPickedUp;
        }

        /// <inheritdoc />
        public void Interact(ActorId actor)
        {
            if (!CanInteract(actor))
            {
                return;
            }

            IsPickedUp = true;
            int total = _inventory.Add(_itemId, _amount);
            var evt = new ItemPickedUp(actor, _itemId, _amount, total, _position);
            ItemPickedUp?.Invoke(evt);
            EventBus.Fire(evt);
        }
    }
}
