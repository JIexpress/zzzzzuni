// UtilityNpcScenePlayModeTests.cs —— TASK-A-TECH-04：效用 AI 第二阶段的场景级证据。
// SCN_Utility 里五个 NPC 同一出生点 (0,1,0)、同一大脑代码、同一世界、同一起始状态，
// 唯一差异是 CFG_NpcPersonality 表的个性权重行（balanced/sociable/curious/brave/lethargic）。
// 断言四件事：
// ① 行为分化：同状态 600 拍逐拍序列两两不同（个性权重分化，可证伪：不分化即红）；
// ② GOAP 两步链：单步修理不可行（无工具）→ 规划器产出 [TakeTool → RepairDevice]，
//    NPC 真实走完 工具箱 → 损坏装置 的空间顺序，装置反激活（全局修复事实回流）；
// ③ 权重门控：同一疲劳泵入后，五个 NPC 按各自权重选出不同行为（手算锚点）；
// ④ 截图按 A-R9-15 口径：离屏探针相机 + RenderTexture + ReadPixels + PNG 落
//    Docs/Parity/utility_personality_*.png（仅图片证据，不做像素差分断言）。

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.View.Npc;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Gameplay.AI;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>SCN_Utility 五个性 NPC 的分化 + GOAP 两步链 + 权重门控 + 截图证据。</summary>
    public sealed class UtilityNpcScenePlayModeTests
    {
        private const string SceneName = "SCN_Utility";
        private static readonly string[] ExpectedNpcIds =
        {
            "npc-a", "npc-b", "npc-c", "npc-d", "npc-e",
        };
        private static readonly string[] ExpectedPersonalityIds =
        {
            "balanced", "sociable", "curious", "brave", "lethargic",
        };

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

        // ---- ① 个性分化：同起点同状态同世界，600 拍两两不同 ----

        [UnityTest]
        public IEnumerator SameStart_FivePersonalityNpcs_PairwiseDivergentSequences()
        {
            yield return LoadSceneAndCollectViews();
            AssertSameStart();
            foreach (NpcUtilityView view in _views)
            {
                Assert.AreEqual(ExpectedPersonalityIds[IdIndex(view.NpcId)], view.PersonalityDef.Id,
                    $"{view.NpcId} 必须绑定 CFG_NpcPersonality 行（配置驱动，非代码硬编码）");
                view.ManualDrive = true; // 确定性驱动：屏蔽真实帧率的 Update
            }

            // 按身份落位而非 FindObjectsOfType 顺序（后者不保证 npc-a 在前）。
            var sequences = new List<string>[ExpectedNpcIds.Length];
            foreach (NpcUtilityView view in _views)
            {
                sequences[IdIndex(view.NpcId)] = new List<string>();
            }

            // 60s 模拟时间（600 × 0.1s）：手算分化窗口最晚的一对在 ~220 拍岔开（见施工报告 2.5）。
            for (int tick = 0; tick < 600; tick++)
            {
                foreach (NpcUtilityView view in _views)
                {
                    sequences[IdIndex(view.NpcId)].Add(view.Step(0.1f));
                }

                if (tick % 25 == 0)
                {
                    yield return null;
                }
            }

            // 首拍锚点（手算）：balanced/brave/lethargic → Repair（中立职责 0.9），
            // sociable → Follow（0.7×2=1.4），curious → Pickup（0.85×2=1.7）。
            Assert.AreEqual("Repair", sequences[0][0], "balanced：修理 0.9 压过捡取 0.85");
            Assert.AreEqual("Follow", sequences[1][0], "sociable：社交 2.0 → 跟随登顶");
            Assert.AreEqual("Pickup", sequences[2][0], "curious：好奇 2.0 → 捡取登顶");
            Assert.AreEqual("Repair", sequences[3][0], "brave：修理职责 0.9 先行，修复后转巡逻");
            Assert.AreEqual("Repair", sequences[4][0], "lethargic：修理职责 0.9 先行，后续休息接管");

            for (int i = 0; i < ExpectedNpcIds.Length; i++)
            {
                for (int j = i + 1; j < ExpectedNpcIds.Length; j++)
                {
                    Assert.IsFalse(sequences[i].SequenceEqual(sequences[j]),
                        $"同状态同世界，{ExpectedNpcIds[i]} 与 {ExpectedNpcIds[j]} 的 600 拍行为序列必须两两不同");
                }
            }

            yield return CaptureScreenshot("utility_personality_divergence.png");
        }

        // ---- ② GOAP 两步链：单步不可行 → [取工具 → 修理] → 空间顺序实证 ----

        [UnityTest]
        public IEnumerator GoapRepairChain_TwoSteps_PlannedAndExecutedInScene()
        {
            yield return LoadSceneAndCollectViews();
            GameObject device = GameObject.Find("BrokenDevice");
            Assert.IsNotNull(device, "场景必须携带损坏装置");
            GameObject toolbox = GameObject.Find("Toolbox");
            Assert.IsNotNull(toolbox, "场景必须携带工具箱");

            var plans = new Dictionary<string, GoapPlan>();
            var repairers = new List<string>();
            foreach (NpcUtilityView view in _views)
            {
                NpcUtilityView captured = view;
                view.Agent.ChainPlanned += p => plans[captured.NpcId] = p;
                view.Agent.DeviceRepaired += _ =>
                {
                    if (!repairers.Contains(captured.NpcId))
                    {
                        repairers.Add(captured.NpcId);
                    }
                };

                // 防御：万一接管前的真实帧已触发过首拍规划（链条在途、不再发事件），
                // 这里把链条显式清零重挂——订阅生效后由受控驱动真实重规划，事件必被捕获。
                view.Agent.SetDeviceBroken(false);
                view.Agent.SetDeviceBroken(true);
            }

            var trajectories = new Dictionary<string, List<Vector3>>();
            foreach (NpcUtilityView view in _views)
            {
                trajectories[view.NpcId] = new List<Vector3> { view.transform.position };
            }

            bool midChainCaptured = false;
            for (int tick = 0; tick < 400; tick++)
            {
                foreach (NpcUtilityView view in _views)
                {
                    view.Step(0.1f);
                    trajectories[view.NpcId].Add(view.transform.position);
                }

                if (!midChainCaptured && _views.Exists(v => v.Agent.HasTool))
                {
                    // 恰在"已取工具、正走向装置"的链中段取证。
                    yield return CaptureScreenshot("utility_personality_goap_chain.png");
                    midChainCaptured = true;
                }

                if (!device.activeInHierarchy)
                {
                    break;
                }

                if (tick % 25 == 0)
                {
                    yield return null;
                }
            }

            Assert.IsFalse(device.activeInHierarchy, "400 拍内装置必须被两步链修复（反激活 = 全局修复事实）");
            Assert.IsNotEmpty(repairers, "必须有 NPC 触发 DeviceRepaired");
            Assert.IsNotEmpty(plans, "至少一个 NPC 必须产出规划");
            Assert.IsTrue(repairers.TrueForAll(id => plans.ContainsKey(id)),
                "每个修复者都必须先规划后执行（谁调谁：Utility 选意图 → GOAP 规划 → 链执行）");

            foreach (KeyValuePair<string, GoapPlan> entry in plans)
            {
                Assert.AreEqual(2, entry.Value.StepCount, "单步不可行（无工具）→ 每份计划必须恰好两步");
                Assert.AreEqual("TakeTool -> RepairDevice", entry.Value.Format(),
                    "规划器必须产出 [取工具 → 修理] 两步链");
            }

            // 空间顺序：每个修复者的轨迹都必须先到工具箱、再到装置。
            foreach (string repairerId in repairers)
            {
                AssertFirstToolboxThenDevice(trajectories[repairerId], toolbox.transform.position,
                    device.transform.position, repairerId);
            }
        }

        /// <summary>轨迹断言：先进入工具箱半径、后进入装置半径（两步链的空间顺序证据）。
        /// 用水平(XZ)距离——NPC transform 的 Y 恒为出生高度 1，而工具箱/装置贴地
        /// (Y=0.35/0.3)，3D 距离永远含 ≥0.42 的 Y 分量，会恒红。</summary>
        private static void AssertFirstToolboxThenDevice(List<Vector3> path, Vector3 toolbox, Vector3 device,
            string repairerId)
        {
            int toolboxTick = path.FindIndex(p => HorizontalDistance(p, toolbox) <= 0.6f);
            int deviceTick = path.FindIndex(p => HorizontalDistance(p, device) <= 0.6f);
            Assert.GreaterOrEqual(toolboxTick, 0, $"{repairerId} 轨迹必须到过工具箱");
            Assert.GreaterOrEqual(deviceTick, 0, $"{repairerId} 轨迹必须到过装置");
            Assert.Less(toolboxTick, deviceTick, $"{repairerId} 必须先取工具后修理（两步链顺序）");
        }

        // ---- ③ 权重门控：同一疲劳状态泵入 → 五个 NPC 各自不同选择（手算锚点）----

        [UnityTest]
        public IEnumerator FatiguePump_SameStateFiveDifferentChoices_ByWeights()
        {
            yield return LoadSceneAndCollectViews();
            foreach (NpcUtilityView view in _views)
            {
                view.ManualDrive = true;
            }

            for (int i = 0; i < 10; i++) // 1s 预跑：全员仍在出生点附近，装置仍损坏
            {
                foreach (NpcUtilityView view in _views)
                {
                    view.Step(0.1f);
                }
            }

            // 同一状态泵入：疲劳 0.9 → Rest 欲望 = Ramp(0.9, 0.35, 0.9)=1.0 × 各自 fatigueWeight。
            foreach (NpcUtilityView view in _views)
            {
                view.Agent.SetFatigue(0.9f);
            }

            var choices = new Dictionary<string, string>();
            foreach (NpcUtilityView view in _views)
            {
                choices[view.NpcId] = view.Step(0.1f);
            }

            // 手算（施工报告 2.6）：疲劳 0.9 时 Rest=1×w_f、Repair=0.9、
            // sociable 的 Follow=0.7×0.695×2≈0.97、curious 的 Pickup=1.7、brave 的 Patrol=0.06。
            Assert.AreEqual("Rest", choices["npc-a"], "balanced（疲劳 1.0）：Rest 1.0 > Repair 0.9");
            Assert.AreEqual("Follow", choices["npc-b"], "sociable（疲劳 0.5）：Follow ≈0.97 > Rest 0.5");
            Assert.AreEqual("Pickup", choices["npc-c"], "curious（疲劳 0.5）：Pickup 1.7 > Rest 0.5");
            Assert.AreEqual("Repair", choices["npc-d"], "brave（疲劳 0.5）：Repair 0.9 > Rest 0.5 > Patrol 0.06");
            Assert.AreEqual("Rest", choices["npc-e"], "lethargic（疲劳 2.0）：Rest 2.0 登顶");
            Assert.AreEqual(4, new HashSet<string>(choices.Values).Count, "同一状态五个选择必须落到四种行为（权重门控分化）");
        }

        // ---- helpers -------------------------------------------------------

        private static int IdIndex(string npcId)
        {
            return System.Array.IndexOf(ExpectedNpcIds, npcId);
        }

        private IEnumerator LoadSceneAndCollectViews()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name, $"{SceneName} did not activate");

            // 场景激活后立刻接管：抢在下一次 MonoBehaviour.Update 之前置 ManualDrive，
            // 不让任何真实帧先跑（真实帧里 Repair 首拍就会规划，ChainPlanned 事件会丢）。
            CollectAndTakeOver();
            yield return null;
            yield return null;
            CollectAndTakeOver(); // 幂等复收：防个别视图晚一帧激活
        }

        private void CollectAndTakeOver()
        {
            NpcUtilityView[] found = Object.FindObjectsOfType<NpcUtilityView>();
            Assert.AreEqual(5, found.Length, "场景必须恰好五个 NpcUtilityView（TASK-A-TECH-04 五个性演示）");
            _views.Clear();
            _views.AddRange(found);
            foreach (NpcUtilityView view in _views)
            {
                view.EnsureAgent(); // 幂等；PlayMode 下 Awake 已建，兜底
                view.ManualDrive = true; // 确定性接管
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
