// NpcAnimatorPlayModeTests.cs —— TASK-A-ANIM-01：人形动画"脑驱动"链的场景级证据。
// SCN_Anim 里两个 NPC 用真实 Kenney 蒙皮模型 + 生成的 CHR_KenneyLocomotion
// 控制器 + NpcAnimatorDriver。断言四条可证伪链（简报口径）：
// ① 脑状态→动画状态：脑 Rest（已到休息点、实测位移≈0）→ 状态机 Idle；
//    脑 Patrol → 状态机 Run 且 Speed>0.1；
// ② 位移朝向一致：Run 中直道中段的实际位移方向与角色朝向夹角 < 30°（转向平滑后）；
// ③ Look-at IK：玩家放在 NPC 前方 45° → 头骨朝向目标（夹角 < 25°）；
//    权重归零 → 头部回到动画姿态（夹角 > 32°）——双向可证伪；
// ④ 速度-位移同步：Speed 参数 = 实测位移速度（容差），且 Run 归一化时间推进率
//    = Speed/RunReferenceSpeed（动画播放速度真的被位移驱动）。
// ⑤（附加）RequestJump → 状态机穿过 Jump 并返回。
// ⑥ TASK-A-ANIM-02 真位移：起跳后 Y 沿纯函数弧线上升到 ≥ baseY+0.3 再回落，
//    T 后精确归位 baseY（±1e-3）；Grounded 由 false 回 true（真值驱动）。
// ⑦ 无二段跳：空中再次 RequestJump 返回 false、IgnoredJumpCount=1、弧线不重置。
// ⑧ 站立起跳：Idle + Jump 触发 → 进入 Jump（Idle→Jump 过渡的可证伪证据）。

using System.Collections;
using Game.Domain.AI.Utility;
using Game.View.Animation;
using Game.View.Npc;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Gameplay;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>SCN_Anim 的脑→动画驱动链四条可证伪用例 + Jump 状态机穿越。</summary>
    public sealed class NpcAnimatorPlayModeTests
    {
        private const string SceneName = "SCN_Anim";

        /// <summary>与 AnimSceneSetup 的布局常数一致（测试键在场景布局上，改动需同步）。</summary>
        private static readonly Vector2 RestPointXz = new Vector2(-2.5f, -2f);
        private static readonly Vector2[] WaypointsXz =
        {
            new Vector2(5f, -5f),
            new Vector2(5f, 5f),
            new Vector2(-5f, 5f),
            new Vector2(-5f, -5f),
        };

        [UnityTest]
        public IEnumerator SceneCarries_TwoAnimatedKenneyNpcs_WithHumanoidRigAndGeneratedController()
        {
            yield return LoadSceneAndSmokeCheck();
        }

        /// <summary>① 脑 Rest（就位后）→ Idle；脑 Patrol → Run 且 Speed &gt; 0.1。</summary>
        [UnityTest]
        public IEnumerator BrainState_IsTheFunctionBehind_TheAnimationState()
        {
            yield return LoadSceneAndSmokeCheck();

            // 移动侧（全程 live）：恋巡逻 NPC → Run 且 Speed > 0.1。
            NpcUtilityView wayfarer = ById("npc-anim-c");
            Animator wayfarerAnimator = AnimatorOf(wayfarer);
            yield return WaitForState(wayfarerAnimator, KenneyLocomotionPlan.RunState, 10f,
                "恋巡逻 NPC 必须进入 Run（脑输出移动 → 动画 Run）");
            float runSpeed = wayfarerAnimator.GetFloat(KenneyLocomotionPlan.SpeedParam);
            Assert.Greater(runSpeed, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                $"Run 状态下 Speed 参数必须 > 0.1（实测 {runSpeed:0.###}）");
            Assert.AreEqual(NpcUtilityAgent.PatrolActionName, wayfarer.Agent.CurrentAction,
                "Run 必须由脑的移动决策驱动，而非自播");

            // 休息侧：走回休息点途中（Rest 决策 + 实际位移 > 0）必须经历 Run
            // ——状态跟的是位移不是动作名；就位停稳（实测位移≈0）后 → Idle。
            NpcUtilityView restful = ById("npc-anim-a");
            Animator restfulAnimator = AnimatorOf(restful);
            NpcAnimatorDriver restfulDriver = DriverOf(restful);
            bool sawRunDuringRestWalk = false;
            yield return WaitUntil(() =>
                {
                    if (restfulAnimator.GetCurrentAnimatorStateInfo(0)
                            .IsName(KenneyLocomotionPlan.RunState)
                        && !restfulAnimator.IsInTransition(0))
                    {
                        sawRunDuringRestWalk = true;
                    }

                    return AtRestPoint(restful)
                           && IsBrainAction(restful, NpcUtilityAgent.RestActionName)
                           && restfulDriver.LastMeasuredSpeed < KenneyLocomotionPlan.LocomotionSpeedThreshold;
                }, 25f, "恋休息 NPC 必须走回休息点并停稳（Rest 决策 + 实测位移≈0）");
            Assert.IsTrue(sawRunDuringRestWalk,
                "Rest 决策的走回休息点阶段必须经历 Run——动画状态是位移的函数，不是动作名的函数");

            // 冻结驱动（参数定格在就位值），消除"疲劳恢复后脑自行切换 Follow"的
            // 窗口竞态，然后断言复合函数的静止端：Rest + 位移≈0 → Idle。
            restful.ManualDrive = true;
            restfulDriver.ManualDrive = true;
            Assert.AreEqual(NpcUtilityAgent.RestActionName, restfulDriver.LastBrainAction,
                "冻结时刻的脑输出必须是 Rest");
            Assert.AreEqual(0f, restfulDriver.LastWrittenSpeed,
                "Rest + 停稳 → Speed 参数钳到精确 0（BrainActionToSpeedParam 死区）");
            yield return WaitForState(restfulAnimator, KenneyLocomotionPlan.IdleState, 5f,
                "脑 Rest + 实测位移≈0 → 状态机 Idle（非播片：换脑即换状态）");
            float idleSpeed = restfulAnimator.GetFloat(KenneyLocomotionPlan.SpeedParam);
            Assert.Less(idleSpeed, KenneyLocomotionPlan.LocomotionSpeedThreshold,
                $"Idle 时 Speed 参数必须低于阈值（实测 {idleSpeed:0.###}）");
        }

        /// <summary>② Run 中实际位移方向与角色朝向一致（角速度平滑转向之后）。</summary>
        [UnityTest]
        public IEnumerator RunState_DisplacementDirectionMatchesFacing_AfterSmoothTurning()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView wayfarer = ById("npc-anim-c");
            yield return WaitForState(AnimatorOf(wayfarer), KenneyLocomotionPlan.RunState, 10f, "进入 Run");
            yield return WaitUntil(() => IsMidLeg(wayfarer), 15f, "巡逻环线必须有直道中段窗口");

            Vector3 start = wayfarer.transform.position;
            yield return WaitRealSeconds(0.4f);
            Vector3 delta = wayfarer.transform.position - start;
            delta.y = 0f;
            Assert.Greater(delta.magnitude, 0.3f, "0.4s 窗口内必须有真实位移（≈0.6m）");

            Vector3 facing = FacingOf(wayfarer);
            float angle = Vector3.Angle(delta.normalized, facing);
            Assert.Less(angle, 30f,
                $"位移方向与朝向夹角必须 < 30°（实测 {angle:0.#}°）——转向平滑后直道上应基本对齐");
        }

        /// <summary>③ Look-at IK：头部骨骼转向视野内玩家；权重归零 → 头部回到动画姿态（双向可证伪）。</summary>
        [UnityTest]
        public IEnumerator LookAtIk_TurnsHeadTowardPlayer_AndReleases_WhenWeightZero()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView restful = ById("npc-anim-a");
            NpcAnimatorDriver driver = DriverOf(restful);
            Animator animator = AnimatorOf(restful);
            yield return WaitUntil(
                () => AtRestPoint(restful)
                      && IsBrainAction(restful, NpcUtilityAgent.RestActionName)
                      && driver.LastMeasuredSpeed < KenneyLocomotionPlan.LocomotionSpeedThreshold,
                25f, "NPC 必须先在休息点停稳（Idle 姿态稳定）");

            // 冻结驱动（参数定格在就位值：Speed≈0 → Idle 过渡自行完成），Animator
            // 仍在逐帧求值 —— 头部姿态 = Idle clip + 实时 IK。先冻结再等状态，
            // 避免"恢复后自行切换行为"的窗口竞态。
            restful.ManualDrive = true;
            driver.ManualDrive = true;
            yield return WaitForState(animator, KenneyLocomotionPlan.IdleState, 5f, "进入 Idle");

            Transform head = animator.GetBoneTransform(UnityEngine.HumanBodyBones.Head);
            Assert.IsNotNull(head, "人形 Avatar 必须解析出头骨（Kenney 骨骼为标准人形命名）");

            // 玩家放到 NPC 前方 45°（55° 全权重锥内），3m 远。
            Transform player = GameObject.Find("Player").transform;
            float yaw = driver.transform.eulerAngles.y;
            Vector3 direction = Quaternion.Euler(0f, yaw + 45f, 0f) * Vector3.forward;
            player.position = new Vector3(
                restful.transform.position.x + (direction.x * 3f), 1f,
                restful.transform.position.z + (direction.z * 3f));

            // 视线向量的标定（Kenney 骨骼实测 2026-11-08）：头骨的
            // transform.forward 与身体前方差 ~134°（Blender 骨骼静息轴不与视觉
            // 前方对齐），而 Unity 人形 Look-at 在 human space 里瞄准——因此
            // 断言用"标定视线"= head.rotation × aimInHeadLocal，其中
            // aimInHeadLocal 在 IK 关闭、角色直视前方时从 head.rotation 反解。
            // 该度量下三段可证伪：关(≈46°) → 开(<25°) → 关(>32°)。
            driver.LookAtWeight = 0f;
            yield return WaitRealSeconds(0.3f); // 头部回到纯动画姿态（直视前方）
            Quaternion headRot0 = head.rotation;
            Vector3 bodyFwd0 = driver.transform.forward;
            Quaternion aimInHeadLocal = Quaternion.Inverse(headRot0) * Quaternion.LookRotation(bodyFwd0);

            driver.LookAtWeight = 1f;
            yield return WaitRealSeconds(0.7f); // IK 收敛
            float angleOn = HeadAimAngle(head, aimInHeadLocal, player);
            float baselineHeadFwdOffDeg = Vector3.SignedAngle(driver.transform.forward, head.forward, Vector3.up);
            Debug.Log($"[NpcAnimatorPlayModeTests] IK 诊断: callbacks={driver.LookIkCallbackCount} " +
                      $"yaw={driver.LastLookYawDeg:0.#} weight={driver.LastLookWeight:0.##} " +
                      $"angleOn={angleOn:0.#} headFwd={head.forward:F2} modelFwd={driver.transform.forward:F2} " +
                      $"headPos={head.position:F2} headFwdVsBody={baselineHeadFwdOffDeg:0.#}°");
            Assert.Less(angleOn, 25f,
                $"IK 开启（权重 {driver.LookAtWeight:0.##}）时头部视线必须朝向玩家（实测 {angleOn:0.#}°；" +
                $"callbacks={driver.LookIkCallbackCount} yaw={driver.LastLookYawDeg:0.#} weight={driver.LastLookWeight:0.##}）");

            driver.LookAtWeight = 0f; // 可证伪对照：权重归零，视线应离开目标
            yield return WaitRealSeconds(0.7f);
            float angleOff = HeadAimAngle(head, aimInHeadLocal, player);
            Assert.Greater(angleOff, 32f,
                $"IK 权重归零后头部视线必须离开目标方向（实测 {angleOff:0.#}°）——否则断言对象不是 IK");
            driver.LookAtWeight = 1f;
        }

        /// <summary>
        /// ④ 速度-位移同步：Speed 参数 = 实测位移速度；Run 归一化时间推进率 =
        /// Speed/RunReferenceSpeed。在"合法窗口"内断言：全程处于 Run 且无过渡、
        /// 参数恒定（同一 locomotion 相位——NPC_C 在玩家侧会切 Follow（2.2m/s），
        /// 相位混杂的窗口作废重采，合法窗口内两律必须成立）。
        /// </summary>
        [UnityTest]
        public IEnumerator RunState_SpeedParameter_TracksMeasuredDisplacement_AndDrivesPlayback()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView wayfarer = ById("npc-anim-c");
            Animator animator = AnimatorOf(wayfarer);
            yield return WaitForState(animator, KenneyLocomotionPlan.RunState, 10f, "进入 Run");

            float deadline = Time.time + 20f;
            while (Time.time < deadline)
            {
                if (!IsSettledInState(animator, KenneyLocomotionPlan.RunState))
                {
                    yield return null;
                    continue;
                }

                float param0 = animator.GetFloat(KenneyLocomotionPlan.SpeedParam);
                if (param0 < KenneyLocomotionPlan.LocomotionSpeedThreshold)
                {
                    yield return null; // 过渡刚起步，参数还没立起来
                    continue;
                }

                float t0 = Time.time;
                Vector3 p0 = wayfarer.transform.position;
                float n0 = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                float paramSum = 0f;
                int paramFrames = 0;
                bool valid = true;
                while (Time.time - t0 < 0.5f)
                {
                    yield return null;
                    float param = animator.GetFloat(KenneyLocomotionPlan.SpeedParam);
                    if (!IsSettledInState(animator, KenneyLocomotionPlan.RunState)
                        || Mathf.Abs(param - param0) > 0.05f)
                    {
                        valid = false; // 相位切换/过渡——窗口作废
                        break;
                    }

                    paramSum += param;
                    paramFrames++;
                }

                if (!valid || animator.IsInTransition(0))
                {
                    continue;
                }

                float t1 = Time.time;
                float n1 = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                Vector3 p1 = wayfarer.transform.position;

                float wallSeconds = t1 - t0;
                Vector3 delta = p1 - p0;
                delta.y = 0f;
                float measuredSpeed = delta.magnitude / wallSeconds;
                float avgParam = paramSum / Mathf.Max(1, paramFrames);
                Assert.That(avgParam, Is.EqualTo(measuredSpeed).Within(Mathf.Max(0.3f, measuredSpeed * 0.2f)),
                    $"Speed 参数（均值 {avgParam:0.###}）必须等于实测位移速度（{measuredSpeed:0.###} m/s）");

                // 播放同步律：d(normalizedTime)/dt = state.speed × Speed / clipLength
                // = (Speed/RunReferenceSpeed) / clipLength。clipLength 从资产实测
                // （Kenney 跑循环 ≈0.67s——归一化时间每秒推进 clipLength 的倒数，
                // 不是 1）。可证伪性：若驱动器没写 Speed（参数恒 0），速率恒为 0；
                // 若写死常数，速率与实测位移脱钩——两条都会红。
                float expectedRate = (avgParam / KenneyLocomotionPlan.RunReferenceSpeed) / RunClipLength(animator);
                float observedRate = (n1 - n0) / wallSeconds;
                Assert.That(observedRate, Is.EqualTo(expectedRate).Within(Mathf.Max(0.15f, expectedRate * 0.3f)),
                    $"Run 归一化时间推进率（{observedRate:0.###}/s）必须 ≈ (Speed/RunReferenceSpeed)/clipLength（{expectedRate:0.###}/s）" +
                    "——动画播放速度真的由位移驱动，不是固定速率播片");
                yield break;
            }

            Assert.Fail("20s 内没有采到合法的恒速 Run 窗口（巡逻环线上必然存在）");
        }

        /// <summary>⑤（附加）RequestJump：状态机 Run→Jump→（exit time + grounded）→回 Run，生成过渡全部贯通。</summary>
        [UnityTest]
        public IEnumerator RequestJump_TraversesTheJumpState_AndReturns()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView wayfarer = ById("npc-anim-c");
            Animator animator = AnimatorOf(wayfarer);
            yield return WaitForState(animator, KenneyLocomotionPlan.RunState, 10f, "进入 Run");

            NpcAnimatorDriver driver = DriverOf(wayfarer);
            Assert.IsTrue(driver.RequestJump(), "地面起跳必须被接受（RequestJump 返回 true）");
            yield return WaitForState(animator, KenneyLocomotionPlan.JumpState, 3f, "Jump 触发器必须把状态机带入 Jump");

            yield return WaitUntil(
                () => !animator.GetCurrentAnimatorStateInfo(0).IsName(KenneyLocomotionPlan.JumpState)
                      && !animator.IsInTransition(0),
                6f, "Jump 必须按 exit time + Grounded 离开");
        }

        /// <summary>
        /// ⑥ TASK-A-ANIM-02 V4：起跳后 Y 沿弧线真的抬升（≥ baseY+0.3），T 后
        /// 精确回落到基准（±1e-3），Grounded 由 false 回 true——位移与参数是同一真值。
        /// 变异可证伪：把 Grounded 写回恒 true，本用例的"空中 false"必红。
        /// </summary>
        [UnityTest]
        public IEnumerator Jump_LiftsTheBody_AlongTheArc_AndLandsExactlyOnTheBase()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView wayfarer = ById("npc-anim-c");
            Animator animator = AnimatorOf(wayfarer);
            NpcAnimatorDriver driver = DriverOf(wayfarer);
            yield return WaitForState(animator, KenneyLocomotionPlan.RunState, 10f, "进入 Run");

            float baseY = driver.transform.position.y;
            Assert.IsTrue(driver.RequestJump(), "起跳必须被接受");

            float peakY = baseY;
            bool sawAirborneFlag = false;
            bool sawJumpState = false;
            float deadline = Time.time + 2f;
            while (Time.time < deadline)
            {
                peakY = Mathf.Max(peakY, driver.transform.position.y);
                if (!animator.GetBool(KenneyLocomotionPlan.GroundedParam))
                {
                    sawAirborneFlag = true;
                }

                if (animator.GetCurrentAnimatorStateInfo(0).IsName(KenneyLocomotionPlan.JumpState))
                {
                    sawJumpState = true;
                }

                yield return null;
            }

            float observedPeak = peakY - baseY;
            Debug.Log($"[A-ANIM-02] jump: baseY={baseY:F4} peakY={peakY:F4} rise={observedPeak:F4} " +
                      $"T={KenneyLocomotionPlan.JumpAirTime:F4} landedY={driver.transform.position.y:F4} " +
                      $"lastJumpHeight={driver.LastJumpHeight:F4} grounded={animator.GetBool(KenneyLocomotionPlan.GroundedParam)}");

            Assert.IsTrue(sawJumpState, "起跳必须真的进入 Jump 状态（状态机与位移同源）");
            Assert.IsTrue(sawAirborneFlag, "空中 Grounded 参数必须为 false（变异：写回恒 true 即必红）");
            Assert.GreaterOrEqual(observedPeak, 0.3f,
                $"Y 必须真的抬升 ≥ 0.3 m（实测 {observedPeak:0.###} m）——否则跳跃只是播片");
            Assert.That(observedPeak, Is.EqualTo(KenneyLocomotionPlan.JumpPeakHeight).Within(0.1f),
                $"峰值必须 ≈ JumpPeakHeight（实测 {observedPeak:0.###}）");

            Assert.That(driver.transform.position.y, Is.EqualTo(baseY).Within(1e-3f),
                $"T 后必须精确归位 baseY={baseY:F4}（实测 {driver.transform.position.y:F4}）");
            Assert.IsTrue(animator.GetBool(KenneyLocomotionPlan.GroundedParam), "落地后 Grounded 必须回 true");
            Assert.AreEqual(1, driver.JumpRequestCount, "只接受了一次起跳请求");
        }

        /// <summary>
        /// ⑦ TASK-A-ANIM-02 V5：空中再次 RequestJump 返回 false、IgnoredJumpCount=1、
        /// 弧线不重置（身体继续按原 elapsed 上升至近峰值并正常落地）。
        /// </summary>
        [UnityTest]
        public IEnumerator AirborneJumpRequest_IsIgnored_WithoutResettingTheArc()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView wayfarer = ById("npc-anim-c");
            Animator animator = AnimatorOf(wayfarer);
            NpcAnimatorDriver driver = DriverOf(wayfarer);
            yield return WaitForState(animator, KenneyLocomotionPlan.RunState, 10f, "进入 Run");

            float baseY = driver.transform.position.y;
            Assert.IsTrue(driver.RequestJump(), "地面起跳必须被接受");
            yield return WaitUntil(() => driver.transform.position.y > baseY + 0.2f, 2f, "起跳后 Y 必须上升");

            float yAtRejection = driver.transform.position.y;
            int acceptedBefore = driver.JumpRequestCount;

            Assert.IsFalse(driver.RequestJump(), "空中再次 RequestJump 必须被拒绝（禁止二段跳）");
            Assert.AreEqual(1, driver.IgnoredJumpCount, "被拒绝的请求必须可观测（IgnoredJumpCount=1）");
            Assert.AreEqual(acceptedBefore, driver.JumpRequestCount, "被拒绝的请求不得计入接受数");

            // A reset would zero the elapsed time and drop the body back to the
            // base on the next drive step; the arc must keep climbing instead.
            yield return null;
            yield return null;
            Assert.GreaterOrEqual(driver.transform.position.y, yAtRejection - 0.02f,
                $"被拒后弧线不得重置（拒绝时 {yAtRejection:F3}，两帧后 {driver.transform.position.y:F3}）");
            Assert.GreaterOrEqual(driver.LastJumpHeight, yAtRejection - baseY - 0.02f, "LastJumpHeight 必须沿原弧线继续");

            yield return WaitUntil(
                () => animator.GetBool(KenneyLocomotionPlan.GroundedParam)
                      && Mathf.Abs(driver.transform.position.y - baseY) < 1e-3f,
                2f, "被拒之后仍必须按原定 T 正常落地归位");
        }

        /// <summary>
        /// ⑧ TASK-A-ANIM-02 V6②：站着（Idle，Speed≈0）起跳必须进入 Jump——
        /// 生成器少了 Idle→Jump 这条过渡时本用例必红。冻结平面驱动（不影响
        /// 驱动器的垂直步进），排除脑自行切行为的竞态。
        /// </summary>
        [UnityTest]
        public IEnumerator StandingJump_FromIdle_EntersJumpState()
        {
            yield return LoadSceneAndSmokeCheck();

            NpcUtilityView restful = ById("npc-anim-a");
            NpcAnimatorDriver driver = DriverOf(restful);
            Animator animator = AnimatorOf(restful);
            yield return WaitUntil(
                () => AtRestPoint(restful)
                      && IsBrainAction(restful, NpcUtilityAgent.RestActionName)
                      && driver.LastMeasuredSpeed < KenneyLocomotionPlan.LocomotionSpeedThreshold,
                25f, "NPC 必须先在休息点停稳");

            restful.ManualDrive = true; // 平面冻结：Speed 参数稳定为 0，只剩垂直步进
            yield return null;
            yield return null; // 让驱动器用冻结后的时钟写一次 Speed=0
            yield return WaitForState(animator, KenneyLocomotionPlan.IdleState, 5f, "进入 Idle");

            float baseY = driver.transform.position.y;
            Assert.AreEqual(0f, driver.LastWrittenSpeed, 1e-5f, "站立起跳前提：Speed 参数为 0（Idle）");
            Assert.IsTrue(driver.RequestJump(), "站着起跳必须被接受");

            yield return WaitForState(animator, KenneyLocomotionPlan.JumpState, 3f,
                "Idle→Jump 过渡必须存在——否则站着起跳状态机原地不动");
            yield return WaitUntil(
                () => animator.GetBool(KenneyLocomotionPlan.GroundedParam)
                      && Mathf.Abs(driver.transform.position.y - baseY) < 1e-3f,
                3f, "站立起跳同样必须落地归位");
            yield return WaitForState(animator, KenneyLocomotionPlan.IdleState, 6f, "落地静止 → 回 Idle");
        }

        // ---- helpers -------------------------------------------------------

        private IEnumerator LoadSceneAndSmokeCheck()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name, $"{SceneName} did not activate");
            yield return null;
            yield return null;

            NpcUtilityView[] views = Object.FindObjectsOfType<NpcUtilityView>();
            Assert.AreEqual(2, views.Length, "SCN_Anim 必须恰好两个带动画的效用 NPC");
            foreach (NpcUtilityView view in views)
            {
                view.EnsureAgent(); // 幂等；兜底 Awake 之外的重建

                NpcAnimatorDriver driver = DriverOf(view);
                Assert.IsNotNull(driver, $"{view.NpcId} 必须挂 NpcAnimatorDriver");

                Animator animator = AnimatorOf(view);
                Assert.IsNotNull(animator, $"{view.NpcId} 的模型必须带 Animator");
                Assert.IsNotNull(animator.runtimeAnimatorController, "必须已绑定生成的控制器");
                Assert.That(animator.runtimeAnimatorController.name, Does.Contain("KenneyLocomotion"),
                    "控制器必须是 CHR_KenneyLocomotion（生成产物）");
                Assert.IsNotNull(animator.avatar, "必须有人形 Avatar");
                Assert.IsTrue(animator.avatar.isHuman, "Avatar 必须是人形（Kenney 骨架标准人形命名，自动映射）");
                Assert.IsFalse(animator.applyRootMotion, "根运动必须关闭——变换由 View/脑驱动，clip 为原地循环");

                Renderer renderer = view.GetComponentInChildren<SkinnedMeshRenderer>();
                Assert.IsNotNull(renderer, "模型必须有蒙皮渲染器（真实 Kenney 模型而非胶囊占位）");
                Assert.That(renderer.bounds.size.y, Is.InRange(1.2f, 2.2f),
                    "角色必须被重标定到人形高度（FBX 原始 ×100 导入尺度必须被压回 ~1.75m）");
            }
        }

        private static NpcUtilityView ById(string npcId)
        {
            NpcUtilityView match = System.Array.Find(
                Object.FindObjectsOfType<NpcUtilityView>(),
                v => string.Equals(v.NpcId, npcId, System.StringComparison.Ordinal));
            Assert.IsNotNull(match, $"场景必须包含 id 为 {npcId} 的 NPC");
            return match;
        }

        private static NpcAnimatorDriver DriverOf(NpcUtilityView view)
        {
            return view.GetComponentInChildren<NpcAnimatorDriver>();
        }

        private static Animator AnimatorOf(NpcUtilityView view)
        {
            return DriverOf(view) != null ? DriverOf(view).GetComponent<Animator>() : null;
        }

        private static Vector3 FacingOf(NpcUtilityView view)
        {
            Vector3 forward = DriverOf(view).transform.forward;
            forward.y = 0f;
            return forward.normalized;
        }

        private static bool AtRestPoint(NpcUtilityView view)
        {
            Vec2 position = view.Agent.Position;
            float dx = position.X - RestPointXz.x;
            float dy = position.Y - RestPointXz.y;
            return (dx * dx) + (dy * dy) < 0.25f; // < 0.5 m
        }

        /// <summary>当前巡逻目标点距离 3.5–8m：直道中段（转角在 0.4m 内完成，10m 直线上此窗口必已对齐）。</summary>
        private static bool IsMidLeg(NpcUtilityView view)
        {
            Vec2 position = view.Agent.Position;
            Vector2 target = WaypointsXz[Mathf.Clamp(view.Agent.WaypointIndex, 0, WaypointsXz.Length - 1)];
            float dx = position.X - target.x;
            float dy = position.Y - target.y;
            float distance = Mathf.Sqrt((dx * dx) + (dy * dy));
            return distance > 3.5f && distance < 8f;
        }

        /// <summary>标定视线与目标的夹角：aim = head.rotation × aimInHeadLocal（见用例内注释的标定说明）。</summary>
        private static float HeadAimAngle(Transform head, Quaternion aimInHeadLocal, Transform player)
        {
            Vector3 lookPoint = player.position + Vector3.up * 0.6f;
            Vector3 toTarget = lookPoint - head.position;
            Vector3 aim = head.rotation * aimInHeadLocal * Vector3.forward;
            return Vector3.Angle(aim, toTarget.normalized);
        }

        private static bool IsBrainAction(NpcUtilityView view, string action)
        {
            return string.Equals(view.Agent.CurrentAction, action, System.StringComparison.Ordinal);
        }

        private static bool IsSettledInState(Animator animator, string stateName)
        {
            return animator.GetCurrentAnimatorStateInfo(0).IsName(stateName) && !animator.IsInTransition(0);
        }

        /// <summary>Run 状态绑定的 clip 长度（秒）——从生成的控制器资产实测，不抄常数。</summary>
        private static float RunClipLength(Animator animator)
        {
            var controller = animator.runtimeAnimatorController as AnimatorController;
            Assert.IsNotNull(controller, "控制器必须是 AnimatorController（可读状态图）");
            foreach (ChildAnimatorState child in controller.layers[0].stateMachine.states)
            {
                if (child.state != null && child.state.name == KenneyLocomotionPlan.RunState)
                {
                    var clip = child.state.motion as AnimationClip;
                    Assert.IsNotNull(clip, "Run 状态必须绑定 AnimationClip");
                    Assert.Greater(clip.length, 0.01f, "Run clip 长度必须为正");
                    return clip.length;
                }
            }

            Assert.Fail("控制器里找不到 Run 状态");
            return 1f;
        }

        private static IEnumerator WaitForState(Animator animator, string stateName, float timeoutSeconds, string what)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                if (info.IsName(stateName) && !animator.IsInTransition(0))
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail($"{what}：等待状态 {stateName} 超时 {timeoutSeconds:0.#}s");
        }

        private static IEnumerator WaitUntil(System.Func<bool> predicate, float timeoutSeconds, string what)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (predicate())
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail($"{what}：等待超时 {timeoutSeconds:0.#}s");
        }

        private static IEnumerator WaitRealSeconds(float seconds)
        {
            float deadline = Time.time + seconds;
            while (Time.time < deadline)
            {
                yield return null;
            }
        }
    }
}
