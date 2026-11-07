// UtilityNpcScenePlayModeTests.cs —— TASK-A-TECH-01：效用 AI 切片的场景级证据。
// SCN_Utility 里三个 NPC 同一出生点 (0,1,0)、同一大脑代码、同一世界，仅序列化的
// 起始状态不同（A 疲劳+恋休息 / B 恋捡取 / C 恋巡逻）。断言三件事：
// ① 行为分化：三种互不相同的当前行为（Rest / Pickup / Patrol）；
// ② 同起点不同行为路径：两两平面距离 > 2m（可证伪：不分化即红）；
// ③ 时间维度自主性：NPC_A 在休息点恢复后自行切换到捡取（历史序列断言）。
// 截图按 A-R9-15 口径：离屏探针相机 + RenderTexture + ReadPixels + PNG 落
// Docs/Parity/（仅图片证据，不做像素差分断言）。

using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.View.Npc;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>SCN_Utility 三个效用 NPC 的分化 + 分路 + 时间自主性 + 截图证据。</summary>
    public sealed class UtilityNpcScenePlayModeTests
    {
        private const string SceneName = "SCN_Utility";

        private readonly List<NpcUtilityView> _views = new List<NpcUtilityView>();

        [SetUp]
        public void SetUp()
        {
            _views.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _views.Clear();
        }

        [UnityTest]
        public IEnumerator SameStart_ThreeNpcs_DivergeIntoDistinctBehaviorsAndPaths()
        {
            yield return LoadSceneAndCollectViews();
            Assert.AreEqual(3, _views.Count, "SCN_Utility 必须携带三个效用 NPC");
            AssertSameStart();
            foreach (NpcUtilityView view in _views)
            {
                view.ManualDrive = true; // 确定性驱动：屏蔽真实帧率的 Update
            }

            // 驱动 2.5s 模拟时间（25 × 0.1s）。
            for (int i = 0; i < 25; i++)
            {
                foreach (NpcUtilityView view in _views)
                {
                    view.Step(0.1f);
                }

                if (i % 5 == 0)
                {
                    yield return null;
                }
            }

            NpcUtilityView a = ById("npc-a");
            NpcUtilityView b = ById("npc-b");
            NpcUtilityView c = ById("npc-c");
            Assert.AreEqual("Rest", a.Agent.CurrentAction, "疲劳 0.95 + 恋休息 → 去休息");
            Assert.AreEqual("Pickup", b.Agent.CurrentAction, "看见掉落物 + 恋捡取 → 去捡");
            Assert.AreEqual("Patrol", c.Agent.CurrentAction, "恋巡逻 → 走路点环线");
            Assert.AreEqual(3, new HashSet<string>
            {
                a.Agent.CurrentAction,
                b.Agent.CurrentAction,
                c.Agent.CurrentAction,
            }.Count, "同起点同世界，三种行为必须互不相同（分化证据）");

            AssertPairwiseSeparation(a, b, c, 2f, "同起点不同行为路径：2.5s 后两两距离必须 > 2m");

            yield return CaptureScreenshot("utility_tech01_divergence.png");
        }

        [UnityTest]
        public IEnumerator TiredNpc_Rests_ThenSwitchesOnItsOwn_WhenRecovered()
        {
            yield return LoadSceneAndCollectViews();
            foreach (NpcUtilityView view in _views)
            {
                view.ManualDrive = true;
            }

            NpcUtilityView a = ById("npc-a");
            Assert.AreEqual("Rest", a.Agent.Tick(0.1f), "起点疲劳 0.95：第一拍即选休息");
            Assert.AreEqual("Rest", a.SelectionHistory[0], "历史序列必须以 Rest 开头");

            float fatigueAtSwitch = -1f;
            string switchedTo = null;
            for (int i = 0; i < 150; i++) // 15s：走回休息点(~4.8s) + 恢复(~1.7s) 远在窗口内
            {
                foreach (NpcUtilityView view in _views)
                {
                    view.Step(0.1f);
                }

                if (fatigueAtSwitch < 0f && !string.Equals(a.Agent.CurrentAction, "Rest", System.StringComparison.Ordinal))
                {
                    fatigueAtSwitch = a.Agent.Fatigue;
                    switchedTo = a.Agent.CurrentAction;
                }

                if (i % 10 == 0)
                {
                    yield return null;
                }
            }

            Assert.GreaterOrEqual(fatigueAtSwitch, 0f, "15s 内必须自行切换行为（无人改动任何权重/状态泵）");
            // 手算切换点：NPC_A 恋休息 1.6 倍，休息欲望 = 1.6·Ramp(f, 0.35, 0.9)；玩家远端跟随欲望封顶 0.7。
            // 休息欲望跌破 0.7 ⇔ f = 0.35 + 0.55·(0.7/1.6) ≈ 0.591 —— 这是曲线设计决定的切换机制点
            // （疲劳 ≥0.9 时休息欲望饱和 1.6，任何其他行动都无法竞争，故切换必然发生在恢复途中）。
            // 上限 0.65 = 手算点 + 余量，同时仍证明净恢复 ≥0.30（起点 0.95 饱和到 1.0）——
            // 若切换发生在饱和期（恢复失效/权重回归），断言必红。原简报版 0.5 阈值错锁了机制点（见施工报告 2.3-1）。
            Assert.Less(fatigueAtSwitch, 0.65f, "切换时的疲劳必须已恢复到跟随接管点（≈0.59）附近——证明切换由恢复驱动而非饱和期被顶替");
            Assert.IsFalse(string.IsNullOrEmpty(switchedTo)
                           || string.Equals(switchedTo, "Rest", System.StringComparison.Ordinal),
                "切换后的必须是真实的非休息行为（此刻掉落物可能已被 NPC_B 捡完，故不锁死具体名字）");
            Assert.AreEqual("Rest", a.SelectionHistory[0], "历史序列开头必须是 Rest");
            Assert.GreaterOrEqual(a.SelectionHistory.Count, 2, "历史序列必须记录到切换");

            yield return CaptureScreenshot("utility_tech01_switch.png");
        }

        // ---- helpers -------------------------------------------------------

        private IEnumerator LoadSceneAndCollectViews()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name, $"{SceneName} did not activate");
            yield return null;
            yield return null;

            NpcUtilityView[] found = Object.FindObjectsOfType<NpcUtilityView>();
            Assert.AreEqual(3, found.Length, "场景必须恰好三个 NpcUtilityView");
            _views.AddRange(found);
            foreach (NpcUtilityView view in _views)
            {
                view.EnsureAgent(); // 幂等；PlayMode 下 Awake 已建，兜底
                view.ManualDrive = true; // 尽早接管，最多放过一个真实帧的位移
            }
        }

        private void AssertSameStart()
        {
            foreach (NpcUtilityView view in _views)
            {
                Vector3 position = view.transform.position;
                Assert.That(position.x, Is.EqualTo(0f).Within(0.5f), $"{view.NpcId} 必须出生在原点附近");
                Assert.That(position.z, Is.EqualTo(0f).Within(0.5f), $"{view.NpcId} 必须出生在原点附近");
            }

            for (int i = 0; i < _views.Count; i++)
            {
                for (int j = i + 1; j < _views.Count; j++)
                {
                    float distance = HorizontalDistance(_views[i].transform.position, _views[j].transform.position);
                    Assert.Less(distance, 0.5f, $"接管前 {_views[i].NpcId}/{_views[j].NpcId} 仍是同一起点");
                }
            }
        }

        private NpcUtilityView ById(string npcId)
        {
            NpcUtilityView match = _views.Find(v => string.Equals(v.NpcId, npcId, System.StringComparison.Ordinal));
            Assert.NotNull(match, $"场景必须包含 id 为 {npcId} 的 NPC");
            return match;
        }

        private static void AssertPairwiseSeparation(NpcUtilityView a, NpcUtilityView b, NpcUtilityView c, float minMeters, string message)
        {
            float ab = HorizontalDistance(a.transform.position, b.transform.position);
            float bc = HorizontalDistance(b.transform.position, c.transform.position);
            float ac = HorizontalDistance(a.transform.position, c.transform.position);
            Assert.Greater(ab, minMeters, $"{message} (A-B={ab:0.00}m)");
            Assert.Greater(bc, minMeters, $"{message} (B-C={bc:0.00}m)");
            Assert.Greater(ac, minMeters, $"{message} (A-C={ac:0.00}m)");
        }

        private static float HorizontalDistance(Vector3 p, Vector3 q)
        {
            float dx = p.x - q.x;
            float dz = p.z - q.z;
            return Mathf.Sqrt((dx * dx) + (dz * dz));
        }

        /// <summary>A-R9-15 同口径：探针相机渲到 RenderTexture，渲染循环出帧后 ReadPixels 落 PNG。</summary>
        private static IEnumerator CaptureScreenshot(string fileName)
        {
            const int width = 480;
            const int height = 270;

            var rig = new GameObject("~UtilityScreenshotRig");
            var camGo = new GameObject("~UtilityScreenshotCamera");
            camGo.transform.SetParent(rig.transform, false);
            camGo.transform.position = new Vector3(0f, 13f, -9.5f);
            camGo.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -12.5f, 10f).normalized);
            Camera camera = camGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 80f;
            camera.fieldOfView = 60f;

            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            target.Create();
            camera.targetTexture = target;
            camera.enabled = true;

            yield return null; // 渲染循环真实出帧（URP：不用 camera.Render()）

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = previous;

            byte[] png = tex.EncodeToPNG();
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Docs", "Parity"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, png);

            Object.Destroy(tex);
            camera.targetTexture = null;
            target.Release();
            Object.Destroy(target);
            Object.Destroy(rig);

            Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length > 1000, "截图必须真实落盘且非空");
            Debug.Log($"[UtilityNpcScenePlayModeTests] wrote pixel evidence: {path} ({width}x{height})");
        }
    }
}
