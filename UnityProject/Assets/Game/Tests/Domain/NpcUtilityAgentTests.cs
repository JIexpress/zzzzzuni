using System;
using System.Collections.Generic;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// NpcUtilityAgent tests (TASK-A-TECH-01) — the autonomy evidence the
    /// brief demands, at Domain level, fully hand-computable:
    /// - the SAME rested NPC patrols, and after nothing but a fatigue pump it
    ///   rests, and after recovering it patrols AGAIN (sequence assertion on
    ///   ActionChosen — no code change, no preset switch: world state alone
    ///   drove every flip);
    /// - the same world (drop present) yields Pickup with empty inventory but
    ///   Patrol once the desire decays with a full one;
    /// - two agents fed identical state streams end up identical
    ///   (determinism), and pure state wiring is observable in WorldState.
    /// </summary>
    public sealed class NpcUtilityAgentTests
    {
        private static readonly Vec2 Player = new Vec2(2f, 0f);
        private static readonly Vec2 RestPoint = new Vec2(5f, 0f);

        private static NpcUtilityAgent CreateAgent(Vec2? start = null, Vec2? restPoint = null)
        {
            var agent = new NpcUtilityAgent(
                "npc-test",
                start ?? Vec2.Zero,
                restPoint ?? RestPoint,
                new[] { new Vec2(3f, 0f), new Vec2(0f, 3f) });
            agent.SetPlayerPosition(Player);
            return agent;
        }

        // ---- 自主性证据 1：同一 NPC，疲劳态休息、满态巡逻、恢复再巡逻 ----

        [Test]
        public void SameNpc_RestedPatrols_TiredRests_RecoveredPatrolsAgain()
        {
            NpcUtilityAgent agent = CreateAgent();
            var seen = new List<string>();
            agent.ActionChosen += seen.Add;

            for (int i = 0; i < 5; i++)
            {
                agent.Tick(0.1f);
            }

            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.CurrentAction, "满态（不疲劳）→ 巡逻");
            Assert.AreEqual(1, seen.Count, "同一状态下连续 Tick 不得反复切换行为");
            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, seen[0]);

            agent.SetFatigue(0.95f);
            agent.Tick(0.1f);
            Assert.AreEqual(NpcUtilityAgent.RestActionName, agent.CurrentAction, "疲劳态 → 休息");
            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual(NpcUtilityAgent.RestActionName, seen[1]);

            // 走回休息点（约 4.1m ≈ 2.8s）并把疲劳从 1.0 恢复到巡逻阈值下（约 1.7s）
            for (int i = 0; i < 55; i++)
            {
                agent.Tick(0.1f);
            }

            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.CurrentAction, "恢复后同一 NPC 回到巡逻");
            Assert.GreaterOrEqual(seen.Count, 3);
            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, seen[2], "序列断言：巡逻 → 休息 → 巡逻（全由状态驱动）");
        }

        // ---- 自主性证据 2：同一世界，状态不同 → 排序不同 → 选择不同 ----

        [Test]
        public void StateChange_FatigueFlipsTheChoice()
        {
            NpcUtilityAgent agent = CreateAgent();

            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.Tick(0.1f), " rested → Patrol");

            agent.SetFatigue(0.95f);
            Assert.AreEqual(NpcUtilityAgent.RestActionName, agent.Tick(0.1f), "只改疲劳 → Rest（同一只 NPC、同一世界）");
        }

        [Test]
        public void StateChange_PickupDesireDecaysWithInventory()
        {
            NpcUtilityAgent agent = CreateAgent();
            agent.SetDropTarget(new Vec2(1f, 0f));

            Assert.AreEqual(NpcUtilityAgent.PickupActionName, agent.Tick(0.1f), "发现掉落物且空载 → 去捡");

            agent.SetInventory(10); // 饱和载货：欲望衰减 0.85*0.1=0.085 < 巡逻 0.299
            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.Tick(0.1f), "同一掉落物，满载 → 不捡，改巡逻");
        }

        // ---- 演示行为逐个落位 ----

        [Test]
        public void FreshRestedNpc_PatrolsTowardWaypoint()
        {
            NpcUtilityAgent agent = CreateAgent();

            agent.Tick(0.1f);

            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.CurrentAction);
            Assert.That(agent.Position.X, Is.EqualTo(0.15f).Within(1e-3f), "1.5m/s·0.1s 朝第一个路点移动");
            Assert.AreEqual(0, agent.WaypointIndex);
        }

        [Test]
        public void Pickup_WalksCollects_RaisesEvent_ClearsTarget()
        {
            NpcUtilityAgent agent = CreateAgent();
            Vec2? collected = null;
            int events = 0;
            agent.DropCollected += position =>
            {
                collected = position;
                events++;
            };

            agent.SetDropTarget(new Vec2(0.5f, 0f)); // 恰好在拾取半径上
            agent.Tick(0.1f);

            Assert.AreEqual(1, agent.Inventory, "进入拾取半径即收集");
            Assert.AreEqual(1, events);
            Assert.IsTrue(collected.HasValue && collected.Value == new Vec2(0.5f, 0f), "事件载荷必须是掉落物位置");

            agent.Tick(0.1f);
            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, agent.CurrentAction,
                "掉落物消费后目标必须清空（否则评分仍会被旧目标撑起来）");
        }

        [Test]
        public void FarPlayer_Follows_AndClosesDistance()
        {
            NpcUtilityAgent agent = CreateAgent();
            agent.SetPlayerPosition(new Vec2(12f, 0f));

            agent.Tick(0.1f);

            Assert.AreEqual(NpcUtilityAgent.FollowActionName, agent.CurrentAction, "玩家远离 → 跟随");
            Assert.That(agent.Position.X, Is.EqualTo(0.22f).Within(1e-3f), "2.2m/s·0.1s 朝玩家移动");
        }

        [Test]
        public void Fatigue_GrowsWhileActing_AndRestRecovers()
        {
            NpcUtilityAgent agent = CreateAgent();
            for (int i = 0; i < 10; i++)
            {
                agent.Tick(0.1f);
            }

            Assert.That(agent.Fatigue, Is.EqualTo(0.02f).Within(1e-4f), "行动 1s 疲劳 +0.02");

            NpcUtilityAgent resting = CreateAgent(start: Vec2.Zero, restPoint: Vec2.Zero); // 出生即在休息点
            resting.SetPlayerPosition(Player);
            resting.SetFatigue(1f);
            for (int i = 0; i < 10; i++)
            {
                resting.Tick(0.1f);
            }

            Assert.AreEqual(NpcUtilityAgent.RestActionName, resting.CurrentAction);
            Assert.That(resting.Fatigue, Is.EqualTo(0.67f).Within(5e-3f), "休息 1s 净恢复 0.33（0.35-0.02）");
        }

        // ---- 确定性与接线 ----

        [Test]
        public void IdenticalAgents_IdenticalStateStream_IdenticalOutcome()
        {
            NpcUtilityAgent a = CreateAgent();
            NpcUtilityAgent b = CreateAgent();

            for (int i = 0; i < 10; i++)
            {
                a.Tick(0.1f);
                b.Tick(0.1f);
            }

            Assert.AreEqual(a.Position, b.Position, "同一状态流必须走出同一轨迹");
            Assert.AreEqual(a.CurrentAction, b.CurrentAction);
            Assert.AreEqual(a.Fatigue, b.Fatigue);
            Assert.AreEqual(a.Clock, b.Clock);
        }

        [Test]
        public void Tick_PumpsWellKnownKeys_IntoWorldState()
        {
            NpcUtilityAgent agent = CreateAgent();
            agent.SetDropTarget(new Vec2(1f, 0f));
            agent.Tick(0.1f);

            Assert.IsTrue(agent.WorldState.TryGet(NPCWorldState.Keys.Fatigue, out float fatigue));
            Assert.Greater(fatigue, 0f);
            Assert.IsTrue(agent.WorldState.TryGet(NPCWorldState.Keys.PlayerDistance, out float playerDistance));
            Assert.That(playerDistance, Is.EqualTo(Vec2.Distance(Vec2.Zero, Player)).Within(1e-3f),
                "传感器在本拍行动前采样：距离按起点位置（拍末位置留给下一拍）");
            Assert.IsTrue(agent.WorldState.TryGet(NPCWorldState.Keys.InventoryCount, out int inventory));
            Assert.AreEqual(0, inventory);
            Assert.IsTrue(agent.WorldState.HasDrop());
            Assert.IsTrue(agent.WorldState.TryGet(NPCWorldState.Keys.DropDistance, out float dropDistance));
            Assert.That(dropDistance, Is.EqualTo(1f).Within(1e-3f), "掉落物距离同样按起点位置");

            agent.SetDropTarget(null);
            agent.Tick(0.1f);
            Assert.IsFalse(agent.WorldState.HasDrop(), "目标清空后键必须移除（评分按无掉落物）");
        }

        [Test]
        public void NonPositiveDelta_IsIgnored()
        {
            NpcUtilityAgent agent = CreateAgent();

            Assert.AreEqual(UtilityBrain.IdleActionName, agent.Tick(0f));
            Assert.AreEqual(UtilityBrain.IdleActionName, agent.Tick(-1f));
            Assert.AreEqual(0f, agent.Clock);
            Assert.AreEqual(Vec2.Zero, agent.Position);
            Assert.AreEqual(0f, agent.Fatigue);
        }

        [Test]
        public void InvalidSetup_Throws()
        {
            Assert.Throws<ArgumentException>(() => new NpcUtilityAgent(
                string.Empty, Vec2.Zero, RestPoint, new Vec2[0]));
            var brainMissing = new NpcUtilityAgent("npc-x", Vec2.Zero, RestPoint, new Vec2[0]);
            // TASK-A-AI-01：默认候选集 +Flee/+Counterattack（态度调制候选，无态度时恒 0 分）→ 5+2=7。
            Assert.AreEqual(7, brainMissing.Brain.ActionCount, "默认候选集必须装配 7 个行动（A-TECH-04 起 +Repair，A-AI-01 起 +态度两候选）");
        }
    }
}
