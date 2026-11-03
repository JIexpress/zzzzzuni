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
    ///
    /// TASK-A-R9-12: every interact is expressed as a
    /// <see cref="PickupCommand"/> and pushed through the registered
    /// <see cref="ICommandManager"/> (Services), so a mis-pick can be undone
    /// for real — the command's Undo removes the item from the
    /// <see cref="Inventory"/> and restores this entity to interactable. When
    /// no manager is registered the command still runs, just unrecorded —
    /// abstract tests and headless setups keep the pre-R9-12 behaviour.
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

        /// <summary>Raised after an undo takes the item back out (TASK-A-R9-12).</summary>
        public event Action<ItemReturned> ItemReturned;

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

            // TASK-A-R9-12: the pickup IS a command. Through the registered
            // manager it lands on the undo stack; without one it executes
            // directly (unrecorded) — same state transition either way.
            var command = new PickupCommand(this, actor);
            if (Services.TryGet(out ICommandManager manager))
            {
                manager.Execute(command);
            }
            else
            {
                command.Execute();
            }
        }

        /// <summary>
        /// Apply body of <see cref="PickupCommand.Execute"/> (idempotent: a
        /// second call on a spent entity is a no-op, per the ICommand
        /// re-entry contract). Grants the stack, marks the entity spent and
        /// notifies through <see cref="ItemPickedUp"/> + the EventBus.
        /// </summary>
        internal void ApplyPickup(ActorId actor)
        {
            if (IsPickedUp)
            {
                return;
            }

            IsPickedUp = true;
            int total = _inventory.Add(_itemId, _amount);
            var evt = new ItemPickedUp(actor, _itemId, _amount, total, _position);
            ItemPickedUp?.Invoke(evt);
            EventBus.Fire(evt);
        }

        /// <summary>
        /// Rollback body of <see cref="PickupCommand.Undo"/> (idempotent no-op
        /// when the entity is not currently picked up). Takes the granted
        /// amount back out of the inventory — a REAL rollback — and restores
        /// the prompt / <see cref="CanInteract"/>. Throws when the inventory
        /// no longer covers the amount: that means a foreign system drained
        /// the stack behind the command's back and an honest revert is
        /// impossible (fail-fast beats a fake undo).
        /// </summary>
        internal void RevertPickup(ActorId actor)
        {
            if (!IsPickedUp)
            {
                return;
            }

            if (!_inventory.TryRemove(_itemId, _amount))
            {
                throw new InvalidOperationException(
                    $"PickupEntity '{_id}': cannot undo — inventory no longer holds " +
                    $"{_amount} of '{_itemId}' (foreign mutation?).");
            }

            IsPickedUp = false;
            var evt = new ItemReturned(actor, _itemId, _amount, _inventory.Count(_itemId));
            ItemReturned?.Invoke(evt);
            EventBus.Fire(evt);
        }
    }
}
