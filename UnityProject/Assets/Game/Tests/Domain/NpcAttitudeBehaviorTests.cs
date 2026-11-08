using System;
using System.Collections.Generic;
using Game.Domain.AI.Memory;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Attitude → behaviour integration tests on <see cref="NpcUtilityAgent"/>
    /// (TASK-A-AI-01). The falsifiable core — 个性×态度=行为:
    /// - THE SAME AttackedBy episode produces DIFFERENT reactions: a fearful
    ///   (low bravery) NPC flees while a brave (high bravery) NPC
    ///   counterattacks — both from derived attitude profiles (hand-computed
    ///   modulation values in the comments);
    /// - the same holds with two CFG_NpcPersonality rows (config-driven link);
    /// - without memory the two new candidates score exactly 0 (bit-identical
    ///   legacy behaviour);
    /// - without new events the attitude recedes by the half-life and the
    ///   behaviour returns to the memory-less baseline (调制可逆);
    /// - fear without a perceived threat does NOT flee (stimulus gating);
    /// - trust boosts the Follow desire by the hand-computed factor.
    /// </summary>
    public sealed class NpcAttitudeBehaviorTests
    {
        private const float Epsilon = 1e-3f;

        private static readonly Vec2 PlayerFar = new Vec2(9f, 0f);
        private static readonly Vec2 ThreatNear = new Vec2(0f, 1f); // 距出生点 1m ≤ ThreatDesireInner(1.5) → proximity 1

        /// <summary>胆小个性（勇敢权重 0.5）与勇敢个性（2.0），其余权重中性。</summary>
        private static NPCPersonalityDef Def(string id, float bravery)
        {
            return new NPCPersonalityDef
            {
                Id = id,
                FatigueWeight = 1f,
                SocialWeight = 1f,
                CuriosityWeight = 1f,
                BraveryWeight = bravery,
            };
        }

        private static NpcUtilityAgent CreateAgent(string id, NPCPersonalityDef def)
        {
            var agent = new NpcUtilityAgent(
                id,
                Vec2.Zero,
                new Vec2(-6f, -4f),
                new[] { new Vec2(6f, -6f), new Vec2(6f, 6f) },
                null,
                null,
                def);
            agent.SetPlayerPosition(PlayerFar); // Follow 满坡度 0.7×social，避免干扰手算锚点
            agent.SetFatigue(0.5f);             // Rest = Ramp(0.5,0.35,0.9) ≈ 0.273
            return agent;
        }

        /// <summary>两名 NPC 在同一拍接收同一 AttackedBy 事件（同型、同效价、同时间域、同来源）。</summary>
        private static void SameAttack(NpcUtilityAgent agent)
        {
            agent.NoteAttackedBy(EntityRef.Player, -1f);
        }

        // ---- 核心证据：同一事件，恐惧 NPC 逃跑 vs 勇敢 NPC 反击 ----

        [Test]
        public void SameAttackedByEvent_FearfulNpcFlees_BraveNpcCounterattacks()
        {
            NpcUtilityAgent timid = CreateAgent("npc-timid", Def("timid", 0.5f));
            NpcUtilityAgent brave = CreateAgent("npc-brave", Def("brave", 2f));
            timid.SetThreatSource(ThreatNear);
            brave.SetThreatSource(ThreatNear);

            timid.Tick(0.1f);
            brave.Tick(0.1f);
            SameAttack(timid);
            SameAttack(brave);

            // 手算（半衰期前因子 1）：timid fear=0.6/0.5=1.2→钳 1.0，anger=0.8×0.5=0.4
            //   → Flee = 1×1×(1+2×1)=3.0 压过 Follow 0.7；Counter = 1×0.4×1.6×0.2=0.128。
            // brave fear=0.6/2=0.3，anger=0.8×2=1.6→钳 1.0
            //   → Counter = 1×1×(1+1.5)×(1−0.8×0.3)=2.5×0.76=1.9 压过 Follow 0.7；Flee = 1×0.3×1.6=0.48。
            string timidChoice = timid.Tick(0.1f);
            string braveChoice = brave.Tick(0.1f);

            Assert.AreEqual(NpcUtilityAgent.FleeActionName, timidChoice, "胆小 NPC：恐惧 1.0 → 逃跑增益 ×3 登顶");
            Assert.AreEqual(NpcUtilityAgent.CounterattackActionName, braveChoice, "勇敢 NPC：愤怒 1.0 → 反击 1.9 登顶（同事件不同反应）");

            Assert.AreEqual(1f, timid.Attitude.Fear, Epsilon, "1.2 → 钳制 1.0");
            Assert.AreEqual(0.4f, timid.Attitude.Anger, 0.01f, "0.4×半衰期因子(0.9966)");
            Assert.AreEqual(0.3f, brave.Attitude.Fear, 0.01f, "0.6/2.0 —— 勇敢者恐惧低（×半衰期因子）");
            Assert.AreEqual(1f, brave.Attitude.Anger, Epsilon, "1.6 → 钳制 1.0");

            // 后续拍：胆小一路逃跑、勇敢一路逼近反击——序列互斥（个性×态度持续可见）。
            for (int tick = 0; tick < 5; tick++)
            {
                Assert.AreNotEqual(NpcUtilityAgent.CounterattackActionName, timid.Tick(0.1f), "胆小 NPC 不反击");
                Assert.AreNotEqual(NpcUtilityAgent.FleeActionName, brave.Tick(0.1f), "勇敢 NPC 不逃跑");
            }
        }

        [Test]
        public void SameAttackedByEvent_SamePersonality_SameReaction()
        {
            NpcUtilityAgent a = CreateAgent("npc-a", Def("timid", 0.5f));
            NpcUtilityAgent b = CreateAgent("npc-b", Def("timid", 0.5f));
            a.SetThreatSource(ThreatNear);
            b.SetThreatSource(ThreatNear);
            a.Tick(0.1f);
            b.Tick(0.1f);
            SameAttack(a);
            SameAttack(b);

            Assert.AreEqual(NpcUtilityAgent.FleeActionName, a.Tick(0.1f), "锚点：恐惧 NPC 逃跑");
            Assert.AreEqual(a.CurrentAction, b.Tick(0.1f), "同个性同事件 → 同反应（确定性回归）");
        }

        // ---- CFG 行驱动的同源联动（不改个性表，态度系数由行派生）----

        private const string PersonalityTablePath = "Assets/Configs/CFG_NpcPersonality.asset";

        [Test]
        public void ConfigRows_BraveCounterattacks_LethargicFlees()
        {
            var table = AssetDatabase.LoadAssetAtPath<NpcPersonalityTable>(PersonalityTablePath);
            Assert.IsNotNull(table, $"CFG 资产缺失：{PersonalityTablePath}");
            NPCPersonalityDef braveRow = table.FindRow("brave");
            NPCPersonalityDef lethargicRow = table.FindRow("lethargic");
            Assert.IsNotNull(braveRow);
            Assert.IsNotNull(lethargicRow);

            NpcUtilityAgent brave = CreateAgent("npc-cfg-brave", braveRow);
            NpcUtilityAgent lethargic = CreateAgent("npc-cfg-lethargic", lethargicRow);
            brave.SetThreatSource(ThreatNear);
            lethargic.SetThreatSource(ThreatNear);
            brave.Tick(0.1f);
            lethargic.Tick(0.1f);
            SameAttack(brave);
            SameAttack(lethargic);

            // 手算：brave 行(勇敢 2.0) → Counter 1.9；lethargic 行(勇敢 0.5) → fear 1.2→1.0 → Flee 3.0。
            Assert.AreEqual(NpcUtilityAgent.CounterattackActionName, brave.Tick(0.1f),
                "CFG brave 行：愤怒系数 0.8×2.0 → 反击登顶");
            Assert.AreEqual(NpcUtilityAgent.FleeActionName, lethargic.Tick(0.1f),
                "CFG lethargic 行：恐惧系数 0.6/0.5 → 逃跑登顶（同一事件，配置驱动的不同反应）");
        }

        // ---- 兼容性：无记忆 → 新候选恰为 0，永不登顶 ----

        [Test]
        public void WithoutMemory_NewAttitudeActionsScoreZeroAndNeverChosen()
        {
            NpcUtilityAgent agent = CreateAgent("npc-plain", Def("balanced", 1f));
            agent.SetThreatSource(ThreatNear); // 有威胁、无记忆 → 恐惧/愤怒 0

            for (int tick = 0; tick < 20; tick++)
            {
                string chosen = agent.Tick(0.1f);
                Assert.AreNotEqual(NpcUtilityAgent.FleeActionName, chosen, "无记忆绝不逃跑");
                Assert.AreNotEqual(NpcUtilityAgent.CounterattackActionName, chosen, "无记忆绝不反击");
            }

            var ranking = new List<UtilityBrain.ScoredAction>(agent.Brain.LastRanking);
            foreach (UtilityBrain.ScoredAction scored in ranking)
            {
                if (scored.Name == NpcUtilityAgent.FleeActionName || scored.Name == NpcUtilityAgent.CounterattackActionName)
                {
                    Assert.AreEqual(0f, scored.Value, Epsilon, "无记忆时态度调制候选的分必须恰为 0（逐位兼容）");
                }
            }

            Assert.AreEqual(NpcAttitude.Neutral, agent.Attitude, "无记忆 → 无态度");
        }

        // ---- 调制可逆：无新事件 → 态度随半衰期回落 → 行为恢复基线 ----

        [Test]
        public void NoNewEvents_AttitudeRecedes_BehaviourReturnsToBaseline()
        {
            NpcUtilityAgent fresh = CreateAgent("npc-fresh", Def("timid", 0.5f));
            NpcUtilityAgent aged = CreateAgent("npc-aged", Def("timid", 0.5f));
            NpcUtilityAgent baseline = CreateAgent("npc-baseline", Def("timid", 0.5f));
            foreach (NpcUtilityAgent agent in new[] { fresh, aged, baseline })
            {
                agent.SetThreatSource(ThreatNear); // 威胁感知一致——唯一变量是记忆的新旧/有无
                agent.Tick(0.1f);
            }

            // 新伤：fresh 注入的就是当前拍事件（时效 ≈ 0）。
            fresh.NoteAttackedBy(EntityRef.Player, -1f);
            // 旧伤：aged 写入同一条事件、时间戳回拨 40s（2 个半衰期）——
            // 等价于"很久前被攻击过、此后再无新事件"（时钟域同源，衰减纯函数接管）。
            aged.Memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, aged.Clock - 40f, EntityRef.Player));

            Assert.AreEqual(NpcUtilityAgent.FleeActionName, fresh.Tick(0.1f), "新伤：恐惧 1.0 → 逃跑增益 ×3 登顶");
            Assert.AreEqual(NpcUtilityAgent.FollowActionName, aged.Tick(0.1f),
                "旧伤：恐惧 1.2×0.5^(40.1/20)≈0.30 → 逃跑 0.48 < 跟随 0.7 → 恢复基线选择");
            Assert.AreEqual(NpcUtilityAgent.FollowActionName, baseline.Tick(0.1f), "无记忆基线：跟随");
            Assert.AreEqual(baseline.CurrentAction, aged.CurrentAction, "调制可逆：旧伤者与无记忆基线做出相同选择");

            Assert.AreEqual(1f, fresh.Attitude.Fear, Epsilon, "新伤恐惧钳制 1.0");
            Assert.That(aged.Attitude.Fear, Is.InRange(0.25f, 0.35f), "手算：1.2×0.5^(40.1/20) ≈ 0.30");
            Assert.AreEqual(0f, baseline.Attitude.Fear, Epsilon);
            Assert.AreEqual(1, aged.Memory.Entries.Count, "衰减可逆 ≠ 删除记忆：条目仍在滚动窗内");
        }

        // ---- 刺激门控：有恐惧、无威胁感知 → 不逃跑 ----

        [Test]
        public void FearWithoutThreatPerception_DoesNotFlee()
        {
            NpcUtilityAgent agent = CreateAgent("npc-scared", Def("timid", 0.5f));
            agent.Tick(0.1f);
            agent.NoteAttackedBy(EntityRef.Player, -1f);
            agent.Tick(0.1f);
            Assert.Greater(agent.Attitude.Fear, 0.9f, "前提：恐惧已被注入");
            Assert.IsFalse(agent.WorldState.HasThreat(), "前提：威胁源未泵入");

            Assert.AreNotEqual(NpcUtilityAgent.FleeActionName, agent.Tick(0.1f), "情绪需要刺激：无威胁感知不逃跑");
        }

        // ---- 信任增益跟随（手算）----

        [Test]
        public void Gifts_BoostFollowDesire_HandComputed()
        {
            NpcUtilityAgent friendly = CreateAgent("npc-friendly", Def("balanced", 1f));
            NpcUtilityAgent plain = CreateAgent("npc-plain", Def("balanced", 1f));

            friendly.Tick(0.1f);
            friendly.NoteGiftedFrom(EntityRef.Player, 1f);
            friendly.NoteGiftedFrom(EntityRef.Player, 1f); // 0.5+0.5 = trust 1.0
            friendly.Tick(0.1f);
            plain.Tick(0.1f);
            plain.Tick(0.1f);

            float friendlyFollow = RankingValueOf(friendly, NpcUtilityAgent.FollowActionName);
            float plainFollow = RankingValueOf(plain, NpcUtilityAgent.FollowActionName);

            Assert.AreEqual(1.05f, friendlyFollow, 0.01f, "手算：0.7×(1+0.5×1.0)=1.05（两次赠礼 → 信任满）");
            Assert.AreEqual(0.7f, plainFollow, Epsilon, "无记忆 → ×1 逐位兼容");
            Assert.That(friendly.Attitude.Trust, Is.InRange(0.95f, 1f), "两次赠礼信任 0.5×2（衰减因子内）");
        }

        private static float RankingValueOf(NpcUtilityAgent agent, string actionName)
        {
            var ranking = new List<UtilityBrain.ScoredAction>(agent.Brain.LastRanking);
            foreach (UtilityBrain.ScoredAction scored in ranking)
            {
                if (scored.Name == actionName)
                {
                    return scored.Value;
                }
            }

            Assert.Fail($"排名中缺少 {actionName}");
            return float.NaN;
        }
    }
}
