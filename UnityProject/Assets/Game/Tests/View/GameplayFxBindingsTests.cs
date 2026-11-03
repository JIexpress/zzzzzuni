using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Domain.AI;
using Game.Domain.Enemy;
using Game.Domain.Interaction;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.View.Tests
{
    /// <summary>
    /// TASK-A-R9-06: the event→spark adapter's unit contract, entirely off-screen
    /// (EditMode, real EventBus, fake emitter — the mock seam the brief asks
    /// for). Covered here:
    ///  - subscribe/teardown through one EventScope (exactly one live
    ///    subscription per source; Shutdown unsubscribes; rebuild re-subscribes);
    ///  - the event→position mapping for both wired sources (attack spark at
    ///    the TARGET = PlayerPosition, pickup spark at the item position, with
    ///    the shared 2D-plane→world convention);
    ///  - the same-frame merge: same frame + same spot = one spark, distinct
    ///    spots and the next frame both re-arm (FrameSource is the internal
    ///    EditMode seam — Time.frameCount cannot advance outside play mode).
    ///
    /// The switch coordination (Enabled=false → events flow, zero pixels) is a
    /// rendered-pixels question and lives in the PlayMode evidence suite.
    /// </summary>
    public sealed class GameplayFxBindingsTests
    {
        private RecordingEmitter _emitter;
        private GameplayFxBindings _bindings;
        private GameObject _host;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _emitter = new RecordingEmitter();
            _frame = 100;

            _host = new GameObject("~GameplayFxBindingsTests");
            _bindings = _host.AddComponent<GameplayFxBindings>();
            _bindings.Build(_emitter);
            _bindings.FrameSource = () => _frame; // internal seam: EditMode cannot advance Time.frameCount
        }

        [TearDown]
        public void TearDown()
        {
            _bindings.Shutdown();
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
                _host = null;
            }

            _bindings = null;
            _emitter = null;
        }

        // ---- subscription lifecycle (EventScope) -----------------------------

        [Test]
        public void Build_SubscribesExactlyOncePerSource()
        {
            // Snapshot includes SetUp's own instance; a second instance adds
            // exactly one subscription per source...
            int attacksBefore = CountSubscribers<EnemyAttackRequested>();
            int pickupsBefore = CountSubscribers<ItemPickedUp>();
            int doorsBefore = CountSubscribers<DoorStateChanged>();
            int enemyStatesBefore = CountSubscribers<EnemyStateChanged>();
            int dialogsBefore = CountSubscribers<DialogAdvanced>();

            var host = new GameObject("~GameplayFxBindingsTestsSecond");
            GameplayFxBindings second = host.AddComponent<GameplayFxBindings>();
            second.Build(_emitter);
            try
            {
                Assert.AreEqual(attacksBefore + 1, CountSubscribers<EnemyAttackRequested>(),
                    "Build must subscribe EnemyAttackRequested exactly once");
                Assert.AreEqual(pickupsBefore + 1, CountSubscribers<ItemPickedUp>(),
                    "Build must subscribe ItemPickedUp exactly once");
                Assert.AreEqual(doorsBefore + 1, CountSubscribers<DoorStateChanged>(),
                    "Build must subscribe DoorStateChanged exactly once (TASK-A-R9-07)");
                Assert.AreEqual(enemyStatesBefore + 1, CountSubscribers<EnemyStateChanged>(),
                    "Build must subscribe EnemyStateChanged exactly once (TASK-A-R9-07)");
                Assert.AreEqual(dialogsBefore + 1, CountSubscribers<DialogAdvanced>(),
                    "Build must subscribe DialogAdvanced exactly once (TASK-A-R9-07)");

                // ...and a redundant Build must be a no-op, not a double subscription.
                second.Build(_emitter);
                Assert.AreEqual(attacksBefore + 1, CountSubscribers<EnemyAttackRequested>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(pickupsBefore + 1, CountSubscribers<ItemPickedUp>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(doorsBefore + 1, CountSubscribers<DoorStateChanged>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(enemyStatesBefore + 1, CountSubscribers<EnemyStateChanged>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(dialogsBefore + 1, CountSubscribers<DialogAdvanced>(),
                    "a second Build call must not double the subscriptions");
            }
            finally
            {
                second.Shutdown();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Shutdown_UnsubscribesAllSources()
        {
            int attacks = CountSubscribers<EnemyAttackRequested>();
            int pickups = CountSubscribers<ItemPickedUp>();
            int doors = CountSubscribers<DoorStateChanged>();
            int enemyStates = CountSubscribers<EnemyStateChanged>();
            int dialogs = CountSubscribers<DialogAdvanced>();

            _bindings.Shutdown();

            Assert.AreEqual(attacks - 1, CountSubscribers<EnemyAttackRequested>(),
                "Shutdown must drop the EnemyAttackRequested subscription (EventScope teardown)");
            Assert.AreEqual(pickups - 1, CountSubscribers<ItemPickedUp>(),
                "Shutdown must drop the ItemPickedUp subscription (EventScope teardown)");
            Assert.AreEqual(doors - 1, CountSubscribers<DoorStateChanged>(),
                "Shutdown must drop the DoorStateChanged subscription (EventScope teardown)");
            Assert.AreEqual(enemyStates - 1, CountSubscribers<EnemyStateChanged>(),
                "Shutdown must drop the EnemyStateChanged subscription (EventScope teardown)");
            Assert.AreEqual(dialogs - 1, CountSubscribers<DialogAdvanced>(),
                "Shutdown must drop the DialogAdvanced subscription (EventScope teardown)");
        }

        [Test]
        public void Rebuild_AfterShutdown_ResubscribesAndCounts()
        {
            _bindings.Shutdown();
            _bindings.Build(_emitter);

            EventBus.Fire(new EnemyAttackRequested("e1", new Vec2(0f, 0f), new Vec2(1f, 1f)));

            Assert.AreEqual(1, _emitter.Calls.Count, "a rebuilt bindings must receive events again");
        }

        [Test]
        public void EventsAfterShutdown_ReachNoOne()
        {
            _bindings.Shutdown();
            int callsBefore = _emitter.Calls.Count;

            EventBus.Fire(new EnemyAttackRequested("e1", new Vec2(0f, 0f), new Vec2(1f, 1f)));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(2f, 2f)));

            Assert.AreEqual(callsBefore, _emitter.Calls.Count,
                "after Shutdown the adapter must be silent — no leaked subscription");
            Assert.AreEqual(0, _bindings.EventsReceived);
        }

        // ---- event → position mapping ---------------------------------------

        [Test]
        public void AttackEvent_SparksAtTheTargetPosition()
        {
            var enemy = new Vec2(10f, 3f);
            var target = new Vec2(4f, 9f);

            EventBus.Fire(new EnemyAttackRequested("enemy-1", enemy, target));

            Assert.AreEqual(1, _emitter.Calls.Count);
            // 2D plane → world: X stays X, Y becomes Z (EnemyView convention), spark floats at SparkHeight.
            Assert.AreEqual(new Vector3(4f, GameplayFxBindings.DefaultSparkHeight, 9f), _emitter.Calls[0],
                "the attack spark must land at the TARGET position (PlayerPosition), not the attacker's");
        }

        [Test]
        public void PickupEvent_SparksAtTheItemPosition()
        {
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(-2f, 6f)));

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(-2f, GameplayFxBindings.DefaultSparkHeight, 6f), _emitter.Calls[0]);
        }

        [Test]
        public void SparkHeight_IsSharedByBothMappings()
        {
            _bindings.SparkHeight = 2.5f;

            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), new Vec2(1f, 1f)));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(3f, 4f)));

            Assert.AreEqual(2, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(1f, 2.5f, 1f), _emitter.Calls[0]);
            Assert.AreEqual(new Vector3(3f, 2.5f, 4f), _emitter.Calls[1]);
        }

        // ---- same-frame merge -------------------------------------------------

        [Test]
        public void SameFrame_SamePosition_MergesToOneSpark()
        {
            var target = new Vec2(7f, 7f);

            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), target));
            EventBus.Fire(new EnemyAttackRequested("enemy-2", new Vec2(1f, 1f), target));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, target));

            Assert.AreEqual(1, _emitter.Calls.Count,
                "three same-frame same-spot events are one visual event");
            Assert.AreEqual(3, _bindings.EventsReceived, "events still arrive while merging");
            Assert.AreEqual(1, _bindings.SparksRequested);
            Assert.AreEqual(2, _bindings.SparksMerged);
        }

        [Test]
        public void SameFrame_DifferentPositions_BothSpark()
        {
            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), new Vec2(1f, 1f)));
            EventBus.Fire(new EnemyAttackRequested("enemy-2", new Vec2(0f, 0f), new Vec2(9f, 9f)));

            Assert.AreEqual(2, _emitter.Calls.Count, "merging is position-scoped, not frame-global");
            Assert.AreEqual(0, _bindings.SparksMerged);
        }

        [Test]
        public void SamePosition_NextFrame_SparksAgain()
        {
            var target = new Vec2(5f, 5f);
            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), target));

            _frame++; // the merge is same-frame only: a fresh frame re-arms it

            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), target));

            Assert.AreEqual(2, _emitter.Calls.Count,
                "a sustained fight (one swing per frame) must keep sparking");
            Assert.AreEqual(2, _bindings.SparksRequested);
            Assert.AreEqual(0, _bindings.SparksMerged);
        }

        [Test]
        public void MergeRadius_GatesNearbyPositions()
        {
            _bindings.MergeRadius = 0.25f;

            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), new Vec2(0f, 0f)));
            EventBus.Fire(new EnemyAttackRequested("enemy-2", new Vec2(0f, 0f), new Vec2(0.1f, 0f))); // within: merge

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(1, _bindings.SparksMerged);

            _frame++;
            EventBus.Fire(new EnemyAttackRequested("enemy-3", new Vec2(0f, 0f), new Vec2(1f, 0f))); // far: spark

            Assert.AreEqual(2, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(1f, GameplayFxBindings.DefaultSparkHeight, 0f), _emitter.Calls[1]);
        }

        [Test]
        public void Merge_IsSourceAgnostic()
        {
            // An attack and a pickup reporting the same spot in one frame are
            // the same visual event too — the merge key never looks at types.
            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), new Vec2(2f, 2f)));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(2f, 2f)));

            Assert.AreEqual(1, _emitter.Calls.Count);
        }

        // ---- TASK-A-R9-07: door / death / dialog sources ----------------------

        [Test]
        public void DoorToggle_SparksAtTheDoorPosition_BothDirections()
        {
            var doorPos = new Vec2(3f, 8f);

            EventBus.Fire(new DoorStateChanged("door-1", true, doorPos));

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(3f, GameplayFxBindings.DefaultSparkHeight, 8f), _emitter.Calls[0],
                "the open spark must land at the door position");

            _frame++; // open and close are frames apart in reality (the merge stays same-frame)
            EventBus.Fire(new DoorStateChanged("door-1", false, doorPos));

            Assert.AreEqual(2, _emitter.Calls.Count, "open and close each owe one small burst");
            Assert.AreEqual(new Vector3(3f, GameplayFxBindings.DefaultSparkHeight, 8f), _emitter.Calls[1],
                "the close spark must land at the same door position");
            Assert.AreEqual(2, _bindings.EventsReceived);
            Assert.AreEqual(2, _bindings.SparksRequested);
            Assert.AreEqual(0, _bindings.SparksMerged);
        }

        [Test]
        public void DoorOpenAndCloseSameFrame_AtTheSameSpot_MergeToOneSpark()
        {
            // A double toggle within one frame is one visual event, exactly
            // like two pickups in one frame — the merge is frame+position only.
            var doorPos = new Vec2(3f, 8f);

            EventBus.Fire(new DoorStateChanged("door-1", true, doorPos));
            EventBus.Fire(new DoorStateChanged("door-1", false, doorPos));

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(2, _bindings.EventsReceived, "both toggles still reach the bindings");
            Assert.AreEqual(1, _bindings.SparksRequested);
            Assert.AreEqual(1, _bindings.SparksMerged);
        }

        [Test]
        public void DoorEventWithoutPosition_SparksAtThePlaneOrigin()
        {
            // Back-compat: the 2-arg (positionless) constructor keeps working
            // and maps to Vec2.Zero exactly like a positionless pickup.
            EventBus.Fire(new DoorStateChanged("door-legacy", true));

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(0f, GameplayFxBindings.DefaultSparkHeight, 0f), _emitter.Calls[0]);
        }

        [Test]
        public void DeathTransition_SparksAtTheEnemyPosition()
        {
            var deathSpot = new Vec2(-4f, 2f);

            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Chase, EnemyStateId.Dead, deathSpot));

            Assert.AreEqual(1, _emitter.Calls.Count, "entering Dead must fire the death burst");
            Assert.AreEqual(new Vector3(-4f, GameplayFxBindings.DefaultSparkHeight, 2f), _emitter.Calls[0],
                "the death spark must land where the enemy fell");
        }

        [Test]
        public void NonDeathTransitions_NeverSpark()
        {
            // Every legal non-terminal transition of the M4 slice, with real
            // positions: routine AI chatter must stay silent, or a patrolling
            // enemy would strobe the screen several times per second.
            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Idle, EnemyStateId.Idle, new Vec2(1f, 1f)));
            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Idle, EnemyStateId.Patrol, new Vec2(1f, 1f)));
            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Patrol, EnemyStateId.Chase, new Vec2(2f, 1f)));
            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Chase, EnemyStateId.Idle, new Vec2(2f, 2f)));
            EventBus.Fire(new EnemyStateChanged("enemy-1", EnemyStateId.Chase, EnemyStateId.Patrol, new Vec2(3f, 2f)));

            Assert.AreEqual(0, _emitter.Calls.Count, "non-death transitions must spark nothing");
            Assert.AreEqual(0, _bindings.EventsReceived,
                "filtered transitions are dropped before the counters — the spark-relevant invariant holds");
            Assert.AreEqual(0, _bindings.SparksRequested);
        }

        [Test]
        public void DialogFinish_SparksOnce_AtTheDialogPosition()
        {
            var npcPos = new Vec2(6f, -1f);

            EventBus.Fire(new DialogAdvanced("npc-elder", 0, "第一行", false, npcPos)); // mid line
            EventBus.Fire(new DialogAdvanced("npc-elder", 1, "最后一行", true, npcPos)); // finished

            Assert.AreEqual(1, _emitter.Calls.Count,
                "only the conversation COMPLETING sparks — per-line bursts would puncture the narrative");
            Assert.AreEqual(new Vector3(6f, GameplayFxBindings.DefaultSparkHeight, -1f), _emitter.Calls[0]);
            Assert.AreEqual(1, _bindings.EventsReceived, "the finished line reaches the pipeline; mid lines do not");
        }

        [Test]
        public void DialogMidLines_NeverSpark()
        {
            EventBus.Fire(new DialogAdvanced("npc-elder", 0, "第一行", false, new Vec2(6f, -1f)));
            EventBus.Fire(new DialogAdvanced("npc-elder", 1, "第二行", false, new Vec2(6f, -1f)));

            Assert.AreEqual(0, _emitter.Calls.Count);
            Assert.AreEqual(0, _bindings.EventsReceived);
        }

        [Test]
        public void FiveSourcesSameFrameSameSpot_MergeIntoOneSpark()
        {
            var spot = new Vec2(7f, 7f);

            EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), spot));
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, spot));
            EventBus.Fire(new DoorStateChanged("door-1", true, spot));
            EventBus.Fire(new EnemyStateChanged("enemy-2", EnemyStateId.Chase, EnemyStateId.Dead, spot));
            EventBus.Fire(new DialogAdvanced("npc-elder", 0, "完", true, spot));

            Assert.AreEqual(1, _emitter.Calls.Count,
                "the merge key is frame+position only — all five sources are one visual event here");
            Assert.AreEqual(5, _bindings.EventsReceived, "every source still reaches the bindings");
            Assert.AreEqual(1, _bindings.SparksRequested);
            Assert.AreEqual(4, _bindings.SparksMerged);
        }

        [Test]
        public void RealDoorToggle_SparksThroughTheRealEventPath()
        {
            // The production chain, not the struct: a real DoorEntity holding a
            // position, Toggle() firing DoorStateChanged on the bus, the
            // bindings mapping it. This is what InteractableProxy wires up.
            // (The entities fire on the bus without subscribing to it, so no
            // teardown beyond the SetUp/TearDown bindings scope is needed.)
            var door = new DoorEntity("door-real", position: new Vec2(9f, 4f));

            door.Toggle();

            Assert.AreEqual(1, _emitter.Calls.Count);
            Assert.AreEqual(new Vector3(9f, GameplayFxBindings.DefaultSparkHeight, 4f), _emitter.Calls[0]);
            Assert.AreEqual(9f, door.Position.X, 1e-6f, "the entity must carry the position it was built with");
        }

        [Test]
        public void RealEnemyFsmKill_SparksDeathBurstAtTheFallSpot()
        {
            var enemy = new EnemyFsm("enemy-real", new Vec2(11f, 5f), new Vec2[0], EnemyTuning.Default);

            Assert.IsTrue(enemy.Kill(), "the kill transition must be accepted");

            Assert.AreEqual(1, _emitter.Calls.Count,
                "exactly one death burst — the initial Idle announcement must not have sparked");
            Assert.AreEqual(new Vector3(11f, GameplayFxBindings.DefaultSparkHeight, 5f), _emitter.Calls[0],
                "the burst lands at the enemy's position at the moment of death");
        }

        [Test]
        public void RealBehaviorTreeKill_SparksDeathBurstAtTheFallSpot()
        {
            // TASK-A-R9-09: the OTHER brain now drives the same death burst
            // with zero particle-side changes — a real behaviour-tree enemy,
            // killed mid-chase, announces EnemyStateChanged{Dead, Position} on
            // the bus exactly like the FSM brain does (the A-R9-07 Dead-only
            // filter is untouched; this test only proves the event arrives).
            var enemy = new EnemyBehaviorTree("bt-real", new Vec2(11f, 5f), new Vec2[0], EnemyTuning.Default);
            enemy.SetPlayerPosition(new Vec2(14f, 5f));
            enemy.Tick(0.1f); // chase: 3 m/s * 0.1 s = 0.3 m toward the player

            enemy.Kill();
            enemy.Kill(); // idempotent: no second announcement, no second burst

            Assert.AreEqual(1, _emitter.Calls.Count,
                "exactly one death burst from the behaviour-tree brain (the second Kill must not re-fire)");
            Assert.That(_emitter.Calls[0].x, Is.EqualTo(11.3f).Within(1e-4f),
                "the burst lands where the behaviour-tree enemy fell (it moved while chasing)");
            Assert.AreEqual(5f, _emitter.Calls[0].z, 1e-6f);
            Assert.AreEqual(GameplayFxBindings.DefaultSparkHeight, _emitter.Calls[0].y, 1e-6f);
        }

        [Test]
        public void RealDialogCompletion_SparksExactlyOnceAtTheEnd()
        {
            var dialog = new DialogEntity("npc-real", new[] { "第一行", "第二行" }, position: new Vec2(2f, 12f));

            Assert.IsTrue(dialog.Advance(), "line 0");
            Assert.AreEqual(0, _emitter.Calls.Count, "a mid line must not spark");

            Assert.IsTrue(dialog.Advance(), "line 1 (final)");
            Assert.AreEqual(1, _emitter.Calls.Count, "exactly one burst when the conversation completes");
            Assert.AreEqual(new Vector3(2f, GameplayFxBindings.DefaultSparkHeight, 12f), _emitter.Calls[0]);

            Assert.IsFalse(dialog.Advance(), "the finished dialog refuses further interacts");
            Assert.AreEqual(1, _emitter.Calls.Count, "no extra burst from refused interacts");
        }

        // ---- defaults / seams -------------------------------------------------

        [Test]
        public void FrameSeam_IsInternal()
        {
            // B-R10-01 discipline: test-only seams in product assemblies must not be public.
            const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo property = typeof(GameplayFxBindings).GetProperty("FrameSource", AnyInstance);
            Assert.IsNotNull(property, "GameplayFxBindings.FrameSource must still exist (the seam was deleted - update this test on purpose)");
            Assert.IsFalse(property.GetMethod.IsPublic, "FrameSource must stay internal (never ship test API)");
        }

        [Test]
        public void Defaults_MatchTheShippedContract()
        {
            Assert.AreEqual(GameplayFxBindings.DefaultMergeRadius, _bindings.MergeRadius, 1e-6f);
            Assert.AreEqual(GameplayFxBindings.DefaultSparkHeight, _bindings.SparkHeight, 1e-6f);
            Assert.AreEqual(0, _bindings.EventsReceived);
            Assert.AreEqual(0, _bindings.SparksRequested);
            Assert.AreEqual(0, _bindings.SparksMerged);
        }

        private static int CountSubscribers<TEvent>()
        {
            return EventBus.SubscriberCount<TEvent>();
        }

        /// <summary>Mock seam: records every forwarded request instead of drawing anything.</summary>
        private sealed class RecordingEmitter : IGameplaySparkEmitter
        {
            public readonly List<Vector3> Calls = new List<Vector3>();

            public int Spawn(Vector3 worldPos)
            {
                Calls.Add(worldPos);
                return 12;
            }
        }
    }
}
