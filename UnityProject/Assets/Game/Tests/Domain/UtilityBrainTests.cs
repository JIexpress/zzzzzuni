using System;
using System.Collections.Generic;
using Game.Domain.AI.Utility;
using NUnit.Framework;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Utility-brain mechanic tests (TASK-A-TECH-01): the falsifiable core of
    /// the slice — same state in, same ranking and same winner out; ties break
    /// deterministically to the earliest registered action; below-threshold
    /// winners idle without executing; cooldowns exclude candidates by the
    /// caller-pumped clock (no wall time). Toy score functions make every
    /// expected winner hand-computable.
    /// </summary>
    public sealed class UtilityBrainTests
    {
        private sealed class Recorder
        {
            public int Executions;
            public void Execute() => Executions++;
        }

        private static NPCWorldState State(float fatigue = 0.5f)
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, fatigue);
            return state;
        }

        [Test]
        public void SameStateInput_SameRanking_SameWinner()
        {
            var brain = new UtilityBrain();
            var first = new Recorder();
            var second = new Recorder();
            brain.Add(new UtilityAction("Low", _ => 0.2f, first.Execute));
            brain.Add(new UtilityAction("High", _ => 0.8f, second.Execute));

            NPCWorldState state = State();
            string chosenOne = brain.Tick(state, 0f);
            var rankingOne = Snapshot(brain.LastRanking);

            string chosenTwo = brain.Tick(state, 1f);
            var rankingTwo = Snapshot(brain.LastRanking);

            Assert.AreEqual("High", chosenOne);
            Assert.AreEqual("High", chosenTwo, "同一状态输入必须选出同一行动（自主性的前提是可复现）");
            CollectionAssert.AreEqual(rankingOne, rankingTwo, "同一状态的排序必须逐项相同");
            Assert.AreEqual(2, first.Executions + second.Executions, "每次 Tick 恰好执行一个赢家");
        }

        [Test]
        public void Ranking_IsDescending_AndDeterministicOnTies()
        {
            var brain = new UtilityBrain();
            var tieA = new Recorder();
            var high = new Recorder();
            var tieC = new Recorder();
            brain.Add(new UtilityAction("TieA", _ => 0.5f, tieA.Execute));
            brain.Add(new UtilityAction("High", _ => 0.8f, high.Execute));
            brain.Add(new UtilityAction("TieC", _ => 0.5f, tieC.Execute));

            string chosen = brain.Tick(State(), 0f);

            Assert.AreEqual("High", chosen);
            Assert.AreEqual(new[] { "High", "TieA", "TieC" }, Names(brain.LastRanking),
                "排序必须降序，同分项按注册顺序（先注册在前）");
        }

        [Test]
        public void TieBreak_EarliestRegisteredWins_RegardlessOfLaterOrder()
        {
            var firstRegistered = new Recorder();
            var secondRegistered = new Recorder();

            var forward = new UtilityBrain();
            forward.Add(new UtilityAction("Alpha", _ => 0.6f, firstRegistered.Execute));
            forward.Add(new UtilityAction("Beta", _ => 0.6f, secondRegistered.Execute));
            Assert.AreEqual("Alpha", forward.Tick(State(), 0f), "同分时先注册者胜（Alpha 先注册）");

            var reversed = new UtilityBrain();
            reversed.Add(new UtilityAction("Beta", _ => 0.6f, secondRegistered.Execute));
            reversed.Add(new UtilityAction("Alpha", _ => 0.6f, firstRegistered.Execute));
            Assert.AreEqual("Beta", reversed.Tick(State(), 0f), "交换注册顺序后胜者必须交换——证明打破规则是注册序而非名字");
        }

        [Test]
        public void BelowThreshold_Idles_WithoutExecuting()
        {
            var brain = new UtilityBrain(0.5f);
            var weak = new Recorder();
            brain.Add(new UtilityAction("Weak", _ => 0.4f, weak.Execute));

            Assert.AreEqual(UtilityBrain.IdleActionName, brain.Tick(State(), 0f), "唯一候选低于阈值 → Idle");
            Assert.AreEqual(0, weak.Executions, "Idle 不得执行任何委托");

            var withStrong = new UtilityBrain(0.5f);
            var strong = new Recorder();
            withStrong.Add(new UtilityAction("Weak", _ => 0.4f, () => { }));
            withStrong.Add(new UtilityAction("Strong", _ => 0.6f, strong.Execute));
            Assert.AreEqual("Strong", withStrong.Tick(State(), 0f), "过阈值的候选照常当选");
            Assert.AreEqual(1, strong.Executions);
        }

        [Test]
        public void Cooldown_ExcludesCandidate_UntilClockPasses()
        {
            var brain = new UtilityBrain();
            var fastAction = new Recorder();
            var fallback = new Recorder();
            brain.Add(new UtilityAction("Burst", _ => 1f, fastAction.Execute, 2f));
            brain.Add(new UtilityAction("Fallback", _ => 0.5f, fallback.Execute));

            Assert.AreEqual("Burst", brain.Tick(State(), 0f));
            Assert.AreEqual("Fallback", brain.Tick(State(), 1f), "冷却期内必须让位给次优候选");
            Assert.AreEqual("Fallback", brain.Tick(State(), 1.9f), "冷却未到（time < 0+2）仍然排除");
            Assert.AreEqual("Burst", brain.Tick(State(), 2f), "time >= lastExecute+cooldown 恢复可选");
            Assert.AreEqual(2, fastAction.Executions);
            Assert.AreEqual(2, fallback.Executions);
        }

        [Test]
        public void CooldownAction_IsMissingFromRanking_WhileCooling()
        {
            var brain = new UtilityBrain();
            brain.Add(new UtilityAction("Burst", _ => 1f, () => { }, 3f));
            brain.Add(new UtilityAction("Fallback", _ => 0.5f, () => { }));

            brain.Tick(State(), 0f);
            brain.Tick(State(), 1f);

            Assert.AreEqual(new[] { "Fallback" }, Names(brain.LastRanking), "冷却中的候选不进入排序");

            brain.Tick(State(), 3f);
            Assert.AreEqual(new[] { "Burst", "Fallback" }, Names(brain.LastRanking), "冷却结束后重新进入排序（按分数降序）");
        }

        [Test]
        public void NaN_Score_NeverSelected()
        {
            var brain = new UtilityBrain();
            var broken = new Recorder();
            var healthy = new Recorder();
            brain.Add(new UtilityAction("Broken", _ => float.NaN, broken.Execute));
            brain.Add(new UtilityAction("Healthy", _ => 0.3f, healthy.Execute));

            Assert.AreEqual("Healthy", brain.Tick(State(), 0f), "NaN 分数按无欲望处理，不得当选");
            Assert.AreEqual(0, broken.Executions);
        }

        [Test]
        public void EmptyBrain_Idles()
        {
            var brain = new UtilityBrain();
            Assert.AreEqual(UtilityBrain.IdleActionName, brain.Tick(State(), 0f));
            Assert.AreEqual(0, brain.ActionCount);
        }

        [Test]
        public void DuplicateActionName_Throws()
        {
            var brain = new UtilityBrain();
            brain.Add(new UtilityAction("Rest", _ => 1f, () => { }));
            Assert.Throws<ArgumentException>(() => brain.Add(new UtilityAction("Rest", _ => 1f, () => { })));
        }

        [Test]
        public void NullState_Throws()
        {
            var brain = new UtilityBrain();
            brain.Add(new UtilityAction("Rest", _ => 1f, () => { }));
            Assert.Throws<ArgumentNullException>(() => brain.Tick(null, 0f));
        }

        private static string[] Names(IReadOnlyList<UtilityBrain.ScoredAction> ranking)
        {
            var names = new string[ranking.Count];
            for (int i = 0; i < ranking.Count; i++)
            {
                names[i] = ranking[i].Name;
            }

            return names;
        }

        private static (string Name, float Value)[] Snapshot(IReadOnlyList<UtilityBrain.ScoredAction> ranking)
        {
            var snapshot = new (string, float)[ranking.Count];
            for (int i = 0; i < ranking.Count; i++)
            {
                snapshot[i] = (ranking[i].Name, ranking[i].Value);
            }

            return snapshot;
        }
    }
}
