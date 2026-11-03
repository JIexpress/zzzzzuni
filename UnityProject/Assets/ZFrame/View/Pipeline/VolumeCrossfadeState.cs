namespace ZFrame.View
{
    /// <summary>
    /// Pure C# equal-power crossfade clock for grade Volume weights
    /// (TASK-A-R9-08 core piece, zero UnityEngine — same discipline as the
    /// A-M2-02 <see cref="CrossfadeState"/> it mirrors).
    ///
    /// Holds the weights of the two stacked Volume slots and animates the
    /// fade of a target profile over a duration using the equal-power cosine
    /// curve: with t measured 0→1 across the fade the outgoing ("from") slot
    /// carries cos(t·π/2) and the incoming ("to") slot sin(t·π/2). At the
    /// midpoint both carry cos(45°) ≈ 0.7071 and wFrom² + wTo² = 1 for every
    /// t — the A-M2-02 audio invariant (report: curve selection). Both
    /// endpoints are exact: (1,0) at the start, (0,1) once complete.
    ///
    /// Curve selection: the linear pair (a+b=1) was rejected because it dips
    /// the combined intensity of additive-ish overrides mid-fade and breaks
    /// the audio-reference semantics; smoothstep has no orthogonal invariant.
    /// The cosine equal-power curve keeps the briefing's named invariant
    /// a²+b²≡1 and reuses the shipped audio crossfade behaviour 1:1.
    ///
    /// The state animates ROLE weights only ("from" slot, "to" slot); which
    /// physical <c>Volume</c> component plays which role is the manager's
    /// bookkeeping — that is what keeps this core free of UnityEngine types.
    ///
    /// Semantics:
    ///  - the tier/profile is the INTENT and switches before any fade runs;
    ///    this clock is the presentation, so <see cref="Abort"/> (and
    ///    completion) settle on the TARGET: weights (0,1), never frozen
    ///    mid-blend;
    ///  - once settled every <see cref="Tick"/> is a no-op returning (0,1)
    ///    (完成停稳 — no drift after the fade ends);
    ///  - <see cref="Start(float,float)"/> accepts a start phase so an
    ///    interrupted fade can re-base on the dominant slot without a weight
    ///    pop (the manager's interrupt rule; see PipelineManager).
    /// </summary>
    public sealed class VolumeCrossfadeState
    {
        private float _duration;
        private float _elapsed;
        private bool _fading;

        /// <summary>
        /// Initializes an idle state settled on the target (weights (0,1) —
        /// "from" empty, "to" full; the manager maps these roles onto its
        /// Volume slots).
        /// </summary>
        public VolumeCrossfadeState()
        {
            _duration = 0f;
            _elapsed = 0f;
            _fading = false;
        }

        /// <summary>True while a fade is in progress (not yet complete).</summary>
        public bool IsFading => _fading;

        /// <summary>
        /// Fade progress 0..1 (elapsed/duration). 1 when no fade is running —
        /// the settled state IS the end of the curve. The manager reads this
        /// before re-Start to re-base an interrupted fade on the dominant slot.
        /// </summary>
        public float Progress01 => _fading && _duration > 0f ? _elapsed / _duration : 1f;

        /// <summary>
        /// Starts a fade over <paramref name="duration"/> seconds, beginning
        /// at <paramref name="startPhase01"/> of the curve (0 = the from slot
        /// fully on; use the previous <see cref="Progress01"/> to re-base an
        /// interrupted fade without a weight pop). A duration &lt;= 0 settles
        /// immediately on the target. Calling Start while settled with a
        /// positive duration always begins a fresh fade from the given phase.
        /// </summary>
        public void Start(float duration, float startPhase01 = 0f)
        {
            if (startPhase01 < 0f)
            {
                startPhase01 = 0f;
            }

            if (duration <= 0f || startPhase01 >= 1f)
            {
                // Snap (or already past the end): settle on the target.
                _duration = 0f;
                _elapsed = 0f;
                _fading = false;
                return;
            }

            _duration = duration;
            _elapsed = startPhase01 * duration; // the phase IS a time offset
            _fading = true;
        }

        /// <summary>
        /// Aborts the animation and lands on the TARGET immediately: weights
        /// (0,1), <see cref="IsFading"/> false. The intent (tier/profile) has
        /// already switched when a fade runs, so aborting the presentation
        /// means committing the intent — never freezing mid-blend. Idempotent.
        /// </summary>
        public void Abort()
        {
            _duration = 0f;
            _elapsed = 0f;
            _fading = false;
        }

        /// <summary>
        /// Advances the fade by <paramref name="dt"/> seconds and returns the
        /// current (weightFrom, weightTo) pair. Call once per frame from the
        /// game loop; safe to call when no fade is running (weights stay
        /// settled at (0,1) — the 停稳 guarantee).
        /// </summary>
        public (float WeightFrom, float WeightTo) Tick(float dt)
        {
            if (_fading)
            {
                _elapsed += dt;
                if (_elapsed >= _duration)
                {
                    _elapsed = _duration;
                    _fading = false;
                }
            }

            return CurrentWeights;
        }

        /// <summary>
        /// Current (weightFrom, weightTo) without advancing time. Settled
        /// fades report the exact target weights (0,1).
        /// </summary>
        public (float WeightFrom, float WeightTo) CurrentWeights
        {
            get
            {
                if (!_fading)
                {
                    return (0f, 1f);
                }

                float t = _elapsed / _duration;
                float toTarget = (float)System.Math.Sin(t * System.Math.PI / 2.0);
                float toFrom = (float)System.Math.Cos(t * System.Math.PI / 2.0);
                return (toFrom, toTarget);
            }
        }
    }
}
