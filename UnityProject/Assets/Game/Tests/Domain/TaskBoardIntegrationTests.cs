using System.Collections.Generic;
using Game.Domain.AI.Coordination;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using ZFrame.Gameplay;
using ZFrame.Gameplay.AI;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Coordination integration tests (TASK-A-AI-02) — the two bridges and
    /// the demo harness the brief demands, all hand-computable:
    /// - UtilityBrain 集成: open board tasks become real candidate actions,
    ///   scored by distance (认领成本=距离: nearer task 0.8 outranks the
    ///   0.3 patrol baseline; farther task 0.2 falls below it), and the
    ///   winning tick CLAIMS the task on the board;
    /// - GOAP 集成: the planner consumes TaskBoard atoms into the canonical
    ///   two-step chain [ClaimTask → ExecuteTask] (claim cost = distance 5,
    ///   total 6), a holder replans to a single execute step, expired tasks
    ///   are invisible to the planner;
    /// - 演示: player + 2 teammates + 3 enemies — task claims don't overlap,
    ///   formation slots are unique, defense assignments don't overlap.
    /// </summary>
    public sealed class TaskBoardIntegrationTests
    {
        private const string AgentA = "npc-a";
        private const string AgentB = "npc-b";

        // ---- UtilityBrain 集成：TaskBoard 任务作为候选行动 ----

        [Test]
        public void TaskClaimsAreRankedByDistance_AndWinTheBrain()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(2f, 0f), float.PositiveInfinity); // task-1：距原点 2
            board.Publish("pickup", new Vec2(8f, 0f), float.PositiveInfinity); // task-2：距原点 8

            var brain = new UtilityBrain();
            foreach (UtilityAction action in TaskBoardCandidates.CreateClaimActions(board, AgentA, 0f))
            {
                brain.Add(action); // 认领成本=距离：近任务分高
            }

            // 巡逻基线（欲望 0.3）：近任务 0.8 > 0.3 > 远任务 0.2
            brain.Add(new UtilityAction(NpcUtilityAgent.PatrolActionName, state => 0.3f, () => { }));

            var state = new NPCWorldState();
            state.Set(TaskBoardCandidates.DistanceStateKey("task-1"), Vec2.Distance(Vec2.Zero, new Vec2(2f, 0f)));
            state.Set(TaskBoardCandidates.DistanceStateKey("task-2"), Vec2.Distance(Vec2.Zero, new Vec2(8f, 0f)));

            string chosen = brain.Tick(state, 0f);

            Assert.AreEqual(TaskBoardCandidates.ActionNameOf("task-1"), chosen, "近任务胜出");
            board.TryGetTask("task-1", out CoordinationTask task);
            Assert.AreEqual(CoordinationTaskStatus.Claimed, task.Status);
            Assert.AreEqual(AgentA, task.ClaimedBy, "执行委托真的在板上完成了认领");
        }

        [Test]
        public void ClaimDesire_IsHandComputable_DistanceDecay()
        {
            var options = new TaskBoardCandidates.TaskClaimOptions();
            var state = new NPCWorldState();

            state.Set(TaskBoardCandidates.DistanceStateKey("task-1"), 2f);
            Assert.That(
                TaskBoardCandidates.ScoreClaim(state, TaskBoardCandidates.DistanceStateKey("task-1"), options),
                Is.EqualTo(0.9f * (8f / 9f)).Within(1e-4f),
                "Fall(2,1,10)=8/9，×0.9 = 0.8（手算锚点）");

            state.Set(TaskBoardCandidates.DistanceStateKey("task-2"), 8f);
            Assert.That(
                TaskBoardCandidates.ScoreClaim(state, TaskBoardCandidates.DistanceStateKey("task-2"), options),
                Is.EqualTo(0.9f * (2f / 9f)).Within(1e-4f),
                "Fall(8,1,10)=2/9，×0.9 = 0.2 < 巡逻 0.3（不值得走过去）");

            Assert.AreEqual(0f, TaskBoardCandidates.ScoreClaim(
                new NPCWorldState(), TaskBoardCandidates.DistanceStateKey("task-x"), options),
                "未感知（键缺失）⇒ 欲望 0");
        }

        [Test]
        public void ClaimedTask_DisappearsFromOtherAgentsCandidates()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(2f, 0f), float.PositiveInfinity);
            board.Claim("task-1", AgentA, 0f);

            List<UtilityAction> candidatesForB = TaskBoardCandidates.CreateClaimActions(board, AgentB, 0f);
            Assert.AreEqual(0, candidatesForB.Count, "已被 a 认领的任务不再是 b 的候选（互斥前置）");

            board.Publish("pickup", new Vec2(3f, 0f), float.PositiveInfinity); // task-2 仍开放
            List<UtilityAction> refreshed = TaskBoardCandidates.CreateClaimActions(board, AgentB, 0f);
            Assert.AreEqual(1, refreshed.Count);
            Assert.AreEqual(TaskBoardCandidates.ActionNameOf("task-2"), refreshed[0].Name);

            refreshed[0].Execute();
            board.TryGetTask("task-2", out CoordinationTask task);
            Assert.AreEqual(AgentB, task.ClaimedBy, "b 的执行委托认领到 task-2");
        }

        // ---- GOAP 集成：规划消费 TaskBoard 任务（两步链先认领再执行）----

        [Test]
        public void Goap_PlansTwoStepChain_ClaimThenExecute_WithDistanceCost()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 4f), float.PositiveInfinity); // task-1：距原点 5

            GoapState world = TaskBoardGoapBridge.BuildWorldState(board, AgentA, 0f);
            Assert.IsTrue(world.Has(TaskBoardGoapBridge.OpenAtomOf("task-1")), "开放任务 → open 原子");
            Assert.IsFalse(world.Has(TaskBoardGoapBridge.ClaimedAtomOf("task-1")));
            Assert.IsFalse(world.Has(TaskBoardGoapBridge.DoneAtomOf("task-1")));

            List<GoapAction> actions = TaskBoardGoapBridge.BuildActions(board, AgentA, Vec2.Zero, 0f);
            Assert.AreEqual(2, actions.Count, "一个开放任务 = claim + execute 两个算子");

            var goal = new GoapState();
            goal.Add(TaskBoardGoapBridge.DoneAtomOf("task-1"));
            GoapPlan plan = GoapPlanner.Plan(world, goal, actions, GoapPlanner.DefaultMaxDepth);

            Assert.IsNotNull(plan, "两步可解：先认领再执行");
            Assert.AreEqual(2, plan.StepCount, "两步链：ClaimTask → ExecuteTask");
            Assert.AreEqual(TaskBoardGoapBridge.ClaimActionNameOf("task-1"), plan.Steps[0].Name);
            Assert.AreEqual(TaskBoardGoapBridge.ExecuteActionNameOf("task-1"), plan.Steps[1].Name);
            Assert.AreEqual(6f, plan.TotalCost, 1e-3f, "认领成本=距离 5 + 执行 1 = 6");
            Assert.AreEqual("ClaimTask:task-1 -> ExecuteTask:task-1", plan.Format());
        }

        [Test]
        public void Goap_ChainExecutesOntoTheBoard_StepByStep()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), float.PositiveInfinity);

            GoapState world = TaskBoardGoapBridge.BuildWorldState(board, AgentA, 0f);
            List<GoapAction> actions = TaskBoardGoapBridge.BuildActions(board, AgentA, Vec2.Zero, 0f);
            var goal = new GoapState();
            goal.Add(TaskBoardGoapBridge.DoneAtomOf("task-1"));
            GoapPlan plan = GoapPlanner.Plan(world, goal, actions);

            // 逐步落地：计划第 1 步（认领）→ 板上真认领；第 2 步（执行）→ 板上真完成。
            Assert.AreEqual(TaskBoardGoapBridge.ClaimActionNameOf("task-1"), plan.Steps[0].Name);
            Assert.IsTrue(board.Claim("task-1", AgentA, 1f), "认领步落地");

            Assert.AreEqual(TaskBoardGoapBridge.ExecuteActionNameOf("task-1"), plan.Steps[1].Name);
            Assert.IsTrue(board.Complete("task-1", AgentA), "执行步落地");
            Assert.AreEqual(0, board.Count, "任务完成离开板");
        }

        [Test]
        public void Goap_HolderReplansToSingleExecuteStep()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), float.PositiveInfinity);
            board.Claim("task-1", AgentA, 0f);

            GoapState world = TaskBoardGoapBridge.BuildWorldState(board, AgentA, 0f);
            Assert.IsTrue(world.Has(TaskBoardGoapBridge.ClaimedAtomOf("task-1")), "自己的持有 → claimed 原子");
            Assert.IsFalse(world.Has(TaskBoardGoapBridge.OpenAtomOf("task-1")), "已认领任务不再开放");

            List<GoapAction> actions = TaskBoardGoapBridge.BuildActions(board, AgentA, Vec2.Zero, 0f);
            var goal = new GoapState();
            goal.Add(TaskBoardGoapBridge.DoneAtomOf("task-1"));
            GoapPlan plan = GoapPlanner.Plan(world, goal, actions);

            Assert.IsNotNull(plan);
            Assert.AreEqual(1, plan.StepCount, "已持有人直接单步执行（无需重复认领）");
            Assert.AreEqual(TaskBoardGoapBridge.ExecuteActionNameOf("task-1"), plan.Steps[0].Name);
            Assert.AreEqual(1f, plan.TotalCost, 1e-5f);
        }

        [Test]
        public void Goap_OtherAgentsClaimsAreInvisibleInMyWorld()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), float.PositiveInfinity);
            board.Claim("task-1", AgentB, 0f);

            GoapState worldForA = TaskBoardGoapBridge.BuildWorldState(board, AgentA, 0f);
            Assert.IsFalse(worldForA.Has(TaskBoardGoapBridge.OpenAtomOf("task-1")), "别人的持有对我既不开放也非我持有");
            Assert.IsFalse(worldForA.Has(TaskBoardGoapBridge.ClaimedAtomOf("task-1")));
            List<GoapAction> actionsForA = TaskBoardGoapBridge.BuildActions(board, AgentA, Vec2.Zero, 0f);
            Assert.AreEqual(0, actionsForA.Count, "a 无可规划算子（符号接地：不能替 b 执行）");
        }

        [Test]
        public void Goap_ExpiredTask_IsInvisibleToThePlanner()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(1f, 0f), 5f);

            GoapState world = TaskBoardGoapBridge.BuildWorldState(board, AgentA, 10f);
            Assert.AreEqual(0, world.AtomCount, "过期任务不产生原子");
            List<GoapAction> actions = TaskBoardGoapBridge.BuildActions(board, AgentA, Vec2.Zero, 10f);
            Assert.AreEqual(0, actions.Count, "过期任务不产生算子");
        }

        // ---- 演示：玩家 + 2 队友 + 3 敌人（简报设计要点 3 的断言）----

        [Test]
        public void SquadDemo_ClaimsAndSlotsAndDefense_AllDisjoint()
        {
            // 玩家在原点；两个队友 NPC；两个拾取任务；三个敌人。
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 0f), float.PositiveInfinity);
            board.Publish("pickup", new Vec2(0f, 3f), float.PositiveInfinity);
            var squad = new SquadCoordinator(board);
            squad.Join(AgentA);
            squad.Join(AgentB);

            // 编队：环形槽唯一（玩家周围两槽，对称）。
            IReadOnlyList<Vec2> slots = squad.UpdateFormation(Vec2.Zero);
            Assert.AreEqual(2, slots.Count);
            Assert.IsTrue(FormationSlot.AreDistinct(slots), "编队槽唯一");

            // 认领不冲突：a 抢 task-1 成功，b 抢 task-1 失败 → b 拿 task-2。
            Assert.IsTrue(squad.TryClaimTask(AgentA, "task-1", 0f));
            Assert.IsFalse(squad.TryClaimTask(AgentB, "task-1", 0f), "认领互斥：不抢同一任务");
            Assert.IsTrue(squad.TryClaimTask(AgentB, "task-2", 0f));
            Assert.AreNotEqual(squad.TaskOf(AgentA), squad.TaskOf(AgentB), "两人任务簿互异");

            // 协防：三个敌人 → 每人至多一目标、目标互不重复。
            squad.SetMemberState(AgentA, slots[0], 0f);
            squad.SetMemberState(AgentB, slots[1], 0f);
            var enemies = new List<AttackAssignment.TargetCandidate>
            {
                new AttackAssignment.TargetCandidate("enemy-1", new Vec2(5f, 0f)),
                new AttackAssignment.TargetCandidate("enemy-2", new Vec2(0f, 5f)),
                new AttackAssignment.TargetCandidate("enemy-3", new Vec2(5f, 5f)),
            };
            AttackAssignment.Plan defense = squad.AssignDefense(enemies);

            Assert.AreEqual(2, defense.AssignedCount, "两队友覆盖两敌，第三个轮空");
            string targetA = defense.TargetOfId(AgentA);
            string targetB = defense.TargetOfId(AgentB);
            Assert.IsNotNull(targetA);
            Assert.IsNotNull(targetB);
            Assert.AreNotEqual(targetA, targetB, "分派不重叠（断言锚点）");
            Assert.AreEqual("enemy-1", targetA, "a 在 (2,0) → 距 enemy-1 3m 最近");
            Assert.AreEqual("enemy-2", targetB, "b 在 (−2,0) → 距 enemy-2 5.385；a 先定后 b 落次近的 enemy-2");
        }

        [Test]
        public void SquadDemo_CompletingTask_LetsTheOtherAgentReplan()
        {
            var board = new TaskBoard();
            board.Publish("pickup", new Vec2(3f, 0f), float.PositiveInfinity);
            var squad = new SquadCoordinator(board);
            squad.Join(AgentA);
            squad.Join(AgentB);

            Assert.IsTrue(squad.TryClaimTask(AgentA, "task-1", 0f));
            Assert.IsFalse(squad.TryClaimTask(AgentB, "task-1", 0f), "a 持有期间 b 无法认领");

            Assert.IsTrue(squad.CompleteTask(AgentA), "a 完成");
            Assert.AreEqual(0, board.Count);
            // 任务已完成并离板；b 的候选与 GOAP 世界随之清空。
            Assert.AreEqual(0, TaskBoardCandidates.CreateClaimActions(board, AgentB, 0f).Count);
            Assert.AreEqual(0, TaskBoardGoapBridge.BuildActions(board, AgentB, Vec2.Zero, 0f).Count);
        }
    }
}
