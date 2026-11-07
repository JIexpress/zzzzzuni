// AssemblyThrusterPlayModeTests.cs —— 推进器施力方向证据 (TASK-A-TECH-02)。
// 控制组（不点火 → 位移≈0）+ 处理组（点火 → 复合体沿推进器 +Z 位移），
// 力方向断言：位移在推进器世界 +Z 上的投影显著为正、横向漂移近零。

using System.Collections;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ZFrame.PlayMode.Tests
{
    public sealed class AssemblyThrusterPlayModeTests : AssemblyPlayModeTestBase
    {
        /// <summary>② 推进器施加力 → 复合体沿推力方向位移。</summary>
        [UnityTest]
        public IEnumerator Thruster_DrivesComposite_AlongItsForward()
        {
            AssemblyPart thruster = Track(AssemblyPartFactory.CreateThruster("t-thruster", new Vector3(0f, 5f, 0f)));
            AssemblyPart payload = Track(AssemblyPartFactory.CreateCube("t-payload", new Vector3(0f, 5f, 3f)));
            thruster.Body.useGravity = false;
            payload.Body.useGravity = false;

            // 推进器移动侧：挂到载荷的 -Z 面（推进器在载荷后方，+Z 指向载荷）。
            AssemblyJoint joint = AssemblyBuilder.Connect(thruster, thruster.Points[0], payload.Points[1]);
            Assert.IsNotNull(joint);
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(thruster).Count, "推进器与载荷成一体");

            AssemblyThruster module = thruster.GetComponent<AssemblyThruster>();
            Vector3 thrustDir = module.ThrustDirection;
            Assert.Greater(Vector3.Dot(thrustDir, Vector3.forward), 0.999f,
                "前置：该对齐下推进器 +Z 应指向世界 +Z（载荷侧，推力把复合体推向载荷方向）");

            // 控制组：不点火，75 步（1.5 s）位移应≈0。
            Vector3 compositeStart = 0.5f * (thruster.Body.position + payload.Body.position);
            StepPhysics(75);
            float controlDrift = (0.5f * (thruster.Body.position + payload.Body.position) - compositeStart).magnitude;
            Assert.Less(controlDrift, 0.02f, $"不点火时复合体不应移动（实测 {controlDrift:F4} m）");

            // 处理组：点火 3 s（覆盖全部 75 步），F=6N / m=1.5kg → 1.5 s 位移≈4.5 m。
            module.Fire(3f);
            Assert.IsTrue(module.IsBurning);
            for (int step = 0; step < 75; step++)
            {
                module.Tick(0.02f);
                Physics.Simulate(0.02f);
            }

            Vector3 compositeEnd = 0.5f * (thruster.Body.position + payload.Body.position);
            Vector3 displacement = compositeEnd - compositeStart;

            float along = Vector3.Dot(displacement, thrustDir);
            float lateral = (displacement - thrustDir * along).magnitude;
            Assert.Greater(along, 1.0f, $"复合体必须沿推力方向位移（实测 {along:F3} m）");
            Assert.Less(lateral, 0.1f, $"横向漂移必须近零（实测 {lateral:F4} m）→ 力方向正确");
            Assert.Greater(along, 10f * controlDrift, "处理组位移必须显著大于控制组");

            yield return null;
        }

        /// <summary>推进器燃烧计时会耗尽（Fire 后 Tick 足够步数 → IsBurning 变 false）。</summary>
        [UnityTest]
        public IEnumerator Thruster_BurnExpires_AfterDuration()
        {
            AssemblyPart thruster = Track(AssemblyPartFactory.CreateThruster("t-thruster", new Vector3(0f, 5f, 0f)));
            AssemblyThruster module = thruster.GetComponent<AssemblyThruster>();
            module.Fire(0.1f);

            for (int step = 0; step < 10 && module.IsBurning; step++)
            {
                module.Tick(0.02f);
            }

            Assert.IsFalse(module.IsBurning, "0.1 s 燃烧应在 5 步内耗尽");
            Assert.Less(module.RemainingBurn, 1e-3f);

            yield return null;
        }
    }
}
