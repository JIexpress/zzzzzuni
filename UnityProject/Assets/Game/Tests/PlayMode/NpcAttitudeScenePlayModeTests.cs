// NpcAttitudeScenePlayModeTests.cs —— TASK-A-AI-01：态度层的场景级证据。
// SCN_Utility 里五个 CFG 个性 NPC 接收【同一场】玩家攻击（同型 AttackedBy、同效价、
// 同来源、同拍写入），唯一差异是个性行派生的态度聚合系数（勇敢 ⇒ 低恐惧/高愤怒）。
// 断言三件事：
// ① 同事件异反应：lethargic/sociable/curious/balanced（勇敢 0.5 或 1.0）→ Flee，
//    brave（勇敢 2.0）→ Counterattack——个性×态度=行为的核心场景证据；
// ② 调制持续且互斥：10 拍窗口内勇敢者绝不逃跑、恐惧者绝不反击；
// ③ 截图按 A-R9-15 口径：离屏探针相机 + RenderTexture + ReadPixels + PNG 落
//    Docs/Parity/npc_attitude_divergence.png（仅图片证据，不做像素差分断言）。

using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Domain.AI.Memory;
using Game.Domain.AI.Utility;
using Game.View.Npc;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Gameplay;

namespace Game.View.PlayMode.Tests
{
    /// <summary>SCN_Utility 五 NPC 同一场攻击的异反应（态度调制）+ 截图证据。</summary>
    public sealed class NpcAttitudeScenePlayModeTests
    {
        private const string SceneName = "SCN_Utility";

        private static readonly string[] ExpectedNpcIds =
        {
            "npc-a", "npc-b", "npc-c", "npc-d", "npc-e",
        };

        private readonly List<NpcUtilityView> _views = new List<NpcUtilityView>();

        [UnityTest]
        public IEnumerator SameAttackScene_FearfulNpcsFlee_BraveNpcCounterattacks()
        {
            yield return LoadSceneAndTakeOver();

            // 场景降噪：停用损坏装置（PumpWorld 随之泵 broken=false），修理职责退出竞争，
            // 让手算锚点只取决于态度调制（Repair 0.9 本就低于 Counter 1.9 / Flee 3.0，双保险）。
            GameObject device = GameObject.Find("BrokenDevice");
            Assert.IsNotNull(device, "场景必须携带损坏装置（SCN_Utility 场景契约）");
            device.SetActive(false);

            // 预跑一拍稳定时钟，然后所有 NPC 接收【同一场】近距离攻击：
            // 威胁源 = 各自当前位置（攻击就发生在身边 → proximity 恒 1，手算锚点与站位无关）。
            foreach (NpcUtilityView view in _views)
            {
                view.Step(0.1f);
            }

            foreach (NpcUtilityView view in _views)
            {
                NpcUtilityAgent agent = view.Agent;
                agent.SetThreatSource(new Vec2(agent.Position.X, agent.Position.Y));
                agent.NoteAttackedBy(EntityRef.Player, -1f);
            }

            // 手算（proximity 1、衰减因子 ≈1，见 TASK-A-AI-01 施工报告）：
            //   勇敢权重 0.5 的行（sociable/curious/lethargic）：fear=0.6/0.5→钳 1.0 → Flee 3.0；
            //   balanced（勇敢 1.0）：fear 0.6 → Flee 1.32 > Pickup 0.85 > Follow 0.7；
            //   brave（勇敢 2.0）：anger=0.8×2→钳 1.0，fear 0.3 → Counter 1.9 > Flee 0.48。
            // 锚点断言打在攻击后首拍（衰减/接近度尚未演化）；10 拍窗口另断言互斥。
            var firstChoices = new Dictionary<string, string>();
            var everFled = new HashSet<string>();
            var everCountered = new HashSet<string>();
            for (int tick = 0; tick < 4; tick++)
            {
                foreach (NpcUtilityView view in _views)
                {
                    RecordChoice(view, tick, firstChoices, everFled, everCountered);
                }

                if (tick % 2 == 0)
                {
                    yield return null; // 让渲染循环真实出帧（截图口径需要）
                }
            }

            // 恰在逃跑/反击进行中取证（恐惧者已离位、勇敢者压在威胁点上）。
            yield return CaptureScreenshot("npc_attitude_divergence.png");

            for (int tick = 4; tick < 10; tick++)
            {
                foreach (NpcUtilityView view in _views)
                {
                    RecordChoice(view, tick, firstChoices, everFled, everCountered);
                }

                if (tick % 5 == 0)
                {
                    yield return null;
                }
            }

            Assert.AreEqual(NpcUtilityAgent.FleeActionName, firstChoices["npc-a"], "balanced：恐惧 0.6 → 逃跑增益 1.32 登顶");
            Assert.AreEqual(NpcUtilityAgent.FleeActionName, firstChoices["npc-b"], "sociable（勇敢 0.5）：恐惧钳 1.0 → Flee 3.0");
            Assert.AreEqual(NpcUtilityAgent.FleeActionName, firstChoices["npc-c"], "curious（勇敢 0.5）：同上");
            Assert.AreEqual(NpcUtilityAgent.CounterattackActionName, firstChoices["npc-d"], "brave（勇敢 2.0）：愤怒 1.0 → Counter 1.9 登顶（同事件异反应）");
            Assert.AreEqual(NpcUtilityAgent.FleeActionName, firstChoices["npc-e"], "lethargic（勇敢 0.5）：恐惧钳 1.0 → Flee 3.0");

            Assert.IsTrue(everCountered.SetEquals(new HashSet<string> { "npc-d" }),
                "10 拍窗口内只有 brave 反击（实际: " + string.Join(",", everCountered) + "）");
            Assert.IsFalse(everFled.Contains("npc-d"), "勇敢者绝不逃跑");
            Assert.That(everFled.Count, Is.GreaterThanOrEqualTo(3), "至少 3 个恐惧主导 NPC 真实逃跑过");

            yield return CaptureScreenshot("npc_attitude_divergence.png");
        }

        // ---- helpers -------------------------------------------------------

        private static void RecordChoice(
            NpcUtilityView view,
            int tick,
            Dictionary<string, string> firstChoices,
            HashSet<string> everFled,
            HashSet<string> everCountered)
        {
            string chosen = view.Step(0.1f);
            if (tick == 0)
            {
                firstChoices[view.NpcId] = chosen;
            }

            if (chosen == NpcUtilityAgent.FleeActionName)
            {
                everFled.Add(view.NpcId);
            }

            if (chosen == NpcUtilityAgent.CounterattackActionName)
            {
                everCountered.Add(view.NpcId);
            }
        }

        private IEnumerator LoadSceneAndTakeOver()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name, $"{SceneName} did not activate");

            CollectAndTakeOver();
            yield return null;
            yield return null;
            CollectAndTakeOver(); // 幂等复收：防个别视图晚一帧激活
        }

        private void CollectAndTakeOver()
        {
            NpcUtilityView[] found = Object.FindObjectsOfType<NpcUtilityView>();
            Assert.AreEqual(5, found.Length, "场景必须恰好五个 NpcUtilityView");
            _views.Clear();
            _views.AddRange(found);
            foreach (NpcUtilityView view in _views)
            {
                view.EnsureAgent();
                Assert.IsNotNull(view.PersonalityDef, $"{view.NpcId} 必须绑定 CFG 个性行（同源联动的前提）");
                Assert.IsNotNull(view.Agent.Memory, "代理必须携带记忆（态度层已接入）");
                view.ManualDrive = true; // 确定性接管
            }
        }

        /// <summary>A-R9-15 同口径：探针相机渲到 RenderTexture，渲染循环出帧后 ReadPixels 落 PNG。</summary>
        private static IEnumerator CaptureScreenshot(string fileName)
        {
            const int width = 480;
            const int height = 270;

            var rig = new GameObject("~AttitudeScreenshotRig");
            var camGo = new GameObject("~AttitudeScreenshotCamera");
            camGo.transform.SetParent(rig.transform, false);
            camGo.transform.position = new Vector3(0f, 7f, -5f);
            camGo.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -6.5f, 5.5f).normalized);
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
            Debug.Log($"[NpcAttitudeScenePlayModeTests] wrote pixel evidence: {path} ({width}x{height})");
        }
    }
}
