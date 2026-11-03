using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Thin View adapter that drives <see cref="FrameGovernor"/> from the
    /// central game loop and lands its measures on the engine
    /// (TASK-A-R8-01 design point 2). AudioService mounting pattern:
    /// self-bootstraps via <see cref="RuntimeInitializeOnLoadMethod"/>, its
    /// Tick runs on the <see cref="ITickQueue"/> at <see cref="TickStage.View"/>
    /// — never Update: the frame order stays GameLoopDriver-owned. EditMode
    /// tests — where MonoBehaviour callbacks are not driven — use the
    /// documented <see cref="Initialize"/> seam (same as AudioService).
    ///
    /// Measure landing:
    ///  - <c>RenderScale</c> → <c>UniversalRenderPipelineAsset.renderScale</c>
    ///    on the ACTIVE pipeline asset. Because <see cref="PipelineManager.SetTier"/>
    ///    swaps the asset per quality level, every asset the governor touches
    ///    has its original value recorded before the first write and all of
    ///    them are restored on disable/shutdown.
    ///    Because renderScale lives on the ASSET, the recorded original also
    ///    goes to <see cref="RenderScaleRecoveryJournal"/> on disk BEFORE the
    ///    first write (TASK-A-R9-02): a crash between write and restore then
    ///    gets healed by <see cref="RenderScaleRecovery"/> on the next boot
    ///    instead of silently becoming the new baseline. The note is a HARD
    ///    precondition: when it cannot be written the measure is skipped (and
    ///    disabled for the rest of the session) rather than dirtying an asset
    ///    no journal describes.
    ///  - <c>Shadows</c> → <see cref="PipelineManager.SetTier"/> DOWNGRADE
    ///    ONLY. Restore semantics (briefing design point): the governor
    ///    captures the user tier when enabled and only ever subtracts from it
    ///    ("做减法") — it never raises the tier above the user's choice, and
    ///    disabling or shutting down returns the pipeline to the user tier
    ///    ("恢复时回用户档位"). Note SetTier persists the tier under
    ///    PlayerPrefs (existing, unchanged PipelineManager semantics); the
    ///    restore path re-persists the user tier, so the setting converges
    ///    back to the user's choice. A user changing the tier mid-governance
    ///    is picked up on the next enable (v1: the settings page is expected
    ///    to toggle the governor off first).
    ///  - <c>PostFx</c> → grade Volume weight (original recorded at enable).
    ///  - <c>TargetFps</c> → terminal warning log; the measure-changed event
    ///    itself is the "give up" signal on the EventBus.
    ///
    /// <see cref="FrameGovernor.Enabled"/> defaults to OFF: until
    /// <see cref="SetEnabled"/>(true) the Tick is a no-op and no setting is
    /// touched.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FrameGovernorHost : MonoBehaviour, ITickable
    {
        private const string LogChannel = "Pipeline";

        /// <summary>Default target frame rate the TargetMs is derived from.</summary>
        public const float DefaultTargetFps = 60f;

        private static FrameGovernorHost _instance;

        private FrameGovernor _governor;
        private IFrameTelemetry _telemetry;
        private RecorderFrameTelemetry _ownedRecorder;
        private GovernorState _applied;
        private readonly Dictionary<RenderPipelineAsset, float> _touchedRenderScales = new Dictionary<RenderPipelineAsset, float>();
        private readonly Dictionary<RenderPipelineAsset, RenderScaleJournalEntry> _journalEntries =
            new Dictionary<RenderPipelineAsset, RenderScaleJournalEntry>();

        // Seams (TASK-A-R9-02): the target abstracts the URP asset so tests can
        // use throwaway CreateInstance assets instead of the real URP_*.asset
        // files; the journal is reachable so tests can point it at a temp dir.
        private IRenderScaleTarget _renderScaleTarget = QualitySettingsRenderScaleTarget.Default;
        private RenderScaleRecoveryJournal _recoveryJournal;

        private QualityTier _userTier = TierMapper.DefaultTier;
        private float _originalGradeWeight = 1f;
        private float _targetFps = DefaultTargetFps;
        private bool _governorEnabled;
        private bool _initialized;
        private bool _warnedNonUrp;
        private bool _warnedJournalWriteFailure;
        private bool _warnedScaleWriteFailure;
        private bool _renderScaleMeasureDisabled;

        /// <summary>The self-bootstrapped instance (null before boot).</summary>
        public static FrameGovernorHost Instance => _instance;

        /// <summary>True while the governor is actively governing (default false).</summary>
        public bool IsGovernorEnabled => _governorEnabled;

        /// <summary>Target frame rate the governor aims for (TargetMs = 1000 / this).</summary>
        public float TargetFps => _targetFps;

        /// <summary>The pure core being driven (null before Initialize; diagnostics/HUD/tests).</summary>
        public FrameGovernor Governor => _governor;

        /// <summary>The telemetry source in use (injected or the recorder-backed default).</summary>
        public IFrameTelemetry Telemetry => _telemetry;

        /// <summary>
        /// Self-bootstrap before the first scene loads. The governor stays
        /// disabled until explicitly enabled — mounting only wires the pump.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~FrameGovernorRoot");
            DontDestroyOnLoad(host);
            host.AddComponent<FrameGovernorHost>().Initialize();
        }

        /// <summary>
        /// Wires loop registration and telemetry. Idempotent;
        /// <paramref name="telemetry"/> overrides the default
        /// <see cref="RecorderFrameTelemetry"/> (test seam / future native
        /// B-R2-03 GPU timestamp source).
        /// <paramref name="renderScaleTarget"/> and
        /// <paramref name="recoveryJournal"/> are the TASK-A-R9-02 seams: null
        /// keeps production behaviour (<see cref="QualitySettingsRenderScaleTarget"/>
        /// + the journal under <see cref="Application.persistentDataPath"/>).
        /// Tests must inject both — the default target writes the real URP assets.
        /// </summary>
        public void Initialize(
            IFrameTelemetry telemetry = null,
            IRenderScaleTarget renderScaleTarget = null,
            RenderScaleRecoveryJournal recoveryJournal = null)
        {
            if (_initialized)
            {
                return;
            }

            _renderScaleTarget = renderScaleTarget ?? QualitySettingsRenderScaleTarget.Default;
            _recoveryJournal = recoveryJournal ?? new RenderScaleRecoveryJournal();

            _instance = this;
            _initialized = true;

            if (telemetry != null)
            {
                _telemetry = telemetry;
                _ownedRecorder = null;
            }
            else
            {
                var recorder = new RecorderFrameTelemetry();
                _telemetry = recorder;
                _ownedRecorder = recorder;
                ZLog.Info(LogChannel, recorder.GpuRecorderAvailable
                    ? "governor telemetry: GPU recorder available (GPU Frame Time)."
                    : "governor telemetry: GPU recorder unavailable — governing on CPU frame cost.");
            }

            // Loop guarantee: same seam as AudioService. IMPORTANT: bind the
            // RETURNED root, not the static Instance — in EditMode test
            // sequences the static can still reference a destroyed GameRoot
            // while EnsureInstance() has just built a fresh one; Initialize on
            // the returned root is the self-healing path (it re-points the
            // static and bootstraps idempotently).
            GameRoot root = GameRoot.EnsureInstance();
            if (!GameRoot.IsBootstrapped)
            {
                root.Initialize();
            }

            Services.Get<ITickQueue>().Register(this, TickStage.View);

            // Disabled placeholder core so the getter is never null; SetEnabled
            // builds the real one from the user baseline.
            _governor = new FrameGovernor(GovernorLadder.Create(PipelineManager.CurrentTier, GetCurrentRenderScale()));
        }

        /// <summary>
        /// Enables/disables governance. Enabling captures the user baseline
        /// (tier, renderScale, grade weight) and builds the measure ladder;
        /// disabling — like <see cref="Shutdown"/> — restores it.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (enabled == _governorEnabled)
            {
                return;
            }

            if (enabled)
            {
                EnableGovernor();
            }
            else
            {
                DisableGovernor("disabled");
            }
        }

        /// <summary>Sets the target frame rate (TargetMs = 1000 / fps); non-positive values fall back to <see cref="DefaultTargetFps"/>.</summary>
        public void SetTargetFps(float fps)
        {
            _targetFps = fps > 0f ? fps : DefaultTargetFps;
            if (_governor != null && _governorEnabled)
            {
                _governor.TargetMs = 1000f / _targetFps;
            }
        }

        /// <summary>
        /// Deterministic teardown seam (also invoked by <see cref="OnDestroy"/>):
        /// restores the user baseline if the governor is active, unregisters
        /// from the game loop and disposes the owned telemetry. Mirrors the
        /// AudioService Initialize/Shutdown pattern so EditMode tests exercise
        /// the exact path the engine runs at teardown. Idempotent.
        /// </summary>
        public void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            _initialized = false;

            if (_governorEnabled)
            {
                DisableGovernor("shutdown");
            }

            _governor = null;

            if (_ownedRecorder != null)
            {
                _ownedRecorder.Dispose();
                _ownedRecorder = null;
            }

            _telemetry = null;

            if (Services.TryGet(out ITickQueue queue))
            {
                queue.Unregister(this);
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>Game-loop tick: pump telemetry, advance the governor, land changes. No-op while disabled.</summary>
        public void Tick(float dt)
        {
            if (!_governorEnabled || _governor == null)
            {
                return; // Enabled=false is a full bypass: no pump, no apply, no events.
            }

            if (_ownedRecorder != null)
            {
                _ownedRecorder.Pump();
            }

            GovernorDecision decision = _governor.Tick(dt, _telemetry);
            if (!decision.Changed)
            {
                return;
            }

            ApplyState(_governor.CurrentState);
            EventBus.Fire(new GovernorMeasureChangedEvent(decision.From, decision.To));

            if (decision.To.Kind == GovernorMeasureKind.TargetFps)
            {
                ZLog.Warn(LogChannel,
                    $"governor: ladder exhausted at '{decision.To}' — target {_targetFps:0}fps cannot be met, giving up.");
            }
        }

        private void EnableGovernor()
        {
            // Idempotent mount guarantee: the tier baseline and the grade
            // volume must exist even if the governor is enabled before the
            // PipelineManager booted (boot order of RuntimeInitializeOnLoad
            // callbacks is undefined).
            PipelineManager.EnsureInstance();
            _userTier = TierMapper.Normalize(PipelineManager.CurrentTier);
            _originalGradeWeight = 1f;
            var volume = PipelineManager.Instance != null ? PipelineManager.Instance.GradeVolume : null;
            if (volume != null)
            {
                _originalGradeWeight = volume.weight;
            }

            float originalScale = GetCurrentRenderScale();
            var ladder = GovernorLadder.Create(_userTier, originalScale, _originalGradeWeight);
            _governor = new FrameGovernor(ladder, GovernorTuning.Default)
            {
                Enabled = true,
                TargetMs = 1000f / _targetFps,
            };
            _applied = _governor.CurrentState; // baseline — nothing applied yet
            _governorEnabled = true;
            ZLog.Info(LogChannel,
                $"governor enabled: target={_targetFps:0}fps ({_governor.TargetMs:0.0}ms), user tier={_userTier}, " +
                $"original renderScale={originalScale:0.00}, ladder levels={ladder.Count}");
        }

        private void DisableGovernor(string reason)
        {
            if (_governor != null)
            {
                _governor.Enabled = false;
            }

            _governorEnabled = false;
            RestoreBaseline(reason);
        }

        /// <summary>
        /// Restore semantics (briefing design point 2): back to the user tier,
        /// the original renderScale on every touched asset and the original
        /// grade volume weight.
        /// </summary>
        private void RestoreBaseline(string reason)
        {
            if (PipelineManager.CurrentTier != _userTier)
            {
                PipelineManager.SetTier(_userTier);
                ZLog.Info(LogChannel, $"governor {reason}: tier restored to user tier {_userTier}.");
            }

            foreach (KeyValuePair<RenderPipelineAsset, float> touched in _touchedRenderScales)
            {
                if (touched.Key is UniversalRenderPipelineAsset urp && urp != null)
                {
                    if (Mathf.Approximately(urp.renderScale, touched.Value))
                    {
                        // Already back at the original value — nothing left to heal.
                        _journalEntries.Remove(touched.Key);
                    }
                    else if (TryWriteScale(urp, touched.Value))
                    {
                        _journalEntries.Remove(touched.Key);
                        ZLog.Info(LogChannel, $"governor {reason}: renderScale of '{urp.name}' restored to {touched.Value:0.00}.");
                    }

                    // A refused write keeps its journal entry: the original value
                    // must survive into the next boot rather than vanish here.
                }
                else
                {
                    // Destroyed/unresolvable asset: keep its journal entry so the
                    // next boot can still heal it instead of losing the original.
                    ZLog.Warn(LogChannel, $"governor {reason}: renderScale of a touched asset could not be restored (asset gone).");
                }
            }

            _touchedRenderScales.Clear();
            CleanupJournalIfFullyRestored();
            _warnedJournalWriteFailure = false;
            _warnedScaleWriteFailure = false;

            var volume = PipelineManager.Instance != null ? PipelineManager.Instance.GradeVolume : null;
            if (volume != null && !Mathf.Approximately(volume.weight, _originalGradeWeight))
            {
                volume.weight = _originalGradeWeight;
                ZLog.Info(LogChannel, $"governor {reason}: grade volume weight restored to {_originalGradeWeight:0.00}.");
            }

            _warnedNonUrp = false;
        }

        private void ApplyState(GovernorState state)
        {
            bool tierChanged = false;
            if (state.ShadowStepsDown != _applied.ShadowStepsDown)
            {
                // Downgrade-only by construction: steps > 0 puts the target
                // strictly below the user tier; the clamp is a belt-and-braces
                // guard of the documented restore semantics.
                var target = (QualityTier)Mathf.Clamp(
                    (int)_userTier - state.ShadowStepsDown,
                    (int)QualityTier.Low,
                    (int)_userTier);
                PipelineManager.SetTier(target);
                tierChanged = true;
            }

            // After a possible tier/asset swap the renderScale must be (re)applied
            // to whatever asset is active NOW.
            if (tierChanged || !Mathf.Approximately(state.RenderScale, _applied.RenderScale))
            {
                ApplyRenderScale(state.RenderScale);
            }

            if (!Mathf.Approximately(state.PostFxWeight, _applied.PostFxWeight))
            {
                ApplyPostFxWeight(state.PostFxWeight);
            }

            _applied = state;
        }

        private void ApplyRenderScale(float scale)
        {
            if (!(_renderScaleTarget.ActiveAsset is UniversalRenderPipelineAsset urp))
            {
                if (!_warnedNonUrp)
                {
                    _warnedNonUrp = true;
                    ZLog.Warn(LogChannel, "governor: active pipeline is not URP — RenderScale measure unavailable (other measures still apply).");
                }

                return;
            }

            // TASK-A-R9-02 round 2: no durable note, no asset change. Once the
            // journal proved unwritable the measure stays off for the rest of the
            // session ("DRS disabled: crash recovery unavailable"), so a broken
            // disk can neither dirty an asset nor spam the log every tick.
            if (_renderScaleMeasureDisabled)
            {
                return;
            }

            bool firstTouch = !_touchedRenderScales.ContainsKey(urp);
            float originalScale = firstTouch ? urp.renderScale : _touchedRenderScales[urp];

            // TASK-A-R9-02 design point 1: the durable note goes to disk BEFORE
            // the asset is dirtied. The reverse order would leave a modified
            // URP asset with no record of its original value after a crash.
            if (!PersistJournalEntry(urp, originalScale, scale))
            {
                AbandonRenderScaleMeasure(urp, originalScale, alreadyDirty: !firstTouch);
                return;
            }

            if (firstTouch)
            {
                _touchedRenderScales.Add(urp, originalScale);
            }

            if (!Mathf.Approximately(urp.renderScale, scale) && TryWriteScale(urp, scale))
            {
                ZLog.Info(LogChannel, $"governor: renderScale of '{urp.name}' -> {scale:0.00}.");
            }
        }

        /// <summary>
        /// Gives up the RenderScale measure for the rest of the session because
        /// the journal refused a write (TASK-A-R9-02 round 2, acceptance
        /// question 3). Order matters:
        ///  1. roll back whatever this session already landed on that asset — the
        ///     session baseline is the user's value, so afterwards the disk needs
        ///     no healing even if step 2 cannot be completed;
        ///  2. invalidate the asset's journal entry ONLY when that rollback really
        ///     landed (TASK-A-R9-03, round-3 legacy 1): the entry is the single
        ///     credential that can heal a dirty asset on the next boot, so
        ///     dropping it while the asset is still dirty is what makes a value
        ///     unrecoverable rather than merely stale;
        ///  3. disable the measure and say so exactly once.
        /// </summary>
        private void AbandonRenderScaleMeasure(UniversalRenderPipelineAsset urp, float baseline, bool alreadyDirty)
        {
            _renderScaleMeasureDisabled = true;

            bool rollbackRefused = false;
            if (alreadyDirty)
            {
                if (Mathf.Approximately(urp.renderScale, baseline))
                {
                    // Already back at the user's value: nothing to undo and
                    // nothing left to sort out on the next boot.
                    _touchedRenderScales.Remove(urp);
                }
                else if (TryWriteScale(urp, baseline))
                {
                    _touchedRenderScales.Remove(urp);
                    ZLog.Info(LogChannel,
                        $"governor: renderScale of '{urp.name}' rolled back to {baseline:0.00} — the recovery journal is unavailable.");
                }
                else
                {
                    // The platform refused the rollback, so the asset still holds
                    // a dirty value. Keep the in-memory baseline (Shutdown retries
                    // the heal) and keep the journal entry — that entry is all the
                    // next boot has to go on.
                    rollbackRefused = true;

                    if (_journalEntries.TryGetValue(urp, out RenderScaleJournalEntry kept))
                    {
                        // PersistJournalEntry had already advanced the in-memory
                        // entry to the value it failed to record; the file on disk
                        // still describes the value the asset actually holds, so
                        // memory is brought back in line with the disk.
                        kept.LastWrittenScale = urp.renderScale;
                    }
                }
            }

            if (rollbackRefused)
            {
                ZLog.Warn(LogChannel,
                    $"governor: renderScale of '{urp.name}' is still {urp.renderScale:0.00} — rollback refused; " +
                    $"journal entry kept so the next boot can heal it back to {baseline:0.00}.");
            }
            else
            {
                InvalidateJournalEntries(urp);
            }

            if (!_warnedJournalWriteFailure)
            {
                _warnedJournalWriteFailure = true;
                ZLog.Warn(LogChannel,
                    $"governor: cannot write the renderScale recovery journal ('{_recoveryJournal?.FilePath}') — " +
                    "DRS disabled: crash recovery unavailable; renderScale stays at its current value for the rest of " +
                    "this session (tier/PostFx measures keep working). A dirty value without a note would be adopted " +
                    "as the user's baseline by the next session.");
            }
        }

        /// <summary>
        /// Drops one asset's recovery entry and makes the file on disk stop
        /// describing it. When the corrected file cannot be written either (the
        /// very failure that got us here) the file is left exactly as it is
        /// (TASK-A-R9-03, round-3 legacy 2).
        ///
        /// The earlier reasoning — "a missing note costs less than a stale note"
        /// — had it backwards. Healing goes through the G3 check: the asset must
        /// still hold exactly <see cref="RenderScaleJournalEntry.LastWrittenScale"/>
        /// or nothing is written. An out-of-date entry is therefore a SAFE
        /// failure (refuse to heal, clean up, warn), while a missing entry is an
        /// IRREVERSIBLE loss: a dirty asset with no note is adopted as the user's
        /// baseline forever, and deleting the file would also strip that
        /// protection from every OTHER asset journalled here.
        /// </summary>
        private void InvalidateJournalEntries(UniversalRenderPipelineAsset urp)
        {
            if (_recoveryJournal == null)
            {
                return;
            }

            _journalEntries.Remove(urp);
            if (_journalEntries.Count == 0)
            {
                _recoveryJournal.Delete();
                return;
            }

            if (!_recoveryJournal.Save(new List<RenderScaleJournalEntry>(_journalEntries.Values)))
            {
                // Keep the previous file: it still protects the assets that are
                // dirty right now. The one entry it over-describes (this asset,
                // already rolled back) fails the next boot's G3 check by
                // construction — a refusal, never a wrong write.
                ZLog.Info(LogChannel,
                    $"governor: cannot rewrite '{_recoveryJournal.FilePath}' without '{urp.name}' — " +
                    "the previous file is kept; its entry for this asset is now stale and will be rejected on the next boot.");
            }
        }

        /// <summary>
        /// Lands a renderScale through the injectable target and keeps a
        /// platform refusal (read-only asset in a Player build, URP downgrade
        /// quirk) to ONE warning per session — never a per-frame log storm
        /// (TASK-A-R9-02 design point 7).
        /// </summary>
        private bool TryWriteScale(RenderPipelineAsset asset, float scale)
        {
            try
            {
                _renderScaleTarget.WriteScale(asset, scale);
                return true;
            }
            catch (System.Exception e)
            {
                if (!_warnedScaleWriteFailure)
                {
                    _warnedScaleWriteFailure = true;
                    ZLog.Warn(LogChannel,
                        $"governor: cannot write renderScale of '{asset.name}' ({e.GetType().Name}: {e.Message}) — " +
                        "the measure is unavailable on this platform/pipeline; further failures are not logged.");
                }

                return false;
            }
        }

        /// <summary>
        /// Writes/updates the crash-recovery note for one asset. Called before
        /// every write, but only the first call per asset creates the entry (the
        /// original value); later calls just advance the last-written value so a
        /// crash is always recoverable from the value actually on disk.
        /// Returns false when the note could not be made durable — the caller
        /// must then leave the asset alone (TASK-A-R9-02 round 2): a renderScale
        /// without a note is a value the next session would adopt as the user's
        /// baseline, which is exactly the compounding this task exists to cut.
        /// </summary>
        private bool PersistJournalEntry(UniversalRenderPipelineAsset urp, float originalScale, float lastWrittenScale)
        {
            if (_recoveryJournal == null)
            {
                return false;
            }

            _journalEntries[urp] = new RenderScaleJournalEntry
            {
                QualityLevel = _renderScaleTarget.IndexOf(urp),
                AssetName = urp.name,
                OriginalScale = originalScale,
                LastWrittenScale = lastWrittenScale,
            };

            return _recoveryJournal.Save(new List<RenderScaleJournalEntry>(_journalEntries.Values));
        }

        /// <summary>
        /// Drops the journal once nothing is left to heal. Called after every
        /// restore (disable/shutdown) — the normal path must leave NO journal
        /// behind (TASK-A-R9-02 G2).
        /// </summary>
        private void CleanupJournalIfFullyRestored()
        {
            if (_recoveryJournal == null || _journalEntries.Count > 0)
            {
                return;
            }

            _recoveryJournal.Delete();
        }

        private void ApplyPostFxWeight(float weight)
        {
            var volume = PipelineManager.Instance != null ? PipelineManager.Instance.GradeVolume : null;
            if (volume != null && !Mathf.Approximately(volume.weight, weight))
            {
                volume.weight = weight;
                ZLog.Info(LogChannel, $"governor: grade volume weight -> {weight:0.00}.");
            }
        }

        private float GetCurrentRenderScale()
        {
            return _renderScaleTarget.ActiveAsset is UniversalRenderPipelineAsset urp ? urp.renderScale : 1f;
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
