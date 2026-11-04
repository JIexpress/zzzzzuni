// KernelCrowdPixelEvidencePlayModeTests.cs —— A-R9-15：默认可见真断言 + 真实像素证据。
// 复用 A-R9-10 已验证的「离屏探针相机 + RenderTexture + ReadPixels」机制（自建
// 相机以便自定义 farClip 并把真实读回的 RGB 像素编码成 PNG 落盘），在同一运行内
// 做「群体开 / 关」A/B：变化像素数 / 占比 / 阈值，证明群体真的出现在画面里，
// 而不是「只有 1 次 Graphics.DrawMeshInstanced 提交调用」。
// 另用 A-R9-10 的相机几何再测一次，量化「若群体不隔离会叠加多少差分」。
// 真实 GPU DrawCall 用 ProfilerRecorder 尽力采样，取不到就在报告如实标受限。

using System;
using System.Collections;
using System.IO;
using Game.Bootstrap;
using Game.View.Effects;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>A-R9-15：开箱可见（PlayMode 真断言）+ 真实像素证据（两图 + 差统计）。</summary>
    public sealed class KernelCrowdPixelEvidencePlayModeTests
    {
        private const int Width = 320;
        private const int Height = 180;
        private const float FarClip = 60f;

        /// <summary>与 A-R9-10 / PixelProbeRig.FrameDiff.MoveEpsilon 同口径，保证数字可比。</summary>
        private const float MoveEpsilon = 0.02f;

        /// <summary>群体必须在真实像素上留下可测差异：变化像素占比下限。</summary>
        private const float MinChangedFraction = 0.010f;

        private CrowdProbe _probe;

        [SetUp]
        public void SetUp()
        {
            GameRoot.Bootstrap();      // 幂等：确保 ITickQueue / TickQueue 已注册
            GameRoot.EnsureInstance(); // 幂等：确保 GameLoopDriver 每帧 TickAll(View) 在跑
            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown();
            ParticleRuntime.SetAmbientDust(false); // 隔离环境尘埃（A-R9-10 同口径）

            // 群体开 / 关由本用例显式控制；AutoEnable 保持默认 true（开箱可见语义）。
            KernelCrowdHost.EnsureInstance().DisposeCrowd();
            _probe = new CrowdProbe();
        }

        [TearDown]
        public void TearDown()
        {
            KernelCrowdHost.Instance?.DisposeCrowd();
            _probe?.Dispose();
            _probe = null;
            ParticleRuntime.SetShaderCandidates(null);
            ParticleRuntime.Shutdown();
        }

        // ---- 用例 1：默认进入 SCN_Game 群体开箱可见（V1 真断言，非「菜单可开」）----
        [UnityTest]
        public IEnumerator DefaultEnterScene_CrowdIsAutoVisible()
        {
            Assert.IsTrue(KernelCrowdHost.AutoEnable,
                "A-R9-15：AutoEnable 默认须为 true（进 SCN_Game 即开箱可见）");

            KernelCrowdHost host = KernelCrowdHost.EnsureInstance();
            host.DisposeCrowd(); // 从干净态起，证明是「进场景」这一步把群体自动拉起
            Assert.IsFalse(host.CrowdActive, "起点须干净，否则证不了自动启用");

            yield return SceneManager.LoadSceneAsync(GameBootstrap.GameSceneName);
            Assert.AreEqual(GameBootstrap.GameSceneName, SceneManager.GetActiveScene().name,
                "SCN_Game did not activate");
            yield return null;
            yield return null;

            // sceneLoaded 门控应已自动 EnsureCrowd；GameLoopDriver 每帧 TickAll(View) 提交。
            Assert.IsTrue(host.CrowdActive, "进 SCN_Game 后群体未自动启用（非开箱可见）");
            Assert.IsNotNull(host.Driver);
            Assert.IsNotNull(host.Renderer);
            Assert.AreEqual(1000, host.Driver.EntityCount);
            Assert.IsTrue(host.Renderer.IsShaderAvailable,
                "URP 兼容材质须解析成功（shader=" + host.Renderer.ResolvedShaderName + "）");
            Assert.AreEqual(1, host.Renderer.LastSubmitCalls,
                "默认可见后仍须是单次 Graphics.DrawMeshInstanced 提交（不得退化为逐实体）");
            Debug.Log($"[A-R9-15] default visible: active={host.CrowdActive} " +
                      $"entities={host.Driver.EntityCount} submitCalls={host.Renderer.LastSubmitCalls} " +
                      $"submittedInstances={host.Renderer.LastSubmittedInstances} " +
                      $"shader={host.Renderer.ResolvedShaderName}");
        }

        // ---- 用例 2：真实像素证据（群体开 / 关两图 + 像素差统计）----
        [UnityTest]
        public IEnumerator CrowdOnVsOff_ChangesRealPixels_AndWritesTwoPngs()
        {
            // 加载真实的 SCN_Game：自带 URP 光照，群体 URP/Lit 材质能正常着色；
            // 场景里除群体外的静态内容在开 / 关两帧都存在，差分会被抵消。
            yield return SceneManager.LoadSceneAsync(GameBootstrap.GameSceneName);
            Assert.AreEqual(GameBootstrap.GameSceneName, SceneManager.GetActiveScene().name);
            GameObject enemy = GameObject.Find("Enemy");
            if (enemy != null)
            {
                enemy.SetActive(false); // park：敌人巡逻会在两帧之间移动像素（A-R9-10 同款隔离）
            }

            yield return null;
            yield return null;

            KernelCrowdHost host = KernelCrowdHost.EnsureInstance();
            // sceneLoaded 已自动 EnsureCrowd；下面显式做开 / 关 A/B 对照。

            // 主证据几何：俯视群体圆盘（FieldRadius=9），整片落在 60m farClip 内。
            _probe.Build(new Vector3(0f, 11f, -13f), new Vector3(0f, 0.6f, 2f));
            yield return null;
            yield return null;

            // 关态：DisposeCrowd 注销 View 档，下一帧起不再有 DrawMeshInstanced 提交。
            host.DisposeCrowd();
            yield return null;
            yield return null;
            CrowdProbe.Frame off = _probe.Capture();
            WritePng(off, "crowd_r9_15_off.png");

            // 开态：EnsureCrowd 重新注册 View 档，GameLoopDriver 每帧提交。
            host.EnsureCrowd();
            Assert.IsTrue(host.CrowdActive);
            yield return null;
            yield return null;
            CrowdProbe.Frame on = _probe.Capture();
            WritePng(on, "crowd_r9_15_on.png");

            CrowdProbe.Diff diff = CrowdProbe.Diff.Between(off, on);
            Debug.Log($"[A-R9-15] pixels (main view, {Width}x{Height}={off.Lumas.Length}px): " +
                      $"changedPixels={diff.ChangedPixels}/{off.Lumas.Length} " +
                      $"changedFraction={diff.ChangedFraction:F4} moved={diff.MovedFraction:F4} " +
                      $"brighter={diff.BrighterFraction:F4} darker={diff.DarkerFraction:F4} " +
                      $"meanAbs={diff.MeanAbs:F5}");

            Assert.Greater(diff.ChangedFraction, MinChangedFraction,
                $"群体开 / 关没有在真实像素上留下可测差异：仅 {diff.ChangedFraction:F4} 的像素变化 " +
                $"(gate {MinChangedFraction:F4})。「1 次提交调用」不等于像素可见，此断言拒绝空证据。");

            // 前置调研量化：用 A-R9-10 的相机几何（rig.Build(-3,1.6,-5.5 -> 1.5,0.9,2.5)）
            // 再测一次，得到「若群体自动出现且不隔离，会叠加进 A-R9-10 相机差分」的量级。
            _probe.Build(new Vector3(-3f, 1.6f, -5.5f), new Vector3(1.5f, 0.9f, 2.5f));
            yield return null;
            yield return null;
            host.DisposeCrowd();
            yield return null;
            yield return null;
            CrowdProbe.Frame r910Off = _probe.Capture();
            host.EnsureCrowd();
            yield return null;
            yield return null;
            CrowdProbe.Frame r910On = _probe.Capture();
            CrowdProbe.Diff r910 = CrowdProbe.Diff.Between(r910Off, r910On);
            Debug.Log($"[A-R9-15] A-R9-10 camera geometry (contamination magnitude if NOT isolated): " +
                      $"moved={r910.MovedFraction:F4} darker={r910.DarkerFraction:F4} " +
                      $"brighter={r910.BrighterFraction:F4} meanAbs={r910.MeanAbs:F5} " +
                      $"changedFraction={r910.ChangedFraction:F4} " +
                      $"(A-R9-10 gates: MovedFraction>0.010, DarkerFraction>0.005)");

            // 真实 GPU DrawCall：尽力采样；batchmode 无渲染负载时 ProfilerRecorder
            // 取不到可信值，只如实记录，绝不编数字、绝不当作像素证据。
            yield return LogGpuDrawCallsBestEffort();
        }

        private static IEnumerator LogGpuDrawCallsBestEffort()
        {
            ProfilerRecorder recorder = default;
            bool valid = false;
            try
            {
                recorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
                valid = recorder.Valid;
            }
            catch (Exception e)
            {
                Debug.Log($"[A-R9-15] GPU DrawCall: ProfilerRecorder 不可用 " +
                          $"({e.GetType().Name}: {e.Message})，如实标受限");
            }

            yield return null;
            yield return null;

            if (valid)
            {
                try
                {
                    long value = recorder.LastValue;
                    Debug.Log($"[A-R9-15] GPU DrawCall (ProfilerRecorder 'Draw Calls Count', 群体开): {value}" +
                              (value <= 0 ? " <=0=batchmode 无渲染负载，判为不可信/受限" : ""));
                }
                catch (Exception e)
                {
                    Debug.Log($"[A-R9-15] GPU DrawCall: 读取失败 ({e.GetType().Name})，如实标受限");
                }
            }
            else
            {
                Debug.Log("[A-R9-15] GPU DrawCall: ProfilerRecorder.Valid=false（无该 stat / 未启用 profiler），如实标受限");
            }

            try { recorder.Dispose(); } catch { /* 默认结构体的 Dispose 可能抛，忽略 */ }
        }

        /// <summary>把真实读回的 RGB 像素编码成 PNG，落到 Docs/Parity/（白名单：仅图片证据）。</summary>
        private static void WritePng(CrowdProbe.Frame frame, string fileName)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Docs", "Parity"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.SetPixels(frame.Colors);
            tex.Apply(false, false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Debug.Log($"[A-R9-15] wrote pixel evidence: {path} ({Width}x{Height}, RGB read back from URP camera)");
        }

        /// <summary>离屏探针：自建相机渲到 RenderTexture，ReadPixels 读回真实像素（非提交层计数）。</summary>
        private sealed class CrowdProbe
        {
            private GameObject _rig;
            private Camera _camera;
            private RenderTexture _target;

            public void Build(Vector3 eye, Vector3 look)
            {
                Dispose();
                _rig = new GameObject("~CrowdPixelProbe");
                var camGo = new GameObject("~CrowdPixelProbeCamera");
                camGo.transform.SetParent(_rig.transform, false);
                camGo.transform.position = eye;
                camGo.transform.rotation = Quaternion.LookRotation((look - eye).normalized);
                _camera = camGo.AddComponent<Camera>();
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = FarClip;
                _camera.fieldOfView = 60f;
                _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                _target.name = "~CrowdPixelProbeTarget";
                _target.Create();
                _camera.targetTexture = _target;
                _camera.enabled = true;
            }

            public Frame Capture()
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                tex.Apply(false, false);
                Color[] colors = tex.GetPixels();
                var lumas = new float[colors.Length];
                for (int i = 0; i < colors.Length; ++i)
                {
                    lumas[i] = 0.2126f * colors[i].r + 0.7152f * colors[i].g + 0.0722f * colors[i].b;
                }

                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(tex);
                return new Frame(lumas, colors);
            }

            public void Dispose()
            {
                if (_camera != null)
                {
                    _camera.targetTexture = null;
                    _camera = null;
                }

                if (_rig != null)
                {
                    UnityEngine.Object.Destroy(_rig);
                    _rig = null;
                }

                if (_target != null)
                {
                    _target.Release();
                    UnityEngine.Object.Destroy(_target);
                    _target = null;
                }
            }

            public readonly struct Frame
            {
                public readonly float[] Lumas;
                public readonly Color[] Colors;

                public Frame(float[] lumas, Color[] colors)
                {
                    Lumas = lumas;
                    Colors = colors;
                }
            }

            public readonly struct Diff
            {
                public readonly int ChangedPixels;
                public readonly float ChangedFraction;
                public readonly float MovedFraction;
                public readonly float BrighterFraction;
                public readonly float DarkerFraction;
                public readonly float MeanAbs;

                private Diff(int changed, float changedFrac, float moved, float brighter, float darker, float meanAbs)
                {
                    ChangedPixels = changed;
                    ChangedFraction = changedFrac;
                    MovedFraction = moved;
                    BrighterFraction = brighter;
                    DarkerFraction = darker;
                    MeanAbs = meanAbs;
                }

                public static Diff Between(Frame a, Frame b)
                {
                    Assert.AreEqual(a.Lumas.Length, b.Lumas.Length, "frames must have the same pixel count");
                    int count = a.Lumas.Length;
                    double sumAbs = 0d;
                    int changed = 0;
                    int brighter = 0;
                    int darker = 0;
                    for (int i = 0; i < count; ++i)
                    {
                        float d = b.Lumas[i] - a.Lumas[i];
                        float ad = Mathf.Abs(d);
                        sumAbs += ad;
                        if (ad > MoveEpsilon) changed++;
                        if (d > MoveEpsilon) brighter++;
                        else if (d < -MoveEpsilon) darker++;
                    }

                    return new Diff(changed, changed / (float)count,
                        (brighter + darker) / (float)count,
                        brighter / (float)count, darker / (float)count,
                        (float)(sumAbs / count));
                }
            }
        }
    }
}
