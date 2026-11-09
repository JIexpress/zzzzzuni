using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using ZFrame.View;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-04 guard: proves on real rendered pixels that the grade
    /// Volume actually reaches the screen, and that switching it off returns
    /// to the exact never-enabled picture.
    ///
    /// Why a PlayMode pixel case and not a "profile is set" assert: the shipped
    /// defect was that <c>Vol_Medium/High</c> were wired to
    /// <c>~GradeVolume.sharedProfile</c> while the camera never had a
    /// <c>UniversalAdditionalCameraData</c>, so URP forced
    /// <c>postProcessEnabled = false</c> (UniversalRenderPipeline.cs:1264-1271).
    /// Every component-level assert stayed green through that. The only
    /// falsifiable statement is "the pixels move".
    ///
    /// Rig (no interactive desktop on this machine, so nothing is eyeballed):
    /// an offscreen probe camera parked 5000 units away from the gameplay
    /// scene (the PlayMode runner leaves SCN_Game active, and its geometry must
    /// stay out of the frame) looking at one URP-Unlit cube at radiance 4.0 —
    /// well above the 0.9 Bloom threshold in both grade profiles. The camera
    /// renders into an ARGBFloat target that is read back with
    /// <c>ReadPixels</c>.
    ///
    /// The verdict is a FRAME DIFF against the same camera's own off-state
    /// frame, not an absolute pixel value: the backdrop is whatever the active
    /// scene renders behind the probe, and a diff is immune to that. Bloom can
    /// only move it one way — neighbouring pixels get brighter — so the primary
    /// gate is the share of pixels that got brighter.
    /// </summary>
    public sealed class PostFxPixelEvidencePlayModeTests
    {
        private const int Width = 320;
        private const int Height = 180;

        /// <summary>Probe park position: far outside anything SCN_Game draws.</summary>
        private static readonly Vector3 ProbeOrigin = new Vector3(5000f, 5000f, 5000f);

        /// <summary>Emitter radiance; the Bloom threshold in every profile is 0.9.</summary>
        private const float EmitterRadiance = 4f;

        /// <summary>A pixel counts as "moved" when its luminance changes by more than this (linear units).</summary>
        private const float MoveEpsilon = 0.02f;

        // ---- thresholds -----------------------------------------------------
        // Measured with the seam in place (see the report table), Medium tier:
        // 17.53% of the frame gets brighter, mean |delta| 0.0336; High: 44.69% /
        // 0.0563. The gates sit ~14x / ~16x below those numbers, and a build
        // without the seam produces EXACTLY 0.0000 (identical frames, measured),
        // so "works" and "reverted" do not overlap at all.
        private const float MinBrighterFraction = 0.012f;
        private const float MinMeanAbsDelta = 0.002f;

        // Restore tolerance: on and off are the same deterministic frame (same
        // scene, same pipeline asset, no temporal effect in any profile), so the
        // admissible residue is float accumulation order only.
        private const float RestoreMeanAbsTolerance = 0.001f;
        private const float RestoreMovedFractionTolerance = 0.002f;

        private const int TimingFrames = 30;

        // Budget probe resolution (the project's parity baseline is 1280x720).
        private const int TimingWidth = 1280;
        private const int TimingHeight = 720;

        private GameObject _rig;
        private Camera _camera;
        private RenderTexture _target;
        private RenderTexture _timingTarget;
        private Material _material;

        [TearDown]
        public void TearDown()
        {
            PostFxCameraRuntime.SetEnabled(true); // leave the shipped default on
            PipelineManager.SetTier(QualityTier.Medium);

            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_rig != null)
            {
                UnityEngine.Object.Destroy(_rig);
            }

            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.Destroy(_target);
            }

            if (_timingTarget != null)
            {
                _timingTarget.Release();
                UnityEngine.Object.Destroy(_timingTarget);
            }

            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
            }
        }

        [UnityTest]
        public IEnumerator PostProcessing_OnVsOff_MovesPixels_AndRestoresBaseline()
        {
            yield return null; // let PipelineManager (BeforeSceneLoad) finish its first apply
            PipelineManager.EnsureInstance();
            PipelineManager.SetTier(QualityTier.Medium);
            yield return null;
            yield return WaitGradeSettled(); // TASK-A-R9-08: the tier switch now crossfades

            BuildRig();
            yield return null;

            // --- the shipped defect, observed live ---------------------------
            Assert.IsFalse(PostFxCameraRuntime.HasCameraData(_camera),
                "probe camera already carries UniversalAdditionalCameraData before anything enabled it");

            // The scope claim in the report: the seam is not a test-only path —
            // the camera that was already in the scene when it loaded got the
            // component from the AfterSceneLoad/sceneLoaded hook, not from this
            // test. Logged so the evidence file shows it.
            Camera shippedCamera = Camera.main;
            if (shippedCamera != null)
            {
                Debug.Log($"[POSTFX] scene camera '{shippedCamera.name}' in '{shippedCamera.gameObject.scene.name}': " +
                          $"hasData={PostFxCameraRuntime.HasCameraData(shippedCamera)} " +
                          $"postOn={PostFxCameraRuntime.IsPostProcessingOn(shippedCamera)} (set by the load hook)");
            }

            // --- arm 1: never enabled (the control) --------------------------
            Frame baseline = default;
            yield return CaptureInto(f => baseline = f);
            Assert.IsFalse(PostFxCameraRuntime.IsPostProcessingOn(_camera),
                "post-processing must be off in the control arm");
            Assert.Greater(baseline.Stats.MaxLuma, 3.5f,
                $"rig sanity: the 4.0 emitter is not visible in the control frame (max luma {baseline.Stats.MaxLuma:F3})");

            // --- arm 2: post-processing on -----------------------------------
            PostFxCameraRuntime.SetEnabled(true);
            bool flagAfterEnable = PostFxCameraRuntime.IsPostProcessingOn(_camera);
            Frame on = default;
            yield return CaptureInto(f => on = f);

            // --- arm 3: back off ---------------------------------------------
            PostFxCameraRuntime.SetEnabled(false);
            Frame off = default;
            yield return CaptureInto(f => off = f);

            LogFrame("Medium.neverEnabled", baseline);
            LogFrame("Medium.postOn", on);
            LogFrame("Medium.postOffAfterOn", off);

            FrameDiff enabledDiff = FrameDiff.Between(baseline, on);
            FrameDiff restoredDiff = FrameDiff.Between(baseline, off);
            Debug.Log($"[POSTFX] Medium on-vs-off diff: meanAbs={enabledDiff.MeanAbs:F5} " +
                      $"brighter={enabledDiff.BrighterFraction:F4} darker={enabledDiff.DarkerFraction:F4} " +
                      $"moved={enabledDiff.MovedFraction:F4}");
            Debug.Log($"[POSTFX] Medium restore diff: meanAbs={restoredDiff.MeanAbs:F6} moved={restoredDiff.MovedFraction:F4}");

            // Pixel verdict FIRST: reverting the seam must turn this red on the
            // pixels, not on a configuration readback (config readbacks are what
            // stayed green through the whole defect).
            Assert.Greater(enabledDiff.BrighterFraction, MinBrighterFraction,
                $"post-processing on/off did not brighten the frame: {enabledDiff.BrighterFraction:F4} of pixels moved " +
                $"(gate {MinBrighterFraction:F4}). Bloom 1.0 over a 4.0 emitter (threshold 0.9) cannot be a no-op.");
            Assert.Greater(enabledDiff.MeanAbs, MinMeanAbsDelta,
                $"post-processing on/off did not move the frame enough: mean |delta| {enabledDiff.MeanAbs:F5} (gate {MinMeanAbsDelta:F4})");

            // Structure second (documents WHICH field did it).
            Assert.IsTrue(flagAfterEnable,
                "camera did not end up with renderPostProcessing = true after enabling the seam");
            Assert.IsTrue(PostFxCameraRuntime.HasCameraData(_camera),
                "UniversalAdditionalCameraData was not added to the camera");

            // --- restore equals never-enabled --------------------------------
            Assert.Less(restoredDiff.MeanAbs, RestoreMeanAbsTolerance,
                $"turning post-processing back off did not return to the never-enabled frame: mean |delta| {restoredDiff.MeanAbs:F6}");
            Assert.Less(restoredDiff.MovedFraction, RestoreMovedFractionTolerance,
                $"pixels still differ after turning post-processing off: moved {restoredDiff.MovedFraction:F4}");

            // --- tier table: the three profiles must not render the same ------
            QualityTier[] tiers = { QualityTier.Low, QualityTier.Medium, QualityTier.High };
            var tierOff = new Frame[tiers.Length];
            var tierOn = new Frame[tiers.Length];
            var tierDiff = new FrameDiff[tiers.Length];
            for (int i = 0; i < tiers.Length; i++)
            {
                PipelineManager.SetTier(tiers[i]);
                yield return null;
                yield return WaitGradeSettled(); // TASK-A-R9-08: measure the SETTLED tier picture

                PostFxCameraRuntime.SetEnabled(false);
                yield return CaptureInto(f => tierOff[i] = f);

                PostFxCameraRuntime.SetEnabled(true);
                yield return CaptureInto(f => tierOn[i] = f);

                tierDiff[i] = FrameDiff.Between(tierOff[i], tierOn[i]);
                LogFrame($"{tiers[i]}.postOff", tierOff[i]);
                LogFrame($"{tiers[i]}.postOn", tierOn[i]);
                Debug.Log($"[POSTFX] tier {tiers[i]}: mean {tierOff[i].Stats.MeanLuma:F5} -> {tierOn[i].Stats.MeanLuma:F5}, " +
                          $"max {tierOff[i].Stats.MaxLuma:F3} -> {tierOn[i].Stats.MaxLuma:F3}, " +
                          $"moved={tierDiff[i].MovedFraction:F4} brighter={tierDiff[i].BrighterFraction:F4} " +
                          $"meanAbs={tierDiff[i].MeanAbs:F5}");
            }

            // Low ships an EMPTY profile (Vol_Low.asset:15 `components: []`): its
            // honest expectation is "Bloom adds no light". It does still clamp the
            // camera's HDR band to 1.0 (the emitter's own 0.44% of pixels get
            // darker) because turning the post-processing path on changes the
            // final blit — that is the path, not the grade. So the Low gate is on
            // "nothing got brighter", not on "nothing moved".
            Assert.Less(tierDiff[0].BrighterFraction, 0.002f,
                $"Low tier is documented as an empty profile but {tierDiff[0].BrighterFraction:F4} of pixels got brighter");

            // Medium = Bloom 0.6, High = Bloom 2.5 + ColorAdjustments (exposure 0.5,
            // saturation 14): both must move the frame, and High must move it
            // substantially more. TASK-A-R9-19 P3 re-balanced the tiers after
            // Tonemapping compressed the HDR top end.
            Assert.Greater(tierDiff[1].BrighterFraction, MinBrighterFraction,
                $"Medium (Bloom 0.6) did not brighten the frame: {tierDiff[1].BrighterFraction:F4}");
            // Measured High/Medium mean|delta| ratio: 1.48x (0.1595 vs 0.1080, Linear +
            // Neutral tonemapping, 2026-11-09).
            Assert.Greater(tierDiff[2].MeanAbs, tierDiff[1].MeanAbs * 1.35f,
                $"High and Medium render the same picture: mean |delta| {tierDiff[2].MeanAbs:F5} vs {tierDiff[1].MeanAbs:F5}");
            Assert.Greater(tierOn[2].Stats.MeanLuma, tierOn[1].Stats.MeanLuma,
                $"High (Bloom 2.5 + ColorAdjustments) is not brighter than Medium: {tierOn[2].Stats.MeanLuma:F5} vs {tierOn[1].Stats.MeanLuma:F5}");

            // --- what the REAL game scene looks like (evidence, not a gate) ---
            // The probe above isolates the seam; this answers "does the player
            // actually see the grade on SCN_Game content". It is logged, not
            // asserted: the slice scene has no emissive art, so whether Bloom is
            // visible there is an art/content question, not a wiring one.
            Camera gameCamera = Camera.main;
            if (gameCamera != null)
            {
                RenderTexture previousTarget = gameCamera.targetTexture;
                gameCamera.targetTexture = _timingTarget;

                PipelineManager.SetTier(QualityTier.Medium);
                yield return null;
                yield return WaitGradeSettled(); // TASK-A-R9-08: settled grade before the real-content diff
                PostFxCameraRuntime.SetEnabled(false);
                yield return null;
                Frame gameOff = ReadBack(_timingTarget, TimingWidth, TimingHeight);

                PostFxCameraRuntime.SetEnabled(true);
                yield return null;
                Frame gameOn = ReadBack(_timingTarget, TimingWidth, TimingHeight);

                gameCamera.targetTexture = previousTarget;

                FrameDiff gameDiff = FrameDiff.Between(gameOff, gameOn);
                LogFrame("SCN_Game.MainCamera.postOff", gameOff);
                LogFrame("SCN_Game.MainCamera.postOn", gameOn);
                Debug.Log($"[POSTFX] SCN_Game Main Camera (real content, {TimingWidth}x{TimingHeight}): " +
                          $"mean {gameOff.Stats.MeanLuma:F5} -> {gameOn.Stats.MeanLuma:F5}, " +
                          $"p95 {gameOff.Stats.P95Luma:F4} -> {gameOn.Stats.P95Luma:F4}, " +
                          $"moved={gameDiff.MovedFraction:F4} brighter={gameDiff.BrighterFraction:F4} " +
                          $"meanAbs={gameDiff.MeanAbs:F5}");
            }
            else
            {
                Debug.Log("[POSTFX] SCN_Game Main Camera not found (tag MainCamera) — real-content evidence skipped");
            }

            // --- frame budget evidence (report-only, machine dependent) -------
            PipelineManager.SetTier(QualityTier.Medium);
            yield return null;
            yield return WaitGradeSettled(); // TASK-A-R9-08: measure at settled weights
            double msOn = MeasureFrameMs(true);
            double msOff = MeasureFrameMs(false);
            Debug.Log($"[POSTFX] frame time @ {TimingWidth}x{TimingHeight}, {TimingFrames} renders, CPU submission wall clock: " +
                      $"postOn {msOn:F3} ms, postOff {msOff:F3} ms, delta {msOn - msOff:F3} ms " +
                      $"(60fps budget 16.667 ms; editor-side submission measure, not a GPU benchmark)");

            // leave the seam in its shipped state
            PostFxCameraRuntime.SetEnabled(true);
        }

        // ---- rig ------------------------------------------------------------

        /// <summary>
        /// TASK-A-R9-08: SetTier now crossfades the grade (方案 §6.4) instead
        /// of swapping <c>sharedProfile</c> instantly. Every measurement here
        /// needs the SETTLED tier picture the old instant swap produced, so
        /// the tier arms wait the fade out before capturing. The realtime
        /// budget keeps a regression a clean assertion failure, not a batch
        /// hang (BootChainSmokeTests discipline).
        /// </summary>
        private static IEnumerator WaitGradeSettled()
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (PipelineManager.Instance != null && PipelineManager.Instance.IsGradeCrossfading &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(PipelineManager.Instance != null && PipelineManager.Instance.IsGradeCrossfading,
                "the grade crossfade did not settle within the budget");
        }

        private void BuildRig()
        {
            _rig = new GameObject("~PostFxPixelRig");

            var camGo = new GameObject("~PostFxProbeCamera");
            camGo.transform.SetParent(_rig.transform, false);
            camGo.transform.position = ProbeOrigin + new Vector3(0f, 0f, -3f);
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            _camera.allowHDR = true;
            _camera.allowMSAA = false;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 12f; // gameplay geometry stays out of the frame
            _camera.fieldOfView = 60f;

            GameObject emitter = GameObject.CreatePrimitive(PrimitiveType.Cube);
            emitter.name = "~PostFxProbeEmitter";
            emitter.transform.SetParent(_rig.transform, false);
            emitter.transform.position = ProbeOrigin;
            emitter.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            var collider = emitter.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.IsNotNull(unlit, "URP unlit shader not found — the probe needs a deterministic emitter");
            _material = new Material(unlit) { name = "~PostFxProbeMaterial" };
            _material.SetColor("_BaseColor", new Color(EmitterRadiance, EmitterRadiance, EmitterRadiance, 1f));
            emitter.GetComponent<MeshRenderer>().sharedMaterial = _material;

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.name = "~PostFxProbeTarget";
            _target.Create();

            _timingTarget = new RenderTexture(TimingWidth, TimingHeight, 24, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            _timingTarget.name = "~PostFxTimingTarget";
            _timingTarget.Create();

            // Enabled + targetTexture: the real URP path renders this camera every
            // frame, post-processing included. Nothing is read from the screen.
            _camera.targetTexture = _target;
            _camera.enabled = true;
        }

        /// <summary>Waits one real frame with the current seam state, then reads the target back.</summary>
        private IEnumerator CaptureInto(Action<Frame> sink)
        {
            yield return null;
            sink(ReadBack());
        }

        private Frame ReadBack()
        {
            return ReadBack(_target, Width, Height);
        }

        private static Frame ReadBack(RenderTexture target, int width, int height)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var tex = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tex.Apply(false, false);
            float[] lumas = LumaOf(tex.GetPixels());
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(tex);

            return new Frame(lumas);
        }

        private static float[] LumaOf(Color[] pixels)
        {
            var lumas = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                lumas[i] = 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            }

            return lumas;
        }

        private double MeasureFrameMs(bool postOn)
        {
            RenderTexture previousTarget = _camera.targetTexture;
            _camera.targetTexture = _timingTarget;

            PostFxCameraRuntime.SetEnabled(postOn);
            _camera.Render(); // warm-up (pipeline switch, RT allocation)

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < TimingFrames; i++)
            {
                _camera.Render();
                GL.Flush();
            }

            sw.Stop();
            _camera.targetTexture = previousTarget;
            return sw.Elapsed.TotalMilliseconds / TimingFrames;
        }

        private static void LogFrame(string label, Frame frame)
        {
            ImageStats s = frame.Stats;
            Debug.Log($"[POSTFX] {label}: meanLuma={s.MeanLuma:F5} stdLuma={s.StdLuma:F5} " +
                      $"min={s.MinLuma:F4} max={s.MaxLuma:F4} p95={s.P95Luma:F4} brightRatio={s.BrightRatio:F4}");
        }

        // ---- statistics -----------------------------------------------------

        private readonly struct ImageStats
        {
            public readonly float MeanLuma;
            public readonly float StdLuma;
            public readonly float P95Luma;
            public readonly float MinLuma;
            public readonly float MaxLuma;

            /// <summary>Share of pixels above 1.0 luminance (the HDR band Bloom feeds on).</summary>
            public readonly float BrightRatio;

            public ImageStats(float mean, float std, float p95, float min, float max, float brightRatio)
            {
                MeanLuma = mean;
                StdLuma = std;
                P95Luma = p95;
                MinLuma = min;
                MaxLuma = max;
                BrightRatio = brightRatio;
            }
        }

        private readonly struct Frame
        {
            public readonly float[] Lumas;
            public readonly ImageStats Stats;

            public Frame(float[] lumas)
            {
                Lumas = lumas;

                int count = lumas.Length;
                double sum = 0d;
                float min = float.MaxValue;
                float max = float.MinValue;
                int bright = 0;
                for (int i = 0; i < count; i++)
                {
                    float luma = lumas[i];
                    sum += luma;
                    if (luma > 1f) bright++;
                    if (luma < min) min = luma;
                    if (luma > max) max = luma;
                }

                float mean = (float)(sum / count);
                double variance = 0d;
                for (int i = 0; i < count; i++)
                {
                    double d = lumas[i] - mean;
                    variance += d * d;
                }

                var sorted = (float[])lumas.Clone();
                Array.Sort(sorted);
                int p95 = Mathf.Clamp((int)(count * 0.95f), 0, count - 1);

                Stats = new ImageStats(mean, Mathf.Sqrt((float)(variance / count)), sorted[p95], min, max,
                    bright / (float)count);
            }
        }

        private readonly struct FrameDiff
        {
            /// <summary>Mean absolute luminance change over all pixels.</summary>
            public readonly float MeanAbs;

            /// <summary>Share of pixels whose luminance rose by more than <see cref="MoveEpsilon"/>.</summary>
            public readonly float BrighterFraction;

            /// <summary>Share of pixels whose luminance fell by more than <see cref="MoveEpsilon"/>.</summary>
            public readonly float DarkerFraction;

            /// <summary>Share of pixels that moved at all (either direction).</summary>
            public readonly float MovedFraction;

            private FrameDiff(float meanAbs, float brighter, float darker, float moved)
            {
                MeanAbs = meanAbs;
                BrighterFraction = brighter;
                DarkerFraction = darker;
                MovedFraction = moved;
            }

            public static FrameDiff Between(Frame a, Frame b)
            {
                Assert.AreEqual(a.Lumas.Length, b.Lumas.Length, "frames must have the same pixel count");
                int count = a.Lumas.Length;
                double sumAbs = 0d;
                int brighter = 0;
                int darker = 0;
                for (int i = 0; i < count; i++)
                {
                    float delta = b.Lumas[i] - a.Lumas[i];
                    sumAbs += Mathf.Abs(delta);
                    if (delta > MoveEpsilon) brighter++;
                    else if (delta < -MoveEpsilon) darker++;
                }

                return new FrameDiff((float)(sumAbs / count), brighter / (float)count, darker / (float)count,
                    (brighter + darker) / (float)count);
            }
        }
    }
}
