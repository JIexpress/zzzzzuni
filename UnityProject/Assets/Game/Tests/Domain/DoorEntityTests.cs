using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Core;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Door template tests (TASK-A-M4-01): two-state toggle, lock refusal,
    /// prompt tracking and the dual notification path (instance event for the
    /// owning View + EventBus for anyone else).
    /// </summary>
    public sealed class DoorEntityTests
    {
        private IDisposable _token;
        private readonly List<DoorStateChanged> _busEvents = new List<DoorStateChanged>();

        [SetUp]
        public void SetUp()
        {
            _token = EventBus.Subscribe<DoorStateChanged>(evt => _busEvents.Add(evt));
        }

        [TearDown]
        public void TearDown()
        {
            _token?.Dispose();
            _token = null;
            _busEvents.Clear();
        }

        [Test]
        public void NewDoor_IsClosedAndInteractable()
        {
            var door = new DoorEntity("door-main");

            Assert.IsFalse(door.IsOpen);
            Assert.IsFalse(door.IsLocked);
            Assert.IsTrue(door.CanInteract(ActorId.Player));
            Assert.AreEqual("Door", door.Info.Kind);
            Assert.AreEqual("door-main", door.Info.Id);
            Assert.AreEqual("按 E 开门", door.Info.Prompt);
        }

        [Test]
        public void Interact_TogglesOpen_AndFiresBothPaths()
        {
            var door = new DoorEntity("door-main");
            var instanceEvents = new List<DoorStateChanged>();
            door.DoorStateChanged += evt => instanceEvents.Add(evt);

            door.Interact(ActorId.Player);

            Assert.IsTrue(door.IsOpen);
            Assert.AreEqual(1, instanceEvents.Count);
            Assert.AreEqual(1, _busEvents.Count);
            Assert.AreEqual("door-main", _busEvents[0].DoorId);
            Assert.IsTrue(_busEvents[0].IsOpen);
            Assert.AreEqual("按 E 关门", door.Info.Prompt, "开门后提示必须切换为关门");
        }

        [Test]
        public void Interact_Twice_ReturnsToClosed()
        {
            var door = new DoorEntity("door-main");

            door.Interact(ActorId.Player);
            door.Interact(ActorId.Player);

            Assert.IsFalse(door.IsOpen);
            Assert.AreEqual(2, _busEvents.Count);
        }

        [Test]
        public void LockedDoor_RefusesInteraction_AndKeepsPrompt()
        {
            var door = new DoorEntity("door-main", locked: true);
            int fired = 0;
            door.DoorStateChanged += _ => fired++;

            Assert.IsFalse(door.CanInteract(ActorId.Player));
            door.Interact(ActorId.Player);

            Assert.IsFalse(door.IsOpen, "锁住的门不得被打开");
            Assert.AreEqual(0, fired);
            Assert.IsEmpty(_busEvents);
            Assert.AreEqual("门已锁", door.Info.Prompt);
        }

        [Test]
        public void Unlocking_RestoresInteraction()
        {
            var door = new DoorEntity("door-main", locked: true);

            door.IsLocked = false;
            door.Interact(ActorId.Player);

            Assert.IsTrue(door.IsOpen);
        }

        [Test]
        public void Toggle_OnLockedDoor_ReturnsFalse()
        {
            var door = new DoorEntity("door-main", locked: true);

            Assert.IsFalse(door.Toggle());
            Assert.IsFalse(door.IsOpen);
        }

        [Test]
        public void StartsOpen_ShowsClosePrompt()
        {
            var door = new DoorEntity("door-main", startsOpen: true);

            Assert.IsTrue(door.IsOpen);
            Assert.AreEqual("按 E 关门", door.Info.Prompt);
        }

        [Test]
        public void EmptyId_Throws()
        {
            Assert.Throws<ArgumentException>(() => new DoorEntity(string.Empty));
            Assert.Throws<ArgumentException>(() => new DoorEntity(null));
        }
    }
}
