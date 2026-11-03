using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Runtime owner of the quality tier matrix (TASK-A-M2-03 design point 3).
    ///
    /// Responsibilities:
    ///  1. <see cref="SetTier"/> — switches QualitySettings level (each level
    ///     binds its own URP asset, so the active RenderPipelineAsset follows),
    ///     activates the tier's grade VolumeProfile, persists the choice under
    ///     <see cref="TierMapper.SettingsKey"/> (PlayerPrefs, M3 settings UI
    ///     will take over) and fires <see cref="TierChangedEvent"/> on the
    ///     EventBus.
    ///  2. Boot: self-mounts like SceneLoader/Audio precedent
    ///     (<see cref="RuntimeInitializeOnLoadMethod"/>, Core stays unaware)
    ///     and re-applies the last stored tier.
    ///
    /// Volume activation strategy (TASK-A-R9-08, supersedes the A-M2-03
    /// single-volume swap): TWO global Volume components stacked on the same
    /// GameObject at the same priority (URP <c>Volume</c> carries no
    /// DisallowMultipleComponent). Slot A (<see cref="GradeVolume"/>) is the
    /// classic grade volume; slot B (<see cref="GradeVolumeAlt"/>) rests at
    /// weight 0 — which VolumeManager skips entirely
    /// (core VolumeManager.cs:469-470), an exact no-op — until a fade lifts
    /// it. A profile switch animates the pair with the pure
    /// <see cref="VolumeCrossfadeState"/> (equal-power, a²+b²≡1):
    /// A = old profile weight 1-x, B = new profile weight x. At rest the pair
    /// always collapses back to the A-M2-03 shape (current profile on A at
    /// weight 1, B at 0), so the "single global grade volume" reading stays
    /// true whenever nothing is fading. Profile assets stay shared/read-only
    /// — the swap moves references, never instantiates.
    ///
    /// Semantics (briefing design point 3): the tier/profile is the INTENT
    /// and switches immediately (<see cref="CurrentTier"/>,
    /// <see cref="CurrentGradeProfile"/>); the crossfade is presentation
    /// driven by <see cref="VolumeCrossfadeHost"/>. A mid-fade switch re-bases
    /// on the dominant slot (no weight pop; see <see cref="StartGradeFade"/>).
    /// <see cref="CrossfadeToProfile"/> exposes the same fade for scene-style
    /// switches without tier semantics.
    ///
    /// Pure mapping logic lives in <see cref="TierMapper"/>; the tier→asset
    /// bindings come from <see cref="PipelineTierTable"/> (Resources).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PipelineManager : MonoBehaviour
    {
        private const string LogChannel = "Pipeline";

        /// <summary>Resources path of the tier table (relative to any Resources root, without extension).</summary>
        public const string TableResourcePath = "CFG_PipelineTiers";

        /// <summary>
        /// Default grade crossfade duration in seconds (方案 §6.4: 运行时
        /// Volume 权重渐变 — the 0.8s equal-power crossfade).
        /// </summary>
        public const float DefaultCrossfadeDuration = 0.8f;

        private static PipelineManager _instance;

        /// <summary>Volume slot A — the grade profile rides here at rest (null until first tier application).</summary>
        private Volume _gradeVolume;

        /// <summary>Volume slot B — stacked on the same GameObject, rests at weight 0 until a fade lifts it.</summary>
        private Volume _gradeVolumeAlt;

        /// <summary>Crossfade roles: the slot fading OUT (weight 1→0) and the slot fading IN (0→1).</summary>
        private Volume _fadeFromVolume;
        private Volume _fadeToVolume;

        /// <summary>The pure equal-power clock driving the slot weights.</summary>
        private readonly VolumeCrossfadeState _crossfade = new VolumeCrossfadeState();

        /// <summary>
        /// The grade profile the manager currently INTENDS (tier switch /
        /// <see cref="CrossfadeToProfile"/> target). Updated immediately —
        /// before any fade runs — because the intent is semantic while the
        /// fade is presentation.
        /// </summary>
        private VolumeProfile _currentProfile;

        private PipelineTierTable _table;

        /// <summary>The self-mounted instance (null only before first use).</summary>
        public static PipelineManager Instance => _instance;

        /// <summary>Tier currently applied (default Medium before any SetTier/boot).</summary>
        public static QualityTier CurrentTier { get; private set; } = TierMapper.DefaultTier;

        /// <summary>True once the manager is mounted.</summary>
        public static bool IsBooted => _instance != null;

        /// <summary>
        /// Grade Volume slot A — carries the active profile at full weight
        /// whenever no fade is running (null until first SetTier).
        /// </summary>
        public Volume GradeVolume => _gradeVolume;

        /// <summary>
        /// Grade Volume slot B — the fade-in slot of the current crossfade;
        /// rests at weight 0 (an exact VolumeManager no-op) when settled.
        /// </summary>
        public Volume GradeVolumeAlt => _gradeVolumeAlt;

        /// <summary>True while a grade crossfade is animating (presentation only).</summary>
        public bool IsGradeCrossfading => _crossfade.IsFading;

        /// <summary>
        /// VolumeProfile the grade currently intends (null until first
        /// SetTier). Returns the fade TARGET immediately after a switch — the
        /// tier is the intent, the crossfade is presentation.
        /// </summary>
        public VolumeProfile CurrentGradeProfile => _currentProfile;

        /// <summary>Self-bootstrap before the first scene loads — Core is never involved.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            EnsureInstance();
            QualityTier stored = TierMapper.FromSettingsValue(
                PlayerPrefs.GetInt(TierMapper.SettingsKey, (int)TierMapper.DefaultTier));
            SetTier(stored);
        }

        /// <summary>
        /// Mounts the manager host (idempotent). Safe in play mode, EditMode
        /// tests and batch validation.
        /// </summary>
        public static PipelineManager EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject("~PipelineRoot");
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            _instance = host.AddComponent<PipelineManager>();
            _instance._table = Resources.Load<PipelineTierTable>(TableResourcePath);
            if (_instance._table == null)
            {
                ZLog.Warn(LogChannel,
                    $"tier table not found at Resources/{TableResourcePath} — volume activation will be skipped.");
            }

            return _instance;
        }

        /// <summary>
        /// Switches to <paramref name="tier"/> at runtime: no restart, no
        /// scene reload. Out-of-range tiers are normalized to Medium by
        /// <see cref="TierMapper.Normalize"/>. The tier change is applied
        /// even when the tier equals <see cref="CurrentTier"/> (idempotent
        /// re-apply), but the event only fires on actual changes.
        /// </summary>
        public static void SetTier(QualityTier tier)
        {
            tier = TierMapper.Normalize(tier);
            EnsureInstance();
            _instance.ApplyTier(tier, CurrentTier);
        }

        /// <summary>
        /// Crossfades the grade to <paramref name="profile"/> over
        /// <paramref name="duration"/> seconds (TASK-A-R9-08 briefing design
        /// point 1: the independent interface for scene-style switches).
        /// Deliberately WITHOUT tier semantics: quality level, persistence and
        /// <see cref="TierChangedEvent"/> are untouched — only the grade
        /// picture animates, and <see cref="CurrentGradeProfile"/> follows the
        /// new intent immediately. A duration &lt;= 0 snaps. A null profile is
        /// rejected with a warning. A request equal to the current intent is a
        /// no-op (settled or already fading toward it).
        /// </summary>
        public void CrossfadeToProfile(VolumeProfile profile, float duration)
        {
            ApplyGradeProfile(profile, duration);
        }

        /// <summary>
        /// Lands a running grade crossfade on its TARGET immediately (weights
        /// settle with the target profile at full weight). The tier/profile is
        /// the intent and has already switched when a fade runs, so aborting
        /// the presentation commits the intent — never freezes mid-blend.
        /// No-op when nothing is fading. Also the "skip animation" seam.
        /// </summary>
        public void AbortGradeCrossfade()
        {
            if (!_crossfade.IsFading)
            {
                return;
            }

            SnapGradeProfile(_currentProfile);
        }

        /// <summary>
        /// Per-frame pump for the grade crossfade — driven by
        /// <see cref="VolumeCrossfadeHost"/> via the ITickQueue at the View
        /// stage (never Update), public so EditMode tests and the host share
        /// exactly one entry point. No-op while no fade is running (weights
        /// stay settled). On completion the pair collapses back to the
        /// canonical rest shape (current profile on slot A at weight 1, slot B
        /// at 0) in the same tick — pixel-identical, both writes land before
        /// the camera renders.
        /// </summary>
        public void TickGradeCrossfade(float dt)
        {
            if (!_crossfade.IsFading)
            {
                return;
            }

            (float weightFrom, float weightTo) = _crossfade.Tick(dt);
            if (_fadeFromVolume != null)
            {
                _fadeFromVolume.weight = weightFrom;
            }

            if (_fadeToVolume != null)
            {
                _fadeToVolume.weight = weightTo;
            }

            if (!_crossfade.IsFading)
            {
                RenormalizeGradeVolumes(); // 完成停稳: back to the A-M2-03 rest shape
            }
        }

        /// <summary>Actual apply body: quality level, volume profile, persistence, event.</summary>
        private void ApplyTier(QualityTier target, QualityTier previous)
        {
            // 1. Quality level swap — the level binds the tier's URP asset, so
            //    GraphicsSettings.currentRenderPipeline follows automatically.
            if (QualitySettings.names.Length == (int)QualityTier.Count)
            {
                QualitySettings.SetQualityLevel(TierMapper.ToQualityLevel(target), applyExpensiveChanges: true);
            }
            else
            {
                ZLog.Warn(LogChannel,
                    $"QualitySettings has {QualitySettings.names.Length} levels, expected {(int)QualityTier.Count} — level swap skipped.");
            }

            // 2. Grade volume profile activation — a tier switch fades to the
            //    new profile (方案 §6.4, TASK-A-R9-08). The intent switches
            //    here immediately; the weights animate afterwards.
            EnsureGradeVolume();
            VolumeProfile profile = _table != null ? _table.GetVolumeProfile(target) : null;
            if (profile != null && _gradeVolume != null)
            {
                ApplyGradeProfile(profile, DefaultCrossfadeDuration);
            }
            else if (profile == null)
            {
                ZLog.Warn(LogChannel, $"no VolumeProfile bound for tier '{target}' — profile not swapped.");
            }

            CurrentTier = target;

            // 3. Persistence (M3 settings UI will take over this key).
            PlayerPrefs.SetInt(TierMapper.SettingsKey, TierMapper.ToSettingsValue(target));
            PlayerPrefs.Save();

            // 4. EventBus notification.
            if (previous != target)
            {
                EventBus.Fire(new TierChangedEvent(previous, target));
            }

            ZLog.Info(LogChannel, $"tier applied: {previous} -> {target}");
        }

        /// <summary>
        /// The grade profile switch body shared by <see cref="SetTier"/> (via
        /// <see cref="ApplyTier"/>) and <see cref="CrossfadeToProfile"/>.
        /// Switches the intent FIRST (<paramref name="target"/> becomes
        /// <see cref="CurrentGradeProfile"/> in the same call), then either
        /// snaps (boot / duration &lt;= 0) or starts/re-bases a fade.
        /// </summary>
        private void ApplyGradeProfile(VolumeProfile target, float duration)
        {
            if (target == null)
            {
                ZLog.Warn(LogChannel, "CrossfadeToProfile: profile must not be null — ignored.");
                return;
            }

            EnsureGradeVolume();

            if (ReferenceEquals(target, _currentProfile))
            {
                return; // already the intent — settled on it or fading toward it; no restart
            }

            bool hadProfile = _currentProfile != null;
            _currentProfile = target; // the intent switches NOW; weights follow

            if (duration <= 0f || !hadProfile)
            {
                // Boot has no previous picture to fade from, and a zero
                // duration IS a hard cut: land the target at full weight.
                SnapGradeProfile(target);
                return;
            }

            StartGradeFade(target, duration);
        }

        /// <summary>
        /// Starts a fade toward <paramref name="target"/>, re-basing on the
        /// dominant slot when one is already running so the dominant picture
        /// never pops (its weight path stays continuous; only the weaker slot
        /// swaps content):
        ///  - from-side dominant (early interrupt): the from slot keeps its
        ///    profile, the new profile takes the to slot, the clock re-starts
        ///    at the current phase;
        ///  - to-side dominant (late interrupt): the old target BECOMES the
        ///    new from slot (roles swap, weight path continuous), the new
        ///    profile takes the other slot, the clock re-starts mirrored at
        ///    1 - phase (cos(π/2 − θ) = sin(θ) keeps the kept slot's weight).
        /// In both branches the pop is bounded by the weaker slot's weight
        /// (≤ cos 45° ≈ 0.7071), and the physical weights are re-applied
        /// immediately so state and Volume components never diverge.
        /// </summary>
        private void StartGradeFade(VolumeProfile target, float duration)
        {
            if (!_crossfade.IsFading)
            {
                // Canonical rest shape: slot A carries the settled profile at
                // weight 1, slot B rests at 0. Fresh fade: A fades out, B in.
                _fadeFromVolume = _gradeVolume;
                _fadeToVolume = _gradeVolumeAlt;
                _gradeVolumeAlt.sharedProfile = target;
                _crossfade.Start(duration, 0f);
            }
            else
            {
                (float weightFrom, float weightTo) = _crossfade.CurrentWeights;
                Volume fromVolume = _fadeFromVolume;
                Volume toVolume = _fadeToVolume;
                if (weightFrom >= weightTo)
                {
                    toVolume.sharedProfile = target; // content swap on the weaker (to) slot
                    _crossfade.Start(duration, _crossfade.Progress01);
                }
                else
                {
                    _fadeFromVolume = toVolume; // the old target becomes the new from — continuous
                    _fadeToVolume = fromVolume;
                    fromVolume.sharedProfile = target; // content swap on the now-weaker slot
                    _crossfade.Start(duration, 1f - _crossfade.Progress01);
                }
            }

            // Re-apply the state weights so the physical pair matches the
            // clock from the first frame (a no-op in the continuous branches).
            (float weightFromNow, float weightToNow) = _crossfade.CurrentWeights;
            if (_fadeFromVolume != null)
            {
                _fadeFromVolume.weight = weightFromNow;
            }

            if (_fadeToVolume != null)
            {
                _fadeToVolume.weight = weightToNow;
            }
        }

        /// <summary>
        /// Hard-cut collapse to the canonical rest shape: the profile rides
        /// slot A at full weight, slot B rests at 0 — the exact single-volume
        /// picture A-M2-03 shipped. Used by boot, zero-duration switches,
        /// <see cref="AbortGradeCrossfade"/> and host Shutdown.
        /// </summary>
        private void SnapGradeProfile(VolumeProfile target)
        {
            if (_gradeVolume != null)
            {
                _gradeVolume.sharedProfile = target;
                _gradeVolume.weight = 1f;
            }

            if (_gradeVolumeAlt != null)
            {
                _gradeVolumeAlt.weight = 0f;
            }

            // Roles consistent with the state's settled (0,1): from = B @ 0, to = A @ 1.
            _fadeFromVolume = _gradeVolumeAlt;
            _fadeToVolume = _gradeVolume;
            _crossfade.Abort();
        }

        /// <summary>
        /// Collapse after a COMPLETED fade back to the canonical rest shape
        /// (current profile on slot A at weight 1, slot B at 0). Pixel-wise
        /// identical to the settled pair — the weights already are (0,1) and
        /// the profile moves between two slots of the same GameObject,
        /// priority and global flag; both writes land in this tick before the
        /// camera renders. Keeping the rest shape canonical is what preserves
        /// the A-M2-03 reading of <see cref="GradeVolume"/> (FrameGovernorHost
        /// captures/restores its weight).
        /// </summary>
        private void RenormalizeGradeVolumes()
        {
            if (_gradeVolume != null && _currentProfile != null)
            {
                _gradeVolume.sharedProfile = _currentProfile;
                _gradeVolume.weight = 1f;
            }

            if (_gradeVolumeAlt != null)
            {
                _gradeVolumeAlt.weight = 0f;
            }

            _fadeFromVolume = _gradeVolumeAlt;
            _fadeToVolume = _gradeVolume;
        }

        /// <summary>
        /// Creates the global grade volume host on demand: one child GameObject
        /// carrying BOTH stacked Volume slots (same priority; URP Volume has no
        /// DisallowMultipleComponent). Slot B starts at weight 0, which
        /// VolumeManager skips entirely — an exact no-op until a fade.
        /// </summary>
        private void EnsureGradeVolume()
        {
            if (_gradeVolume != null)
            {
                return;
            }

            var go = new GameObject("~GradeVolume");
            go.transform.SetParent(transform, false);
            _gradeVolume = go.AddComponent<Volume>();
            _gradeVolume.isGlobal = true;
            _gradeVolume.priority = 10f;

            _gradeVolumeAlt = go.AddComponent<Volume>();
            _gradeVolumeAlt.isGlobal = true;
            _gradeVolumeAlt.priority = 10f;
            _gradeVolumeAlt.weight = 0f;

            // Rest-shape roles: from = B @ 0, to = A @ 1 (matches the settled state).
            _fadeFromVolume = _gradeVolumeAlt;
            _fadeToVolume = _gradeVolume;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
