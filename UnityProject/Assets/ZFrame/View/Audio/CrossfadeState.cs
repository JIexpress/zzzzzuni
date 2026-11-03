namespace ZFrame.View
{
    /// <summary>
    /// Pure C# equal-power crossfade clock (TASK-A-M2-02 core piece, zero
    /// UnityEngine). Holds the weights of two BGM tracks and animates the
    /// fade-in of a target track over a duration using the equal-power cosine
    /// curve. With t measured 0→1 across the fade, the target track gets
    /// sin(t·π/2) and the other track cos(t·π/2) — at the midpoint both carry
    /// cos(45°) ≈ 0.707, and wTarget² + wOther² = 1 for every t, which keeps
    /// perceived loudness constant during the fade.
    ///
    /// The consumer (AudioService) applies the returned weights to the two
    /// AudioSources each frame; fading out both tracks (StopBgm) is expressed
    /// by targeting the track that carries no clip.
    /// </summary>
    public sealed class CrossfadeState
    {
        // Track indices used by Start: 0 = track A, 1 = track B.
        private int _target;
        private float _duration;
        private float _elapsed;
        private bool _fading;

        /// <summary>
        /// Initializes an idle state settled on track A (weights 1, 0) —
        /// nothing has played yet.
        /// </summary>
        public CrossfadeState()
        {
            _target = 0;
            _duration = 0f;
            _elapsed = 0f;
            _fading = false;
        }

        /// <summary>Track index the fade is heading toward (0 or 1).</summary>
        public int Target => _target;

        /// <summary>True while a fade is in progress (not yet complete).</summary>
        public bool IsFading => _fading;

        /// <summary>
        /// Starts a fade toward <paramref name="targetTrack"/> over
        /// <paramref name="duration"/> seconds. A duration &lt;= 0 snaps
        /// immediately to the target. Starting a fade toward the track that is
        /// already fully active while no fade is running is a no-op.
        /// </summary>
        public void Start(int targetTrack, float duration)
        {
            if (targetTrack != 0 && targetTrack != 1)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(targetTrack), targetTrack, "CrossfadeState: target track must be 0 or 1.");
            }

            if (!_fading && targetTrack == _target)
            {
                return; // already settled on the requested target
            }

            _target = targetTrack;
            _duration = duration > 0f ? duration : 0f;
            _elapsed = 0f;
            _fading = _duration > 0f;
        }

        /// <summary>
        /// Advances the fade by <paramref name="dt"/> seconds and returns the
        /// current (trackA, trackB) weights. Call once per frame from the game
        /// loop; safe to call when no fade is running (weights stay settled).
        /// </summary>
        public (float WeightA, float WeightB) Tick(float dt)
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

            return CurrentWeights();
        }

        /// <summary>
        /// Current (trackA, trackB) weights without advancing time — the
        /// settled state when no fade is running.
        /// </summary>
        public (float WeightA, float WeightB) CurrentWeights()
        {
            if (!_fading)
            {
                return _target == 0 ? (1f, 0f) : (0f, 1f);
            }

            float t = _duration > 0f ? _elapsed / _duration : 1f;
            float toTarget = (float)System.Math.Sin(t * System.Math.PI / 2.0);
            float toOther = (float)System.Math.Cos(t * System.Math.PI / 2.0);

            return _target == 0 ? (toTarget, toOther) : (toOther, toTarget);
        }
    }
}
