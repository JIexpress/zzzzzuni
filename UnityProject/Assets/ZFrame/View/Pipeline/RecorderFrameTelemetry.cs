using System;
using Unity.Profiling;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Engine-side <see cref="IFrameTelemetry"/> built on Unity's profiler
    /// recorder API (TASK-A-R8-01 design point 1): GPU frame cost from the
    /// "GPU Frame Time" counter, CPU frame cost from the unscaled wall-clock
    /// interval between <see cref="Pump"/> calls.
    ///
    /// GPU RECORDER PLATFORM AVAILABILITY (briefing-required note): the
    /// "GPU Frame Time" counter exists on GPU-backed profilers (D3D11/D3D12,
    /// Vulkan, Metal); it is NOT available on OpenGL/OpenGL ES targets and
    /// editor/player support can vary per platform and graphics driver. The
    /// availability probe (<c>Valid</c>) plus the non-positive-value gate in
    /// <see cref="GpuFrameMs"/> make every unsupported case degrade to the
    /// documented CPU fallback (the governor then governs on CPU frame cost —
    /// see <see cref="FrameGovernor"/>). When the counter is missing at
    /// construction, the recorder is not even started. Unity staff recommend
    /// FrameTimingManager as the more universal GPU-timing API — it needs the
    /// "Frame Timing Stats" player setting, so it is a documented follow-up,
    /// not part of this task (briefing pins the Recorder path).
    /// </summary>
    public sealed class RecorderFrameTelemetry : IFrameTelemetry, IDisposable
    {
        /// <summary>Profiler counter name of the total GPU frame duration.</summary>
        public const string GpuMarkerName = "GPU Frame Time";

        /// <summary>Categories probed for <see cref="GpuMarkerName"/> (first existing one wins).</summary>
        private static readonly ProfilerCategory[] GpuMarkerCategories =
        {
            ProfilerCategory.Render,
            ProfilerCategory.Internal,
        };

        private const string LogChannel = "Pipeline";

        private ProfilerRecorder _gpu;
        private readonly bool _gpuAvailable;
        private double _lastPumpTime = -1.0;
        private float _cpuFrameMs;

        /// <summary>True when the platform exposes the GPU frame counter (else this telemetry is CPU-only).</summary>
        public bool GpuRecorderAvailable => _gpuAvailable;

        public RecorderFrameTelemetry()
        {
            // The counter is registered under different categories depending on
            // the Unity version/platform — probe in order and take the first
            // that exists (Valid); anything else degrades to CPU-only.
            foreach (ProfilerCategory category in GpuMarkerCategories)
            {
                var gpuRecorder = ProfilerRecorder.StartNew(
                    category,
                    GpuMarkerName,
                    capacity: 1,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                             ProfilerRecorderOptions.WrapAroundWhenCapacityReached);
                if (gpuRecorder.Valid)
                {
                    _gpuAvailable = true;
                    _gpu = gpuRecorder;
                    break;
                }

                gpuRecorder.Stop();
                gpuRecorder.Dispose();
            }

            if (!_gpuAvailable)
            {
                ZLog.Info(LogChannel, $"GPU profiler counter '{GpuMarkerName}' not available on this platform — governor falls back to CPU frame cost.");
            }
        }

        /// <summary>Last completed GPU frame duration in ms; 0 when unavailable or no sample yet.</summary>
        public float GpuFrameMs
        {
            get
            {
                if (!_gpuAvailable || !_gpu.Valid || _gpu.LastValue <= 0)
                {
                    return 0f;
                }

                return _gpu.LastValue * 1e-6f; // nanoseconds -> milliseconds
            }
        }

        /// <summary>Unscaled wall-clock interval between the last two <see cref="Pump"/> calls in ms (0 before the second pump).</summary>
        public float CpuFrameMs => _cpuFrameMs;

        /// <summary>
        /// Samples the CPU frame cost; the host calls this at the top of its
        /// game-loop tick. The first call only establishes the baseline and
        /// yields 0 (an unusable sample the governor ignores).
        /// </summary>
        public void Pump()
        {
            double now = Time.unscaledTimeAsDouble;
            if (_lastPumpTime >= 0.0)
            {
                _cpuFrameMs = (float)((now - _lastPumpTime) * 1000.0);
            }

            _lastPumpTime = now;
        }

        /// <summary>Stops and releases the profiler recorder (idempotent).</summary>
        public void Dispose()
        {
            if (_gpu.Valid)
            {
                _gpu.Stop();
                _gpu.Dispose();
            }
        }
    }
}
