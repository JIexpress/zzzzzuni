using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Gameplay;
using ZFrame.Gameplay.AI;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Personality-weight tests (TASK-A-TECH-04) — the user's ask, verbatim:
    /// "每个人的都不一样，或者叫权重". The falsifiable core:
    /// - neutral (no personality / all-1.0) is bit-identical to the old brain
    ///   (A-TECH-01 compatibility);
    /// - the brain multiplies raw desire by the action's trait weight —
    ///   hand-computed values assert the exact arithmetic;
    /// - SAME state + different CFG personalities → different winners
    ///   (divergence, the core new evidence);
    /// - SAME personality + same state → same choice (determinism);
    /// - FIVE CFG_NpcPersonality rows fed through FIVE agents with an
    ///   identical state stream produce pairwise-different action sequences
    ///   (the 5-NPC divergence proof, config-driven end to end);
    /// - the GOAP repair chain: single-step infeasible → planner produces
    ///   [TakeTool → RepairDevice] and the agent walks toolbox → device.
    /// </summary>
    public sealed class UtilityPersonalityTests
    {
        private static NPCWorldState NeutralState()
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, 0.5f);
            return state;
        }

        private static NPCPersonalityDef Def(string id, float fatigue, float social, float curiosity, float bravery)
        {
            return new NPCPersonalityDef
            {
                Id = id,
                FatigueWeight = fatigue,
                SocialWeight = social,
                CuriosityWeight = curiosity,
                BraveryWeight = bravery,
            };
        }

        // ---- 中性兼容：无个性 = 全 1.0 = 旧行为 ----

        [Test]
        public void NoPersonality_WeightedScoresEqualRaw()
        {
            var brain = new UtilityBrain();
            brain.Add(new UtilityAction("A", _ => 0.4f, () => { }, 0f, UtilityTraits.Fatigue));
            brain.Add(new UtilityAction("B", _ => 0.6f, () => { }));

            brain.Tick(NeutralState(), 0f);

            Assert.AreEqual("B", brain.LastSelected);
            Assert.AreEqual(0.400f, brain.LastRanking.First(r => r.Name == "A").Value, 1e-5f,
                "无个性时加权值必须等于原始分（A-TECH-01 行为逐位兼容）");
            Assert.AreEqual(0.600f, brain.LastRanking.First(r => r.Name == "B").Value, 1e-5f);
        }

        [Test]
        public void AllOnesDef_BehavesLikeNeutral()
        {
            var neutral = new UtilityBrain();
            var ones = new UtilityBrain(Def("ones", 1f, 1f, 1f, 1f));
            foreach (UtilityBrain brain in new[] { neutral, ones })
            {
                brain.Add(new UtilityAction("A", _ => 0.4f, () => { }, 0f, UtilityTraits.Fatigue));
                brain.Add(new UtilityAction("B", _ => 0.6f, () => { }, 0f, UtilityTraits.Social));
            }

            neutral.Tick(NeutralState(), 0f);
            string neutralDump = neutral.FormatRanking();
            ones.Tick(NeutralState(), 0f);

            Assert.AreEqual(neutral.LastSelected, ones.LastSelected);
            Assert.AreEqual(neutralDump, ones.FormatRanking(), "全 1.0 权重必须与无个性逐位一致");
        }

        // ---- 乘法调制：手算加权值 + 特征隔离 ----

        [Test]
        public void Personality_MultipliesOnlyItsTrait_HandComputed()
        {
            var brain = new UtilityBrain(Def("p", fatigue: 2f, social: 0.5f, curiosity: 1f, bravery: 1f));
            brain.Add(new UtilityAction("A", _ => 0.4f, () => { }, 0f, UtilityTraits.Fatigue));
            brain.Add(new UtilityAction("B", _ => 0.4f, () => { }, 0f, UtilityTraits.Social));
            brain.Add(new UtilityAction("C", _ => 0.4f, () => { }));

            string winner = brain.Tick(NeutralState(), 0f);

            Assert.AreEqual("A", winner, "0.4×2.0=0.8 必须登顶");
            Assert.AreEqual(0.8f, brain.LastRanking.First(r => r.Name == "A").Value, 1e-5f, "乘法调制：0.4×2.0");
            Assert.AreEqual(0.2f, brain.LastRanking.First(r => r.Name == "B").Value, 1e-5f, "乘法调制：0.4×0.5");
            Assert.AreEqual(0.4f, brain.LastRanking.First(r => r.Name == "C").Value, 1e-5f, "无标签行动 = 中立 1.0");
            Assert.AreEqual(2f, brain.PersonalityWeightFor(UtilityTraits.Fatigue), 1e-5f);
            Assert.AreEqual(0.5f, brain.PersonalityWeightFor(UtilityTraits.Social), 1e-5f);
            Assert.AreEqual(1f, brain.PersonalityWeightFor(null), 1e-5f, "无标签 → 1.0");
        }

        [Test]
        public void WeightCanPushBelowThreshold_AndIdle()
        {
            // 原始 0.08 × 0.5 = 0.04 < 阈值 0.05 → 空转（权重参与阈值判定的手算证据）。
            var brain = new UtilityBrain(Def("low", 1f, 1f, 1f, 0.5f));
            brain.Add(new UtilityAction("A", _ => 0.08f, () => { }, 0f, UtilityTraits.Bravery));

            Assert.AreEqual(UtilityBrain.IdleActionName, brain.Tick(NeutralState(), 0f));
        }

        [Test]
        public void UnknownTrait_Throws()
        {
            Assert.Throws<ArgumentException>(() => new UtilityAction(
                "X", _ => 1f, () => { }, 0f, "stubbornness"));
        }

        // ---- 核心分化：同状态、不同个性 → 不同选择 ----

        [Test]
        public void DifferentPersonalities_SameState_DifferentWinners()
        {
            var collector = new UtilityBrain(Def("curious", 0.5f, 0.5f, 2f, 0.5f));
            var follower = new UtilityBrain(Def("sociable", 0.5f, 2f, 0.5f, 0.5f));
            foreach (UtilityBrain brain in new[] { collector, follower })
            {
                brain.Add(new UtilityAction("Pickup", _ => 0.85f, () => { }, 0f, UtilityTraits.Curiosity));
                brain.Add(new UtilityAction("Follow", _ => 0.7f, () => { }, 0f, UtilityTraits.Social));
            }

            NPCWorldState state = NeutralState();

            Assert.AreEqual("Pickup", collector.Tick(state, 0f), "好奇 2.0：0.85×2=1.7 → 捡取");
            Assert.AreEqual("Follow", follower.Tick(NeutralState(), 1f), "社交 2.0：0.7×2=1.4 → 跟随（同状态不同个性→不同选择）");
        }

        [Test]
        public void SamePersonality_SameState_SameChoice_Deterministic()
        {
            var first = new UtilityBrain(Def("p", 1.8f, 0.6f, 1.2f, 0.9f));
            var second = new UtilityBrain(Def("p", 1.8f, 0.6f, 1.2f, 0.9f));
            foreach (UtilityBrain brain in new[] { first, second })
            {
                brain.Add(new UtilityAction("A", _ => 0.4f, () => { }, 0f, UtilityTraits.Fatigue));
                brain.Add(new UtilityAction("B", _ => 0.5f, () => { }, 0f, UtilityTraits.Curiosity));
            }

            string one = first.Tick(NeutralState(), 0f);
            string two = second.Tick(NeutralState(), 0f);
            string repeat = first.Tick(NeutralState(), 1f);

            Assert.AreEqual(one, two, "同个性同状态 → 同选择（确定性回归）");
            Assert.AreEqual(one, repeat, "同一大脑重复打分也必须稳定");
            Assert.AreEqual(first.FormatRanking(), second.FormatRanking());
        }

        // ---- 排名 dump ----

        [Test]
        public void FormatRanking_DumpsWeightedOrdering()
        {
            var brain = new UtilityBrain(Def("p", 1f, 1f, 2f, 1f));
            brain.Add(new UtilityAction("Pickup", _ => 0.85f, () => { }, 0f, UtilityTraits.Curiosity));
            brain.Add(new UtilityAction("Follow", _ => 0.7f, () => { }, 0f, UtilityTraits.Social));

            brain.Tick(NeutralState(), 0f);

            string dump = brain.FormatRanking();
            Assert.IsTrue(dump.Contains("Pickup=1.700"), $"dump 必须含加权值，实际: {dump}");
            Assert.IsTrue(dump.Contains("Follow=0.700"), $"dump 必须含加权值，实际: {dump}");
            Assert.IsTrue(dump.IndexOf("Pickup", StringComparison.Ordinal) < dump.IndexOf("Follow", StringComparison.Ordinal),
                "dump 必须按分数降序");
        }

        // ---- CFG_NpcPersonality 表 + 5 个 NPC 分化实证（核心新证据）----

        private const string PersonalityTablePath = "Assets/Configs/CFG_NpcPersonality.asset";
        private static readonly string[] ExpectedRowIds = { "balanced", "sociable", "curious", "brave", "lethargic" };

        private static NpcPersonalityTable LoadPersonalityTable()
        {
            var table = AssetDatabase.LoadAssetAtPath<NpcPersonalityTable>(PersonalityTablePath);
            Assert.IsNotNull(table, $"CFG 资产缺失：{PersonalityTablePath}（先跑 NpcPersonalityConfigSetup）");
            return table;
        }

        [Test]
        public void ConfigRows_FivePersonalities_InRange_PairwiseDifferent()
        {
            NpcPersonalityTable table = LoadPersonalityTable();

            Assert.AreEqual(5, table.Rows.Count, "CFG_NpcPersonality 必须恰好 5 行");
            CollectionAssert.AreEqual(ExpectedRowIds, table.Rows.Select(r => r.Id).ToArray(), "行序 = 演示顺序");

            foreach (NPCPersonalityDef row in table.Rows)
            {
                foreach (float weight in new[] { row.FatigueWeight, row.SocialWeight, row.CuriosityWeight, row.BraveryWeight })
                {
                    Assert.That(weight, Is.InRange(0.5f, 2f), $"行 {row.Id} 权重越界（配置校验口径 0.5~2.0）");
                }
            }

            for (int i = 0; i < table.Rows.Count; i++)
            {
                for (int j = i + 1; j < table.Rows.Count; j++)
                {
                    Assert.IsFalse(table.Rows[i].SameWeightsAs(table.Rows[j]),
                        $"行 {table.Rows[i].Id}/{table.Rows[j].Id} 权重向量相同 → 演示必然复读");
                }
            }
        }

        [Test]
        public void FiveConfigPersonalities_IdenticalWorld_PairwiseDivergentSequences()
        {
            NpcPersonalityTable table = LoadPersonalityTable();
            var agents = new List<NpcUtilityAgent>();
            foreach (string id in ExpectedRowIds)
            {
                NPCPersonalityDef row = table.FindRow(id);
                Assert.IsNotNull(row, $"CFG 行 {id} 缺失");
                agents.Add(CreatePumpedAgent(row));
            }

            // 共享世界控制器：第一人称捡走掉落物 / 修好装置 → 全体感知更新（同世界）。
            foreach (NpcUtilityAgent agent in agents)
            {
                agent.DropCollected += _ =>
                {
                    foreach (NpcUtilityAgent other in agents)
                    {
                        other.SetDropTarget(null);
                    }
                };
                agent.DeviceRepaired += _ =>
                {
                    foreach (NpcUtilityAgent other in agents)
                    {
                        other.SetDeviceBroken(false);
                    }
                };
            }

            var sequences = agents.Select(_ => new List<string>()).ToArray();
            for (int tick = 0; tick < 600; tick++)
            {
                for (int i = 0; i < agents.Count; i++)
                {
                    sequences[i].Add(agents[i].Tick(0.1f));
                }
            }

            // 首拍锚点（手算，见施工报告 2.4）：balanced/brave/lethargic → Repair（中立职责 0.9），
            // sociable → Follow（0.7×2=1.4），curious → Pickup（0.85×2=1.7）。
            Assert.AreEqual(NpcUtilityAgent.RepairActionName, sequences[0][0]);
            Assert.AreEqual(NpcUtilityAgent.FollowActionName, sequences[1][0]);
            Assert.AreEqual(NpcUtilityAgent.PickupActionName, sequences[2][0]);
            Assert.AreEqual(NpcUtilityAgent.RepairActionName, sequences[3][0]);
            Assert.AreEqual(NpcUtilityAgent.RepairActionName, sequences[4][0]);

            for (int i = 0; i < agents.Count; i++)
            {
                Assert.AreEqual(ExpectedRowIds[i], agents[i].PersonalityDef.Id, "权重来自 CFG 行而非代码");
            }

            for (int i = 0; i < agents.Count; i++)
            {
                for (int j = i + 1; j < agents.Count; j++)
                {
                    Assert.IsFalse(sequences[i].SequenceEqual(sequences[j]),
                        $"同状态同世界，个性 {ExpectedRowIds[i]} 与 {ExpectedRowIds[j]} 的行为序列必须两两不同（600 拍逐拍不等）");
                }
            }
        }

        [Test]
        public void SameConfigPersonality_IdenticalWorld_IdenticalSequence()
        {
            NpcPersonalityTable table = LoadPersonalityTable();
            NPCPersonalityDef row = table.FindRow("brave");
            var a = CreatePumpedAgent(row);
            var b = CreatePumpedAgent(row);

            for (int tick = 0; tick < 100; tick++)
            {
                Assert.AreEqual(a.Tick(0.1f), b.Tick(0.1f), $"第 {tick} 拍选择必须一致（同个性确定性回归）");
                Assert.AreEqual(a.Position, b.Position, "同个性同世界必须走出同一轨迹");
            }
        }

        // ---- GOAP 修理链：规划产出 + 执行顺序实证 ----

        [Test]
        public void RepairChain_PlannerProducesTwoSteps_AgentWalksToolboxThenDevice()
        {
            NpcUtilityAgent agent = CreatePumpedAgent(null); // balanced 中立
            var plans = new List<GoapPlan>();
            int repaired = 0;
            agent.ChainPlanned += plans.Add;
            agent.DeviceRepaired += _ => repaired++;

            string firstChoice = agent.Tick(0.1f);

            Assert.AreEqual(NpcUtilityAgent.RepairActionName, firstChoice, "装置损坏 + 工具箱在望 → 中立职责 Repair 登顶");
            Assert.IsNotNull(agent.ActiveChain, "首拍必须产出规划");
            Assert.AreEqual(2, agent.ActiveChain.StepCount, "单步不可行（无工具）→ 必须两步");
            Assert.AreEqual(NpcUtilityAgent.GoapTakeToolActionName, agent.ActiveChain.Steps[0].Name);
            Assert.AreEqual(NpcUtilityAgent.GoapRepairActionName, agent.ActiveChain.Steps[1].Name);
            Assert.AreEqual("TakeTool -> RepairDevice", plans[0].Format());

            bool hadTool = agent.HasTool;
            int toolboxTick = -1;
            int deviceTick = -1;
            for (int tick = 0; tick < 120 && repaired == 0; tick++)
            {
                agent.Tick(0.1f);
                hadTool |= agent.HasTool;
                if (toolboxTick < 0 && Vec2.Distance(agent.Position, new Vec2(-3f, 3f)) <= 0.6f)
                {
                    toolboxTick = tick;
                }

                if (deviceTick < 0 && Vec2.Distance(agent.Position, new Vec2(4f, -3f)) <= 0.6f)
                {
                    deviceTick = tick;
                }
            }

            Assert.AreEqual(1, repaired, "两步链执行完毕必须恰好修复一次");
            Assert.IsTrue(hadTool, "执行链中途必须真实拿到工具（规划原子 has_tool 落地）");
            Assert.GreaterOrEqual(toolboxTick, 0, "轨迹必须先到工具箱");
            Assert.Greater(deviceTick, toolboxTick, "先工具箱后装置 —— 两步链的空间顺序证据");
        }

        [Test]
        public void BrokenDeviceWithoutToolbox_NoRepairDesire()
        {
            // 无工具箱感知：前瞻链必然无解 → Repair 欲望必须为 0（饿死防护），
            // 满态 NPC（跟随 0.7 > 巡逻 0.3）改选 Follow 而非被 Repair 堵死。
            var agent = new NpcUtilityAgent(
                "npc-no-toolbox",
                Vec2.Zero,
                new Vec2(-6f, -4f),
                new[] { new Vec2(6f, -6f), new Vec2(6f, 6f) });
            agent.SetPlayerPosition(new Vec2(9f, 0f));
            agent.SetDeviceTarget(new Vec2(4f, -3f));
            agent.SetDeviceBroken(true);

            string chosen = agent.Tick(0.1f);

            Assert.AreNotEqual(NpcUtilityAgent.RepairActionName, chosen, "无工具来源不得选 Repair");
            Assert.AreEqual(NpcUtilityAgent.FollowActionName, chosen, "手算：Follow 0.7 是无工具箱时的赢家");
            Assert.IsNull(agent.ActiveChain, "未选 Repair 就不得规划");
        }

        // ---- helpers ----

        private static NpcUtilityAgent CreatePumpedAgent(NPCPersonalityDef personality)
        {
            var agent = new NpcUtilityAgent(
                personality != null ? "npc-" + personality.Id : "npc-neutral",
                Vec2.Zero,
                new Vec2(-6f, -4f),
                new[] { new Vec2(6f, -6f), new Vec2(6f, 6f), new Vec2(-6f, 6f), new Vec2(-6f, -6f) },
                null,
                null,
                personality);
            // 同一个世界：玩家远端（Follow 满坡度）、掉落物、工具箱、损坏装置。
            agent.SetPlayerPosition(new Vec2(9f, 0f));
            agent.SetDropTarget(new Vec2(3f, 0f));
            agent.SetToolboxPosition(new Vec2(-3f, 3f));
            agent.SetDeviceTarget(new Vec2(4f, -3f));
            agent.SetDeviceBroken(true);
            return agent;
        }
    }
}
