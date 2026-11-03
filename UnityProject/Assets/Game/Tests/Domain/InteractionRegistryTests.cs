using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Core;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Registry tests (TASK-A-M4-01): registration book-keeping, the single
    /// "nearest" slot, the EventBus notification contract and the pure
    /// nearest-candidate selection rule the View scanner relies on.
    /// </summary>
    public sealed class InteractionRegistryTests
    {
        private sealed class FakeInteractable : IInteractable
        {
            private readonly string _id;

            public FakeInteractable(string id, bool canInteract = true)
            {
                _id = id;
                CanInteractResult = canInteract;
            }

            public bool CanInteractResult { get; set; }

            public int InteractCount { get; private set; }

            public ActorId LastActor { get; private set; }

            public InteractInfo Info => new InteractInfo(_id, "prompt-" + _id, "Fake");

            public bool CanInteract(ActorId actor)
            {
                return CanInteractResult;
            }

            public void Interact(ActorId actor)
            {
                InteractCount++;
                LastActor = actor;
            }
        }

        private InteractionRegistry _registry;
        private IDisposable _token;
        private readonly List<InteractionTargetChanged> _events = new List<InteractionTargetChanged>();

        [SetUp]
        public void SetUp()
        {
            _registry = new InteractionRegistry();
            _token = EventBus.Subscribe<InteractionTargetChanged>(evt => _events.Add(evt));
        }

        [TearDown]
        public void TearDown()
        {
            _token?.Dispose();
            _token = null;
            _events.Clear();
        }

        [Test]
        public void Register_AddsAndRejectsDuplicates()
        {
            var door = new FakeInteractable("door");

            Assert.IsTrue(_registry.Register(door));
            Assert.IsFalse(_registry.Register(door), "同一实例重复注册必须拒绝");
            Assert.IsFalse(_registry.Register(null));
            Assert.AreEqual(1, _registry.Count);
            Assert.IsTrue(_registry.Contains(door));
        }

        [Test]
        public void Unregister_RemovesInstance()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);

            Assert.IsTrue(_registry.Unregister(door));
            Assert.IsFalse(_registry.Unregister(door));
            Assert.AreEqual(0, _registry.Count);
            Assert.IsFalse(_registry.Contains(door));
        }

        [Test]
        public void SetNearest_FiresTargetChangedWithInfo()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);

            Assert.IsTrue(_registry.SetNearest(door));
            Assert.AreSame(door, _registry.Nearest);
            Assert.AreEqual("prompt-door", _registry.NearestInfo.Prompt);

            Assert.AreEqual(1, _events.Count);
            Assert.IsTrue(_events[0].HasTarget);
            Assert.AreEqual("door", _events[0].Info.Id);
        }

        [Test]
        public void SetNearest_SameInstanceTwice_FiresOnlyOnce()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);
            _registry.SetNearest(door);

            Assert.IsFalse(_registry.SetNearest(door), "目标未变化不得重复上报");
            Assert.AreEqual(1, _events.Count);
        }

        [Test]
        public void SetNearest_UnregisteredInstance_IsRejected()
        {
            var door = new FakeInteractable("door");
            var stranger = new FakeInteractable("stranger");
            _registry.Register(door);
            _registry.SetNearest(door);
            _events.Clear();

            Assert.IsFalse(_registry.SetNearest(stranger), "未注册实例不得成为最近目标");
            Assert.AreSame(door, _registry.Nearest);
            Assert.IsEmpty(_events);
        }

        [Test]
        public void ClearNearest_FiresClearedEventOnce()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);
            _registry.SetNearest(door);
            _events.Clear();

            _registry.ClearNearest();
            _registry.ClearNearest(); // second call: nothing to clear

            Assert.IsNull(_registry.Nearest);
            Assert.AreEqual(1, _events.Count);
            Assert.IsFalse(_events[0].HasTarget);
            Assert.IsFalse(_events[0].Info.IsValid);
        }

        [Test]
        public void Unregister_CurrentNearest_ClearsAndNotifies()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);
            _registry.SetNearest(door);
            _events.Clear();

            _registry.Unregister(door);

            Assert.IsNull(_registry.Nearest, "销毁的目标不得留下悬空提示");
            Assert.AreEqual(1, _events.Count);
            Assert.IsFalse(_events[0].HasTarget);
        }

        [Test]
        public void InteractNearest_ActivatesTargetForActor()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);
            _registry.SetNearest(door);

            Assert.IsTrue(_registry.InteractNearest(ActorId.Player));
            Assert.AreEqual(1, door.InteractCount);
            Assert.AreEqual(ActorId.Player, door.LastActor);
        }

        [Test]
        public void InteractNearest_NoTarget_ReturnsFalse()
        {
            Assert.IsFalse(_registry.InteractNearest(ActorId.Player));
        }

        [Test]
        public void InteractNearest_TargetRefuses_DoesNotInteract()
        {
            var door = new FakeInteractable("door", canInteract: false);
            _registry.Register(door);
            _registry.SetNearest(door);

            Assert.IsFalse(_registry.InteractNearest(ActorId.Player));
            Assert.AreEqual(0, door.InteractCount);
        }

        [Test]
        public void PickNearest_ChoosesClosestWithinRange()
        {
            var far = new FakeInteractable("far");
            var near = new FakeInteractable("near");
            var middle = new FakeInteractable("middle");
            var candidates = new List<InteractionCandidate>
            {
                new InteractionCandidate(far, 3f),
                new InteractionCandidate(near, 1f),
                new InteractionCandidate(middle, 2f),
            };

            Assert.AreSame(near, InteractionRegistry.PickNearest(candidates, 5f));
        }

        [Test]
        public void PickNearest_SkipsOutOfRangeAndNull()
        {
            var tooFar = new FakeInteractable("tooFar");
            var inRange = new FakeInteractable("inRange");
            var candidates = new List<InteractionCandidate>
            {
                new InteractionCandidate(tooFar, 9f),
                new InteractionCandidate(null, 0.1f),
                new InteractionCandidate(inRange, 1.5f),
            };

            Assert.AreSame(inRange, InteractionRegistry.PickNearest(candidates, 2f));
        }

        [Test]
        public void PickNearest_EmptyOrNullList_ReturnsNull()
        {
            Assert.IsNull(InteractionRegistry.PickNearest(null, 2f));
            Assert.IsNull(InteractionRegistry.PickNearest(new List<InteractionCandidate>(), 2f));
        }

        [Test]
        public void PickNearest_Tie_KeepsFirstCandidate()
        {
            var first = new FakeInteractable("first");
            var second = new FakeInteractable("second");
            var candidates = new List<InteractionCandidate>
            {
                new InteractionCandidate(first, 1f),
                new InteractionCandidate(second, 1f),
            };

            Assert.AreSame(first, InteractionRegistry.PickNearest(candidates, 2f), "同距离时保持射线先命中者");
        }

        [Test]
        public void Clear_DropsEverything()
        {
            var door = new FakeInteractable("door");
            _registry.Register(door);
            _registry.SetNearest(door);

            _registry.Clear();

            Assert.AreEqual(0, _registry.Count);
            Assert.IsNull(_registry.Nearest);
        }

        [Test]
        public void InteractionWorld_Interact_UsesPlayerActor()
        {
            var world = new InteractionWorld();
            var door = new FakeInteractable("door");
            world.Registry.Register(door);
            world.Registry.SetNearest(door);

            Assert.IsTrue(world.Interact());
            Assert.AreEqual(ActorId.Player, door.LastActor);
        }

        [Test]
        public void InteractionWorld_ResetShared_DropsTheSharedRegistry()
        {
            InteractionWorld first = InteractionWorld.Shared;
            InteractionWorld.ResetShared();
            InteractionWorld second = InteractionWorld.Shared;

            Assert.AreNotSame(first, second);
        }
    }
}
