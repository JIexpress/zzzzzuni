namespace ZFrame.Core
{
    /// <summary>
    /// Pure C# <see cref="IGameTime"/> implementation. Time is pumped in from
    /// the outside via <see cref="Advance"/> (the GameLoopDriver passes
    /// UnityEngine.Time.unscaledDeltaTime); no UnityEngine dependency, so
    /// EditMode tests can drive the clock by hand.
    /// </summary>
    public sealed class GameTime : IGameTime
    {
        /// <inheritdoc />
        public float DeltaTime { get; private set; }

        /// <inheritdoc />
        public float UnscaledDeltaTime { get; private set; }

        /// <inheritdoc />
        public float TimeScale { get; set; } = 1f;

        /// <inheritdoc />
        public bool IsPaused { get; set; }

        /// <inheritdoc />
        public float TotalUnscaled { get; private set; }

        /// <summary>
        /// Pumps one frame of real time through the clock. Call exactly once
        /// per frame, before any stage ticks.
        /// </summary>
        /// <param name="unscaledDt">Real elapsed seconds since the previous Advance.</param>
        public void Advance(float unscaledDt)
        {
            UnscaledDeltaTime = unscaledDt;
            TotalUnscaled += unscaledDt;
            DeltaTime = IsPaused ? 0f : unscaledDt * TimeScale;
        }
    }
}
