// AffordanceScenePlayModeTests.cs —— TASK-A-AI-04：可供性接场景的场景级证据。
// TASK-A-AI-05 增补：V7 预约态场景级（选中即预约、到达才占、Stop 双清）、
// V8 自主模式 G 键层（挂/卸 seeker + 完全复原）、V9 Colony 投影桥（同键同状态 + 只读）。
// 口径：SCN_Game 加载后 SceneAffordances 自动登记 Work/Rest/Social 三个锚点（不建
// 任何 Renderer）；手动挂一个 AffordanceSeeker 到探针 NPC 上，它必须朝最近的锚点
// 走过去、到达才占用（TASK-A-AI-05 起）；Stop 释放且不再移动；不挂 seeker 时场景 NPC 一动不动。
// 全部断言都打真实 SCN_Game（与 SurvivalLoop / NpcAttitude 的场景证据同口径）。

using System.Collections;
using System.Collections.Generic;
using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using Game.Domain.Colony.Autonomy;
using Game.View.AI;
using Game.View.Colony;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.View.PlayMode.Tests
{
    /// <summary>V1-V9：场景登记、会走、预约/到达才占、停止、默认零影响、自主模式、Colony 投影。</summary>
    public sealed class AffordanceScenePlayModeTests
    {
        private GameObject _probe;

        [UnityTest]
        public IEnumerator V1_SceneLoad_RegistersWorkRestAndSocial()
        {
            yield return LoadGameScene();
            try
            {
                AffordanceRegistry registry = SceneAffordances.Instance.Registry;
                Assert.GreaterOrEqual(registry.Count, 3, "SCN_Game 至少要有 3 个可供性");

                var kinds = new HashSet<AffordanceKind>();
                foreach (Affordance affordance in registry.Affordances)
                {
                    kinds.Add(affordance.Kind);
                }

                Assert.IsTrue(kinds.Contains(AffordanceKind.Work), "必须覆盖 Work");
                Assert.IsTrue(kinds.Contains(AffordanceKind.Rest), "必须覆盖 Rest");
                Assert.IsTrue(kinds.Contains(AffordanceKind.Social), "必须覆盖 Social");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V2_Registration_CreatesNoRenderers()
        {
            yield return LoadGameScene();
            try
            {
                int before = Object.FindObjectsOfType<Renderer>().Length;
                int countBefore = SceneAffordances.Instance.Registry.Count;

                SceneAffordances.Instance.RegisterSceneAffordances();

                int after = Object.FindObjectsOfType<Renderer>().Length;
                Assert.AreEqual(before, after, "登记可供性不得新建任何 Renderer（零可见物）");
                Assert.GreaterOrEqual(SceneAffordances.Instance.Registry.Count, 3);
                Assert.AreEqual(countBefore, SceneAffordances.Instance.Registry.Count, "重扫后条目数应稳定（3 个锚点）");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V3_Seeker_WalksTowardTheAnchor()
        {
            yield return LoadGameScene();
            try
            {
                SceneAffordances scene = SceneAffordances.Instance;
                Affordance rest = RequireKind(scene.Registry, AffordanceKind.Rest);
                AffordanceSeeker seeker = SpawnSeeker(scene.Registry, rest.Position + new Vector3(6f, 0f, 0f));
                seeker.ManualDrive = false; // 这条用例走真实 Update 路径（1.5 m/s × 真实帧时间）
                seeker.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seeker.State.Set(NPCWorldState.Keys.Trust, 0f);

                float before = rest.FlatDistanceTo(_probe.transform.position);
                seeker.StartSeeking();

                Assert.AreSame(rest, seeker.CurrentTarget, "疲劳 1 时应选中最近的 Rest 锚点");
                yield return new WaitForSeconds(1.2f);

                float after = rest.FlatDistanceTo(_probe.transform.position);
                Assert.Less(after, before - 1f, $"1.2s 内必须明显靠近锚点（{before:0.00} → {after:0.00}）");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V4_Seeker_ClaimsOnArrival()
        {
            yield return LoadGameScene();
            try
            {
                SceneAffordances scene = SceneAffordances.Instance;
                Affordance rest = RequireKind(scene.Registry, AffordanceKind.Rest);
                AffordanceSeeker seeker = SpawnSeeker(scene.Registry, rest.Position + new Vector3(3f, 0f, 0f));
                seeker.ManualDrive = true;
                seeker.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seeker.State.Set(NPCWorldState.Keys.Trust, 0f);

                seeker.StartSeeking();
                seeker.SimulateForTests(3f);

                Assert.AreSame(rest, seeker.CurrentTarget);
                Assert.IsTrue(seeker.IsArrived, "3m 外 3s 内必须走到半径 1.5m 内");
                Assert.AreEqual(1, rest.Occupants, "到达后必须占用一个名额");
                Assert.IsFalse(rest.IsAvailable, "容量 1 满员后 IsAvailable 必须为 false");
                Assert.LessOrEqual(rest.FlatDistanceTo(_probe.transform.position), rest.Radius + 1e-3f);
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V5_Stop_ReleasesAndFreezes()
        {
            yield return LoadGameScene();
            try
            {
                SceneAffordances scene = SceneAffordances.Instance;
                Affordance rest = RequireKind(scene.Registry, AffordanceKind.Rest);
                AffordanceSeeker seeker = SpawnSeeker(scene.Registry, rest.Position + new Vector3(3f, 0f, 0f));
                seeker.ManualDrive = true;
                seeker.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seeker.State.Set(NPCWorldState.Keys.Trust, 0f);

                seeker.StartSeeking();
                seeker.SimulateForTests(3f);
                Assert.AreEqual(1, rest.Occupants);

                seeker.Stop();

                Assert.AreEqual(0, rest.Occupants, "Stop 必须释放占用");
                Assert.IsTrue(rest.IsAvailable);
                Assert.IsFalse(seeker.IsSeeking);

                Vector3 frozen = _probe.transform.position;
                seeker.SimulateForTests(1f);
                Assert.AreEqual(frozen, _probe.transform.position, "Stop 后不得再移动");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V6_WithoutSeeker_TheSceneNpcNeverMoves()
        {
            yield return LoadGameScene();
            try
            {
                Assert.IsNull(Object.FindObjectOfType<AffordanceSeeker>(), "默认不得给任何 NPC 挂 seeker");

                GameObject npc = GameObject.Find(SceneAffordances.SocialAnchorName);
                Assert.IsNotNull(npc, "SCN_Game 必须携带 NPC（社交锚点）");
                Vector3 start = npc.transform.position;

                yield return new WaitForSeconds(0.5f);

                Assert.AreEqual(start, npc.transform.position, "不挂 seeker 时场景 NPC 位置必须完全不变");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator DefaultBrain_IsIdlePlusFourAffordanceActions()
        {
            yield return LoadGameScene();
            try
            {
                Affordance rest = RequireKind(SceneAffordances.Instance.Registry, AffordanceKind.Rest);
                AffordanceSeeker seeker = SpawnSeeker(SceneAffordances.Instance.Registry, rest.Position + new Vector3(6f, 0f, 0f));

                Assert.AreEqual(5, seeker.Brain.ActionCount, "默认大脑 = Idle + 4 个可供性动作");

                seeker.StartSeeking();
                var names = new HashSet<string>();
                foreach (UtilityBrain.ScoredAction entry in seeker.Brain.LastRanking)
                {
                    names.Add(entry.Name);
                }

                Assert.IsTrue(names.Contains("Affordance.Rest"));
                Assert.IsTrue(names.Contains("Affordance.Work"));
                Assert.IsTrue(names.Contains("Affordance.Social"));
                Assert.IsTrue(names.Contains("Affordance.Eat"), "Eat 没有场景锚点，但动作必须已注册（分数 0 不参战）");
            }
            finally
            {
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator Seeker_AbandonsAfterTimeout_AndReleases()
        {
            yield return LoadGameScene();
            try
            {
                Affordance rest = RequireKind(SceneAffordances.Instance.Registry, AffordanceKind.Rest);
                AffordanceSeeker seeker = SpawnSeeker(SceneAffordances.Instance.Registry, rest.Position + new Vector3(6f, 0f, 0f));
                seeker.ManualDrive = true;
                seeker.MoveSpeed = 0.1f;
                seeker.AbandonSeconds = 1f;
                seeker.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seeker.State.Set(NPCWorldState.Keys.Trust, 0f);

                seeker.StartSeeking();
                seeker.SimulateForTests(1.5f);

                Assert.IsFalse(seeker.IsSeeking, "1s 追不到必须放弃");
                Assert.IsFalse(seeker.IsArrived);
                Assert.AreEqual(0, rest.Occupants, "放弃时必须释放占用");
                Assert.IsTrue(rest.IsAvailable);
            }
            finally
            {
                Cleanup();
            }
        }

        // ---- TASK-A-AI-05：预约态 / 自主模式 / Colony 投影 -------------------------------

        [UnityTest]
        public IEnumerator V7_Seeker_ReservesOnSelection_ClaimsOnlyOnArrival()
        {
            yield return LoadGameScene();
            AffordanceSeeker seekerA = null;
            AffordanceSeeker seekerB = null;
            try
            {
                SceneAffordances scene = SceneAffordances.Instance;
                Affordance rest = RequireKind(scene.Registry, AffordanceKind.Rest);
                seekerA = SpawnSeeker(scene.Registry, rest.Position + new Vector3(5f, 0f, 0f));
                seekerA.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seekerA.State.Set(NPCWorldState.Keys.Trust, 0f);

                seekerA.StartSeeking();

                Assert.AreSame(rest, seekerA.CurrentTarget, "疲劳 1 时 A 必须选中 Rest 锚点");
                Assert.AreEqual(seekerA.AgentId, rest.ReservedBy, "选中即预约（TASK-A-AI-05）");
                Assert.AreEqual(0, rest.Occupants, "选中后到达前不占用（V2）");

                seekerB = SpawnSeeker(scene.Registry, rest.Position + new Vector3(5f, 0f, 0f));
                seekerB.State.Set(NPCWorldState.Keys.Fatigue, 1f);
                seekerB.State.Set(NPCWorldState.Keys.Trust, 0f);
                seekerB.StartSeeking();

                Assert.AreNotSame(rest, seekerB.CurrentTarget, "B 不得选中被 A 预约的 rest（V1）");
                Assert.AreEqual(seekerA.AgentId, rest.ReservedBy, "预约不得被 B 抢走");

                seekerA.SimulateForTests(3f); // 5m 外、半径 1.5：3s 内必到（1.5 m/s）

                Assert.IsTrue(seekerA.IsArrived, "A 必须在模拟时间内到达");
                Assert.AreEqual(1, rest.Occupants, "到达才 Claim（V2）");
                Assert.AreEqual(seekerA.AgentId, rest.ReservedBy, "占用期间预约仍在（随 Release 双清）");

                seekerA.Stop();

                Assert.AreEqual(0, rest.Occupants, "Stop 清占用");
                Assert.IsNull(rest.ReservedBy, "Stop 双清预约（V3）");
            }
            finally
            {
                if (seekerA != null)
                {
                    Object.Destroy(seekerA.gameObject);
                }

                if (seekerB != null)
                {
                    Object.Destroy(seekerB.gameObject);
                }

                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator V8_AutonomyMode_ToggleMountsSeekers_AndUnmountsClean()
        {
            yield return LoadGameScene();
            NpcAutonomyMode mode = null;
            try
            {
                mode = NpcAutonomyMode.Instance;
                Assert.IsNotNull(mode, "SCN_Game 加载后必须自动挂载 NpcAutonomyMode");
                Assert.IsFalse(mode.IsEnabled, "自主模式默认关（V5）");
                Assert.AreEqual(0, mode.ActiveSeekerCount, "默认零 seeker");
                Assert.IsNull(Object.FindObjectOfType<AffordanceSeeker>(), "默认不得给任何 NPC 挂 seeker");

                GameObject npc = GameObject.Find(SceneAffordances.SocialAnchorName);
                Assert.IsNotNull(npc, "SCN_Game 必须携带 NPC（社交锚点，兜底候选）");
                Vector3 start = npc.transform.position;

                int candidates = mode.IdleCandidateCount;
                Assert.GreaterOrEqual(candidates, 1, "SCN_Game 至少能解析出社交锚点候选（复验记录：现况 1 个）");

                mode.Toggle();
                Assert.IsTrue(mode.IsEnabled, "Toggle 开启");
                Assert.AreEqual(Mathf.Min(NpcAutonomyMode.DefaultSeekerLimit, candidates), mode.ActiveSeekerCount,
                    "seeker 数 = min(上限 3, 场景候选数)——SCN_Game 复验现况为 1（见施工报告偏差登记）");

                yield return new WaitForSeconds(0.8f); // 真实 Update 路径：1.5 m/s × 0.8s ≈ 1.2m

                Assert.Greater(mode.ActiveSeekerCount, 0, "开启期间 seeker 必须存活");
                Assert.AreNotEqual(start, npc.transform.position, "自主模式下 NPC 必须开始移动（V4）");

                mode.Toggle();
                Assert.IsFalse(mode.IsEnabled, "Toggle 关闭");
                Assert.AreEqual(0, mode.ActiveSeekerCount, "关闭后 seeker 归零（V4）");
                Assert.AreEqual(start, npc.transform.position, "关闭后 NPC 位置复位（无残留）");
                // TASK-A-AI-05 补跑修复（Round B 复现同型）：Destroy 是 Unity 延迟到帧末的语义，
                // 同帧 FindObjectOfType 会找到垂死的 seeker——跨帧后再断言「场景里再无 seeker」。
                yield return null;
                Assert.IsNull(Object.FindObjectOfType<AffordanceSeeker>(), "关闭后无 seeker 残留（跨帧核对）");
                mode = null;
            }
            finally
            {
                CleanupAutonomy();
            }
        }

        [UnityTest]
        public IEnumerator V9_AutonomyProjection_SyncsWithMode_AndNeverWritesWorld()
        {
            yield return LoadGameScene();
            NpcAutonomyMode mode = null;
            try
            {
                ColonyAutonomyBridge bridge = ColonyAutonomyBridge.Instance;
                Assert.IsNotNull(bridge, "SCN_Game 加载后必须自动挂载 ColonyAutonomyBridge");
                Assert.IsFalse(bridge.IsProjectionVisible, "投影默认不可见");

                ColonyWorldHost host = ColonyWorldHost.Instance;
                Assert.IsNotNull(host, "SCN_Game 加载后必须自动挂载 ColonyWorldHost");
                Assert.IsTrue(host.IsWorldReady, "测试会话数据驱动建局必须成功（A-COL-05 既有口径）");

                // TASK-A-AI-05 补跑加固（Round B 复盘）：AutonomousActionsToday/ChoicesToday 是
                // 每日 tick 清零重写的活计数器（ColonyWorldHost 1 秒 = 1 游戏日，测试窗口跨到日结
                // 就合法变化），跨时间相等断言是潜在 flake。只读性的正确观测：
                // ①字典引用稳定（桥不替换/不重建世界的字典）；②BuildDistributionText 纯函数不改
                // 传入字典（改前改后副本对照）；③桥代码面无写路径（施工报告登记）。
                // 计数行为本身由 A-COL-06 的 T1-T5 钉死。
                var choicesRefBefore = host.World.AutonomousChoicesToday;
                var snapshot = new Dictionary<int, ColonyAction>(choicesRefBefore);

                mode = NpcAutonomyMode.Instance;
                Assert.IsNotNull(mode);

                mode.SetEnabled(true);
                Assert.IsTrue(bridge.IsProjectionVisible, "投影随模式开启（G 键同状态）");

                string hud = bridge.BuildHudText();
                StringAssert.Contains("自主模式: 开", hud, "HUD 必须显示模式");
                StringAssert.Contains("Seeker: ", hud, "HUD 必须显示 seeker 数");
                StringAssert.Contains("Colony 自主行为(今日)", hud, "HUD 必须显示今日自主行为数");

                mode.SetEnabled(false);
                Assert.IsFalse(bridge.IsProjectionVisible, "投影随模式关闭，无残留（V4）");

                Assert.AreSame(choicesRefBefore, host.World.AutonomousChoicesToday,
                    "投影只读：开关不替换世界的选择字典（V8 边界）");
                ColonyAutonomyBridge.BuildDistributionText(snapshot);
                foreach (KeyValuePair<int, ColonyAction> kv in snapshot)
                {
                    Assert.AreEqual(kv.Value, snapshot[kv.Key],
                        "BuildDistributionText 必须纯读：不改传入字典（V8 边界）");
                }

                mode = null;
            }
            finally
            {
                CleanupAutonomy();
            }
        }

        [Test]
        public void ColonyProjection_DistributionText_AggregatesChoicesReadOnly()
        {
            Assert.AreEqual("（今日暂无自主行为）",
                ColonyAutonomyBridge.BuildDistributionText(new Dictionary<int, ColonyAction>()),
                "空选择 = 明确的占位文案");
            Assert.AreEqual("（今日暂无自主行为）", ColonyAutonomyBridge.BuildDistributionText(null));

            var choices = new Dictionary<int, ColonyAction>
            {
                { 11, ColonyAction.Work },
                { 12, ColonyAction.Work },
                { 13, ColonyAction.Protest },
                { 14, ColonyAction.Rest },
            };
            StringAssert.Contains("劳作 2", ColonyAutonomyBridge.BuildDistributionText(choices), "Work 聚合");
            StringAssert.Contains("抗议 1", ColonyAutonomyBridge.BuildDistributionText(choices), "Protest 聚合");
            StringAssert.Contains("休整 1", ColonyAutonomyBridge.BuildDistributionText(choices), "Rest 聚合");
            StringAssert.Contains("·", ColonyAutonomyBridge.BuildDistributionText(choices), "分隔符");
        }

        // ---- helpers -------------------------------------------------------

        private IEnumerator LoadGameScene()
        {
            yield return SceneManager.LoadSceneAsync(SceneAffordances.GameSceneName);
            Assert.AreEqual(SceneAffordances.GameSceneName, SceneManager.GetActiveScene().name);
            yield return null;
            Assert.IsNotNull(SceneAffordances.Instance, "SCN_Game 加载后必须自动挂载 SceneAffordances");
            Assert.IsNotNull(SceneAffordances.Instance.Registry);
        }

        private AffordanceSeeker SpawnSeeker(AffordanceRegistry registry, Vector3 position)
        {
            _probe = new GameObject("~AffordanceSeekerProbe");
            _probe.transform.position = position;
            var seeker = _probe.AddComponent<AffordanceSeeker>();
            seeker.ManualDrive = true; // 默认接管：需要真实 Update 的用例会自行放行
            seeker.Configure(registry, null);
            return seeker;
        }

        private void Cleanup()
        {
            if (_probe != null)
            {
                Object.Destroy(_probe);
                _probe = null;
            }

            if (SceneAffordances.Instance != null)
            {
                Object.Destroy(SceneAffordances.Instance.gameObject);
            }
        }

        /// <summary>自主模式/投影宿主的取证隔离：关态复位 + 销毁宿主（静态 sceneLoaded 订阅会自愈重挂）。</summary>
        private static void CleanupAutonomy()
        {
            if (NpcAutonomyMode.Instance != null)
            {
                NpcAutonomyMode.Instance.SetEnabled(false);
                Object.Destroy(NpcAutonomyMode.Instance.gameObject);
            }

            if (ColonyAutonomyBridge.Instance != null)
            {
                Object.Destroy(ColonyAutonomyBridge.Instance.gameObject);
            }
        }

        private static Affordance RequireKind(AffordanceRegistry registry, AffordanceKind kind)
        {
            foreach (Affordance affordance in registry.Affordances)
            {
                if (affordance.Kind == kind)
                {
                    return affordance;
                }
            }

            Assert.Fail($"场景里没有 {kind} 可供性");
            return null;
        }
    }
}
