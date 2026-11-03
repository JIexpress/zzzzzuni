using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// Pure C# SFX play-rate limiter (TASK-A-M2-02 core piece, zero
    /// UnityEngine). Two rules, both per frame:
    ///  1. Same-<see cref="id"/> dedup — an id may play at most once per frame
    ///     (protects against "12 identical gunshots on one physics frame").
    ///  2. Frame budget — at most <see cref="MaxPerFrame"/> plays pass per
    ///     frame regardless of id.
    ///
    /// The consumer calls <see cref="BeginFrame"/> once per game-loop tick and
    /// gates every SFX attempt through <see cref="TryPlay"/>, which returns
    /// false (play rejected) when either rule blocks it.
    /// </summary>
    public sealed class SfxLimiter
    {
        /// <summary>How many SFX plays may pass in a single frame.</summary>
        public int MaxPerFrame { get; }

        private readonly HashSet<string> _playedThisFrame = new HashSet<string>();
        private int _passedThisFrame;

        /// <summary>Creates a limiter with the given per-frame play budget.</summary>
        public SfxLimiter(int maxPerFrame)
        {
            MaxPerFrame = maxPerFrame < 1 ? 1 : maxPerFrame;
        }

        /// <summary>
        /// Starts a new frame: clears the same-id dedup set and the frame
        /// budget. Call once per tick before any <see cref="TryPlay"/>.
        /// </summary>
        public void BeginFrame()
        {
            _playedThisFrame.Clear();
            _passedThisFrame = 0;
        }

        /// <summary>
        /// Requests one play of <paramref name="id"/> in the current frame.
        /// Returns true when the play passes (id not yet played this frame AND
        /// frame budget not exhausted) and records it; false when rejected.
        /// </summary>
        public bool TryPlay(string id)
        {
            if (_playedThisFrame.Contains(id))
            {
                return false; // same sound already played this frame
            }

            if (_passedThisFrame >= MaxPerFrame)
            {
                return false; // frame budget exhausted
            }

            _playedThisFrame.Add(id);
            _passedThisFrame++;
            return true;
        }

        /// <summary>Number of plays that passed this frame (debug/tests).</summary>
        public int PassedThisFrame => _passedThisFrame;
    }
}
