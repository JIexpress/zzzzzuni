namespace ZFrame.Core
{
    /// <summary>
    /// Read-only view of the game clock. Logic systems receive this via
    /// <see cref="Services"/> instead of touching UnityEngine.Time so they
    /// stay unit-testable and the pause policy lives in one place.
    /// </summary>
    public interface IGameTime
    {
        /// <summary>Delta time of the current frame, scaled by <see cref="TimeScale"/>; 0 while paused.</summary>
        float DeltaTime { get; }

        /// <summary>Unscaled delta time of the current frame (real elapsed time).</summary>
        float UnscaledDeltaTime { get; }

        /// <summary>Multiplier applied to <see cref="DeltaTime"/>. Pause takes precedence over any scale.</summary>
        float TimeScale { get; }

        /// <summary>True while the Logic stage is frozen. Input/View/Late keep ticking.</summary>
        bool IsPaused { get; }

        /// <summary>Total unscaled seconds elapsed since boot.</summary>
        float TotalUnscaled { get; }
    }
}
