// AffordancePlayModeTests.cs —— TASK-A-AI-03 的场景侧最小证据（1 条）。
// TASK-A-AI-05 语义更新（Round B 复现修复，登记于施工报告）：Execute 由「选中即 Claim」
// 改为「选中只 Reserve」（到达才由 seeker Claim 转正式占用），占用断言随新契约更新为
// 预约断言——原意图「Tick 必须选中最近的床」不变。
// 三个同类可供性（3m / 6m / 9m）+ 一个普通 UtilityBrain：用的是 PlayMode 程序集里的纯逻辑
// 路径（无场景加载），证明接入效用层在运行时同样成立。

using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.PlayMode.Tests
{
    /// <summary>三可供性 + 一 brain：Tick 选中最近者并预约它（TASK-A-AI-05 起到达才占容量）。</summary>
    public sealed class AffordancePlayModeTests
    {
        [Test]
        public void Tick_WithThreeAffordances_SelectsTheNearest()
        {
            var registry = new AffordanceRegistry();
            var far = new Affordance("bed-far", AffordanceKind.Rest, new Vector3(9f, 0f, 0f), 0.5f, 1);
            var near = new Affordance("bed-near", AffordanceKind.Rest, new Vector3(3f, 0f, 0f), 0.5f, 1);
            var mid = new Affordance("bed-mid", AffordanceKind.Rest, new Vector3(6f, 0f, 0f), 0.5f, 1);
            registry.Register(far);
            registry.Register(mid);
            registry.Register(near); // 注册顺序与距离顺序不一致：胜负只能由距离决定

            var brain = new UtilityBrain();
            brain.Add(new UtilityAction(UtilityBrain.IdleActionName, _ => 0.1f, () => { }));
            AffordanceAction rest = AffordanceAction.CreateTracked(
                registry, AffordanceKind.Rest, "npc-playmode", () => Vector3.zero, 20f);
            brain.Add(rest.Action);

            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, 0.8f);
            string chosen = brain.Tick(state, 0.1f);

            Assert.AreEqual("Affordance.Rest", chosen);
            Assert.AreSame(near, rest.LastClaimed, "必须选中（预约）最近的床");
            Assert.AreEqual("npc-playmode", near.ReservedBy, "选中即预约最近的床");
            Assert.AreEqual(0, near.Occupants, "选中后未到达不占容量（TASK-A-AI-05：到达才 Claim）");
            Assert.AreEqual(0, mid.Occupants);
            Assert.AreEqual(0, far.Occupants);
        }
    }
}
