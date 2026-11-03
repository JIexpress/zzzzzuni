using System;
using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// The undoable form of one pickup (TASK-A-R9-12): Execute applies the
    /// collection through <see cref="PickupEntity.ApplyPickup"/>, Undo rolls
    /// the WORLD state back for real through
    /// <see cref="PickupEntity.RevertPickup"/> — the item count leaves the
    /// <see cref="Inventory"/> again and the entity turns interactable once
    /// more. This is a genuine rollback (state is re-derived, nothing is
    /// faked): after Undo the pre-pickup and post-undo states are
    /// indistinguishable, including <see cref="Inventory.DistinctCount"/> and
    /// the entity's prompt.
    ///
    /// NOT undone: presentation side effects of the original pickup — the hit
    /// spark has already been spent and visuals do not travel back in time
    /// (GameplayFxBindings stays subscribed to <see cref="ItemPickedUp"/> only).
    /// </summary>
    public sealed class PickupCommand : ICommand
    {
        private readonly PickupEntity _entity;
        private readonly ActorId _actor;

        /// <summary>Creates the command for one pickup attempt.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
        public PickupCommand(PickupEntity entity, ActorId actor)
        {
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _actor = actor;
        }

        /// <summary>The entity this command picks up / gives back.</summary>
        public PickupEntity Entity => _entity;

        /// <summary>Player-facing label for the undo HUD ("撤销：拾取 gold×3").</summary>
        public string Label => $"拾取 {_entity.ItemId}×{_entity.Amount}";

        /// <summary>Applies the pickup (Execute and Redo path).</summary>
        public void Execute()
        {
            _entity.ApplyPickup(_actor);
        }

        /// <summary>Reverts the pickup (Undo path — real inventory + entity rollback).</summary>
        public void Undo()
        {
            _entity.RevertPickup(_actor);
        }
    }
}
