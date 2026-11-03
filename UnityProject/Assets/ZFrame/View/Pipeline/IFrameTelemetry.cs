namespace ZFrame.View
{
    /// <summary>
    /// Per-frame cost sample pumped into <see cref="FrameGovernor"/> every tick
    /// (TASK-A-R8-01 design point 1). Implementations live on the engine side
    /// (<see cref="RecorderFrameTelemetry"/>); tests inject fakes, which is what
    /// keeps the governor core EditMode-testable without a player loop.
    /// </summary>
    public interface IFrameTelemetry
    {
        /// <summary>
        /// GPU cost of the last completed frame in milliseconds.
        /// The sentinel value <c>0f</c> (or any non-positive value) means
        /// "GPU timing unavailable on this platform" — the governor then
        /// falls back to <see cref="CpuFrameMs"/> and notes the downgrade.
        /// </summary>
        float GpuFrameMs { get; }

        /// <summary>Full CPU frame cost in milliseconds (unscaled, wall clock).</summary>
        float CpuFrameMs { get; }
    }
}
