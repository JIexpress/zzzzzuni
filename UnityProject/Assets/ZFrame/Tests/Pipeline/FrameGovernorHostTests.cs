using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the FrameGovernorHost adapter (TASK-A-R8-01
    /// design point 3): ITickQueue registration, full bypass while disabled,
    /// measure landing on the real pipeline (renderScale / tier / volume
    /// weight), EventBus events, and the restore semantics — disable and
    /// Shutdown return the pipeline to the user tier and the captured
    /// originals. Mutating tests restore quality level / PlayerPrefs in
    /// teardown (same hygiene as <see cref="PipelineManagerTests"/>).
    /// </summary>
    public sealed class FrameGovernorHostTests
    {
        private const float BadMs = 30f; // > TargetMs(60fps) * 1.1

        private readonly List<GovernorMeasureChangedEvent> _events = new List<GovernorMeasureChangedEvent>();
        private IDisposable _eventSubscription;
        private FakeTelemetry _telemetry;
        private FrameGovernorHost _host;

        private int _initialQualityLevel;
        private int _initialStoredTier;
        private bool _hadStoredTier;
        private readonly Dictionary<string, float> _originalScales = new Dictionary<string, float>();
        private float _originalWeight = 1f;
        private string _journalRoot;

        [SetUp]
        public void SetUp()
        {
            _initialQualityLevel = QualitySettings.GetQualityLevel();
            _hadStoredTier = PlayerPrefs.HasKey(TierMapper.SettingsKey);
            _initialStoredTier = PlayerPrefs.GetInt(TierMapper.SettingsKey, -1);

            // TASK-A-R9-02: the host persists a renderScale recovery journal on
            // the first write; point it at a temp dir so EditMode never touches
            // the real persistentDataPath.
            _journalRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "zframe_host_journal_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(_journalRoot);

            // User baseline for every test: the user picked High.
            PipelineManager.SetTier(QualityTier.High);

            _originalScales.Clear();
            PipelineTierTable table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assume.That(table, Is.Not.Null, "Resources/" + PipelineManager.TableResourcePath + " must exist");
            foreach (QualityTier tier in new[] { QualityTier.Low, QualityTier.Medium, QualityTier.High })
            {
                _originalScales[table.GetPipelineAsset(tier).name] = ((UniversalRenderPipelineAsset)table.GetPipelineAsset(tier)).renderScale;
            }

            var volume = PipelineManager.Instance != null ? PipelineManager.Instance.GradeVolume : null;
            _originalWeight = volume != null ? volume.weight : 1f;

            _telemetry = new FakeTelemetry();
            // NOTE: Unity Test Framework reuses this fixture instance across
            // test cases (observed: an earlier test's collected events were
            // still visible here), so the recorder list must start empty.
            _events.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (_eventSubscription != null)
            {
                _eventSubscription.Dispose();
                _eventSubscription = null;
            }

            if (_host != null)
            {
                _host.Shutdown(); // restore path must hold even mid-test
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

            if (!string.IsNullOrEmpty(_journalRoot) && System.IO.Directory.Exists(_journalRoot))
            {
                System.IO.Directory.Delete(_journalRoot, recursive: true);
            }
        }

        private sealed class FakeTelemetry : IFrameTelemetry
        {
            public float GpuFrameMs { get; set; }
            public float CpuFrameMs { get; set; }
        }

        private void NewHost()
        {
            var go = new GameObject("~FrameGovernorHostTest");
            _host = go.AddComponent<FrameGovernorHost>();
            // Injected fake telemetry (no player loop needed) + a temp journal
            // root (TASK-A-R9-02) so the real persistentDataPath stays clean.
            _host.Initialize(_telemetry, null, new RenderScaleRecoveryJournal(_journalRoot));
        }

        private void Pump(float seconds, float dt = 0.5f)
        {
            _telemetry.GpuFrameMs = 0f; // CPU fallback path (documented unsupported-GPU case)
            _telemetry.CpuFrameMs = BadMs;
            int ticks = Mathf.CeilToInt(seconds / dt);
            for (int i = 0; i < ticks; i++)
            {
                _host.Tick(dt);
            }
        }

        private void WalkToExhaustion()
        {
            Pump(21f); // 42 ticks × 0.5s: 10 moves need 10 × M(2s) = 40 ticks, + margin
        }

        private UniversalRenderPipelineAsset ActiveUrp()
        {
            return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        }

        // ---- loop wiring ---------------------------------------------------------

        [Test]
        public void Initialize_RegistersIntoViewTickStage_ShutdownUnregisters()
        {
            // Bind the RETURNED root (self-healing against a stale destroyed
            // static from earlier test classes) — never the static Instance.
            GameRoot root = GameRoot.EnsureInstance();
            if (!GameRoot.IsBootstrapped)
            {
                root.Initialize();
            }

            ITickQueue queue = Services.Get<ITickQueue>();
            int before = queue.Count(TickStage.View);

            NewHost();
            Assert.That(queue.Count(TickStage.View), Is.EqualTo(before + 1),
                "the host must be driven by the central game loop, never Update");

            _host.Shutdown();
            Assert.That(queue.Count(TickStage.View), Is.EqualTo(before));
        }

        [Test]
        public void Initialize_DisabledByDefault_TelemetryWired()
        {
            NewHost();
            Assert.That(_host.IsGovernorEnabled, Is.False, "Enabled must default OFF (briefing design point)");
            Assert.That(_host.Governor, Is.Not.Null);
            Assert.That(_host.Governor.Enabled, Is.False);
            Assert.That(_host.Telemetry, Is.SameAs(_telemetry), "injected telemetry wins over the recorder default");
        }

        // ---- bypass ---------------------------------------------------------------

        [Test]
        public void Tick_WhileDisabled_FullyBypasses()
        {
            NewHost();
            _eventSubscription = EventBus.Subscribe<GovernorMeasureChangedEvent>(_events.Add);
            float scaleBefore = ActiveUrp().renderScale;
            QualityTier tierBefore = PipelineManager.CurrentTier;
            float weightBefore = PipelineManager.Instance.GradeVolume.weight;

            WalkToExhaustion(); // far more overload than any window needs

            Assert.That(_events, Is.Empty, "disabled governor must not fire events");
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(scaleBefore));
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(tierBefore));
            Assert.That(PipelineManager.Instance.GradeVolume.weight, Is.EqualTo(weightBefore));
        }

        // ---- full ladder walk -------------------------------------------------------

        [Test]
        public void SetEnabled_WalksDownLadder_InFixedOrder_WithEvents()
        {
            NewHost();
            _eventSubscription = EventBus.Subscribe<GovernorMeasureChangedEvent>(_events.Add);
            _host.SetEnabled(true);
            Assert.That(_host.IsGovernorEnabled, Is.True);

            WalkToExhaustion();

            // 1..5 RenderScale rungs, 6..7 Shadows, 8..9 PostFx, 10 TargetFps.
            Assert.That(_events.Count, Is.EqualTo(10), "exactly one event per ladder rung");
            for (int i = 0; i < 5; i++)
            {
                Assert.That(_events[i].Measure, Is.EqualTo(GovernorMeasureKind.RenderScale), $"event {i}");
                Assert.That(_events[i].To.Value, Is.EqualTo(0.95f - 0.05f * i).Within(1e-4f));
            }

            Assert.That(_events[5].Measure, Is.EqualTo(GovernorMeasureKind.Shadows));
            Assert.That(_events[6].Measure, Is.EqualTo(GovernorMeasureKind.Shadows));
            Assert.That(_events[7].Measure, Is.EqualTo(GovernorMeasureKind.PostFx));
            Assert.That(_events[8].Measure, Is.EqualTo(GovernorMeasureKind.PostFx));
            Assert.That(_events[9].Measure, Is.EqualTo(GovernorMeasureKind.TargetFps));
            Assert.That(_events[9].From.Kind, Is.EqualTo(GovernorMeasureKind.PostFx));

            // Physical landing after the full walk (accumulated state).
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(0.75f).Within(1e-4f),
                "RenderScale measures accumulate down to the 0.75 floor");
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Low),
                "High user tier downgraded two notches = the Low floor");
            Assert.That(PipelineManager.Instance.GradeVolume.weight, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(_host.Governor.LastSampleUsedGpu, Is.False, "fake telemetry is CPU-only — fallback in effect");
        }

        [Test]
        public void SetEnabled_FirstRung_AppliesRenderScaleOnActiveAsset()
        {
            NewHost();
            _host.SetEnabled(true);

            Pump(2.2f); // one full M window

            Assert.That(ActiveUrp().renderScale, Is.EqualTo(0.95f).Within(1e-4f));
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.High), "no tier change inside the RenderScale band");
        }

        // ---- restore semantics --------------------------------------------------------

        [Test]
        public void Shutdown_RestoresUserTier_AndAllOriginals()
        {
            NewHost();
            _host.SetEnabled(true);
            WalkToExhaustion();
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Low), "precondition: walk reached the shadow band");

            _host.Shutdown();

            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.High),
                "restore semantics: back to the user tier");
            PipelineTierTable table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            foreach (QualityTier tier in new[] { QualityTier.Low, QualityTier.Medium, QualityTier.High })
            {
                var asset = (UniversalRenderPipelineAsset)table.GetPipelineAsset(tier);
                Assert.That(asset.renderScale, Is.EqualTo(_originalScales[asset.name]).Within(1e-5f),
                    $"touched asset '{asset.name}' must be restored to its original renderScale");
            }

            Assert.That(PipelineManager.Instance.GradeVolume.weight, Is.EqualTo(_originalWeight).Within(1e-5f));
            Assert.That(_host.IsGovernorEnabled, Is.False);
        }

        [Test]
        public void SetEnabledFalse_Restores_AsWell()
        {
            NewHost();
            _host.SetEnabled(true);
            Pump(15f); // deep into the shadow band (7 rungs worth)
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Low));

            _host.SetEnabled(false);
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.High), "disable must restore the user tier too");
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(_originalScales[ActiveUrp().name]).Within(1e-5f));
        }

        [Test]
        public void Reenable_AfterRestore_CapturesFreshBaseline()
        {
            NewHost();
            _host.SetEnabled(true);
            Pump(2.2f); // scale 0.95 applied
            _host.SetEnabled(false);
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(_originalScales[ActiveUrp().name]).Within(1e-5f));

            _host.SetEnabled(true);
            Pump(2.2f);
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "the second session must walk the same first rung from the restored baseline");
        }

        [Test]
        public void Shutdown_WithoutEnable_IsHarmless()
        {
            NewHost();
            float scaleBefore = ActiveUrp().renderScale;
            QualityTier tierBefore = PipelineManager.CurrentTier;

            _host.Shutdown();

            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(tierBefore));
            Assert.That(ActiveUrp().renderScale, Is.EqualTo(scaleBefore));
        }

        // ---- target fps ---------------------------------------------------------------

        [Test]
        public void SetTargetFps_MapsToTargetMs()
        {
            NewHost();
            _host.SetTargetFps(120f);
            _host.SetEnabled(true);
            Assert.That(_host.Governor.TargetMs, Is.EqualTo(1000f / 120f).Within(1e-3f));
            Assert.That(_host.TargetFps, Is.EqualTo(120f));

            _host.SetTargetFps(-5f);
            Assert.That(_host.TargetFps, Is.EqualTo(FrameGovernorHost.DefaultTargetFps), "non-positive falls back");
        }

        // ---- default telemetry ---------------------------------------------------------

        [Test]
        public void RecorderTelemetry_DefaultConstruction_IsSafeOnAnyPlatform()
        {
            // The GPU recorder availability is platform-dependent (documented in
            // the type and the 施工报告); BOTH outcomes must be harmless.
            using (var telemetry = new RecorderFrameTelemetry())
            {
                Assert.That(telemetry.GpuFrameMs, Is.GreaterThanOrEqualTo(0f), "no negative GPU sample");
                Assert.That(telemetry.CpuFrameMs, Is.EqualTo(0f).Within(1e-5f), "no CPU sample before the second pump");
                telemetry.Pump();
                Assert.That(float.IsNaN(telemetry.CpuFrameMs), Is.False, "pump must not produce NaN");
            }
        }
    }
}
