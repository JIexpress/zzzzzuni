using System;

namespace ZFrame.View
{
    /// <summary>
    /// The FrameGovernor pure core (TASK-A-R8-01 design point 1): a hysteresis
    /// state machine over per-frame cost samples driving one measure ladder.
    ///
    /// Contract (all engine-free, EditMode-testable with injected time and
    /// telemetry — this type touches neither UnityEngine nor global state):
    ///  - <see cref="Enabled"/> defaults to FALSE: the governor never
    ///    intervenes until explicitly enabled (briefing design point).
    ///  - Effective frame cost per tick = <see cref="IFrameTelemetry.GpuFrameMs"/>
    ///    when positive (GPU timing available), else
    ///    <see cref="IFrameTelemetry.CpuFrameMs"/> (documented CPU fallback).
    ///  - Hysteresis: a sample strictly below TargetMs·(1−UpThreshold)
    ///    accumulates the UP timer; strictly above TargetMs·(1+DownThreshold)
    ///    the DOWN timer; inside the deadband both timers reset. Stepping one
    ///    rung requires the full sustained window (N for up, M for down,
    ///    N &gt; M) and resets both timers afterwards, so every step needs a
    ///    fresh full window — alternating load can never oscillate the level.
    ///  - Ladder levels are 0 (no measure) … <see cref="GovernorLadder.Count"/>
    ///    (TargetFps give-up rung); moves are one rung per window at most.
    ///  - A disabled governor, a non-positive <see cref="TargetMs"/>, a null
    ///    sample or a non-positive dt all fully bypass the tick AND reset the
    ///    timers (no stale accumulation across gaps).
    /// </summary>
    public sealed class FrameGovernor
    {
        private readonly GovernorLadder _ladder;
        private readonly GovernorTuning _tuning;
        private float _upTimer;
        private float _downTimer;
        private int _level;

        /// <summary>
        /// Master switch, default FALSE — the governor only intervenes after
        /// an explicit opt-in (settings page wiring, TASK-A-R8-01).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Target frame cost in milliseconds (1000 / target fps). Zero or negative disables governance.</summary>
        public float TargetMs { get; set; }

        /// <summary>The immutable measure ladder this governor walks.</summary>
        public GovernorLadder Ladder => _ladder;

        /// <summary>Current ladder level: 0 = no measure, <see cref="GovernorLadder.Count"/> = give-up rung.</summary>
        public int Level => _level;

        /// <summary>Accumulated desired settings state at the current level.</summary>
        public GovernorState CurrentState => _ladder.StateAtIndex(_level);

        /// <summary>True when the last processed sample used GPU timing (false = CPU fallback was used).</summary>
        public bool LastSampleUsedGpu { get; private set; }

        /// <summary>Sustained-comfort timer in seconds (diagnostics/tests).</summary>
        public float UpTimer => _upTimer;

        /// <summary>Sustained-overload timer in seconds (diagnostics/tests).</summary>
        public float DownTimer => _downTimer;

        /// <summary>
        /// Creates a governor over <paramref name="ladder"/>. Passing
        /// <c>default</c> for <paramref name="tuning"/> selects
        /// <see cref="GovernorTuning.Default"/>; any explicit tuning keeps the
        /// N &gt; M validation of <see cref="GovernorTuning"/>.
        /// </summary>
        public FrameGovernor(GovernorLadder ladder, GovernorTuning tuning = default)
        {
            _ladder = ladder ?? throw new ArgumentNullException(nameof(ladder));
            _tuning = tuning.UpWindowSeconds > 0f ? tuning : GovernorTuning.Default;
        }

        /// <summary>
        /// Advances the state machine by <paramref name="dtSeconds"/> with one
        /// telemetry sample. Returns whether the active level changed; the
        /// timers survive between ticks, so callers must pump every frame.
        /// </summary>
        public GovernorDecision Tick(float dtSeconds, IFrameTelemetry sample)
        {
            if (!Enabled || TargetMs <= 0f || sample == null || dtSeconds <= 0f)
            {
                ResetTimers();
                return GovernorDecision.None(_level);
            }

            float gpuMs = sample.GpuFrameMs;
            float cpuMs = sample.CpuFrameMs;
            LastSampleUsedGpu = gpuMs > 0f;
            float frameMs = LastSampleUsedGpu ? gpuMs : cpuMs;
            if (frameMs <= 0f)
            {
                // Unusable sample (e.g. first frame after boot): never let a
                // gap silently satisfy a "sustained" window.
                ResetTimers();
                return GovernorDecision.None(_level);
            }

            float dt = Math.Min(dtSeconds, GovernorTuning.MaxDeltaSeconds);
            float upBound = TargetMs * (1f - _tuning.UpThreshold);
            float downBound = TargetMs * (1f + _tuning.DownThreshold);

            if (frameMs < upBound)
            {
                _upTimer += dt;
                _downTimer = 0f;
            }
            else if (frameMs > downBound)
            {
                _downTimer += dt;
                _upTimer = 0f;
            }
            else
            {
                _upTimer = 0f;
                _downTimer = 0f;
            }

            int from = _level;
            if (_downTimer >= _tuning.DownWindowSeconds && from < _ladder.Count)
            {
                _level = from + 1;
                ResetTimers();
            }
            else if (_upTimer >= _tuning.UpWindowSeconds && from > 0)
            {
                _level = from - 1;
                ResetTimers();
            }

            return from == _level
                ? GovernorDecision.None(_level)
                : GovernorDecision.Change(from, _level, _ladder.MeasureAtLevel(from), _ladder.MeasureAtLevel(_level));
        }

        private void ResetTimers()
        {
            _upTimer = 0f;
            _downTimer = 0f;
        }
    }
}
