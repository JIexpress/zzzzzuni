using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using static ZFrame.PlayMode.Tests.PixelProbeRig;
using UnityEngine.TestTools;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-08 guard: the grade Volume crossfade must be falsifiable on
    /// real rendered pixels, not on "weights moved". Same defect class as
    /// A-R9-04/05/06 (configuration that reads back fine while the screen does
    /// not change).
    ///
    /// Rig = the shared <see cref="PixelProbeRig"/> plus the A-R9-04 emitter:
    /// an offscreen probe camera parked 5000 units from the gameplay scene,
    /// one URP-Unlit cube at radiance 4.0 (well above the 0.9 Bloom threshold
    /// in Vol_Medium/Vol_High), ARGBFloat readback. Post-processing is switched
    /// on through <see cref="PostFxCameraRuntime"/> — the profiles only reach
    /// pixels through it.
    ///
    /// The three verdicts (briefing design point 4):
    ///  1. FRAME-COUNT: a hard cut (duration 0) lands on the target picture
    ///     with the very first rendered frame and never moves again, while the
    ///     equal-power fade over 0.8s shows >= 3 intermediate frames that
    ///     differ from BOTH endpoints and approach the target — the 方案 §6.4
    ///     "渐变 not 硬切" evidence.
    ///  2. CONSISTENCY: the fade's settled frame equals the direct snap's
    ///     frame (mean |delta| and moved fraction at float-noise level) — the
    ///     weight-0 second Volume and the rest-shape collapse are exact no-ops.
    ///  3. ABORT: a fade aborted mid-flight lands on the direct-target frame
    ///     in a single frame (the intent already switched; aborting the
    ///     presentation commits it).
    /// </summary>
    public sealed class VolumeCrossfadePixelEvidencePlayModeTests
    {
        /// <summary>Realtime ceiling for waiting a fade out (batch machines are slow; hang > fail).</summary>
        private const float FadeSettleBudgetSeconds = 10f;

        /// <summary>Realtime ceiling for the per-frame sampling loop of one fade.</summary>
        private const float FadeSampleBudgetSeconds = 8f;

        // ---- gates ----------------------------------------------------------
        // Calibrated against the A-R9-04 measurements (Medium on-vs-off meanAbs
        // 0.0336, High 0.0563 — the Medium→High direct switch moves the frame
        // by the same order). A reverted crossfade degenerates into the hard
        // cut: intermediate count drops to 0, so "works" and "broken" never
        // overlap. Measured values are restated in the assertion messages.
        private const float MinSwitchBrighter = 0.012f;

        /// <summary>An intermediate frame must differ from BOTH endpoints by more than this (mean |delta|).</summary>
        private const float IntermediateEpsilon = 0.0015f;

        /// <summary>Frame-count gate: the fade must show at least this many intermediate frames.</summary>
        private const int MinIntermediateFrames = 3;

        /// <summary>Consistency: the settled fade frame vs the direct snap — float noise only.</summary>
        private const float ConsistencyMeanAbsTolerance = 0.002f;
        private const float ConsistencyMovedTolerance = 0.002f;

        private PixelProbeRig _rig;
        private GameObject _emitter;
        private Material _emitterMaterial;
        private RenderTexture _target;

        [TearDown]
        public void TearDown()
        {
            if (_rig != null)
            {
                _rig.Dispose();
                _rig = null;
            }

            if (_emitter != null)
            {
                UnityEngine.Object.Destroy(_emitter);
                _emitter = null;
            }

            if (_emitterMaterial != null)
            {
                UnityEngine.Object.Destroy(_emitterMaterial);
                _emitterMaterial = null;
            }

            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }

            PostFxCameraRuntime.SetEnabled(true); // leave the shipped default on
            PipelineManager.SetTier(QualityTier.Medium);
        }

        [UnityTest]
        public IEnumerator GradeCrossfade_TransitionsOverMultipleFrames_AndMatchesTheDirectTarget()
        {
            yield return SettleAt(QualityTier.Medium);
            BuildRig();
            yield return null;
            yield return null;

            var manager = PipelineManager.Instance;
            PipelineTierTable table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assert.IsNotNull(table, "the tier table must be loadable for the Medium/High profiles");
            VolumeProfile mediumProfile = table.GetVolumeProfile(QualityTier.Medium);
            VolumeProfile highProfile = table.GetVolumeProfile(QualityTier.High);

            // ---- the control frame (Medium settled) --------------------------
            PixelProbeRig.Frame medium = default;
            yield return _rig.CaptureInto(f => medium = f);
            // The post-processing path clamps the camera's HDR band to ~1.0
            // (A-R9-04's documented final-blit behaviour), so the 4.0 emitter
            // reads at the ceiling — 0.9 still proves it dominates the 0.02
            // background, which is all the rig sanity needs.
            Assert.Greater(medium.Stats.MaxLuma, 0.9f,
                $"rig sanity: the 4.0 emitter is not visible (max luma {medium.Stats.MaxLuma:F3})");

            // ---- arm 1: the HARD CUT (duration 0) ----------------------------
            manager.CrossfadeToProfile(highProfile, 0f);
            Assert.IsFalse(manager.IsGradeCrossfading, "duration 0 IS a hard cut — no fade may run");

            PixelProbeRig.Frame hardCut = default;
            yield return _rig.CaptureInto(f => hardCut = f);
            PixelProbeRig.Frame hardCutStable = default;
            yield return _rig.CaptureInto(f => hardCutStable = f);

            PixelProbeRig.FrameDiff switchDiff = PixelProbeRig.FrameDiff.Between(medium, hardCut);
            PixelProbeRig.FrameDiff hardCutDrift = PixelProbeRig.FrameDiff.Between(hardCut, hardCutStable);
            Debug.Log($"[VOLXFADE] hard cut Medium->High: brighter={switchDiff.BrighterFraction:F4} " +
                      $"meanAbs={switchDiff.MeanAbs:F5}; drift after the cut: meanAbs={hardCutDrift.MeanAbs:F6} " +
                      $"moved={hardCutDrift.MovedFraction:F4}");

            Assert.Greater(switchDiff.BrighterFraction, MinSwitchBrighter,
                $"the Medium->High switch did not move the frame (brighter {switchDiff.BrighterFraction:F4}, " +
                "gate 0.0120) — Bloom 2.5 + ColorAdjustments vs Bloom 1.0 cannot be a no-op");
            Assert.Less(hardCutDrift.MovedFraction, ConsistencyMovedTolerance,
                $"the hard cut kept changing after the first frame ({hardCutDrift.MovedFraction:F4} moved) — " +
                "a hard cut must land with the FIRST rendered frame (硬切 1 帧到位)");

            // ---- arm 2: the FADE (equal-power, 0.8s) --------------------------
            manager.CrossfadeToProfile(mediumProfile, 0f);
            yield return null;
            PixelProbeRig.Frame backAtMedium = default;
            yield return _rig.CaptureInto(f => backAtMedium = f);
            PixelProbeRig.FrameDiff snapBackDiff = PixelProbeRig.FrameDiff.Between(medium, backAtMedium);
            Debug.Log($"[VOLXFADE] snap back to Medium: meanAbs={snapBackDiff.MeanAbs:F6} moved={snapBackDiff.MovedFraction:F4}");
            Assert.Less(snapBackDiff.MeanAbs, ConsistencyMeanAbsTolerance,
                $"the settled pair (weight-0 second Volume + collapsed rest shape) is not pixel-identical to the " +
                $"never-faded Medium frame: mean |delta| {snapBackDiff.MeanAbs:F6}");

            manager.CrossfadeToProfile(highProfile, PipelineManager.DefaultCrossfadeDuration);
            Assert.IsTrue(manager.IsGradeCrossfading, "the fade must be running after the switch");

            var intermediates = new List<FrameSample>();
            var allSamples = new List<FrameSample>();
            PixelProbeRig.Frame final = default;
            float deadline = Time.realtimeSinceStartup + FadeSampleBudgetSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                PixelProbeRig.Frame frame = default;
                yield return _rig.CaptureInto(f => frame = f);
                bool fading = manager.IsGradeCrossfading;
                var sample = new FrameSample(
                    allSamples.Count,
                    manager.GradeVolumeAlt != null ? manager.GradeVolumeAlt.weight : -1f,
                    PixelProbeRig.FrameDiff.Between(medium, frame).MeanAbs,
                    PixelProbeRig.FrameDiff.Between(hardCut, frame).MeanAbs,
                    fading);
                allSamples.Add(sample);

                if (!fading)
                {
                    final = frame;
                    break;
                }

                if (sample.DistToStart > IntermediateEpsilon && sample.DistToTarget > IntermediateEpsilon)
                {
                    intermediates.Add(sample);
                }
            }

            Assert.IsFalse(manager.IsGradeCrossfading, "the fade must settle within the sampling budget");
            LogSamples("fade Medium->High", allSamples);

            Assert.GreaterOrEqual(intermediates.Count, MinIntermediateFrames,
                $"the fade showed only {intermediates.Count} intermediate frames (gate {MinIntermediateFrames}) — " +
                "a 0.8s equal-power crossfade cannot collapse to a hard cut");

            // The sampled sequence must APPROACH the target: the last
            // intermediate is measurably closer than the first one.
            Assert.Greater(intermediates[0].DistToTarget, intermediates[intermediates.Count - 1].DistToTarget,
                $"the intermediate frames do not approach the target ({intermediates[0].DistToTarget:F5} -> " +
                $"{intermediates[intermediates.Count - 1].DistToTarget:F5})");

            // ---- arm 3: CONSISTENCY — the fade's settled frame IS the direct target
            PixelProbeRig.FrameDiff consistency = PixelProbeRig.FrameDiff.Between(final, hardCut);
            Debug.Log($"[VOLXFADE] consistency (fade end vs direct snap): meanAbs={consistency.MeanAbs:F6} " +
                      $"moved={consistency.MovedFraction:F4}");
            Assert.Less(consistency.MeanAbs, ConsistencyMeanAbsTolerance,
                $"the finished fade does not equal the directly-set target: mean |delta| {consistency.MeanAbs:F6} " +
                "(gate 0.0020) — 渐变完成必须落在与硬切相同的像素上");
            Assert.Less(consistency.MovedFraction, ConsistencyMovedTolerance,
                $"pixels still differ after the fade settled: moved {consistency.MovedFraction:F4}");
        }

        [UnityTest]
        public IEnumerator GradeCrossfade_Abort_LandsOnTheDirectTargetImmediately()
        {
            yield return SettleAt(QualityTier.Medium);
            BuildRig();
            yield return null;
            yield return null;

            var manager = PipelineManager.Instance;
            PipelineTierTable table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assert.IsNotNull(table, "the tier table must be loadable for the Medium/High profiles");

            // Reference: the direct target picture (hard cut).
            PixelProbeRig.Frame medium = default;
            yield return _rig.CaptureInto(f => medium = f);
            manager.CrossfadeToProfile(table.GetVolumeProfile(QualityTier.High), 0f);
            PixelProbeRig.Frame directTarget = default;
            yield return _rig.CaptureInto(f => directTarget = f);
            Assert.Greater(PixelProbeRig.FrameDiff.Between(medium, directTarget).MeanAbs, IntermediateEpsilon,
                "rig sanity: the Medium->High switch must move pixels for this evidence to mean anything");

            // Snap back, then start a LONG fade (10s — three frames cannot finish it).
            manager.CrossfadeToProfile(table.GetVolumeProfile(QualityTier.Medium), 0f);
            yield return null;
            manager.CrossfadeToProfile(table.GetVolumeProfile(QualityTier.High), 10f);
            Assert.IsTrue(manager.IsGradeCrossfading, "precondition: the long fade is running");
            yield return null;
            yield return null;
            yield return null;
            Assert.IsTrue(manager.IsGradeCrossfading, "precondition: still mid-fade after 3 frames");
            Assert.Greater(manager.GradeVolume.weight, 0f, "precondition: the outgoing slot is mid-flight");
            Assert.Less(manager.GradeVolumeAlt.weight, 1f, "precondition: the incoming slot has not landed");
            Debug.Log($"[VOLXFADE] abort arm mid-flight weights: A={manager.GradeVolume.weight:F4} " +
                      $"B={manager.GradeVolumeAlt.weight:F4}");

            manager.AbortGradeCrossfade();
            Assert.IsFalse(manager.IsGradeCrossfading, "abort settles immediately");

            PixelProbeRig.Frame aborted = default;
            yield return _rig.CaptureInto(f => aborted = f);
            PixelProbeRig.FrameDiff abortDiff = PixelProbeRig.FrameDiff.Between(aborted, directTarget);
            Debug.Log($"[VOLXFADE] abort vs direct target: meanAbs={abortDiff.MeanAbs:F6} moved={abortDiff.MovedFraction:F4}");
            Assert.Less(abortDiff.MeanAbs, ConsistencyMeanAbsTolerance,
                $"the aborted fade did not land on the directly-set target: mean |delta| {abortDiff.MeanAbs:F6} " +
                "(gate 0.0020) — abort commits the intent in ONE frame");
            Assert.Less(abortDiff.MovedFraction, ConsistencyMovedTolerance,
                $"pixels still differ after abort: moved {abortDiff.MovedFraction:F4}");
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>
        /// Brings the manager to <paramref name="tier"/> and waits until any
        /// fade (e.g. one started by a previous test's teardown) has settled.
        /// </summary>
        private static IEnumerator SettleAt(QualityTier tier)
        {
            yield return null; // let BeforeSceneLoad bootstraps finish their first apply
            PipelineManager.EnsureInstance();
            PipelineManager.SetTier(tier);

            float deadline = Time.realtimeSinceStartup + FadeSettleBudgetSeconds;
            while (PipelineManager.Instance.IsGradeCrossfading && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsFalse(PipelineManager.Instance.IsGradeCrossfading,
                "the grade crossfade did not settle within the budget");
        }

        /// <summary>
        /// Probe rig = the shared PixelProbeRig (camera parked at FarPark) plus
        /// the A-R9-04 emitter: one URP-Unlit cube at radiance 4.0, above the
        /// 0.9 Bloom threshold of both grade profiles.
        /// </summary>
        private void BuildRig()
        {
            _rig = new PixelProbeRig();
            _rig.Build(PixelProbeRig.FarPark + new Vector3(0f, 0f, -3f), PixelProbeRig.FarPark);

            _emitter = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _emitter.name = "~VolCrossfadeProbeEmitter";
            _emitter.transform.position = PixelProbeRig.FarPark;
            _emitter.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            Collider emitterCollider = _emitter.GetComponent<Collider>();
            if (emitterCollider != null)
            {
                UnityEngine.Object.Destroy(emitterCollider);
            }

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.IsNotNull(unlit, "URP unlit shader not found — the probe needs a deterministic emitter");
            _emitterMaterial = new Material(unlit) { name = "~VolCrossfadeProbeMaterial" };
            _emitterMaterial.SetColor("_BaseColor", new Color(4f, 4f, 4f, 1f));
            _emitter.GetComponent<MeshRenderer>().sharedMaterial = _emitterMaterial;

            // Post-processing is the only path from the profiles to the pixels;
            // SetEnabled applies to every game camera, the fresh probe included.
            PostFxCameraRuntime.SetEnabled(true);
            Assert.IsTrue(PostFxCameraRuntime.IsPostProcessingOn(_rig.Camera),
                "the probe camera must render with post-processing for the profiles to reach pixels");
        }

        private static void LogSamples(string label, List<FrameSample> samples)
        {
            var builder = new StringBuilder();
            builder.Append($"[VOLXFADE] {label}: {samples.Count} sampled frames\n");
            foreach (FrameSample sample in samples)
            {
                builder.Append($"  frame {sample.Index:D3}: wTo={sample.WeightTo:F4} " +
                               $"distStart={sample.DistToStart:F5} distTarget={sample.DistToTarget:F5}" +
                               (sample.Fading ? "" : "  <- settled") + "\n");
            }

            Debug.Log(builder.ToString());
        }

        private readonly struct FrameSample
        {
            public readonly int Index;
            public readonly float WeightTo;
            public readonly float DistToStart;
            public readonly float DistToTarget;
            public readonly bool Fading;

            public FrameSample(int index, float weightTo, float distToStart, float distToTarget, bool fading)
            {
                Index = index;
                WeightTo = weightTo;
                DistToStart = distToStart;
                DistToTarget = distToTarget;
                Fading = fading;
            }
        }
    }
}
