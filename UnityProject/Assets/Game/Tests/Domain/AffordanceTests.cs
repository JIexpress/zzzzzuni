// AffordanceTests.cs —— TASK-A-AI-03：可供性扫描的纯逻辑证据（V1-V6）。
// TASK-A-AI-05 增补 R 系预约态用例：Reserve 不占容量、FindBest 跳过他人预约（agent-aware）、
// 自己的预约不算占位、Release 双清（占用+预约）。
// 这一层的可伪证核心：容量是硬约束、重复 Claim 幂等、FindBest 只返回
// 「可用且未满员的最近者」、扫描预算不抛只计数、动作打分 = 需求权重 × 距离衰减。
// 全部手算锚点，无时钟、无随机、无 UnityEngine 生命周期。

using System;
using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using UnityEngine;

namespace Game.Domain.Tests
{
    /// <summary>可供性注册表 / 容量 / 最近可用 / 预算 / 需求权重 / 接入效用。</summary>
    public sealed class AffordanceTests
    {
        private static Affordance Bed(string id, float x, float z = 0f, int capacity = 1)
        {
            return new Affordance(id, AffordanceKind.Rest, new Vector3(x, 0f, z), 0.5f, capacity);
        }

        private static NPCWorldState State(float fatigue, float trust = 0f, bool deviceBroken = false)
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, fatigue);
            state.Set(NPCWorldState.Keys.Trust, trust);
            state.Set(NPCWorldState.Keys.DeviceBroken, deviceBroken);
            return state;
        }

        // ---- V1 注册 / 注销 -------------------------------------------------------

        [Test]
        public void RegisterAndUnregister_CountTracksEntries_RepeatUnregisterDoesNotThrow()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed-a", 2f);

            registry.Register(bed);
            Assert.AreEqual(1, registry.Count);

            registry.Register(Bed("bed-b", 4f));
            Assert.AreEqual(2, registry.Count);

            Assert.IsTrue(registry.Unregister(bed), "首次注销必须报告移除");
            Assert.AreEqual(1, registry.Count);

            Assert.IsFalse(registry.Unregister(bed), "重复注销返回 false（不抛）");
            Assert.IsFalse(registry.Unregister(null), "null 注销返回 false（不抛）");
            Assert.AreEqual(1, registry.Count);
        }

        [Test]
        public void Register_SameInstanceTwice_Throws()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed-a", 2f);
            registry.Register(bed);

            Assert.Throws<ArgumentException>(() => registry.Register(bed));
            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
        }

        // ---- V2 容量 -------------------------------------------------------------

        [Test]
        public void CapacityOne_ClaimFills_ReleaseRestores_RepeatClaimIsIdempotent()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed-a", 1f, capacity: 1);
            registry.Register(bed);

            Assert.IsTrue(registry.Claim(bed, "npc-a"));
            Assert.AreEqual(1, bed.Occupants);
            Assert.IsFalse(bed.IsAvailable, "容量 1 被占后必须不可用");

            Assert.IsFalse(registry.Claim(bed, "npc-b"), "满员时第二个代理 Claim 必须失败");
            Assert.AreEqual(1, bed.Occupants, "失败 Claim 不得增加占用");

            Assert.IsTrue(registry.Claim(bed, "npc-a"), "同一代理重复 Claim 幂等（返回 true）");
            Assert.AreEqual(1, bed.Occupants, "幂等 Claim 不得重复占位");

            Assert.IsTrue(registry.Release(bed, "npc-a"));
            Assert.IsTrue(bed.IsAvailable, "释放后必须恢复可用");
            Assert.IsFalse(registry.Release(bed, "npc-a"), "重复 Release 返回 false（不抛）");
        }

        [Test]
        public void CapacityTwo_SecondAgentFits_ThirdIsRejected()
        {
            var registry = new AffordanceRegistry();
            Affordance table = new Affordance("table", AffordanceKind.Social, Vector3.zero, 0.6f, 2);
            registry.Register(table);

            Assert.IsTrue(registry.Claim(table, "npc-a"));
            Assert.IsTrue(table.IsAvailable, "2 容量只占 1 时仍可用");
            Assert.IsTrue(registry.Claim(table, "npc-b"));
            Assert.IsFalse(table.IsAvailable);
            Assert.IsFalse(registry.Claim(table, "npc-c"));
            Assert.AreEqual(2, table.Occupants);
        }

        // ---- V3 最近可用 ---------------------------------------------------------

        [Test]
        public void FindBest_ReturnsNearestAvailable_OfTheRequestedKind()
        {
            var registry = new AffordanceRegistry();
            Affordance near = Bed("near", 2f);
            Affordance far = Bed("far", 9f);
            Affordance other = new Affordance("well", AffordanceKind.Eat, new Vector3(0.5f, 0f, 0f), 0.5f, 1);
            registry.Register(far);
            registry.Register(other);
            registry.Register(near); // 注册顺序故意打乱：最近者必须按距离赢，不靠顺序

            Affordance best = registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f);

            Assert.AreSame(near, best, "必须返回更近的同类可供性");
        }

        [Test]
        public void FindBest_SkipsFullNearest_ReturnsNextNearest()
        {
            var registry = new AffordanceRegistry();
            Affordance near = Bed("near", 2f);
            Affordance far = Bed("far", 6f);
            registry.Register(near);
            registry.Register(far);

            Assert.IsTrue(registry.Claim(near, "npc-x"));
            Assert.IsFalse(near.IsAvailable);

            Assert.AreSame(far, registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f),
                "最近者满员时必须退到次近者（V7 变异靶点）");

            Assert.IsTrue(registry.Claim(far, "npc-y"));
            Assert.IsNull(registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f),
                "全部满员时返回 null");
        }

        [Test]
        public void FindBest_RespectsMaxDistance_OnTheXZPlaneOnly()
        {
            var registry = new AffordanceRegistry();
            Affordance high = new Affordance("high-bed", AffordanceKind.Rest, new Vector3(0f, 50f, 3f), 0.5f, 1);
            registry.Register(high);

            Affordance best = registry.FindBest(Vector3.zero, AffordanceKind.Rest, 5f);

            Assert.AreSame(high, best, "只看 XZ：Y=50 不得让 3m 外的床变远");
            Assert.IsNull(registry.FindBest(Vector3.zero, AffordanceKind.Rest, 2f), "超出 maxDistance 返回 null");
        }

        // ---- V5 扫描预算 ---------------------------------------------------------

        [Test]
        public void FindBest_ScanBudgetExceeded_CountsAndStillReturnsBestFound()
        {
            var registry = new AffordanceRegistry { ScanBudget = 10 };
            Affordance nearest = Bed("nearest", 1f); // 索引 5，必须被扫描到
            for (int i = 0; i < 100; i++)
            {
                registry.Register(i == 5 ? nearest : Bed("bed-" + i, 10f + i));
            }

            Affordance best = null;
            Assert.DoesNotThrow(() => best = registry.FindBest(Vector3.zero, AffordanceKind.Rest, 200f),
                "超预算不得抛异常");

            Assert.AreSame(nearest, best, "预算内仍必须返回扫到的最优者");
            Assert.Greater(registry.BudgetExceededCount, 0, "超预算必须计数");
        }

        [Test]
        public void DefaultScanBudget_CoversSmallRegistries_WithoutCounting()
        {
            var registry = new AffordanceRegistry();
            Assert.AreEqual(64, registry.ScanBudget, "默认预算 64（为 20+ NPC 预留）");

            for (int i = 0; i < 10; i++)
            {
                registry.Register(Bed("bed-" + i, i + 1));
            }

            Assert.IsNotNull(registry.FindBest(Vector3.zero, AffordanceKind.Rest, 100f));
            Assert.AreEqual(0, registry.BudgetExceededCount, "未超预算不得计数");
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.ScanBudget = 0);
        }

        // ---- V4 需求权重 ---------------------------------------------------------

        [Test]
        public void RestScore_ScalesWithFatigue_AndIsZeroWithoutAffordance()
        {
            var registry = new AffordanceRegistry();
            registry.Register(Bed("bed", 5f));
            AffordanceAction rest = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Rest, "npc-a", () => Vector3.zero, 10f);

            float tired = rest.Action.Score(State(0.9f));
            float fresh = rest.Action.Score(State(0.2f));

            Assert.Greater(tired, fresh, "疲劳高时 Rest 分必须更高");
            Assert.AreEqual(0.45f, tired, 1e-4f, "手算：0.9 疲劳 × (1 − 5/10) = 0.45");
            Assert.AreEqual(0.1f, fresh, 1e-4f, "手算：0.2 × 0.5 = 0.1");
        }

        [Test]
        public void Score_NoAvailableAffordance_IsZero_AndScoreDoesNotClaim()
        {
            var registry = new AffordanceRegistry();
            AffordanceAction social = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Social, "npc-a", () => Vector3.zero);
            Assert.AreEqual(0f, social.Action.Score(State(1f, trust: 1f)),
                "无可用可供性时 Social 分恰为 0");

            // 满员与「只有别的种类」都必须落到 0。
            Affordance table = new Affordance("table", AffordanceKind.Social, new Vector3(1f, 0f, 0f), 0.5f, 1);
            registry.Register(table);
            Assert.Greater(social.Action.Score(State(1f, trust: 1f)), 0f);

            Assert.IsTrue(registry.Claim(table, "npc-x"));
            Assert.AreEqual(0f, social.Action.Score(State(1f, trust: 1f)), "满员后打分必须归 0");
            Assert.AreEqual(1, table.Occupants, "Score 必须纯读：不得顺手 Claim");
        }

        [Test]
        public void WorkNeed_FollowsDeviceBroken_SocialNeed_FollowsTrust()
        {
            Assert.AreEqual(1f, AffordanceAction.NeedWeight(State(0f, deviceBroken: true), AffordanceKind.Work));
            Assert.AreEqual(0.5f, AffordanceAction.NeedWeight(State(0f, deviceBroken: false), AffordanceKind.Work));
            Assert.AreEqual(0.5f, AffordanceAction.NeedWeight(State(0f, trust: 0f), AffordanceKind.Social));
            Assert.AreEqual(1f, AffordanceAction.NeedWeight(State(0f, trust: 1f), AffordanceKind.Social));
            Assert.AreEqual(
                UtilityTraits.Fatigue,
                AffordanceAction.CreateTracked(new AffordanceRegistry(), AffordanceKind.Rest, "npc-a", () => Vector3.zero).Action.PersonalityTrait);
        }

        // ---- V6 接入效用 ---------------------------------------------------------

        [Test]
        public void Brain_SelectsAffordanceAction_WhenAvailable_FallsBackToIdleWhenRemoved()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed", 2f);
            registry.Register(bed);

            var brain = new UtilityBrain();
            brain.Add(new UtilityAction(UtilityBrain.IdleActionName, _ => 0.1f, () => { }));
            AffordanceAction rest = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Rest, "npc-a", () => Vector3.zero, 10f);
            brain.Add(rest.Action);

            string chosen = brain.Tick(State(0.8f), 0.1f);

            Assert.AreEqual("Affordance.Rest", chosen, "有可用可供性时必须选中可供性动作");
            Assert.AreSame(bed, rest.LastClaimed, "Execute 必须记录 LastClaimed（TASK-A-AI-05 起为预约目标）");
            // TASK-A-AI-05 语义更新（登记于施工报告）：A-AI-03 时代「选中即 Claim」被改为
            // 「选中只 Reserve、到达才 Claim」，本断言随新契约更新为预约断言。
            Assert.AreEqual(0, bed.Occupants, "TASK-A-AI-05：选中只预约不占容量（到达才 Claim）");
            Assert.AreEqual("npc-a", bed.ReservedBy, "TASK-A-AI-05：Execute 必须写预约");

            registry.Unregister(bed);
            Assert.AreEqual(UtilityBrain.IdleActionName, brain.Tick(State(0.8f), 0.2f),
                "撤掉可供性后必须回 Idle");

            rest.ReleaseClaim();
            Assert.AreEqual(0, bed.Occupants, "ReleaseClaim 释放占用");
            Assert.IsNull(bed.ReservedBy, "TASK-A-AI-05：ReleaseClaim 同时清预约（双清）");
        }

        // ---- R 预约态（TASK-A-AI-05）---------------------------------------------

        [Test]
        public void Reserve_BlocksOthersFindBest_SelfStillSelectable_ReleaseRestores()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed", 2f);
            registry.Register(bed);

            Assert.IsTrue(registry.Reserve(bed, "npc-a"), "首预约必须成功");
            Assert.AreEqual("npc-a", bed.ReservedBy, "预约必须落在 ReservedBy");
            Assert.AreEqual(0, bed.Occupants, "预约不占容量");

            Assert.IsNull(registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f, "npc-b"),
                "A 预约后 B 的 FindBest 必须跳过该条（V7 变异靶点）");
            Assert.IsNull(registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f),
                "匿名调用同样跳过被预约条目");
            Assert.AreSame(bed, registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f, "npc-a"),
                "A 自己仍能选中自己的预约");

            AffordanceAction actionB = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Rest, "npc-b", () => Vector3.zero, 20f);
            AffordanceAction actionA = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Rest, "npc-a", () => Vector3.zero, 20f);
            Assert.AreEqual(0f, actionB.Action.Score(State(0.8f)), "B 的动作打分必须归 0（预约排除）");
            Assert.Greater(actionA.Action.Score(State(0.8f)), 0f, "A 对自己的预约仍可打分选中");

            Assert.IsTrue(registry.Release(bed, "npc-a"), "释放必须报告清了预约");
            Assert.IsNull(bed.ReservedBy, "Release 必须同时清预约");
            Assert.AreSame(bed, registry.FindBest(Vector3.zero, AffordanceKind.Rest, 20f, "npc-b"),
                "释放后恢复可被 B 选中");
        }

        [Test]
        public void Reserve_IsIdempotent_ReservedByBlocksOthers_ButNotSelf()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed", 1f, capacity: 1);
            registry.Register(bed);

            Assert.IsTrue(registry.Reserve(bed, "npc-a"));
            Assert.IsTrue(registry.Reserve(bed, "npc-a"), "重复预约幂等（返回 true）");
            Assert.IsFalse(registry.Reserve(bed, "npc-b"), "他人预约必须失败");
            Assert.AreEqual("npc-a", bed.ReservedBy, "失败的他人预约不得覆盖预约人");
            Assert.AreEqual(0, bed.Occupants, "预约不占容量");

            Assert.IsFalse(bed.IsAvailable, "参数版 IsAvailable：被预约即不可用（未满员且未被任何人预约才可用）");
            Assert.IsTrue(bed.IsAvailableFor("npc-a"), "自己的预约不算占位");
            Assert.IsFalse(bed.IsAvailableFor("npc-b"), "他人视角下被预约不可用");
            Assert.IsFalse(bed.IsAvailableFor(null), "匿名视角下被预约不可用");

            Assert.Throws<ArgumentNullException>(() => registry.Reserve(null, "npc-a"));
            Assert.Throws<ArgumentException>(() => registry.Reserve(bed, ""));
        }

        [Test]
        public void Claim_AfterOwnReserve_ConvertsToOccupancy_ReleaseClearsBoth()
        {
            var registry = new AffordanceRegistry();
            Affordance bed = Bed("bed", 1f, capacity: 1);
            registry.Register(bed);

            Assert.IsTrue(registry.Reserve(bed, "npc-a"));
            Assert.AreEqual(0, bed.Occupants, "选中（预约）后到达前 Occupants 必须 0（V2）");

            Assert.IsTrue(registry.Claim(bed, "npc-a"), "预约人到达后 Claim 必须成功（自己的预约不算占位）");
            Assert.AreEqual(1, bed.Occupants, "到达后 Occupants == 1（V2）");
            Assert.AreEqual("npc-a", bed.ReservedBy, "Claim 不清预约（随 Release 双清）");

            Assert.IsFalse(registry.Claim(bed, "npc-c"), "被预约后他人 Claim 必须失败");
            Assert.AreEqual(1, bed.Occupants, "失败 Claim 不得改变占用");

            Assert.IsTrue(registry.Release(bed, "npc-a"));
            Assert.AreEqual(0, bed.Occupants, "放弃/停止后占用清零（V3）");
            Assert.IsNull(bed.ReservedBy, "放弃/停止后预约清零（V3）");
            Assert.IsTrue(bed.IsAvailable, "双清后恢复可用");
        }

        [Test]
        public void Release_ByOtherAgent_KeepsReservation_RepeatReleaseReturnsFalse()
        {
            var registry = new AffordanceRegistry();
            Affordance table = new Affordance("table", AffordanceKind.Social, Vector3.zero, 0.6f, 2);
            registry.Register(table);

            Assert.IsTrue(registry.Reserve(table, "npc-a"));
            // TASK-A-AI-05 补跑修复（Round A 复现）：预约也挡他人的 Claim（R3 已断言同口径），
            // 本用例原稿误断言 B 可占容量 2 的另一名额——与产品语义矛盾，按语义修正用例。
            Assert.IsFalse(registry.Claim(table, "npc-b"), "被 A 预约后 B 不得占用（预约同时挡选择与占用）");
            Assert.IsFalse(registry.Release(table, "npc-b"), "B 无任何 stake：Release 返回 false（不抛）");
            Assert.AreEqual("npc-a", table.ReservedBy, "B 的 Release 不得动 A 的预约");
            Assert.AreEqual(0, table.Occupants, "B 无从占用");

            Assert.IsTrue(registry.Release(table, "npc-a"), "A 释放预约（占用本就为 0）");
            Assert.IsNull(table.ReservedBy, "A 的预约已清");

            Assert.IsFalse(registry.Release(table, "npc-a"), "重复 Release 返回 false（不抛）");
            Assert.IsFalse(registry.Release(table, "npc-z"), "从未持 stake 的代理 Release 返回 false");
        }
    }
}
