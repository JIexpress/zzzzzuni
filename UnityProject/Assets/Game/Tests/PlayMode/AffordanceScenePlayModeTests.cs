// AffordanceScenePlayModeTests.cs —— TASK-A-AI-04：可供性接场景的场景级证据。
// 口径：SCN_Game 加载后 SceneAffordances 自动登记 Work/Rest/Social 三个锚点（不建
// 任何 Renderer）；手动挂一个 AffordanceSeeker 到探针 NPC 上，它必须朝最近的锚点
// 走过去、到达即占用；Stop 释放且不再移动；不挂 seeker 时场景 NPC 一动不动。
// 全部断言都打真实 SCN_Game（与 SurvivalLoop / NpcAttitude 的场景证据同口径）。

using System.Collections;
using System.Collections.Generic;
using Game.Domain.AI.Affordances;
using Game.Domain.AI.Utility;
using Game.View.AI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.View.PlayMode.Tests
{
    /// <summary>V1-V6：场景登记、会走、占用、停止、默认零影响。</summary>
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
