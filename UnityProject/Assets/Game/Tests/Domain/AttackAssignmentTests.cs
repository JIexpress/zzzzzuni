using System;
using System.Collections.Generic;
using Game.Domain.AI.Coordination;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// AttackAssignment tests (TASK-A-AI-02) — the falsifiable 分派不重叠
    /// core, all hand-computed:
    /// - 3 attackers vs 3 targets at staggered distances resolve to three
    ///   DISTINCT pairs (no two attackers share a target);
    /// - more attackers than targets → the leftovers get NO target (null,
    ///   never "also pile on");
    /// - 态度分派: at equal distance the angrier attacker wins the target;
    /// - determinism: same input → same plan (format string equal).
    /// </summary>
    public sealed class AttackAssignmentTests
    {
        private static List<AttackAssignment.AttackerCandidate> Attackers(params (string id, float x, float anger)[] specs)
        {
            var attackers = new List<AttackAssignment.AttackerCandidate>();
            foreach ((string id, float x, float anger) in specs)
            {
                attackers.Add(new AttackAssignment.AttackerCandidate(id, new Vec2(x, 0f), anger));
            }

            return attackers;
        }

        private static List<AttackAssignment.TargetCandidate> Targets(params (string id, float x)[] specs)
        {
            var targets = new List<AttackAssignment.TargetCandidate>();
            foreach ((string id, float x) in specs)
            {
                targets.Add(new AttackAssignment.TargetCandidate(id, new Vec2(x, 0f)));
            }

            return targets;
        }

        // ---- 核心证据：分派不重叠 ----

        [Test]
        public void ThreeAttackers_ThreeTargets_AllPairsDistinct()
        {
            // 手算：a0@0 → e1 距 1；a1@2 → e1 距 1（并列但 a0 先）、e2 距 1；a2@4 → e2 距 1（被占）、e3 距 1。
            // 贪心全局最近对：a0-e1、a1-e2、a2-e3 —— 三对互不重叠。
            var attackers = Attackers(("npc-a", 0f, 0f), ("npc-b", 2f, 0f), ("npc-c", 4f, 0f));
            var targets = Targets(("enemy-1", 1f), ("enemy-2", 3f), ("enemy-3", 5f));

            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets);

            Assert.AreEqual(3, plan.AssignedCount, "三人全部领到目标");
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-a"));
            Assert.AreEqual("enemy-2", plan.TargetOfId("npc-b"));
            Assert.AreEqual("enemy-3", plan.TargetOfId("npc-c"));
            CollectionAssert.AllItemsAreUnique(plan.TargetOf, "分派不重叠：目标列表无重复");
        }

        [Test]
        public void MoreAttackersThanTargets_LeftoversGetNoTarget()
        {
            var attackers = Attackers(("npc-a", 0f, 0f), ("npc-b", 1f, 0f), ("npc-c", 2f, 0f));
            var targets = Targets(("enemy-1", 1f));

            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets);

            Assert.AreEqual(1, plan.AssignedCount, "容量 1：只有最近的一人上");
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-b"), "b@1 距目标 0m 最近，胜出");
            Assert.IsNull(plan.TargetOfId("npc-a"), "轮空者显式无目标，不得挤向已占目标");
            Assert.IsNull(plan.TargetOfId("npc-c"));
        }

        [Test]
        public void CapacityTwo_AllowsTwoAttackersOnOneTarget()
        {
            var attackers = Attackers(("npc-a", 0f, 0f), ("npc-b", 1f, 0f));
            var targets = Targets(("enemy-1", 5f));

            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets, 2);

            Assert.AreEqual(2, plan.AssignedCount, "容量 2：两人可同标（显式包抄位）");
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-a"));
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-b"));
        }

        // ---- 核心证据：距离+态度 ----

        [Test]
        public void AngrierAttacker_WinsEquidistantTarget()
        {
            // 手算：a@0、b@4、目标 e@2 —— 原始距离都为 2。
            // 有效距离 = d / (1 + 1.5×anger)：a 怒 0 → 2；b 怒 1 → 0.8 → b 胜。
            var attackers = Attackers(("npc-calm", 0f, 0f), ("npc-angry", 4f, 1f));
            var targets = Targets(("enemy-1", 2f));

            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets);

            Assert.AreEqual(1, plan.AssignedCount);
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-angry"), "态度分派：怒者视距更近，抢到目标");
            Assert.IsNull(plan.TargetOfId("npc-calm"), "败者轮空");
        }

        [Test]
        public void NearerPair_WinsOverFartherPair()
        {
            var attackers = Attackers(("npc-far", 0f, 0.5f), ("npc-near", 3f, 0f));
            var targets = Targets(("enemy-1", 1f), ("enemy-2", 6f));
            // 手算：far(a0)：e1 距 1/1.75=0.571；e2 距 6/1.75=3.43。near(a1)：e1 距 2；e2 距 3。
            // 最小对 = (far, e1)=0.571 → far 上 e1；次小 = (near, e1)=2 被占 → (near, e2)=3。
            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets);

            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-far"), "更近的对先分派（愤怒缩短视距 0.571 < 2）");
            Assert.AreEqual("enemy-2", plan.TargetOfId("npc-near"));
        }

        // ---- 确定性 ----

        [Test]
        public void SameInput_SamePlan_Deterministic()
        {
            var attackers = Attackers(("npc-a", 0f, 0.2f), ("npc-b", 2f, 0.9f));
            var targets = Targets(("enemy-1", 1f), ("enemy-2", 4f));

            string first = AttackAssignment.Assign(attackers, targets).Format();
            string second = AttackAssignment.Assign(attackers, targets).Format();

            Assert.AreEqual(first, second, "同输入同分派（无 RNG、无字典序意外）");
            StringAssert.Contains("npc-a->", first);
            StringAssert.Contains("npc-b->", first);
        }

        [Test]
        public void AngerClampedIntoUnitRange()
        {
            var attackers = Attackers(("npc-hot", 0f, 5f), ("npc-b", 4f, 0f));
            var targets = Targets(("enemy-1", 2f));
            // 怒 5 被夹到 1：有效距离 = 2/2.5 = 0.8 < b 的 4 → hot 胜（与怒 1 等效）。
            AttackAssignment.Plan plan = AttackAssignment.Assign(attackers, targets);
            Assert.AreEqual("enemy-1", plan.TargetOfId("npc-hot"), "愤怒超出 0..1 被夹断（等效于怒 1）");
        }

        // ---- 防御性校验 ----

        [Test]
        public void Assign_RejectsDuplicateIds()
        {
            var attackers = Attackers(("npc-a", 0f, 0f), ("npc-a", 1f, 0f));
            var targets = Targets(("enemy-1", 5f));
            Assert.Throws<ArgumentException>(() => AttackAssignment.Assign(attackers, targets), "攻击者 id 重复");

            var one = Attackers(("npc-a", 0f, 0f));
            var same = Targets(("npc-a", 5f));
            Assert.Throws<ArgumentException>(() => AttackAssignment.Assign(one, same), "攻击者与目标 id 撞名同样拒绝");
        }

        [Test]
        public void Assign_RejectsCapacityBelowOne()
        {
            var attackers = Attackers(("npc-a", 0f, 0f));
            var targets = Targets(("enemy-1", 5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => AttackAssignment.Assign(attackers, targets, 0));
        }

        [Test]
        public void Assign_EmptyInputs_GiveEmptyPlan()
        {
            AttackAssignment.Plan plan = AttackAssignment.Assign(
                new List<AttackAssignment.AttackerCandidate>(),
                new List<AttackAssignment.TargetCandidate>());
            Assert.AreEqual(0, plan.AssignedCount);
            Assert.AreEqual(0, plan.AttackerIds.Count);
        }
    }
}
