using System;
using System.Collections.Generic;
using Game.Domain.AI.Coordination;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// SquadCoordinator tests (TASK-A-AI-02) — the squad-level invariants,
    /// all hand-computed:
    /// - 编队槽唯一: N members form N distinct ring slots around the leader;
    ///   leaving re-forms the ring;
    /// - 成员任务不冲突: one member holds at most one task, two members can
    ///   never hold the same task (board mutex), completion/leave release;
    /// - 协防分派不重叠: two members vs three enemies resolve to two DISTINCT
    ///   targets, angrier member wins ties (distance + attitude).
    /// </summary>
    public sealed class SquadCoordinatorTests
    {
        private static TaskBoard BoardWithTwoTasks()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 0f), float.PositiveInfinity); // task-1
            board.Publish("pickup", new Vec2(0f, 3f), float.PositiveInfinity); // task-2
            return board;
        }

        // ---- 编队槽唯一 ----

        [Test]
        public void TwoMembers_GetDistinctRingSlots()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-a");
            squad.Join("npc-b");

            IReadOnlyList<Vec2> positions = squad.UpdateFormation(new Vec2(10f, 20f));

            Assert.AreEqual(2, positions.Count);
            Assert.That(positions[0].X, Is.EqualTo(12f).Within(1e-4f), "成员 a 占槽 0：角 0 → (12,20)");
            Assert.That(positions[0].Y, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(positions[1].X, Is.EqualTo(8f).Within(1e-4f), "成员 b 占槽 1：角 π → (8,20)");
            Assert.That(positions[1].Y, Is.EqualTo(20f).Within(1e-4f));
            Assert.IsTrue(FormationSlot.AreDistinct(positions), "环形槽两两相异（槽唯一）");
            Assert.AreEqual(0, squad.SlotIndexOf("npc-a"));
            Assert.AreEqual(1, squad.SlotIndexOf("npc-b"));
            Assert.That(Vec2.Distance(squad.FormationPositionOf("npc-a"), new Vec2(12f, 20f)), Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void ThreeMembers_FormEvenRing_LeaveReforms()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-a");
            squad.Join("npc-b");
            squad.Join("npc-c");

            IReadOnlyList<Vec2> three = squad.UpdateFormation(new Vec2(10f, 20f));
            Assert.AreEqual(3, three.Count);
            Assert.IsTrue(FormationSlot.AreDistinct(three), "三人环位互异");
            Assert.That(three[1].X, Is.EqualTo(9f).Within(1e-3f), "槽 1：角 2π/3 → (10−1, 20+√3)");
            Assert.That(three[1].Y, Is.EqualTo(20f + 1.7320508f).Within(1e-3f));

            squad.Leave("npc-b");
            IReadOnlyList<Vec2> two = squad.UpdateFormation(new Vec2(10f, 20f));
            Assert.AreEqual(2, two.Count, "离队后重排：两人两槽");
            Assert.IsTrue(FormationSlot.AreDistinct(two));
            Assert.AreEqual(-1, squad.SlotIndexOf("npc-b"), "离队者无槽");
        }

        [Test]
        public void JoinDuplicate_OrLeaveUnknown_Throws()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-a");
            Assert.Throws<ArgumentException>(() => squad.Join("npc-a"), "重复入队是接线 bug");
            Assert.Throws<ArgumentException>(() => squad.Join(""), "空 id");
            Assert.Throws<ArgumentException>(() => squad.Leave("npc-404"), "未知成员离队");
        }

        [Test]
        public void UpdateFormation_WithNoMembers_IsNoop()
        {
            var squad = new SquadCoordinator();
            Assert.AreEqual(0, squad.UpdateFormation(Vec2.Zero).Count);
        }

        // ---- 核心证据：成员任务不冲突 ----

        [Test]
        public void MemberHoldsAtMostOneTask_BoardMutexStillHolds()
        {
            var squad = new SquadCoordinator(BoardWithTwoTasks());
            squad.Join("npc-a");
            squad.Join("npc-b");

            Assert.IsTrue(squad.TryClaimTask("npc-a", "task-1", 1f), "a 认领 task-1");
            Assert.IsFalse(squad.TryClaimTask("npc-a", "task-2", 1f), "成员任务不冲突：一人至多一项");
            Assert.IsFalse(squad.TryClaimTask("npc-b", "task-1", 1f), "板上互斥：b 也不能抢 a 已持有的 task-1");
            Assert.IsTrue(squad.TryClaimTask("npc-b", "task-2", 1f), "b 认领剩下的 task-2");

            Assert.AreEqual("task-1", squad.TaskOf("npc-a"));
            Assert.AreEqual("task-2", squad.TaskOf("npc-b"));
        }

        [Test]
        public void CompleteTask_FreesMemberForNextClaim()
        {
            var squad = new SquadCoordinator(BoardWithTwoTasks());
            squad.Join("npc-a");
            squad.TryClaimTask("npc-a", "task-1", 1f);

            Assert.IsFalse(squad.CompleteTask("npc-b"), "没持任务的人不能完成");
            Assert.IsTrue(squad.CompleteTask("npc-a"), "持有人完成");
            Assert.IsNull(squad.TaskOf("npc-a"), "完成后成员空手");
            Assert.AreEqual(1, squad.Board.Count, "板上只剩 task-2");
            Assert.IsTrue(squad.TryClaimTask("npc-a", "task-2", 2f), "空手后可再认领");
        }

        [Test]
        public void ReleaseTask_PutsTaskBackToOpen()
        {
            var squad = new SquadCoordinator(BoardWithTwoTasks());
            squad.Join("npc-a");
            squad.TryClaimTask("npc-a", "task-1", 1f);

            Assert.IsTrue(squad.ReleaseTask("npc-a"));
            Assert.IsNull(squad.TaskOf("npc-a"));
            squad.Board.TryGetTask("task-1", out CoordinationTask task);
            Assert.AreEqual(CoordinationTaskStatus.Open, task.Status, "放生：任务回板保持开放");
            Assert.AreEqual(2, squad.Board.Count, "放生不删任务");
        }

        [Test]
        public void Leave_HoldingTask_ReleasesItToBoard()
        {
            var squad = new SquadCoordinator(BoardWithTwoTasks());
            squad.Join("npc-a");
            squad.TryClaimTask("npc-a", "task-1", 1f);

            squad.Leave("npc-a");
            squad.Board.TryGetTask("task-1", out CoordinationTask task);
            Assert.AreEqual(CoordinationTaskStatus.Open, task.Status, "离队即放生任务（队友可接手）");
            Assert.IsNull(squad.TaskOf("npc-a"), "离队者任务簿清空");
        }

        [Test]
        public void TryClaimTask_WithoutBoard_Throws()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-a");
            Assert.Throws<InvalidOperationException>(() => squad.TryClaimTask("npc-a", "task-1", 1f));
        }

        [Test]
        public void TryClaimTask_UnknownMember_Throws()
        {
            var squad = new SquadCoordinator(BoardWithTwoTasks());
            Assert.Throws<ArgumentException>(() => squad.TryClaimTask("npc-404", "task-1", 1f));
        }

        // ---- 协防：分派不重叠（距离+态度）----

        [Test]
        public void AssignDefense_TwoMembers_ThreeEnemies_NoOverlap()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-a");
            squad.Join("npc-b");
            squad.SetMemberState("npc-a", new Vec2(1f, 0f), 0f);
            squad.SetMemberState("npc-b", new Vec2(0f, 1f), 0f);

            var enemies = new List<AttackAssignment.TargetCandidate>
            {
                new AttackAssignment.TargetCandidate("enemy-1", new Vec2(5f, 0f)),   // a 距 4（最近）
                new AttackAssignment.TargetCandidate("enemy-2", new Vec2(0f, 5f)),   // b 距 4（次近）
                new AttackAssignment.TargetCandidate("enemy-3", new Vec2(5f, 5f)),   // 无人覆盖（容量 1）
            };

            AttackAssignment.Plan plan = squad.AssignDefense(enemies);

            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-a"));
            Assert.AreEqual("enemy-2", plan.TargetOfId("npc-b"));
            Assert.AreNotEqual(plan.TargetOfId("npc-a"), plan.TargetOfId("npc-b"), "协防不重叠");
            Assert.AreEqual(2, plan.AssignedCount, "两人两目标，第三个敌人显式轮空（不打同一目标）");
        }

        [Test]
        public void AssignDefense_AngrierMemberWinsTie()
        {
            var squad = new SquadCoordinator();
            squad.Join("npc-calm");
            squad.Join("npc-angry");
            squad.SetMemberState("npc-calm", new Vec2(0f, 0f), 0f);
            squad.SetMemberState("npc-angry", new Vec2(4f, 0f), 1f);

            var enemies = new List<AttackAssignment.TargetCandidate>
            {
                new AttackAssignment.TargetCandidate("enemy-1", new Vec2(2f, 0f)), // 两人等距 2
            };

            AttackAssignment.Plan plan = squad.AssignDefense(enemies);
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-angry"), "态度分派：怒者有效距离 0.8 < 2");
            Assert.IsNull(plan.TargetOfId("npc-calm"));
        }

        [Test]
        public void SetMemberState_UnknownMember_Throws()
        {
            var squad = new SquadCoordinator();
            Assert.Throws<ArgumentException>(() => squad.SetMemberState("npc-404", Vec2.Zero, 0f));
        }
    }
}
