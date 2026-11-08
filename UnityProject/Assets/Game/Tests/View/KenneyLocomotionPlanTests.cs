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
    ///    brain→parameter mapping and the look-at weight curve must be pure
    ///    functions with the exact constants the controller bakes.
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

            AnimatorStateTransition jumpToIdle = FindTransition(jump, "Idle");
            Assert.IsNotNull(jumpToIdle, "Jump→Idle 过渡必须存在");
            Assert.IsTrue(jumpToIdle.hasExitTime, "Jump→Idle 走 exit time");
            AssertHasCondition(jumpToIdle, AnimatorConditionMode.If, "Grounded", 0f);

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
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Idle"), "重建后 Jump→Idle 必须存在");
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
            Assert.IsNotNull(FindTransition(FindState(root, "Jump"), "Idle"), "落盘后 Jump→Idle 过渡必须存活");
            Assert.AreEqual("Idle", root.defaultState.name, "落盘后默认状态必须仍是 Idle");
            AssertHasCondition(FindTransition(FindState(root, "Idle"), "Run"), AnimatorConditionMode.Greater, "Speed", 0.1f);
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
