// AssemblyPlayModeTestBase.cs —— 装配切片 PlayMode 用例公共台架 (TASK-A-TECH-02)。
// 物理用确定性手动仿真：SetUp 切 Physics.simulationMode = Script，TearDown 复原
// Fixed 并销毁本用例创建的对象。所有物理断言与帧率无关、可复现。

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>Base rig for the assembly PlayMode cases (manual physics harness).</summary>
    public abstract class AssemblyPlayModeTestBase
    {
        private readonly List<Object> _owned = new List<Object>();

        [SetUp]
        public void BaseSetUp()
        {
            Assert.AreEqual(SimulationMode.FixedUpdate, Physics.simulationMode,
                "precondition: 上一个用例的 TearDown 必须把物理模式复原为 Fixed");
            Physics.simulationMode = SimulationMode.Script;
        }

        [TearDown]
        public void BaseTearDown()
        {
            Physics.simulationMode = SimulationMode.FixedUpdate;
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    // DestroyImmediate 而非 Destroy：Destroy 是帧末延迟，下一个
                    // 用例会与"待销毁但仍在物理世界里飘"的上一用例对象互撞
                    //（实测：③ 的下落对撞上了 ① 的滑行对，分离断言随机红）。
                    // 用例边界上没有任何对象引用这些测试台架，立即销毁安全。
                    Object.DestroyImmediate(owned);
                }
            }

            _owned.Clear();
        }

        /// <summary>Registers an object for TearDown cleanup.</summary>
        protected T Track<T>(T owned) where T : Object
        {
            _owned.Add(owned);
            return owned;
        }

        /// <summary>Steps the physics simulation deterministically (default 0.02 s per step).</summary>
        protected static void StepPhysics(int steps, float dt = 0.02f)
        {
            for (int i = 0; i < steps; i++)
            {
                Physics.Simulate(dt);
            }
        }
    }
}
