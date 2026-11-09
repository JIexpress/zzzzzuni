using System;
using System.IO;
using Game.Domain.AI.Utility;
using Game.View.Animation;
using Game.View.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.View.Tests
{
    /// <summary>
    /// TASK-A-ANIM-01 EditMode pins. Two layers:
    /// 1. The PURE drive chain (<see cref="KenneyLocomotionPlan"/>) — the
    ///    brain→parameter mapping, the look-at weight curve and (A-ANIM-02)
    ///    the jump arc must be pure functions with the exact constants the
    ///    controller bakes.
    /// 2. The generated controller ASSET — building it twice from the same
    ///    plan must produce the same content (idempotence: parameters,
    ///    states, default state, transition conditions, IK pass, Run playback
    ///    multiplier). The test builds into a throwaway asset path and
    ///    deletes it, so the committed controller artifact is untouched.
    /// </summary>
    public sealed class KenneyLocomotionPlanTests
    {
        private const string TempControllerDir = "Assets/Game/Tests/View/Temp_KenneyCtl";
        private const string TempControllerPath = TempControllerDir + "/CFG_KenneyLocomotionTest.controller";

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(TempControllerDir))
            {
                AssetDatabase.DeleteAsset(TempControllerDir);
                AssetDatabase.Refresh();
            }
        }

        // ---- 1. pure plan ----------------------------------------------------

        [Test]
        public void Plan_Constants_MatchTheBrief()
        {
            Assert.AreEqual("Speed", KenneyLocomotionPlan.SpeedParam, "简报口径: Speed float 参数");
            Assert.AreEqual("Grounded", KenneyLocomotionPlan.GroundedParam, "简报口径: Grounded bool 参数");
            Assert.AreEqual("Jump", KenneyLocomotionPlan.JumpParam, "简报口径: Jump trigger 参数");
            Assert.AreEqual(new[] { "Idle", "Run", "Jump" },
                new[] { KenneyLocomotionPlan.IdleState, KenneyLocomotionPlan.RunState, KenneyLocomotionPlan.JumpState },
                "简报口径: 状态 Idle/Run/Jump");
            Assert.AreEqual("Idle", KenneyLocomotionPlan.DefaultState, "默认状态必须是 Idle");
            Assert.AreEqual(0.1f, KenneyLocomotionPlan.LocomotionSpeedThreshold, 1e-6f, "简报口径: Speed>0.1→Run");
            Assert.Greater(KenneyLocomotionPlan.RunReferenceSpeed, 0f, "播放倍率标定常数必须为正");
            Assert.Less(KenneyLocomotionPlan.LocomotionEpsilon, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                "Rest 死区必须严格低于过渡阈值，两个判据才不打架");
            Assert.AreEqual(0.05f, KenneyLocomotionPlan.IdleToJumpBlend, 1e-6f, "简报口径: IdleToJumpBlend=0.05");
            Assert.AreEqual(0.1f, KenneyLocomotionPlan.JumpToRunBlend, 1e-6f, "简报口径: JumpToRunBlend=0.1");
        }

        // ---- 1b. jump arc (TASK-A-ANIM-02, V1/V2) ---------------------------

        [Test]
        public void JumpArc_Constants_DeriveFromPeakHeightAndGravity()
        {
            Assert.AreEqual(0.75f, KenneyLocomotionPlan.JumpPeakHeight, 1e-6f, "简报口径: 峰值 0.75 m");
            Assert.AreEqual(9.81f, KenneyLocomotionPlan.JumpGravity, 1e-6f, "简报口径: 重力 9.81");

            // v0 = sqrt(2gh), T = 2v0/g — derived, not hand-written.
            float expectedV0 = Mathf.Sqrt(2f * 0.75f * 9.81f);
            Assert.AreEqual(expectedV0, KenneyLocomotionPlan.JumpInitialSpeed, 1e-4f, "起跳速度 v0=sqrt(2gh)");
            Assert.AreEqual(2f * expectedV0 / 9.81f, KenneyLocomotionPlan.JumpAirTime, 1e-4f, "滞空时间 T=2v0/g");
            Assert.Greater(KenneyLocomotionPlan.JumpAirTime, 0.1f, "滞空时间必须为正（驱动器按它判定落地）");
        }

        /// <summary>V1：两端为 0、峰值 = JumpPeakHeight、关于 T/2 对称。</summary>
        [Test]
        public void JumpHeightAt_IsAClampedSymmetricParabola()
        {
            float airTime = KenneyLocomotionPlan.JumpAirTime;

            Assert.AreEqual(0f, KenneyLocomotionPlan.JumpHeightAt(0f), 1e-6f, "起跳瞬间高度 0");
            Assert.AreEqual(0f, KenneyLocomotionPlan.JumpHeightAt(airTime), 1e-4f, "落地瞬间高度 0");
            Assert.AreEqual(KenneyLocomotionPlan.JumpPeakHeight, KenneyLocomotionPlan.JumpHeightAt(airTime * 0.5f),
                1e-3f, "峰值必须 = JumpPeakHeight");

            // Symmetry h(t) = h(T−t), and the mid-air samples sit strictly
            // between the ground and the peak (a real arc, not a plateau).
            foreach (float fraction in new[] { 0.1f, 0.25f, 1f / 3f, 0.4f })
            {
                float t = airTime * fraction;
                float mirrored = airTime - t;
                Assert.AreEqual(KenneyLocomotionPlan.JumpHeightAt(t), KenneyLocomotionPlan.JumpHeightAt(mirrored),
                    1e-4f, $"弧线必须关于 T/2 对称（fraction={fraction:0.##}）");
                Assert.Greater(KenneyLocomotionPlan.JumpHeightAt(t), 0f, "空中高度必须为正");
                Assert.Less(KenneyLocomotionPlan.JumpHeightAt(t), KenneyLocomotionPlan.JumpPeakHeight,
                    "空中高度必须低于峰值");
            }

            // Clamped: before take-off and past landing both read 0, no throw.
            Assert.AreEqual(0f, KenneyLocomotionPlan.JumpHeightAt(-1f), 1e-6f, "负时间按在地面处理");
            Assert.AreEqual(0f, KenneyLocomotionPlan.JumpHeightAt(airTime * 3f), 1e-6f, "超时按已落地处理");
            Assert.AreEqual(0f, KenneyLocomotionPlan.JumpHeightAt(float.NaN), 1e-6f, "NaN 不抛且不产生高度");

            // Purity: same input, same output.
            Assert.AreEqual(KenneyLocomotionPlan.JumpHeightAt(airTime * 0.3f),
                KenneyLocomotionPlan.JumpHeightAt(airTime * 0.3f));
        }

        /// <summary>V2：t&lt;T 为空中、t=T 已落地；负数/极端值不抛。</summary>
        [Test]
        public void AirborneAt_IsTrueOnlyInsideTheArc()
        {
            float airTime = KenneyLocomotionPlan.JumpAirTime;

            Assert.IsTrue(KenneyLocomotionPlan.AirborneAt(0f), "起跳瞬间即空中");
            Assert.IsTrue(KenneyLocomotionPlan.AirborneAt(airTime * 0.999f), "临近落地仍为空中");
            Assert.IsFalse(KenneyLocomotionPlan.AirborneAt(airTime), "t=T 已落地（Grounded 必须回 true）");
            Assert.IsFalse(KenneyLocomotionPlan.AirborneAt(airTime * 2f), "超时已落地");

            Assert.DoesNotThrow(() => KenneyLocomotionPlan.AirborneAt(-0.5f), "负时间不得抛异常");
            Assert.IsFalse(KenneyLocomotionPlan.AirborneAt(-0.5f), "负时间按已落地处理");
            Assert.DoesNotThrow(() => KenneyLocomotionPlan.AirborneAt(float.MaxValue));
            Assert.IsFalse(KenneyLocomotionPlan.AirborneAt(float.NaN), "NaN 按已落地处理");
        }

        [Test]
        public void BrainActionToSpeedParam_IsPureAndRestClampsBelowEpsilon()
        {
            // Rest-like brain states clamp to an exact 0 at/below the epsilon:
            // the Run→Idle transition (Speed < 0.1) then fires deterministically.
            Assert.AreEqual(0f, KenneyLocomotionPlan.BrainActionToSpeedParam(NpcUtilityAgent.RestActionName, 0f));
            Assert.AreEqual(0f, KenneyLocomotionPlan.BrainActionToSpeedParam(NpcUtilityAgent.RestActionName, 0.049f));
            Assert.AreEqual(0f, KenneyLocomotionPlan.BrainActionToSpeedParam(UtilityBrain.IdleActionName, 0.05f));

            // Above the epsilon the body really moves — a Rest still WALKING to
            // the rest point keeps its measured speed (no idle slide).
            Assert.AreEqual(1.5f, KenneyLocomotionPlan.BrainActionToSpeedParam(NpcUtilityAgent.RestActionName, 1.5f));

            // Locomotion actions pass the measured speed through unchanged —
            // the mapping is pure (same inputs, same output).
            foreach (string action in new[]
                     {
                         NpcUtilityAgent.PatrolActionName,
                         NpcUtilityAgent.FollowActionName,
                         NpcUtilityAgent.PickupActionName,
                     })
            {
                Assert.AreEqual(2.2f, KenneyLocomotionPlan.BrainActionToSpeedParam(action, 2.2f), action);
                Assert.AreEqual(0f, KenneyLocomotionPlan.BrainActionToSpeedParam(action, 0f), action);
            }

            // Purity: repeated calls identical.
            float first = KenneyLocomotionPlan.BrainActionToSpeedParam(NpcUtilityAgent.RestActionName, 0.049f);
            float second = KenneyLocomotionPlan.BrainActionToSpeedParam(NpcUtilityAgent.RestActionName, 0.049f);
            Assert.AreEqual(first, second);
        }

        [Test]
        public void LookAtWeightFor_IsFullInsideFadesAndZeroBeyond()
        {
            const float full = 55f;
            const float fade = 85f;

            Assert.AreEqual(1f, KenneyLocomotionPlan.LookAtWeightFor(0f, full, fade), "正前方全权重");
            Assert.AreEqual(1f, KenneyLocomotionPlan.LookAtWeightFor(full, full, fade), "满权重边界内");
            Assert.AreEqual(0f, KenneyLocomotionPlan.LookAtWeightFor(fade, full, fade), "淡出边界外零权重");
            Assert.AreEqual(0f, KenneyLocomotionPlan.LookAtWeightFor(120f, full, fade), "身后目标零权重（不拧脖子）");
            Assert.AreEqual(1f, KenneyLocomotionPlan.LookAtWeightFor(-30f, full, fade), "yaw 对称");

            // Linear midpoint.
            Assert.AreEqual(0.5f, KenneyLocomotionPlan.LookAtWeightFor(70f, full, fade), 1e-5f, "中点线性 0.5");

            // Degenerate configuration must throw (fail fast, not silently).
            Assert.Throws<ArgumentOutOfRangeException>(
                () => KenneyLocomotionPlan.LookAtWeightFor(10f, 85f, 55f));
        }

        // ---- 2. generated controller asset ----------------------------------

        [Test]
        public void Controller_BuildsFromPlan_WithParamsStatesTransitionsAndIkPass()
        {
            Directory.CreateDirectory(TempControllerDir);
            AssetDatabase.Refresh();
            var report = new System.Text.StringBuilder();
            var controller = AnimRigSetup.EnsureController(TempControllerPath, report);

            Assert.IsNotNull(controller, "controller 资产必须生成");

            // Parameters: exactly the brief's set with the brief's types.
            Assert.AreEqual(3, controller.parameters.Length, "必须恰好三个参数");
            AssertParam(controller, "Speed", AnimatorControllerParameterType.Float);
            AssertParam(controller, "Grounded", AnimatorControllerParameterType.Bool);
            AssertParam(controller, "Jump", AnimatorControllerParameterType.Trigger);

            // Layer 0 with IK pass (OnAnimatorIK requires it).
            Assert.AreEqual(1, controller.layers.Length, "恰好一层");
            Assert.IsTrue(controller.layers[0].iKPass, "Base layer 必须开启 IK pass，否则 OnAnimatorIK 不触发");

            AnimatorStateMachine root = controller.layers[0].stateMachine;
            Assert.AreEqual(3, root.states.Length, "必须恰好三个状态");
            Assert.IsNotNull(FindState(root, "Idle"), "Idle 状态");
            Assert.IsNotNull(FindState(root, "Run"), "Run 状态");
            Assert.IsNotNull(FindState(root, "Jump"), "Jump 状态");
            Assert.AreEqual("Idle", root.defaultState.name, "默认状态 Idle");

            // Transitions and conditions.
            AnimatorState idle = FindState(root, "Idle");
            AnimatorState run = FindState(root, "Run");
            AnimatorState jump = FindState(root, "Jump");

            AnimatorStateTransition idleToRun = FindTransition(idle, "Run");
            Assert.IsNotNull(idleToRun, "Idle→Run 过渡必须存在");
            Assert.IsFalse(idleToRun.hasExitTime, "参数驱动过渡不得带 exit time");
            AssertHasCondition(idleToRun, AnimatorConditionMode.Greater, "Speed", 0.1f);

            AnimatorStateTransition runToIdle = FindTransition(run, "Idle");
            Assert.IsNotNull(runToIdle, "Run→Idle 过渡必须存在");
            AssertHasCondition(runToIdle, AnimatorConditionMode.Less, "Speed", 0.1f);

            AnimatorStateTransition runToJump = FindTransition(run, "Jump");
            Assert.IsNotNull(runToJump, "Run→Jump 过渡必须存在");
            AssertHasCondition(runToJump, AnimatorConditionMode.If, "Jump", 0f);

            // TASK-A-ANIM-02: standing take-off + speed-aware landing exit.
            AnimatorStateTransition idleToJump = FindTransition(idle, "Jump");
            Assert.IsNotNull(idleToJump, "Idle→Jump 过渡必须存在（站着起跳，V3/V6）");
            Assert.IsFalse(idleToJump.hasExitTime, "Idle→Jump 只由 Jump 触发器驱动，不得带 exit time");
            AssertHasCondition(idleToJump, AnimatorConditionMode.If, "Jump", 0f);

            AnimatorStateTransition jumpToRun = FindTransition(jump, "Run");
            Assert.IsNotNull(jumpToRun, "Jump→Run 过渡必须存在（落地时仍移动 → 回 Run）");
            Assert.IsTrue(jumpToRun.hasExitTime, "Jump→Run 必须走 exit time，与 Jump→Idle 同口径");
            Assert.AreEqual(KenneyLocomotionPlan.JumpExitTime, jumpToRun.exitTime, 1e-4f,
                "Jump→Run exitTime = JumpExitTime");
            AssertHasCondition(jumpToRun, AnimatorConditionMode.Greater, "Speed",
                KenneyLocomotionPlan.LocomotionSpeedThreshold);
            AssertHasCondition(jumpToRun, AnimatorConditionMode.If, "Grounded", 0f);

            AnimatorStateTransition jumpToIdle = FindTransition(jump, "Idle");
            Assert.IsNotNull(jumpToIdle, "Jump→Idle 过渡必须存在");
            Assert.IsTrue(jumpToIdle.hasExitTime, "Jump→Idle 走 exit time");
            AssertHasCondition(jumpToIdle, AnimatorConditionMode.If, "Grounded", 0f);

            Assert.AreEqual(6, CountTransitions(root),
                "简报口径: 6 条过渡（Idle>Run / Run>Idle / Run>Jump / Idle>Jump / Jump>Run / Jump>Idle）");

            // Run playback sync wire: the state's speed is parameter-driven.
            Assert.That(run.speed, Is.EqualTo(1f / KenneyLocomotionPlan.RunReferenceSpeed).Within(1e-5f),
                "Run 状态播放倍率 = 1/RunReferenceSpeed");
            Assert.AreEqual("Speed", run.speedParameter, "Run 播放倍率绑定 Speed 参数（位移同步的线）");

            // Motions come from the split animation FBX (humanoid retarget — no copies).
            Assert.IsNotNull(idle.motion, "Idle 必须绑定 clip");
            Assert.IsNotNull(run.motion, "Run 必须绑定 clip");
            Assert.IsNotNull(jump.motion, "Jump 必须绑定 clip");
        }

        /// <summary>
        /// 重建语义（2022.3 选型）：EnsureController 采用 DELETE + CREATE——
        /// 原地重建会丢过渡的 destination/conditions（真实事故，见施工报告）。
        /// 因此"幂等"的口径是【内容等价】：连跑两次，图拓扑完全一致，且重建后
        /// 落盘资产仍然完整（EnsureController 内建的 persisted verify 会先把关）。
        /// guid 会被更换——场景引用由 AnimSceneSetup（每次先跑 AnimRigSetup）重建。
        /// </summary>
        [Test]
        public void Controller_Regenerate_ProducesEquivalentContent()
        {
            Directory.CreateDirectory(TempControllerDir);
            AssetDatabase.Refresh();
            var report = new System.Text.StringBuilder();

            AnimRigSetup.EnsureController(TempControllerPath, report);
            AnimRigSetup.EnsureController(TempControllerPath, report);
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(TempControllerPath);

            Assert.IsNotNull(controller, "重建后 controller 必须存在");
            Assert.AreEqual(3, controller.parameters.Length, "重建不得累积参数");
            Assert.AreEqual(3, controller.layers[0].stateMachine.states.Length, "重建不得累积状态");
            Assert.AreEqual(1, controller.layers.Length, "重建不得累积层");
            Assert.IsTrue(controller.layers[0].iKPass, "重建后 IK pass 必须开启");
            AnimatorStateMachine root = controller.layers[0].stateMachine;
            Assert.IsNotNull(FindTransition(FindState(root, "Idle"), "Run"), "重建后 Idle→Run 必须存在");
            Assert.IsNotNull(FindTransition(FindState(root, "Run"), "Idle"), "重建后 Run→Idle 必须存在");
            Assert.IsNotNull(FindTransition(FindState(root, "Run"), "Jump"), "重建后 Run→Jump 必须存在");
            Assert.IsNotNull(FindTransition(FindState(root, "Idle"), "Jump"), "重建后 Idle→Jump 必须存在");
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Run"), "重建后 Jump→Run 必须存在");
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Idle"), "重建后 Jump→Idle 必须存在");
            Assert.AreEqual(6, CountTransitions(root), "重建不得累积或丢失过渡");
            Assert.AreEqual("Idle", root.defaultState.name, "重建后默认状态 Idle");
        }

        /// <summary>
        /// 持久化回归锁（真实事故，2026-11-08）：脚本创建的 AnimatorStateTransition
        /// 若未 AddObjectToAsset，内存里一切正常、SaveAssets 后过渡引用序列化为
        /// {fileID: 0}——运行期状态机永远停在默认状态。此用例强制落盘重载后再断言。
        /// </summary>
        [Test]
        public void Controller_Transitions_SurviveSaveAndReload()
        {
            Directory.CreateDirectory(TempControllerDir);
            AssetDatabase.Refresh();
            var report = new System.Text.StringBuilder();
            AnimRigSetup.EnsureController(TempControllerPath, report);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(TempControllerPath, ImportAssetOptions.ForceUpdate);

            AnimatorController persisted = AssetDatabase.LoadAssetAtPath<AnimatorController>(TempControllerPath);
            Assert.IsNotNull(persisted, "重载后 controller 必须存在");
            AnimatorStateMachine root = persisted.layers[0].stateMachine;
            Assert.AreEqual(3, root.states.Length, "落盘后必须仍有三个状态");
            Assert.IsNotNull(FindTransition(FindState(root, "Idle"), "Run"), "落盘后 Idle→Run 过渡必须存活");
            Assert.IsNotNull(FindTransition(FindState(root, "Run"), "Idle"), "落盘后 Run→Idle 过渡必须存活");
            Assert.IsNotNull(FindTransition(FindState(root, "Run"), "Jump"), "落盘后 Run→Jump 过渡必须存活");
            Assert.IsNotNull(FindTransition(FindState(root, "Idle"), "Jump"), "落盘后 Idle→Jump 过渡必须存活");
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Run"), "落盘后 Jump→Run 过渡必须存活");
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Idle"), "落盘后 Jump→Idle 过渡必须存活");
            Assert.AreEqual(6, CountTransitions(root), "落盘后必须仍有六条过渡");
            Assert.AreEqual("Idle", root.defaultState.name, "落盘后默认状态必须仍是 Idle");
            AssertHasCondition(FindTransition(FindState(root, "Idle"), "Run"), AnimatorConditionMode.Greater, "Speed", 0.1f);
            AssertHasCondition(FindTransition(FindState(root, "Jump"), "Run"), AnimatorConditionMode.Greater, "Speed", 0.1f);
        }

        // ---- helpers -----------------------------------------------------------

        private static void AssertParam(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                if (parameter.name == name)
                {
                    Assert.AreEqual(type, parameter.type, $"参数 {name} 类型");
                    return;
                }
            }

            Assert.Fail($"缺少参数 {name}");
        }

        private static AnimatorState FindState(AnimatorStateMachine root, string name)
        {
            foreach (ChildAnimatorState child in root.states)
            {
                if (child.state != null && child.state.name == name)
                {
                    return child.state;
                }
            }

            return null;
        }

        private static AnimatorStateTransition FindTransition(AnimatorState from, string toName)
        {
            foreach (AnimatorStateTransition transition in from.transitions)
            {
                if (transition.destinationState != null && transition.destinationState.name == toName)
                {
                    return transition;
                }
            }

            return null;
        }

        private static int CountTransitions(AnimatorStateMachine root)
        {
            int count = 0;
            foreach (ChildAnimatorState child in root.states)
            {
                if (child.state != null)
                {
                    count += child.state.transitions.Length;
                }
            }

            return count;
        }

        /// <summary>
        /// TASK-A-ANIM-03: foot-IK weight is zero on flat ground, rises with
        /// slope and with height residual, saturates at 1, and degrades to 0
        /// on invalid limits (never throws).
        /// </summary>
        [Test]
        public void FootIkWeight_IsZeroOnFlatGround_AndSaturatesAtTheLimits()
        {
            Assert.AreEqual(0f, KenneyLocomotionPlan.FootIkWeightFor(0f, 0f, 32f, 0.22f), 1e-6f,
                "平地且无落差时必须为 0（脚不改动画姿态）");
            Assert.AreEqual(1f, KenneyLocomotionPlan.FootIkWeightFor(32f, 0f, 32f, 0.22f), 1e-6f,
                "坡度到达上限时必须为 1");
            Assert.AreEqual(1f, KenneyLocomotionPlan.FootIkWeightFor(0f, 0.22f, 32f, 0.22f), 1e-6f,
                "落差到达上限时必须为 1");
            Assert.AreEqual(0.5f, KenneyLocomotionPlan.FootIkWeightFor(16f, 0f, 32f, 0.22f), 1e-4f,
                "坡度半程应为线性 0.5");
            Assert.AreEqual(0.5f, KenneyLocomotionPlan.FootIkWeightFor(0f, -0.11f, 32f, 0.22f), 1e-4f,
                "负落差取绝对值，同样线性");

            Assert.AreEqual(1f, KenneyLocomotionPlan.FootIkWeightFor(90f, 1f, 32f, 0.22f), 1e-6f,
                "超限必须夹到 1，不许溢出");
            Assert.AreEqual(0f, KenneyLocomotionPlan.FootIkWeightFor(30f, 0.2f, 0f, 0.22f), 1e-6f,
                "坡度为 0 的非法上限必须返回 0");
            Assert.AreEqual(0f, KenneyLocomotionPlan.FootIkWeightFor(30f, 0.2f, 32f, 0f), 1e-6f,
                "落差上限为 0 时必须返回 0");
        }

        private static void AssertHasCondition(
            AnimatorStateTransition transition,
            AnimatorConditionMode mode,
            string parameter,
            float threshold)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.mode == mode && condition.parameter == parameter
                    && Mathf.Abs(condition.threshold - threshold) < 1e-4f)
                {
                    return;
                }
            }

            Assert.Fail($"过渡 {transition}/{transition.destinationState?.name} 缺少条件 {mode} {parameter} {threshold}");
        }
    }
}
