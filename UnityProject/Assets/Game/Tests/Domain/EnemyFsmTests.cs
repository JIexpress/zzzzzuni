using System;
using System.Collections.Generic;
using Game.Domain.Enemy;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Enemy FSM tests (TASK-A-M4-01): the four-state flow driven purely by
    /// the pumped player distance, movement rates, waypoint walking and the
    /// terminal Dead state. No scene, no transforms.
    /// </summary>
    public sealed class EnemyFsmTests
    {
        private static readonly Vec2 FarAway = new Vec2(100f, 100f);

        private static EnemyTuning Tuning => new EnemyTuning(4f, 6f, 1.5f, 3f, 1f, 0.1f);

        private static EnemyFsm CreateEnemy(Vec2 start, Vec2[] waypoints, Vec2 player)
        {
            var enemy = new EnemyFsm("enemy-1", start, waypoints, Tuning);
            enemy.SetPlayerPosition(player);
            return enemy;
        }

        [Test]
        public void Constructor_StartsIdle_AndAnnouncesInitialState()
        {
            var events = new List<EnemyStateChanged>();
            using (EventBus.Subscribe<EnemyStateChanged>(evt => events.Add(evt)))
            {
                EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);

                Assert.AreEqual(EnemyStateId.Idle, enemy.StateId);
                Assert.AreEqual(1, events.Count, "初始状态必须广播一次，View 才能初始化");
                Assert.AreEqual(EnemyStateId.Idle, events[0].Current);
                Assert.AreEqual(EnemyStateId.Idle, events[0].Previous);
                Assert.AreEqual("enemy-1", events[0].EnemyId);
            }
        }

        [Test]
        public void Idle_WithoutWaypoints_NeverPatrols()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);

            enemy.Tick(10f);

            Assert.AreEqual(EnemyStateId.Idle, enemy.StateId, "没有巡逻路线就原地待命");
            Assert.AreEqual(Vec2.Zero, enemy.Position);
        }

        [Test]
        public void Idle_AfterIdleSeconds_WithWaypoints_EntersPatrol()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(5f, 0f) }, FarAway);

            enemy.Tick(0.5f);
            Assert.AreEqual(EnemyStateId.Idle, enemy.StateId, "计时未到不得开始巡逻");

            enemy.Tick(0.6f);
            Assert.AreEqual(EnemyStateId.Patrol, enemy.StateId);
        }

        [Test]
        public void Idle_PlayerInsideChaseRange_EntersChase()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));

            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId);
        }

        [Test]
        public void Chase_MovesTowardPlayerAtChaseSpeed()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));
            enemy.Tick(0.1f); // Idle → Chase

            enemy.Tick(0.5f); // 3 m/s * 0.5 s = 1.5 m

            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId);
            Assert.AreEqual(1.5f, enemy.Position.X, 1e-4f);
            Assert.AreEqual(0f, enemy.Position.Y, 1e-4f);
            Assert.AreEqual(1.5f, enemy.DistanceToPlayer, 1e-4f);
        }

        [Test]
        public void Chase_BetweenRanges_KeepsChasing()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));
            enemy.Tick(0.1f);
            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId);

            enemy.SetPlayerPosition(new Vec2(5f, 0f)); // > ChaseRange(4) 但 < LoseRange(6)
            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId, "滞回区间内不得放弃追击");
        }

        [Test]
        public void Chase_LostPlayer_ReturnsToPatrol_WhenWaypointsExist()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(0f, 5f) }, new Vec2(3f, 0f));
            enemy.Tick(0.1f);
            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId);

            enemy.SetPlayerPosition(new Vec2(7f, 0f)); // > LoseRange(6)
            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Patrol, enemy.StateId);
        }

        [Test]
        public void Chase_LostPlayer_ReturnsToIdle_WithoutWaypoints()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));
            enemy.Tick(0.1f);
            Assert.AreEqual(EnemyStateId.Chase, enemy.StateId);

            enemy.SetPlayerPosition(new Vec2(7f, 0f));
            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Idle, enemy.StateId);
        }

        [Test]
        public void Patrol_MovesTowardWaypoint()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(10f, 0f) }, FarAway);
            enemy.Tick(1f); // Idle → Patrol

            enemy.Tick(1f); // 1.5 m/s * 1 s

            Assert.AreEqual(EnemyStateId.Patrol, enemy.StateId);
            Assert.AreEqual(1.5f, enemy.Position.X, 1e-4f);
        }

        [Test]
        public void Patrol_ReachingWaypoint_AdvancesIndex()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new[] { new Vec2(0.05f, 0f), new Vec2(5f, 0f) }, FarAway);
            enemy.Tick(1f); // Idle → Patrol
            Assert.AreEqual(0, enemy.WaypointIndex);

            enemy.Tick(0.1f); // 0.15 m > 0.05 m，到达并被夹在路点上

            Assert.AreEqual(1, enemy.WaypointIndex);
        }

        [Test]
        public void Patrol_WaypointIndexWrapsAround()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new[] { Vec2.Zero, Vec2.Zero }, FarAway);
            enemy.Tick(1f); // Idle → Patrol

            enemy.Tick(0.1f);
            Assert.AreEqual(1, enemy.WaypointIndex);

            enemy.Tick(0.1f);
            Assert.AreEqual(0, enemy.WaypointIndex, "路点循环回绕");
        }

        [Test]
        public void Patrol_WithoutWaypoints_DropsBackToIdle()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);
            Assert.IsTrue(enemy.RequestState(EnemyStateId.Patrol));

            enemy.Tick(0.1f);

            Assert.AreEqual(EnemyStateId.Idle, enemy.StateId);
        }

        [Test]
        public void Kill_EntersTerminalDeadState()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));

            Assert.IsTrue(enemy.Kill());

            Assert.AreEqual(EnemyStateId.Dead, enemy.StateId);
            Assert.IsTrue(enemy.IsDead);
            Assert.IsFalse(enemy.RequestState(EnemyStateId.Idle), "Dead 是终态，不得复活");
            Assert.IsFalse(enemy.RequestState(EnemyStateId.Chase));
        }

        [Test]
        public void Dead_DoesNotMoveOrChangeOnTick()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));
            enemy.Kill();

            enemy.Tick(1f);

            Assert.AreEqual(EnemyStateId.Dead, enemy.StateId);
            Assert.AreEqual(Vec2.Zero, enemy.Position);
        }

        [Test]
        public void Kill_Twice_ReturnsFalse()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);
            enemy.Kill();

            Assert.IsFalse(enemy.Kill(), "重复击杀必须被拒绝");
        }

        [Test]
        public void StateChanged_ReportsPreviousAndCurrent()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);
            var events = new List<EnemyStateChanged>();
            enemy.EnemyStateChanged += evt => events.Add(evt);

            enemy.SetPlayerPosition(new Vec2(3f, 0f));
            enemy.Tick(0.1f);

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(EnemyStateId.Idle, events[0].Previous);
            Assert.AreEqual(EnemyStateId.Chase, events[0].Current);
        }

        [Test]
        public void StateChanged_IsAlsoPublishedOnTheEventBus()
        {
            var events = new List<EnemyStateChanged>();
            using (EventBus.Subscribe<EnemyStateChanged>(evt => events.Add(evt)))
            {
                EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], FarAway);
                events.Clear();

                enemy.SetPlayerPosition(new Vec2(3f, 0f));
                enemy.Tick(0.1f);

                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(EnemyStateId.Chase, events[0].Current);
            }
        }

        [Test]
        public void Tick_NonPositiveDelta_IsIgnored()
        {
            EnemyFsm enemy = CreateEnemy(Vec2.Zero, new Vec2[0], new Vec2(3f, 0f));

            enemy.Tick(0f);
            enemy.Tick(-1f);

            Assert.AreEqual(EnemyStateId.Idle, enemy.StateId, "非正 dt 不得推进状态机");
        }

        [Test]
        public void DistanceToPlayer_TracksPumpedPosition()
        {
            EnemyFsm enemy = CreateEnemy(new Vec2(1f, 1f), new Vec2[0], FarAway);

            enemy.SetPlayerPosition(new Vec2(4f, 5f));

            Assert.AreEqual(5f, enemy.DistanceToPlayer, 1e-4f);
        }

        [Test]
        public void Constructor_RejectsEmptyId()
        {
            Assert.Throws<ArgumentException>(() => new EnemyFsm(string.Empty, Vec2.Zero, null, Tuning));
        }

        [Test]
        public void Tuning_RejectsInvertedRanges()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EnemyTuning(6f, 4f, 1f, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EnemyTuning(-1f, 4f, 1f, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EnemyTuning(1f, 2f, -1f, 1f, 1f));
        }
    }
}
