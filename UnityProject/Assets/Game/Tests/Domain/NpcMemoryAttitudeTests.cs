using System;
using System.Collections.Generic;
using Game.Domain.AI.Memory;
using Game.Domain.AI.Utility;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Memory store + attitude aggregation pure-math tests (TASK-A-AI-01).
    /// The falsifiable core, all hand-computed:
    /// - half-life decay: 1 → 0.5 → 0.25 (fresh / 1× / 2× half-life);
    /// - capacity 32 rolling: the 33rd add evicts the OLDEST entry;
    /// - aggregation: per-type weighted sum + per-dimension gains (手算值);
    /// - per-NPC profile from the CFG personality row: brave ⇒ LOW fear gain,
    ///   HIGH anger gain (个性×态度同源联动, no table change);
    /// - modulation formulas: flee amplified by fear, attack suppressed by
    ///   fear / scaled by anger, follow boosted by trust (identity at 0);
    /// - trigger-source feed: EnemyAttackRequested in sight ⇒ SawDeath memory;
    ///   ItemPickedUp in sight ⇒ PlayerNear trust memory; out of sight ⇒ none.
    /// </summary>
    public sealed class NpcMemoryAttitudeTests
    {
        private const float Epsilon = 1e-4f;

        // ---- 半衰期纯函数 ----

        [Test]
        public void DecayWeight_HalfLife_HandComputed()
        {
            Assert.AreEqual(1f, NpcAttitudeMath.DecayWeight(0f, 10f), Epsilon, "age 0 → 权重 1");
            Assert.AreEqual(0.5f, NpcAttitudeMath.DecayWeight(10f, 10f), Epsilon, "age = 半衰期 → 恰好 0.5");
            Assert.AreEqual(0.25f, NpcAttitudeMath.DecayWeight(20f, 10f), Epsilon, "age = 2×半衰期 → 0.25");
            Assert.AreEqual(0.125f, NpcAttitudeMath.DecayWeight(30f, 10f), Epsilon, "age = 3×半衰期 → 0.125");
        }

        [Test]
        public void DecayWeight_MonotoneInAge_AndFutureClamped()
        {
            float previous = 2f;
            for (int age = 0; age <= 80; age += 5)
            {
                float weight = NpcAttitudeMath.DecayWeight(age, 20f);
                Assert.Less(weight, previous, $"age {age} 的衰减权重必须严格递减");
                previous = weight;
            }

            Assert.AreEqual(1f, NpcAttitudeMath.DecayWeight(-5f, 10f), Epsilon, "未来时间戳按 age 0（满权重）处理");
        }

        [Test]
        public void DecayWeight_NonPositiveHalfLife_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NpcAttitudeMath.DecayWeight(1f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => NpcAttitudeMath.DecayWeight(1f, -1f));
        }

        // ---- 记忆条目校验 ----

        [Test]
        public void MemoryEntry_ValenceOutsideDomain_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MemoryEntry(MemoryEventType.AttackedBy, 1.5f, 0f, EntityRef.Player));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MemoryEntry(MemoryEventType.AttackedBy, float.NaN, 0f, EntityRef.Player));
            Assert.DoesNotThrow(
                () => new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));
        }

        [Test]
        public void EntityRef_EmptyId_Throws_AndValueEquality()
        {
            Assert.Throws<ArgumentException>(() => new EntityRef(""));
            Assert.Throws<ArgumentException>(() => new EntityRef(null));

            Assert.AreEqual(new EntityRef("player"), EntityRef.Player);
            Assert.AreNotEqual(EntityRef.Player, new EntityRef("enemy-1"));
            Assert.AreEqual("actor-0", EntityRef.FromActor(ActorId.Player).Id);
        }

        // ---- 容量 32 滚动丢弃最旧 ----

        [Test]
        public void Capacity32_Adding33rd_EvictsOldest()
        {
            var memory = new NpcMemory();
            Assert.AreEqual(32, memory.Capacity, "简报口径：容量上限 32");

            for (int i = 0; i < 32; i++)
            {
                memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, i, EntityRef.Player));
                Assert.IsFalse(memory.Entries.Count > 32);
            }

            Assert.AreEqual(32, memory.Entries.Count);
            Assert.AreEqual(0f, memory.Entries[0].Timestamp, Epsilon);

            bool evicted = memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 32f, EntityRef.Player));

            Assert.IsTrue(evicted, "第 33 条必须挤掉最旧一条");
            Assert.AreEqual(32, memory.Entries.Count, "滚动后仍为 32 条");
            Assert.AreEqual(1f, memory.Entries[0].Timestamp, Epsilon, "最旧（t=0）必须被丢弃");
            Assert.AreEqual(32f, memory.Entries[31].Timestamp, Epsilon, "最新条目在队尾");
        }

        [Test]
        public void NonDefaultCapacity_RollsToo()
        {
            var memory = new NpcMemory(3);
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 0f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 1f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 2f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 3f, EntityRef.Player));

            Assert.AreEqual(3, memory.Entries.Count);
            Assert.AreEqual(1f, memory.Entries[0].Timestamp, Epsilon);
        }

        // ---- Aggregate：按类型过滤 + 时间衰减加权和（纯函数，确定性） ----

        [Test]
        public void Aggregate_TypeFiltered_TimeDecayWeighted_HandComputed()
        {
            var memory = new NpcMemory();
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 10f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 0f, EntityRef.Player));

            // now=10, halfLife=10：t=0 的权重 0.5，t=10 的权重 1 → 攻击合计 -(0.5+1) = -1.5。
            float attacks = memory.Aggregate(MemoryEventType.AttackedBy, 10f, 10f);
            Assert.AreEqual(-1.5f, attacks, Epsilon, "时间衰减加权和：近期权重大（手算 -1.5）");

            float gifts = memory.Aggregate(MemoryEventType.GiftedFrom, 10f, 10f);
            Assert.AreEqual(0.5f, gifts, Epsilon, "礼物条目按类型过滤后只剩 t=0 那条（0.5×+1）");

            float seen = memory.Aggregate(MemoryEventType.SawDeath, 10f, 10f);
            Assert.AreEqual(0f, seen, Epsilon, "无该类型条目 → 0");

            // 确定性：同记忆 + 同 now → 同结果。
            Assert.AreEqual(attacks, memory.Aggregate(MemoryEventType.AttackedBy, 10f, 10f), 0f);
        }

        // ---- 态度聚合（记忆 × 系数 → Fear/Anger/Trust）----

        [Test]
        public void Evaluate_MixedEpisodes_HandComputed()
        {
            var memory = new NpcMemory();
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 20f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.GiftedFrom, 1f, 0f, EntityRef.Player));

            var profile = NpcAttitudeProfile.Neutral();
            // now = 20 = halfLife：攻击 fresh（decay 1）→ fear 0.6 / anger 0.8；礼物 age 20（decay 0.5）→ trust 0.25。
            NpcAttitude attitude = NpcAttitudeMath.Evaluate(memory, profile, 20f);

            Assert.AreEqual(0.6f, attitude.Fear, Epsilon, "手算：1×1×0.6");
            Assert.AreEqual(0.8f, attitude.Anger, Epsilon, "手算：1×1×0.8");
            Assert.AreEqual(0.25f, attitude.Trust, Epsilon, "手算：0.5×1×0.5（半衰期衰减）");
        }

        [Test]
        public void Evaluate_OldFearFades_NewFearDominates()
        {
            var memory = new NpcMemory();
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));

            var profile = NpcAttitudeProfile.Neutral();
            float fresh = NpcAttitudeMath.Evaluate(memory, profile, 0f).Fear;
            float aged = NpcAttitudeMath.Evaluate(memory, profile, 40f).Fear;

            Assert.AreEqual(0.6f, fresh, Epsilon);
            Assert.AreEqual(0.15f, aged, Epsilon, "2 个半衰期后恐惧衰减到 1/4（旧记忆 ≠ 新记忆——简报可证伪用例）");
            Assert.Less(aged, fresh);
        }

        [Test]
        public void Evaluate_ClampsAtOne()
        {
            var memory = new NpcMemory();
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));
            memory.Add(new MemoryEntry(MemoryEventType.AttackedBy, -1f, 0f, EntityRef.Player));

            NpcAttitude attitude = NpcAttitudeMath.Evaluate(memory, NpcAttitudeProfile.Neutral(), 0f);

            Assert.AreEqual(1f, attitude.Fear, Epsilon, "0.6×3 = 1.8 → 钳制 1.0");
            Assert.AreEqual(1f, attitude.Anger, Epsilon, "0.8×3 = 2.4 → 钳制 1.0");
        }

        [Test]
        public void Evaluate_EmptyMemory_NeutralAttitude()
        {
            NpcAttitude attitude = NpcAttitudeMath.Evaluate(new NpcMemory(), NpcAttitudeProfile.Neutral(), 100f);
            Assert.AreEqual(NpcAttitude.Neutral, attitude, "无记忆 → 无态度（行为基线）");
        }

        // ---- 个性×态度同源联动：勇敢 ⇒ 低恐惧系数 / 高愤怒系数 ----

        [Test]
        public void Profile_FromPersonality_BraveHasLowFearGain_HighAngerGain()
        {
            var brave = new NPCPersonalityDef { Id = "brave", BraveryWeight = 2f, SocialWeight = 1f };
            var timid = new NPCPersonalityDef { Id = "timid", BraveryWeight = 0.5f, SocialWeight = 1f };

            NpcAttitudeProfile braveProfile = NpcAttitudeProfile.ForPersonality(brave);
            NpcAttitudeProfile timidProfile = NpcAttitudeProfile.ForPersonality(timid);

            Assert.AreEqual(0.3f, braveProfile.FearGain(MemoryEventType.AttackedBy), Epsilon, "0.6/2.0 —— 勇敢恐惧系数低");
            Assert.AreEqual(1.2f, timidProfile.FearGain(MemoryEventType.AttackedBy), Epsilon, "0.6/0.5 —— 胆小恐惧系数高");
            Assert.AreEqual(1.6f, braveProfile.AngerGain(MemoryEventType.AttackedBy), Epsilon, "0.8×2.0 —— 勇敢愤怒系数高");
            Assert.AreEqual(0.4f, timidProfile.AngerGain(MemoryEventType.AttackedBy), Epsilon, "0.8×0.5");

            Assert.Less(braveProfile.FearGain(MemoryEventType.AttackedBy), timidProfile.FearGain(MemoryEventType.AttackedBy),
                "同源联动：勇敢个性的恐惧系数必须低于胆小个性（CFG 行派生，不改表）");
            Assert.Greater(braveProfile.AngerGain(MemoryEventType.AttackedBy), timidProfile.AngerGain(MemoryEventType.AttackedBy));
        }

        [Test]
        public void Profile_NullPersonality_NeutralGains()
        {
            NpcAttitudeProfile profile = NpcAttitudeProfile.ForPersonality(null);
            Assert.AreEqual(NpcAttitudeProfile.BaseAttackedByFear, profile.FearGain(MemoryEventType.AttackedBy), Epsilon);
            Assert.AreEqual(NpcAttitudeProfile.BaseAttackedByAnger, profile.AngerGain(MemoryEventType.AttackedBy), Epsilon);
            Assert.AreEqual(NpcAttitudeProfile.BaseSawDeathFear, profile.FearGain(MemoryEventType.SawDeath), Epsilon);
            Assert.AreEqual(NpcAttitudeProfile.BaseGiftedFromTrust, profile.TrustGain(MemoryEventType.GiftedFrom), Epsilon);
            Assert.AreEqual(NpcAttitudeProfile.BasePlayerNearTrust, profile.TrustGain(MemoryEventType.PlayerNear), Epsilon);
            Assert.AreEqual(NpcAttitudeProfile.DefaultHalfLifeSeconds, profile.HalfLifeSeconds, Epsilon);
        }

        [Test]
        public void Profile_GainsNeverLeakAcrossDimensions()
        {
            NpcAttitudeProfile profile = NpcAttitudeProfile.Neutral();
            Assert.AreEqual(0f, profile.FearGain(MemoryEventType.GiftedFrom), Epsilon, "礼物绝不滋生恐惧");
            Assert.AreEqual(0f, profile.AngerGain(MemoryEventType.GiftedFrom), Epsilon);
            Assert.AreEqual(0f, profile.TrustGain(MemoryEventType.AttackedBy), Epsilon, "攻击绝不滋生信任");
        }

        // ---- 调制公式（恐惧→逃跑增益、攻击抑制；愤怒→反击增益；信任→跟随增益）----

        [Test]
        public void FleeDesire_FearGatesAndAmplifies_HandComputed()
        {
            Assert.AreEqual(0f, NpcAttitudeMath.FleeDesire(0f, 1f), Epsilon, "无恐惧 → 不逃（基线可逆的数学根据）");
            Assert.AreEqual(3f, NpcAttitudeMath.FleeDesire(1f, 1f), Epsilon, "fear 1 → ×3（1+2×1）");
            Assert.AreEqual(0.5f, NpcAttitudeMath.FleeDesire(0.5f, 0.5f), Epsilon, "0.5×0.5×(1+1) = 0.5");
        }

        [Test]
        public void CounterDesire_AngerGates_FearSuppresses_HandComputed()
        {
            Assert.AreEqual(0f, NpcAttitudeMath.CounterDesire(0f, 0f, 1f), Epsilon, "无愤怒 → 不反击");
            Assert.AreEqual(2.5f, NpcAttitudeMath.CounterDesire(1f, 0f, 1f), Epsilon, "anger 1, fear 0 → ×2.5");
            Assert.AreEqual(0.5f, NpcAttitudeMath.CounterDesire(1f, 1f, 1f), Epsilon, "anger 1, fear 1 → 2.5×0.2（恐惧抑制攻击）");
            Assert.AreEqual(0.9152f, NpcAttitudeMath.CounterDesire(0.8f, 0.6f, 1f), Epsilon, "0.8×2.2×0.52 = 0.9152");
            Assert.AreEqual(0f, NpcAttitudeMath.CounterDesire(1f, 1f, 0f), Epsilon, "无威胁接近度 → 0");
        }

        [Test]
        public void FollowTrustGain_IdentityAtZeroTrust()
        {
            Assert.AreEqual(1f, NpcAttitudeMath.FollowTrustGain(0f), Epsilon, "trust 0 → ×1（旧行为逐位兼容）");
            Assert.AreEqual(1.5f, NpcAttitudeMath.FollowTrustGain(1f), Epsilon, "trust 1 → ×1.5");
        }

        // ---- 触发源挂接：现有事件 → NPC 记忆 ----

        [Test]
        public void Feed_EnemyAttackInSight_WritesSawDeathMemory()
        {
            NpcUtilityAgent agent = CreateAgentAtOrigin();
            var feed = new NpcMemoryFeed(agent, 12f);
            var swing = new global::Game.Domain.AI.EnemyAttackRequested("enemy-1", new Vec2(3f, 0f), new Vec2(5f, 0f));

            bool perceived = feed.Observe(swing);

            Assert.IsTrue(perceived, "视野内的敌挥击必须被感知");
            Assert.AreEqual(1, agent.Memory.Entries.Count);
            Assert.AreEqual(MemoryEventType.SawDeath, agent.Memory.Entries[0].Type);
            Assert.AreEqual("enemy-1", agent.Memory.Entries[0].Source.Id);
            Assert.AreEqual(-0.8f, agent.Memory.Entries[0].Valence, Epsilon);

            agent.Tick(0.1f);
            // 手算：0.8 效价 × 0.5 SawDeath 恐惧系数 = 0.4；时间戳 0、now 0.1 → 再乘衰减 0.5^(0.1/20) ≈ 0.9966。
            Assert.AreEqual(0.4f, agent.Attitude.Fear, 0.01f, "SawDeath 恐惧注入（半衰期因子内）");
        }

        [Test]
        public void Feed_EnemyAttackOutOfSight_NoMemory()
        {
            NpcUtilityAgent agent = CreateAgentAtOrigin();
            var feed = new NpcMemoryFeed(agent, 12f);
            var swing = new global::Game.Domain.AI.EnemyAttackRequested("enemy-1", new Vec2(13f, 0f), new Vec2(15f, 0f));

            Assert.IsFalse(feed.Observe(swing), "视野外不感知");
            Assert.AreEqual(0, agent.Memory.Entries.Count);
        }

        [Test]
        public void Feed_PlayerPickupInSight_WritesTrustMicroMemory()
        {
            NpcUtilityAgent agent = CreateAgentAtOrigin();
            var feed = new NpcMemoryFeed(agent, 12f);
            var pickup = new ItemPickedUp(ActorId.Player, "coin", 1, 1, new Vec2(2f, 2f));

            bool perceived = feed.Observe(pickup);

            Assert.IsTrue(perceived, "视野内的玩家拾取必须被感知");
            Assert.AreEqual(MemoryEventType.PlayerNear, agent.Memory.Entries[0].Type);
            Assert.AreEqual("actor-0", agent.Memory.Entries[0].Source.Id);

            agent.Tick(0.1f);
            Assert.AreEqual(0.1f, agent.Attitude.Trust, 0.01f, "信任微增：0.1 的 PlayerNear 系数（同源 social=1，半衰期因子内）");
        }

        [Test]
        public void Feed_PickupWithoutActorOrSight_Ignored()
        {
            NpcUtilityAgent agent = CreateAgentAtOrigin();
            var feed = new NpcMemoryFeed(agent, 12f);

            var farPickup = new ItemPickedUp(ActorId.Player, "coin", 1, 1, new Vec2(50f, 0f));
            var nonePickup = new ItemPickedUp(ActorId.None, "coin", 1, 1, new Vec2(0f, 0f));

            Assert.IsFalse(feed.Observe(farPickup));
            Assert.IsFalse(feed.Observe(nonePickup), "无主拾取不归因");
            Assert.AreEqual(0, agent.Memory.Entries.Count);
        }

        [Test]
        public void Feed_InvalidSightRadius_Throws()
        {
            NpcUtilityAgent agent = CreateAgentAtOrigin();
            Assert.Throws<ArgumentOutOfRangeException>(() => new NpcMemoryFeed(agent, 0f));
            Assert.Throws<ArgumentNullException>(() => new NpcMemoryFeed(null, 12f));
        }

        // ---- helpers ----

        private static NpcUtilityAgent CreateAgentAtOrigin()
        {
            return new NpcUtilityAgent(
                "npc-memory",
                Vec2.Zero,
                new Vec2(-6f, -4f),
                new[] { new Vec2(6f, -6f), new Vec2(6f, 6f) });
        }
    }
}
