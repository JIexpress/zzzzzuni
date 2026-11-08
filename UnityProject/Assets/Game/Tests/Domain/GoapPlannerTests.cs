using System;
using System.Collections.Generic;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using ZFrame.Gameplay.AI;

namespace Game.Domain.Tests
{
    /// <summary>
    /// GOAP minimal planner tests (TASK-A-TECH-04) — the falsifiable core of
    /// the forward-looking slice, all hand-computable:
    /// - single-step infeasible (repair needs the tool) → the planner MUST
    ///   return the two-step chain [TakeTool → RepairDevice] (the briefing's
    ///   工具箱→损坏装置 scenario);
    /// - precondition already met → single step;
    /// - no applicable route → explicit null (never a silent empty plan);
    /// - depth cap: the same problem fails at maxDepth=1 and solves at 2;
    /// - determinism: same input → same plan; cycles terminate via the
    ///   visited set (mutually undoing actions cannot loop).
    /// </summary>
    public sealed class GoapPlannerTests
    {
        private static GoapState State(params string[] atoms)
        {
            var state = new GoapState();
            foreach (string atom in atoms)
            {
                state.Add(atom);
            }

            return state;
        }

        /// <summary>The demo's repair problem: two operators, tool-gated repair.</summary>
        private static GoapAction[] RepairActions()
        {
            return new[]
            {
                new GoapAction("TakeTool", null, new[] { "has_tool" }, null, 1f),
                new GoapAction("RepairDevice", new[] { "has_tool", "device_broken" },
                    new[] { "device_repaired" }, new[] { "device_broken" }, 1f),
            };
        }

        // ---- 核心证据：单步不可行 → 两步链 ----

        [Test]
        public void SingleStepInfeasible_PlannerProducesTwoStepChain()
        {
            // 起始世界：装置坏了、手里没工具。RepairDevice 的前置 has_tool 不满足 → 单步无解。
            GoapState start = State("device_broken");
            GoapState goal = State("device_repaired");

            GoapPlan plan = GoapPlanner.Plan(start, goal, RepairActions(), GoapPlanner.DefaultMaxDepth);

            Assert.IsNotNull(plan, "两步可解的问题必须产出计划");
            Assert.AreEqual(2, plan.StepCount, "必须恰好两步：取工具 → 修理");
            Assert.AreEqual("TakeTool", plan.Steps[0].Name);
            Assert.AreEqual("RepairDevice", plan.Steps[1].Name);
            Assert.AreEqual(2f, plan.TotalCost, 1e-5f, "总代价 = 两步代价之和");
            Assert.AreEqual("TakeTool -> RepairDevice", plan.Format());
        }

        [Test]
        public void PreconditionAlreadyMet_ProducesSingleStep()
        {
            GoapState start = State("device_broken", "has_tool"); // 工具已在手
            GoapState goal = State("device_repaired");

            GoapPlan plan = GoapPlanner.Plan(start, goal, RepairActions(), GoapPlanner.DefaultMaxDepth);

            Assert.IsNotNull(plan);
            Assert.AreEqual(1, plan.StepCount, "前置已满足时不得多规划取工具");
            Assert.AreEqual("RepairDevice", plan.Steps[0].Name);
        }

        [Test]
        public void Planner_SkipsInapplicableDistractor()
        {
            var actions = new List<GoapAction>(RepairActions())
            {
                // 不可满足的干扰项：magic_wrench 在世界里永远不存在。
                new GoapAction("SummonTool", new[] { "magic_wrench" }, new[] { "has_tool" }, null, 0.1f),
            };

            GoapPlan plan = GoapPlanner.Plan(
                State("device_broken"), State("device_repaired"), actions, GoapPlanner.DefaultMaxDepth);

            Assert.IsNotNull(plan);
            Assert.AreEqual(2, plan.StepCount);
            Assert.AreEqual("TakeTool", plan.Steps[0].Name, "规划器必须跳过前置不可满足的干扰动作");
        }

        // ---- 无解：明确失败 ----

        [Test]
        public void UnreachableGoal_ReturnsNull()
        {
            // 只有取工具、没有修理动作 → device_repaired 永远不可达。
            var actions = new[] { new GoapAction("TakeTool", null, new[] { "has_tool" }, null, 1f) };

            GoapPlan plan = GoapPlanner.Plan(
                State("device_broken"), State("device_repaired"), actions, GoapPlanner.DefaultMaxDepth);

            Assert.IsNull(plan, "无解必须显式返回 null（不得返回空计划伪装成功）");
        }

        [Test]
        public void PreconditionsUnmetAnywhere_ReturnsNull()
        {
            // 修理动作需要 has_tool，但世界没有能产出它的动作。
            var actions = new[]
            {
                new GoapAction("RepairDevice", new[] { "has_tool", "device_broken" },
                    new[] { "device_repaired" }, new[] { "device_broken" }, 1f),
            };

            GoapPlan plan = GoapPlanner.Plan(
                State("device_broken"), State("device_repaired"), actions, GoapPlanner.DefaultMaxDepth);

            Assert.IsNull(plan, "前置在任何可达状态都不满足 → 无解");
        }

        // ---- 深度上限 ----

        [Test]
        public void DepthCap_TooShallowFails_DeeperSolves()
        {
            GoapState start = State("device_broken");
            GoapState goal = State("device_repaired");

            GoapPlan shallow = GoapPlanner.Plan(start, goal, RepairActions(), 1);
            Assert.IsNull(shallow, "深度上限 1 时两步链不可达 → null");

            GoapPlan deep = GoapPlanner.Plan(start, goal, RepairActions(), 2);
            Assert.IsNotNull(deep, "深度上限 2 时同一问题必须可解");
            Assert.AreEqual(2, deep.StepCount);
        }

        [Test]
        public void DepthCapZero_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GoapPlanner.Plan(State(), State("x"), new GoapAction[0], 0));
        }

        // ---- 已满足目标与确定性 ----

        [Test]
        public void GoalAlreadySatisfied_ReturnsEmptyPlan()
        {
            GoapPlan plan = GoapPlanner.Plan(
                State("device_repaired"), State("device_repaired"), RepairActions(), GoapPlanner.DefaultMaxDepth);

            Assert.IsNotNull(plan, "目标已满足是平凡可解");
            Assert.AreEqual(0, plan.StepCount, "已满足 → 空计划（零步）");
            Assert.AreEqual(0f, plan.TotalCost, 1e-5f);
        }

        [Test]
        public void SameInput_SamePlan_Deterministic()
        {
            GoapState start = State("device_broken");
            GoapState goal = State("device_repaired");

            GoapPlan first = GoapPlanner.Plan(start.Clone(), goal.Clone(), RepairActions(), 3);
            GoapPlan second = GoapPlanner.Plan(start.Clone(), goal.Clone(), RepairActions(), 3);

            Assert.AreEqual(first.Format(), second.Format(), "同一输入必须产出同一计划（前瞻的确定性契约）");
            Assert.AreEqual(first.TotalCost, second.TotalCost, 1e-5f);
        }

        [Test]
        public void Planner_DoesNotMutateInputs()
        {
            GoapState start = State("device_broken");
            GoapState goal = State("device_repaired");
            string startKey = start.ToKey();
            string goalKey = goal.ToKey();

            GoapPlanner.Plan(start, goal, RepairActions(), 3);

            Assert.AreEqual(startKey, start.ToKey(), "规划不得改写起始状态");
            Assert.AreEqual(goalKey, goal.ToKey(), "规划不得改写目标状态");
        }

        // ---- 环终止：互逆动作不得死循环 ----

        [Test]
        public void MutuallyUndoingActions_TerminateViaVisitedSet()
        {
            // A 加 x 撤 y，B 加 y 撤 x —— 互相抵消；z 永远不可达，
            // 深度上限 3 内规划器必须借 visited 集终止并返回 null。
            var actions = new[]
            {
                new GoapAction("Open", null, new[] { "x" }, new[] { "y" }, 1f),
                new GoapAction("Close", new[] { "x" }, new[] { "y" }, new[] { "x" }, 1f),
            };

            GoapPlan plan = GoapPlanner.Plan(State(), State("z"), actions, 3);

            Assert.IsNull(plan, "环不得死循环；z 不可达 → null");
        }

        // ---- 非法入参 ----

        [Test]
        public void InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() =>
                GoapPlanner.Plan(null, State("x"), new GoapAction[0], 3));
            Assert.Throws<ArgumentNullException>(() =>
                GoapPlanner.Plan(State(), null, new GoapAction[0], 3));
            Assert.Throws<ArgumentNullException>(() =>
                GoapPlanner.Plan(State(), State("x"), null, 3));
        }

        [Test]
        public void ContradictoryEffects_Throw()
        {
            Assert.Throws<ArgumentException>(() => new GoapAction(
                "Buggy", null, new[] { "x" }, new[] { "x" }, 1f));
        }
    }
}
