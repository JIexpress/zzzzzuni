using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-05 guards: the two runtime-created particle effects must be
    /// falsifiable on real rendered pixels, not on "a ParticleSystem exists".
    ///
    /// The defect class under test is the same as A-R9-04's: configuration that
    /// reads back fine while the screen does not change. A particle system that
    /// never renders (stripped shader, dead material, disabled by accident)
    /// reports a healthy <c>particleCount</c> all day. So every verdict here is a
    /// FRAME DIFF against the same camera's own control frame.
    ///
    /// Rig (no interactive desktop, nothing is eyeballed): an offscreen probe
    /// camera parked 5000 units from the gameplay scene, solid 0.02 background,
    /// 1.2 m from the burst point, rendering into a 320x180 ARGBFloat target
    /// that is read back with <c>ReadPixels</c>. The control frame is uniform,
    /// so any brighter pixel can only come from a particle.
    /// </summary>
    public sealed class ParticlePixelEvidencePlayModeTests
    {
        private const int Width = 320;
        private const int Height = 180;

        /// <summary>Probe park position: far outside anything the test scene draws.</summary>
        private static readonly Vector3 ProbeOrigin = new Vector3(5000f, 5000f, 5000f);

        /// <summary>Camera sits this far from the burst, so a 0.05-0.10 m spark is ~15-30 px.</summary>
        private const float BurstDistance = 1.2f;

        /// <summary>A pixel counts as "moved" when its luminance changes by more than this.</summary>
        private const float MoveEpsilon = 0.02f;

        // ---- thresholds -----------------------------------------------------
        // Calibrated from the measurements in Docs/Logs/TASK-A-R9-05.md, in the
        // same way A-R9-04 did it: the gate sits below the measured signal, and
        // the reverted build measures EXACTLY 0.0000 (bit-identical frames), so
        // "works" and "broken" never overlap. Measured values are restated next
        // to each gate.
        //
        // Effect A: 12 sparks cover only a small part of the frame, so the gate
        // is on BOTH the area (brighter) and the energy (meanAbs).
        //   measured brighter = 0.0109 -> gate 0.0040 (2.7x)
        //   measured meanAbs  = 0.0095 -> gate 0.0020 (4.7x)
        private const float MinSparkBrighter = 0.004f;
        private const float MinSparkMeanAbs = 0.002f;

        // Effect B: dust deliberately covers MORE pixels than a spark burst but
        // carries 18x less energy (measured meanAbs 0.00051 vs 0.00947), so
        // "quiet" is measured as energy and peak brightness, not as area.
        //   measured brighter = 0.0027 -> gate 0.0008 (3.4x)
        //   measured meanAbs ratio to A = 0.054 -> gate 0.40 (7.4x)
        private const float MinDustBrighter = 0.0008f;
        private const float DustToSparkMaxMeanAbsRatio = 0.4f;

        // 0.55 s after the burst every particle is past its 0.45 s lifetime, so
        // the frame must be back to the control. Tolerance is float noise only.
        private const float FadeBrighterTolerance = 0.004f;
        private const float FadeMeanAbsTolerance = 0.002f;

        // Enabled=false may not move a single pixel; control frames are
        // bit-identical here, so anything above zero is a real leak.
        private const float DisabledMovedTolerance = 0.0005f;

        /// <summary>Renders per arm for the report-only frame-cost line.</summary>
        private const int TimingFrames = 30;

        private GameObject _rig;
        private Camera _camera;
        private RenderTexture _target;

        [TearDown]
        public void TearDown()
        {
            ParticleRuntime.SetShaderCandidates(null); // restore the shipped candidate list
            ParticleRuntime.Shutdown(); // shipped defaults, nothing left running

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
        }

        [UnityTest]
        public IEnumerator HitSpark_Burst_BrightensFrame_ThenFadesBackToControl()
        {
            yield return null;
            BuildRig();
            yield return null;
            yield return null;

            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false); // isolate A from B
            yield return null;

            Frame baseline = default;
            yield return CaptureInto(f => baseline = f);
            LogFrame("A.control", baseline);
            Assert.Less(baseline.Stats.MaxLuma, 0.1f,
                $"rig sanity: the control frame is not empty (max luma {baseline.Stats.MaxLuma:F3}) - " +
                "something other than particles is in front of the probe camera");

            Vector3 burstPos = ProbeOrigin;
            int emitted = ParticleRuntime.SpawnHitSpark(burstPos);

            // 2 frames later the burst is at full brightness (lifetime 0.22-0.45 s).
            yield return null;
            yield return null;
            Frame burst = default;
            yield return CaptureInto(f => burst = f);
            LogFrame("A.burst", burst);

            FrameDiff burstDiff = FrameDiff.Between(baseline, burst);
            Debug.Log($"[PARTICLES] A burst vs control: meanAbs={burstDiff.MeanAbs:F5} " +
                      $"brighter={burstDiff.BrighterFraction:F4} darker={burstDiff.DarkerFraction:F4} " +
                      $"moved={burstDiff.MovedFraction:F4} shader='{ParticleRuntime.ResolvedShaderName}'");

            // Past the longest lifetime: everything must be gone.
            yield return new WaitForSeconds(0.55f);
            yield return null;
            Frame faded = default;
            yield return CaptureInto(f => faded = f);
            LogFrame("A.faded", faded);

            FrameDiff fadeDiff = FrameDiff.Between(baseline, faded);
            Debug.Log($"[PARTICLES] A fade-back vs control: meanAbs={fadeDiff.MeanAbs:F6} " +
                      $"brighter={fadeDiff.BrighterFraction:F4} moved={fadeDiff.MovedFraction:F4} " +
                      $"alive={ParticleRuntime.HitSparkParticleCount}");

            // Pixel verdict first - a build that never draws must fail here.
            Assert.Greater(burstDiff.BrighterFraction, MinSparkBrighter,
                $"hit spark did not draw: {burstDiff.BrighterFraction:F4} of pixels got brighter " +
                $"(gate {MinSparkBrighter:F4}). 12 sparks over a 1.2 m gap cannot be a no-op; " +
                "this is what a stripped particle shader looks like.");
            Assert.Greater(burstDiff.MeanAbs, MinSparkMeanAbs,
                $"hit spark barely moved the frame: mean |delta| {burstDiff.MeanAbs:F5} (gate {MinSparkMeanAbs:F4})");
            Assert.AreEqual(12, emitted, "SpawnHitSpark did not emit its default 12 particles");

            // ... and it must be short-lived, as the brief requires (<= 0.5 s).
            Assert.Less(fadeDiff.BrighterFraction, FadeBrighterTolerance,
                $"particles outlived their lifetime: {fadeDiff.BrighterFraction:F4} of pixels still brighter after 0.55 s");
            Assert.Less(fadeDiff.MeanAbs, FadeMeanAbsTolerance,
                $"frame did not return to the control after 0.55 s: mean |delta| {fadeDiff.MeanAbs:F6}");
            Assert.AreEqual(0, ParticleRuntime.HitSparkParticleCount,
                "particles are still alive after 0.55 s");

            // Bounded by construction: 4 pooled systems x 64.
            Assert.LessOrEqual(ParticleRuntime.HitSparkParticleCount, ParticleRuntime.SparkPoolSize * ParticleRuntime.SparkMaxParticles,
                "spark particle count exceeded the pool's hard cap");
            Assert.LessOrEqual(ParticleRuntime.SparkSystemCount, ParticleRuntime.SparkPoolSize,
                "spark pool grew past its fixed size");

            // --- frame cost (report-only: machine load decides the absolute) --
            double msOn = MeasureFrameMs(true);
            double msOff = MeasureFrameMs(false);
            Debug.Log($"[PARTICLES] frame time @{Width}x{Height}, {TimingFrames} renders, CPU submission wall clock: " +
                      $"sparksOn {msOn:F3} ms, allOff {msOff:F3} ms, delta {msOn - msOff:F3} ms " +
                      "(60fps budget 16.667 ms; editor-side submission measure, not a GPU benchmark)");
        }

        [UnityTest]
        public IEnumerator AmbientDust_IsPresentButQuiet_AndNeverExceedsItsCap()
        {
            yield return null;
            BuildRig();
            yield return null;
            yield return null;

            AnchorDustToProbeCamera();
            yield return null;

            // --- reference amplitude: effect A in the same rig ----------------
            Frame control = default;
            yield return CaptureInto(f => control = f);

            ParticleRuntime.SpawnHitSpark(ProbeOrigin);
            yield return null;
            yield return null;
            Frame spark = default;
            yield return CaptureInto(f => spark = f);
            FrameDiff sparkDiff = FrameDiff.Between(control, spark);
            Debug.Log($"[PARTICLES] B reference (spark 12): brighter={sparkDiff.BrighterFraction:F4} " +
                      $"meanAbs={sparkDiff.MeanAbs:F5}");

            yield return new WaitForSeconds(0.55f); // let A die so B is measured alone
            Frame clean = default;
            yield return CaptureInto(f => clean = f);
            FrameDiff cleanDiff = FrameDiff.Between(control, clean);
            Assert.Less(cleanDiff.MovedFraction, FadeBrighterTolerance,
                $"reference burst did not clear before the dust measurement ({cleanDiff.MovedFraction:F4})");
            LogFrame("B.cleanControl", clean);

            // --- effect B ----------------------------------------------------
            ParticleRuntime.SetAmbientDust(true);
            yield return new WaitForSeconds(2f);

            Frame dust = default;
            yield return CaptureInto(f => dust = f);
            LogFrame("B.dustOn", dust);

            FrameDiff dustDiff = FrameDiff.Between(clean, dust);
            Debug.Log($"[PARTICLES] B dust vs control: meanAbs={dustDiff.MeanAbs:F5} " +
                      $"brighter={dustDiff.BrighterFraction:F4} darker={dustDiff.DarkerFraction:F4} " +
                      $"ratioToA={(sparkDiff.BrighterFraction > 0f ? dustDiff.BrighterFraction / sparkDiff.BrighterFraction : 0f):F3}");

            Assert.Greater(dustDiff.BrighterFraction, MinDustBrighter,
                $"ambient dust is not visible at all: {dustDiff.BrighterFraction:F4} of pixels got brighter " +
                $"(gate {MinDustBrighter:F4}) - 'exists' must mean pixels, not a particleCount");
            Assert.Less(dustDiff.MeanAbs, sparkDiff.MeanAbs * DustToSparkMaxMeanAbsRatio,
                $"ambient dust is as loud as a hit effect: mean |delta| {dustDiff.MeanAbs:F5} vs spark " +
                $"{sparkDiff.MeanAbs:F5} (ratio gate {DustToSparkMaxMeanAbsRatio:F2}). Dust may cover more " +
                "pixels than a burst, but it must not carry comparable energy.");
            Assert.Less(dust.Stats.MaxLuma, spark.Stats.MaxLuma,
                $"a dust mote is as bright as a spark: max luma {dust.Stats.MaxLuma:F3} vs {spark.Stats.MaxLuma:F3}");
            Assert.IsTrue(ParticleRuntime.IsAmbientDustOn, "ambient dust reports itself as off after being turned on");

            // --- cap: sample a full 8 s of wall clock ------------------------
            // 12/s with a 6-9 s lifetime must settle around 72-108 particles.
            // The gate is the structural cap (200); the point of the sample is
            // that the count converges instead of growing (no accumulation).
            int peakDust = 0;
            int peakTotal = 0;
            float until = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                peakDust = Mathf.Max(peakDust, ParticleRuntime.AmbientParticleCount);
                peakTotal = Mathf.Max(peakTotal, ParticleRuntime.TotalParticleCount);
                if (ParticleRuntime.AmbientParticleCount > ParticleRuntime.AmbientMaxParticles)
                {
                    Assert.Fail($"dust particleCount {ParticleRuntime.AmbientParticleCount} exceeded maxParticles " +
                                $"{ParticleRuntime.AmbientMaxParticles} after {8f - (until - Time.realtimeSinceStartup):F1} s");
                }
            }

            Debug.Log($"[PARTICLES] B cap sample (8 s wall): peakDust={peakDust} peakTotal={peakTotal} " +
                      $"settled={ParticleRuntime.AmbientParticleCount} cap={ParticleRuntime.AmbientMaxParticles}");
            Assert.LessOrEqual(peakDust, ParticleRuntime.AmbientMaxParticles,
                $"dust peaked at {peakDust} particles, cap is {ParticleRuntime.AmbientMaxParticles}");
            Assert.Greater(peakDust, 30,
                $"dust did not reach its steady state in 8 s (peak {peakDust}) - the rate/lifetime pair is wrong");

            // Turning B off must actually remove it (and leave the switch alone).
            ParticleRuntime.SetAmbientDust(false);
            Assert.IsFalse(ParticleRuntime.IsAmbientDustOn, "ambient dust stayed on after SetAmbientDust(false)");
            yield return null;
            Assert.AreEqual(0, ParticleRuntime.AmbientParticleCount,
                "dust particles survived being turned off");
        }

        [UnityTest]
        public IEnumerator Disabled_EmitsNothing_AndThePoolStaysBounded()
        {
            yield return null;
            BuildRig();
            yield return null;
            yield return null;

            // Ask for dust as well: "off" has to beat the ambient emitter too.
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(true);
            ParticleRuntime.SetAmbientAnchor(_camera);
            yield return null;

            ParticleRuntime.SetEnabled(false);
            yield return null;
            yield return null;

            Frame baseline = default;
            yield return CaptureInto(f => baseline = f);

            // 10 spawn calls while disabled: all must be no-ops.
            for (int i = 0; i < 10; i++)
            {
                ParticleRuntime.SpawnHitSpark(ProbeOrigin + new Vector3(0.01f * i, 0f, 0f));
            }

            yield return null;
            yield return null;

            Frame afterSpawn = default;
            yield return CaptureInto(f => afterSpawn = f);
            FrameDiff disabledDiff = FrameDiff.Between(baseline, afterSpawn);
            Debug.Log($"[PARTICLES] disabled spawn vs baseline: meanAbs={disabledDiff.MeanAbs:F6} " +
                      $"moved={disabledDiff.MovedFraction:F4} total={ParticleRuntime.TotalParticleCount} " +
                      $"lastEmitted={ParticleRuntime.LastSpawnEmitted}");

            Assert.AreEqual(0, ParticleRuntime.TotalParticleCount,
                "particles exist while ParticleRuntime.Enabled == false");
            Assert.AreEqual(0, ParticleRuntime.LastSpawnEmitted,
                "SpawnHitSpark reported an emission while disabled");
            Assert.Less(disabledDiff.MovedFraction, DisabledMovedTolerance,
                $"pixels moved while particles were disabled: {disabledDiff.MovedFraction:F4} " +
                $"(tolerance {DisabledMovedTolerance:F4}, control frames are bit-identical)");
            Assert.Less(disabledDiff.MeanAbs, FadeMeanAbsTolerance,
                $"frame changed while particles were disabled: mean |delta| {disabledDiff.MeanAbs:F6}");

            // --- leak: 100 spawns may not grow the instance count ------------
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false);
            yield return null;
            yield return null;

            int before = CountParticleSystems();
            for (int i = 0; i < 100; i++)
            {
                ParticleRuntime.SpawnHitSpark(ProbeOrigin + new Vector3(0.02f * (i % 3), 0.01f * (i % 5), 0f), 12);
                if (i % 5 == 0)
                {
                    yield return null;
                }
            }

            yield return null;
            yield return null;

            int after = CountParticleSystems();
            int alive = ParticleRuntime.TotalParticleCount;
            Debug.Log($"[PARTICLES] 100 spawns: particleSystems {before} -> {after}, alive={alive}, " +
                      $"pool={ParticleRuntime.SparkSystemCount}/{ParticleRuntime.SparkPoolSize}, " +
                      $"perSystemCap={ParticleRuntime.SparkMaxParticles}");

            Assert.LessOrEqual(after - before, ParticleRuntime.SparkPoolSize,
                $"pool leaked: {after - before} new ParticleSystem instances for 100 spawns " +
                $"(the pool may only ever create {ParticleRuntime.SparkPoolSize})");
            Assert.LessOrEqual(after, before + ParticleRuntime.SparkPoolSize,
                "ParticleSystem instance count is not bounded by the pool size");
            Assert.LessOrEqual(alive, ParticleRuntime.SparkPoolSize * ParticleRuntime.SparkMaxParticles,
                $"alive particles {alive} exceeded the pool's hard cap " +
                $"{ParticleRuntime.SparkPoolSize * ParticleRuntime.SparkMaxParticles}");
        }

        /// <summary>
        /// The fallback chain is not decoration: with the URP particle shader
        /// unavailable (which is exactly what a player build looks like when the
        /// shader is stripped) the second candidate must still put pixels on
        /// screen. Without this case a broken fallback would only show up as
        /// "particles vanish in the build", i.e. after shipping.
        /// </summary>
        [UnityTest]
        public IEnumerator FallbackShader_StillDraws_WhenThePreferredShaderIsMissing()
        {
            yield return null;
            BuildRig();
            yield return null;
            yield return null;

            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false);
            ParticleRuntime.SetShaderCandidates(
                "__R905_Missing_URP_Particles__",
                "Legacy Shaders/Particles/Alpha Blended",
                "Sprites/Default");
            yield return null;

            Frame control = default;
            yield return CaptureInto(f => control = f);

            // Resolution is lazy (first spawn), so the burst comes before the
            // structural assert - and the pixel verdict stays first anyway.
            ParticleRuntime.SpawnHitSpark(ProbeOrigin);
            yield return null;
            yield return null;
            Frame burst = default;
            yield return CaptureInto(f => burst = f);

            FrameDiff diff = FrameDiff.Between(control, burst);
            Debug.Log($"[PARTICLES] fallback draw: shader='{ParticleRuntime.ResolvedShaderName}' " +
                      $"brighter={diff.BrighterFraction:F4} meanAbs={diff.MeanAbs:F5} " +
                      $"probe='{ParticleRuntime.ShaderProbeReport}'");

            // Gate is deliberately low: this asserts "the fallback is not a
            // black hole", not "the fallback looks as good as URP".
            Assert.Greater(diff.BrighterFraction, 0.002f,
                $"fallback shader drew nothing: {diff.BrighterFraction:F4} of pixels moved (gate 0.0020) - " +
                $"probe='{ParticleRuntime.ShaderProbeReport}'");
            Assert.Greater(diff.MeanAbs, 0.0002f,
                $"fallback shader frame delta too small: mean |delta| {diff.MeanAbs:F5}");
            Assert.AreEqual("Legacy Shaders/Particles/Alpha Blended", ParticleRuntime.ResolvedShaderName,
                $"the fallback chain did not fall through to the built-in particle shader " +
                $"(probe='{ParticleRuntime.ShaderProbeReport}')");
            Assert.IsFalse(ParticleRuntime.SelfDisabled, "runtime self-disabled even though a candidate resolved");
        }

        // ---- rig ------------------------------------------------------------
        private void BuildRig()
        {
            _rig = new GameObject("~ParticlePixelRig");

            var camGo = new GameObject("~ParticleProbeCamera");
            camGo.transform.SetParent(_rig.transform, false);
            camGo.transform.position = ProbeOrigin + new Vector3(0f, 0f, -BurstDistance);
            camGo.transform.rotation = Quaternion.identity; // looking +Z, at the burst point
            _camera = camGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            _camera.allowHDR = true;
            _camera.allowMSAA = false;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 20f;
            _camera.fieldOfView = 60f;

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.name = "~ParticleProbeTarget";
            _target.Create();

            _camera.targetTexture = _target;
            _camera.enabled = true;
        }

        /// <summary>Dust follows an explicit anchor: the probe camera, not the scene one.</summary>
        private void AnchorDustToProbeCamera()
        {
            ParticleRuntime.SetEnabled(true);
            ParticleRuntime.SetAmbientDust(false);
            ParticleRuntime.SetAmbientAnchor(_camera);
        }

        private IEnumerator CaptureInto(Action<Frame> sink)
        {
            yield return null;
            sink(ReadBack());
        }

        private Frame ReadBack()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _target;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            tex.Apply(false, false);
            float[] lumas = LumaOf(tex.GetPixels());
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(tex);
            return new Frame(lumas);
        }

        private static int CountParticleSystems()
        {
            return Resources.FindObjectsOfTypeAll<ParticleSystem>().Length;
        }

        /// <summary>
        /// 30 renders with a fresh burst every frame vs 30 renders with the
        /// runtime switched off. CPU submission wall clock only (no GPU wait),
        /// which is exactly the slice's "does spawning cost us anything" question.
        /// </summary>
        private double MeasureFrameMs(bool particlesOn)
        {
            ParticleRuntime.SetEnabled(particlesOn);
            _camera.Render(); // warm-up

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < TimingFrames; i++)
            {
                if (particlesOn)
                {
                    ParticleRuntime.SpawnHitSpark(ProbeOrigin);
                }

                _camera.Render();
                GL.Flush();
            }

            stopwatch.Stop();
            ParticleRuntime.SetEnabled(true);
            return stopwatch.Elapsed.TotalMilliseconds / TimingFrames;
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

        private static void LogFrame(string label, Frame frame)
        {
            ImageStats s = frame.Stats;
            Debug.Log($"[PARTICLES] {label}: meanLuma={s.MeanLuma:F5} stdLuma={s.StdLuma:F5} " +
                      $"min={s.MinLuma:F4} max={s.MaxLuma:F4} p95={s.P95Luma:F4}");
        }

        // ---- statistics -----------------------------------------------------

        private readonly struct ImageStats
        {
            public readonly float MeanLuma;
            public readonly float StdLuma;
            public readonly float P95Luma;
            public readonly float MinLuma;
            public readonly float MaxLuma;

            public ImageStats(float mean, float std, float p95, float min, float max)
            {
                MeanLuma = mean;
                StdLuma = std;
                P95Luma = p95;
                MinLuma = min;
                MaxLuma = max;
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
                for (int i = 0; i < count; i++)
                {
                    float luma = lumas[i];
                    sum += luma;
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

                Stats = new ImageStats(mean, Mathf.Sqrt((float)(variance / count)), sorted[p95], min, max);
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
