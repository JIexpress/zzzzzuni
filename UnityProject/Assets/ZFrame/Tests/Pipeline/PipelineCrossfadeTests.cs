using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the grade crossfade on the manager
    /// (TASK-A-R9-08): the two stacked Volume slots, the intent-switches-first
    /// semantics (GetTier returns the new tier/profile immediately — the fade
    /// is presentation), the equal-power advance to a settled canonical rest
    /// shape, Abort, mid-fade re-base continuity on both dominance branches,
    /// the scene-style <see cref="PipelineManager.CrossfadeToProfile"/>
    /// interface and the VolumeCrossfadeHost pump + Shutdown settle. Fades are
    /// advanced with the same public pump the host uses; no play session is
    /// needed and no asset is touched (weights are component state).
    /// </summary>
    public sealed class PipelineCrossfadeTests
    {
        private const float HalfSqrt2 = 0.70710678f;

        private int _initialQualityLevel;
        private int _initialStoredTier;
        private bool _hadStoredTier;
        private PipelineTierTable _table;
        private VolumeCrossfadeHost _host;

        [SetUp]
        public void SetUp()
        {
            _initialQualityLevel = QualitySettings.GetQualityLevel();
            _hadStoredTier = PlayerPrefs.HasKey(TierMapper.SettingsKey);
            _initialStoredTier = PlayerPrefs.GetInt(TierMapper.SettingsKey, -1);

            // Defensive: tests here assume a FRESH manager (the first profile
            // application snaps). Any manager left over by another fixture
            // would make SetTier fade instead.
            if (PipelineManager.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(PipelineManager.Instance.gameObject);
            }

            _table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assume.That(_table, Is.Not.Null, $"Resources/{PipelineManager.TableResourcePath} must exist");
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                _host.Shutdown(); // unregisters + settles any fade it left running
                UnityEngine.Object.DestroyImmediate(_host.gameObject);
                _host = null;
            }

            if (QualitySettings.GetQualityLevel() != _initialQualityLevel &&
                _initialQualityLevel >= 0 && _initialQualityLevel < QualitySettings.names.Length)
            {
                QualitySettings.SetQualityLevel(_initialQualityLevel, false);
            }

            if (_hadStoredTier)
            {
                PlayerPrefs.SetInt(TierMapper.SettingsKey, _initialStoredTier);
            }
            else
            {
                PlayerPrefs.DeleteKey(TierMapper.SettingsKey);
            }

            PlayerPrefs.Save();

            if (PipelineManager.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(PipelineManager.Instance.gameObject);
            }
        }

        /// <summary>Snaps the grade to a tier (fresh manager: the first application never fades).</summary>
        private static void SnapToTier(QualityTier tier)
        {
            PipelineManager.SetTier(tier);
            Assert.That(PipelineManager.Instance.IsGradeCrossfading, Is.False,
                "the first profile application on a fresh manager must snap (no previous picture to fade from)");
        }

        private VolumeProfile Profile(QualityTier tier)
        {
            return _table.GetVolumeProfile(tier);
        }

        private static void Pump(PipelineManager manager, float totalSeconds, float step)
        {
            for (float t = 0f; t < totalSeconds; t += step)
            {
                manager.TickGradeCrossfade(step);
            }
        }

        // ---- SetTier starts a fade; the intent switches first ----------------------

        [Test]
        public void SetTier_StartsFade_AndSwitchesIntentImmediately()
        {
            SnapToTier(QualityTier.High);

            PipelineManager.SetTier(QualityTier.Low);

            var manager = PipelineManager.Instance;
            Assert.That(manager.IsGradeCrossfading, Is.True, "a tier switch must fade (方案 §6.4)");
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Low),
                "GetTier semantics: the tier is the intent and returns the NEW tier immediately");
            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.Low)),
                "CurrentGradeProfile is the fade TARGET, immediately");

            // Slot layout per the briefing: A = old profile @ 1, B = new @ 0.
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.High)),
                "slot A still carries the outgoing profile while the fade runs");
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.sharedProfile, Is.EqualTo(Profile(QualityTier.Low)),
                "slot B carries the incoming profile");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.isGlobal, Is.True, "both slots must be global");
        }

        [Test]
        public void SetTier_SameTierWhileSettled_DoesNotRestartFade()
        {
            SnapToTier(QualityTier.High);

            PipelineManager.SetTier(QualityTier.High); // idempotent re-apply

            Assert.That(PipelineManager.Instance.IsGradeCrossfading, Is.False,
                "a request equal to the current intent must not start a fade");
            Assert.That(PipelineManager.Instance.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
        }

        // ---- fade advance, midpoint, completion -------------------------------------

        [Test]
        public void GradeCrossfade_AdvancesThroughMidpoint_AndSettlesAtCanonicalRestShape()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;
            PipelineManager.SetTier(QualityTier.Low);

            Pump(manager, PipelineManager.DefaultCrossfadeDuration / 2f, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.True);
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(HalfSqrt2).Within(1e-3f),
                "midpoint: both slots carry cos(45°) — the equal-power curve on real volumes");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(HalfSqrt2).Within(1e-3f));

            Pump(manager, PipelineManager.DefaultCrossfadeDuration, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.False, "the fade must complete");

            // 停稳 at the canonical rest shape: the settled profile back on slot A.
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Low)));
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f),
                "at rest slot A carries the settled profile at full weight (A-M2-03 reading preserved)");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.Low)));

            Pump(manager, 1f, 0.1f); // extra ticks change nothing
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
        }

        // ---- abort -----------------------------------------------------------------

        [Test]
        public void AbortGradeCrossfade_LandsTargetAtFullWeight_Immediately()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;
            PipelineManager.SetTier(QualityTier.Low);
            Pump(manager, 0.2f, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.True, "precondition: mid-fade");
            Assert.That(manager.GradeVolume.weight, Is.LessThan(1f), "precondition: slot A already faded down");

            manager.AbortGradeCrossfade();

            Assert.That(manager.IsGradeCrossfading, Is.False);
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Low)),
                "abort lands the TARGET profile on slot A");
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f),
                "the target ends at FULL weight (Shutdown 恢复语义 shares this primitive)");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));

            manager.AbortGradeCrossfade(); // idempotent
            Assert.That(manager.IsGradeCrossfading, Is.False);
        }

        // ---- mid-fade re-base continuity ---------------------------------------------

        [Test]
        public void SetTier_MidFade_EarlyInterrupt_KeepsDominantFromSlotContinuous()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;
            PipelineManager.SetTier(QualityTier.Low);
            Pump(manager, 0.2f, 0.05f); // 0.2s of 0.8s = phase 0.25 → from-side dominant (cos 22.5° ≈ 0.924)

            float expectedFrom = 0.92388f;
            float expectedTo = 0.38268f;
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(expectedFrom).Within(1e-3f), "precondition: phase 0.25");

            PipelineManager.SetTier(QualityTier.Medium); // interrupt toward a third profile

            Assert.That(manager.IsGradeCrossfading, Is.True);
            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.Medium)),
                "the intent still switches immediately");
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.High)),
                "the dominant from slot keeps its profile (no pop on the dominant picture)");
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(expectedFrom).Within(1e-4f),
                "the dominant slot's weight path is continuous");
            Assert.That(manager.GradeVolumeAlt.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)),
                "the new profile takes the weaker slot");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(expectedTo).Within(1e-4f),
                "the weaker slot's weight is preserved (only its content swaps)");

            Pump(manager, PipelineManager.DefaultCrossfadeDuration + 0.1f, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.False);
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)));
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void SetTier_MidFade_LateInterrupt_OldTargetBecomesTheNewFrom()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;
            PipelineManager.SetTier(QualityTier.Low);
            Pump(manager, 0.6f, 0.05f); // 0.6s of 0.8s = phase 0.75 → to-side dominant (sin 67.5° ≈ 0.924)

            float expectedDominant = 0.92388f;
            float expectedWeak = 0.38268f;
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(expectedDominant).Within(1e-3f),
                "precondition: the to slot (B) dominates");

            PipelineManager.SetTier(QualityTier.Medium); // late interrupt

            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.Medium)));
            Assert.That(manager.GradeVolumeAlt.sharedProfile, Is.EqualTo(Profile(QualityTier.Low)),
                "the old target (dominant) becomes the new from slot, content unchanged");
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(expectedDominant).Within(1e-4f),
                "the dominant slot's weight path is continuous across the re-base");
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)),
                "the new profile takes the now-weaker slot");
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(expectedWeak).Within(1e-4f),
                "mirrored phase: cos(π/2 − θ) = sin(θ) keeps the weaker slot's weight too");

            Pump(manager, PipelineManager.DefaultCrossfadeDuration + 0.1f, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.False);
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)));
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
        }

        // ---- the independent scene-style interface -------------------------------------

        [Test]
        public void CrossfadeToProfile_FadesGrade_WithoutTouchingTierSemantics()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;

            manager.CrossfadeToProfile(Profile(QualityTier.Medium), 0.4f);

            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.High),
                "scene-style fades carry no tier semantics");
            Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo((int)QualityTier.High),
                "quality level untouched");
            Assert.That(PlayerPrefs.GetInt(TierMapper.SettingsKey, -1), Is.EqualTo((int)QualityTier.High),
                "persistence untouched");
            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.Medium)),
                "the grade intent follows immediately");
            Assert.That(manager.IsGradeCrossfading, Is.True);

            Pump(manager, 0.4f, 0.05f);
            Assert.That(manager.IsGradeCrossfading, Is.False);
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)));
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void CrossfadeToProfile_ZeroDuration_SnapsImmediately()
        {
            SnapToTier(QualityTier.Medium);
            var manager = PipelineManager.Instance;

            manager.CrossfadeToProfile(Profile(QualityTier.High), 0f);

            Assert.That(manager.IsGradeCrossfading, Is.False, "duration <= 0 IS a hard cut");
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.High)));
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void CrossfadeToProfile_Null_IsRejectedWithoutStateChange()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;

            manager.CrossfadeToProfile(null, 0.4f);

            Assert.That(manager.CurrentGradeProfile, Is.EqualTo(Profile(QualityTier.High)));
            Assert.That(manager.IsGradeCrossfading, Is.False);
        }

        [Test]
        public void CrossfadeToProfile_SameAsCurrentIntent_IsANoOp()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;

            manager.CrossfadeToProfile(Profile(QualityTier.High), 0.4f);

            Assert.That(manager.IsGradeCrossfading, Is.False,
                "already the intent (settled) — no restart, matching the audio crossfade reference");
        }

        // ---- the host adapter ------------------------------------------------------------

        [Test]
        public void VolumeCrossfadeHost_PumpsTheManager_AndShutdownSettlesTargetFullWeight()
        {
            SnapToTier(QualityTier.High);
            var manager = PipelineManager.Instance;

            GameRoot root = GameRoot.EnsureInstance();
            if (!GameRoot.IsBootstrapped)
            {
                root.Initialize();
            }

            ITickQueue queue = Services.Get<ITickQueue>();
            int viewCountBefore = queue.Count(TickStage.View);

            _host = new GameObject("~VolumeCrossfadeTestHost").AddComponent<VolumeCrossfadeHost>();
            _host.Initialize();
            Assert.That(queue.Count(TickStage.View), Is.EqualTo(viewCountBefore + 1),
                "the host must be driven by the central game loop, never Update");

            PipelineManager.SetTier(QualityTier.Low);
            Assert.That(manager.IsGradeCrossfading, Is.True);

            _host.Tick(0.4f); // the host pump — not the manager pump — advances the fade
            _host.Tick(0.4f);
            Assert.That(manager.IsGradeCrossfading, Is.False, "two 0.4s ticks complete the 0.8s fade");
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Low)));

            PipelineManager.SetTier(QualityTier.Medium); // start a fade the loop will never tick again
            Assert.That(manager.IsGradeCrossfading, Is.True);

            _host.Shutdown();
            Assert.That(queue.Count(TickStage.View), Is.EqualTo(viewCountBefore),
                "Shutdown unregisters from the loop");
            Assert.That(manager.IsGradeCrossfading, Is.False);
            Assert.That(manager.GradeVolume.sharedProfile, Is.EqualTo(Profile(QualityTier.Medium)),
                "Shutdown 恢复: the target profile lands at full weight (nothing would keep ticking)");
            Assert.That(manager.GradeVolume.weight, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(manager.GradeVolumeAlt.weight, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void VolumeCrossfadeHost_TickWithoutManager_IsHarmless()
        {
            if (PipelineManager.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(PipelineManager.Instance.gameObject);
            }

            _host = new GameObject("~VolumeCrossfadeTestHost").AddComponent<VolumeCrossfadeHost>();
            _host.Initialize();

            Assert.DoesNotThrow(() => _host.Tick(0.1f),
                "the pump must no-op while the manager has not mounted (boot order is undefined)");
        }
    }
}
