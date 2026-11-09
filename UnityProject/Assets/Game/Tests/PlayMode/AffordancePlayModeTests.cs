// AffordancePlayModeTests.cs —— TASK-A-AI-03 的场景侧最小证据（1 条）。
// 三个同类可供性（3m / 6m / 9m）+ 一个普通 UtilityBrain：Tick 一拍后必须选中
// 最近的那个，并经 Execute 真正占用它的容量。用的是 PlayMode 程序集里的纯逻辑
// 路径（无场景加载），证明接入效用层在运行时同样成立。

using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.PlayMode.Tests
{
    /// <summary>三可供性 + 一 brain：Tick 选中最近者并占用容量。</summary>
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
            Assert.AreSame(near, rest.LastClaimed, "必须选中并占用最近的床");
            Assert.AreEqual(1, near.Occupants);
            Assert.AreEqual(0, mid.Occupants);
            Assert.AreEqual(0, far.Occupants);
        }
    }
}
