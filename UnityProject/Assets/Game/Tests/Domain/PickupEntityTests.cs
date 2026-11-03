using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Pickup template tests (TASK-A-M4-01): one-shot semantics, inventory
    /// accounting and the <see cref="ItemPickedUp"/> payload.
    /// </summary>
    public sealed class PickupEntityTests
    {
        private IDisposable _token;
        private readonly List<ItemPickedUp> _busEvents = new List<ItemPickedUp>();

        [SetUp]
        public void SetUp()
        {
            _token = EventBus.Subscribe<ItemPickedUp>(evt => _busEvents.Add(evt));
        }

        [TearDown]
        public void TearDown()
        {
            _token?.Dispose();
            _token = null;
            _busEvents.Clear();
        }

        [Test]
        public void Interact_AddsToInventory_AndConsumesEntity()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);

            pickup.Interact(ActorId.Player);

            Assert.IsTrue(pickup.IsPickedUp);
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.IsFalse(pickup.CanInteract(ActorId.Player), "拾取物一次性");
            Assert.AreEqual(string.Empty, pickup.Info.Prompt, "已拾取后提示必须清空");
        }

        [Test]
        public void Interact_FiresEventWithAmountAndTotal()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);
            var instanceEvents = new List<ItemPickedUp>();
            pickup.ItemPickedUp += evt => instanceEvents.Add(evt);

            pickup.Interact(ActorId.Player);

            Assert.AreEqual(1, instanceEvents.Count);
            Assert.AreEqual(1, _busEvents.Count);

            ItemPickedUp evt = _busEvents[0];
            Assert.AreEqual(ActorId.Player, evt.Actor);
            Assert.AreEqual("gold", evt.ItemId);
            Assert.AreEqual(3, evt.Amount);
            Assert.AreEqual(3, evt.TotalCount);
        }

        [Test]
        public void Interact_Twice_DoesNotDoubleCount()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, inventory);

            pickup.Interact(ActorId.Player);
            pickup.Interact(ActorId.Player);

            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.AreEqual(1, _busEvents.Count);
        }

        [Test]
        public void TwoPickups_AccumulateTotal()
        {
            var inventory = new Inventory();
            var first = new PickupEntity("pickup-1", "gold", 3, inventory);
            var second = new PickupEntity("pickup-2", "gold", 4, inventory);

            first.Interact(ActorId.Player);
            second.Interact(ActorId.Player);

            Assert.AreEqual(7, inventory.Count("gold"));
            Assert.AreEqual(7, _busEvents[1].TotalCount);
        }

        [Test]
        public void Constructor_RejectsInvalidArguments()
        {
            var inventory = new Inventory();

            Assert.Throws<ArgumentException>(() => new PickupEntity(string.Empty, "gold", 1, inventory));
            Assert.Throws<ArgumentException>(() => new PickupEntity("pickup", string.Empty, 1, inventory));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PickupEntity("pickup", "gold", 0, inventory));
            Assert.Throws<ArgumentNullException>(() => new PickupEntity("pickup", "gold", 1, null));
        }

        [Test]
        public void ExposesItemIdentityAndAmount()
        {
            var pickup = new PickupEntity("pickup-gold-1", "gold", 3, new Inventory());

            Assert.AreEqual("pickup-gold-1", pickup.Id);
            Assert.AreEqual("gold", pickup.ItemId);
            Assert.AreEqual(3, pickup.Amount);
            Assert.AreEqual("Pickup", pickup.Info.Kind);
        }

        // ---- TASK-A-R9-06: the event carries the pickup's plane position ----

        [Test]
        public void Interact_WithPosition_EventCarriesItOnTheBus()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity(
                "pickup-gold-2", "gold", 1, inventory, position: new Vec2(3f, 7f));

            pickup.Interact(ActorId.Player);

            Assert.AreEqual(1, _busEvents.Count);
            Assert.AreEqual(new Vec2(3f, 7f), _busEvents[0].Position);
        }

        [Test]
        public void Interact_WithoutPosition_EventCarriesZero()
        {
            var inventory = new Inventory();
            var pickup = new PickupEntity("pickup-gold-3", "gold", 1, inventory);

            pickup.Interact(ActorId.Player);

            Assert.AreEqual(Vec2.Zero, _busEvents[0].Position,
                "the 4-arg constructor must keep reporting Zero (compatibility)");
        }

        [Test]
        public void ItemPickedUp_ConstructorOverloads_MatchPositionSemantics()
        {
            var legacy = new ItemPickedUp(ActorId.Player, "gold", 1, 1);
            var located = new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(2f, 5f));

            Assert.AreEqual(Vec2.Zero, legacy.Position);
            Assert.AreEqual(new Vec2(2f, 5f), located.Position);
        }
    }
}
