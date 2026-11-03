using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Pickup-through-the-command-stack tests (TASK-A-R9-12): the undo path
    /// must roll the WORLD state back for real — the granted amount leaves the
    /// <see cref="Inventory"/> again (stack shape and
    /// <see cref="Inventory.DistinctCount"/> included) and the entity turns
    /// interactable once more. No fake undo anywhere in the chain.
    /// </summary>
    public sealed class PickupCommandTests
    {
        private CommandManager _commands;
        private readonly List<ItemPickedUp> _picked = new List<ItemPickedUp>();
        private readonly List<ItemReturned> _returned = new List<ItemReturned>();
        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private bool _managerRegistered;

        [SetUp]
        public void SetUp()
        {
            _tokens.Add(EventBus.Subscribe<ItemPickedUp>(evt => _picked.Add(evt)));
            _tokens.Add(EventBus.Subscribe<ItemReturned>(evt => _returned.Add(evt)));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            _picked.Clear();
            _returned.Clear();

            if (_managerRegistered)
            {
                Services.Unregister<ICommandManager>();
                _managerRegistered = false;
            }

            _commands = null;
        }

        /// <summary>Registers a fresh command stack under ICommandManager (Services seam).</summary>
        private CommandManager RegisterStack()
        {
            _commands = new CommandManager();
            Services.Register<ICommandManager>(_commands);
            _managerRegistered = true;
            return _commands;
        }

        [Test]
        public void Interact_Undo_Redo_RollbackIsRealOnBothSides()
        {
            RegisterStack();
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory, "按 E 拾取");

            pickup.Interact(ActorId.Player);
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.IsTrue(pickup.IsPickedUp);
            Assert.AreEqual(string.Empty, pickup.Info.Prompt, "prompt cleared while spent");

            Assert.IsTrue(_commands.Undo());
            Assert.AreEqual(0, inventory.Count("gold"), "REAL rollback: the granted amount left the inventory");
            Assert.AreEqual(0, inventory.DistinctCount, "stack shape restored exactly (key removed at zero)");
            Assert.AreEqual(0, inventory.TotalCount);
            Assert.IsFalse(pickup.IsPickedUp, "entity state rolled back");
            Assert.IsTrue(pickup.CanInteract(ActorId.Player), "the spot can be picked again");
            Assert.AreEqual("按 E 拾取", pickup.Info.Prompt, "prompt restored");

            Assert.IsTrue(_commands.Redo());
            Assert.AreEqual(3, inventory.Count("gold"), "redo re-applies the pickup");
            Assert.IsTrue(pickup.IsPickedUp);
            Assert.AreEqual(string.Empty, pickup.Info.Prompt);
        }

        [Test]
        public void Undo_FiresItemReturned_WithRollbackPayload()
        {
            RegisterStack();
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-wood-1", "wood", 2, inventory);
            var instanceEvents = new List<ItemReturned>();
            pickup.ItemReturned += evt => instanceEvents.Add(evt);

            pickup.Interact(ActorId.Player);
            inventory.Add("wood", 5); // foreign stack: total was 7 before the undo
            _returned.Clear();
            instanceEvents.Clear();

            Assert.IsTrue(_commands.Undo());

            Assert.AreEqual(1, _returned.Count, "bus event fired once");
            ItemReturned evt = _returned[0];
            Assert.AreEqual(ActorId.Player, evt.Actor);
            Assert.AreEqual("wood", evt.ItemId);
            Assert.AreEqual(2, evt.Amount, "only the granted amount leaves, foreign stacks stay");
            Assert.AreEqual(5, evt.TotalCount, "total reported AFTER the removal");
            Assert.AreEqual(1, instanceEvents.Count, "the entity's own event mirrors the bus event");
            Assert.AreEqual(5, inventory.Count("wood"));
        }

        [Test]
        public void Interact_WithoutRegisteredManager_StillApplies_Unrecorded()
        {
            // No RegisterStack() on purpose: the pre-R9-12 behaviour must keep
            // working for abstract setups (existing PickupEntityTests rely on it).
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);

            Assert.DoesNotThrow(() => pickup.Interact(ActorId.Player));
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.IsTrue(pickup.IsPickedUp);
        }

        [Test]
        public void MultiplePickups_UndoInReverseOrder_AcrossItems()
        {
            RegisterStack();
            var inventory = new Inventory();
            var gold = new PickupEntity("pickup-gold-1", "gold", 3, inventory);
            var wood = new PickupEntity("pickup-wood-1", "wood", 2, inventory);

            gold.Interact(ActorId.Player);
            wood.Interact(ActorId.Player);
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.AreEqual(2, inventory.Count("wood"));

            Assert.IsTrue(_commands.Undo());
            Assert.AreEqual(0, inventory.Count("wood"), "newest first");
            Assert.IsFalse(wood.IsPickedUp);
            Assert.AreEqual(3, inventory.Count("gold"), "older pickup untouched");

            Assert.IsTrue(_commands.Undo());
            Assert.AreEqual(0, inventory.Count("gold"));
            Assert.IsFalse(gold.IsPickedUp);
            Assert.AreEqual(0, inventory.TotalCount, "fully rolled back world");

            Assert.IsTrue(_commands.Redo());
            Assert.IsTrue(_commands.Redo());
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.AreEqual(2, inventory.Count("wood"));
            Assert.IsTrue(gold.IsPickedUp && wood.IsPickedUp);
        }

        [Test]
        public void Undo_ThenRepick_StartsNewTimeline_NoRedo()
        {
            RegisterStack();
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);

            pickup.Interact(ActorId.Player);
            _commands.Undo();
            pickup.Interact(ActorId.Player); // the restored spot is picked again

            Assert.IsFalse(_commands.CanRedo, "the new execute severed the redo chain");
            Assert.IsFalse(_commands.Redo());
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.AreEqual(1, _commands.UndoDepth, "the repick is the one undoable command");
        }

        [Test]
        public void Command_Label_DescribesThePickup()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);
            var command = new PickupCommand(pickup, ActorId.Player);

            Assert.AreEqual("拾取 gold×3", command.Label);
            Assert.AreSame(pickup, command.Entity);
        }

        [Test]
        public void Command_RejectsNullEntity()
        {
            Assert.Throws<ArgumentNullException>(() => new PickupCommand(null, ActorId.Player));
        }

        [Test]
        public void Undo_AfterForeignInventoryDrain_FailsFast()
        {
            RegisterStack();
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);

            pickup.Interact(ActorId.Player);
            Assert.IsTrue(inventory.TryRemove("gold", 3), "precondition: a foreign system drained the stack");

            // A rollback that cannot happen must not silently pretend it did:
            // the exception surfaces and the command leaves both stacks.
            Assert.Throws<InvalidOperationException>(() => _commands.Undo());
            Assert.IsFalse(_commands.CanUndo, "dropped from the undo stack");
            Assert.IsFalse(_commands.CanRedo, "...and never reached redo");
            Assert.IsTrue(pickup.IsPickedUp, "entity state unchanged (no half-rollback)");
            Assert.AreEqual(0, _returned.Count, "no ItemReturned fired for a failed revert");
        }
    }
}
