using System;
using Unity.Profiling;
using UnityEngine.Profiling;

namespace ZFrame.View
{
    /// <summary>
    /// HUD 的采样器：持有 <see cref="ProfilerRecorder"/> 句柄。
    ///
    /// **懒启动**是本类的核心约束：句柄只在 <see cref="Start"/> 里创建，<see cref="Stop"/>
    /// 里 Stop+Dispose。HUD 不可见时组件根本不会调用 Start，因此"关闭"状态下
    /// 既没有 recorder 也没有逐帧读取——不采样才是真的不采样，不是"采样了但不画"。
    ///
    /// 数据来源（全部 Unity 侧，不碰原生）：
    /// - Draw Calls / SetPass Calls / Triangles：<see cref="ProfilerCategory.Render"/> 计数器；
    /// - 显存：<see cref="Profiler.GetAllocatedMemoryForGraphicsDriver"/>；
    /// - GPU 时间："GPU Frame Time" 计数器（纳秒），平台不支持时 <see cref="GpuAvailable"/> 为 false。
    ///
    /// 任何 recorder 创建失败都只影响对应字段（返回 0/不可用），绝不抛到游戏循环。
    /// </summary>
    public sealed class PerformanceHudSampler : IDisposable
    {
        /// <summary>每帧 DrawCall 计数（Render 类别）。</summary>
        public const string DrawCallsMarker = "Draw Calls Count";

        /// <summary>每帧 SetPass 计数（Render 类别）。</summary>
        public const string SetPassMarker = "SetPass Calls Count";

        /// <summary>每帧三角面数（Render 类别）。</summary>
        public const string TrianglesMarker = "Triangles Count";

        /// <summary>GPU 帧时间计数器（纳秒；与 RecorderFrameTelemetry 保持一致）。</summary>
        public const string GpuMarkerName = "GPU Frame Time";

        private static readonly ProfilerCategory[] GpuMarkerCategories =
        {
            ProfilerCategory.Render,
            ProfilerCategory.Internal,
        };

        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _setPass;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _gpu;

        /// <summary>测试用：当前活着的采样器数量（关闭后必须回到 0）。</summary>
        internal static int LiveSamplerCount { get; private set; }

        /// <summary>是否正在采样。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>GPU 计数器是否可用（不支持时 HUD 显示占位符）。</summary>
        public bool GpuAvailable { get; private set; }

        public long DrawCalls => Read(_drawCalls);

        public long SetPassCalls => Read(_setPass);

        public long Triangles => Read(_triangles);

        /// <summary>graphics driver 分配量（字节）；平台不支持时为 0。</summary>
        public long GraphicsMemoryBytes
        {
            get
            {
                try
                {
                    return Profiler.GetAllocatedMemoryForGraphicsDriver();
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>最近一帧 GPU 时间（毫秒）；不可用/无样本时为 -1。</summary>
        public double GpuMilliseconds
        {
            get
            {
                if (!GpuAvailable || !_gpu.Valid || _gpu.Count == 0) return -1.0;
                long nanoseconds = _gpu.LastValue;
                if (nanoseconds <= 0) return -1.0;
                return nanoseconds * 1e-6;
            }
        }

        /// <summary>启动采样（幂等）。失败时对应读数为 0，不抛异常。</summary>
        public void Start()
        {
            if (IsRunning) return;

            _drawCalls = StartRecorder(ProfilerCategory.Render, DrawCallsMarker);
            _setPass = StartRecorder(ProfilerCategory.Render, SetPassMarker);
            _triangles = StartRecorder(ProfilerCategory.Render, TrianglesMarker);

            for (int i = 0; i < GpuMarkerCategories.Length; i++)
            {
                ProfilerRecorder candidate = StartRecorder(GpuMarkerCategories[i], GpuMarkerName);
                if (candidate.Valid)
                {
                    _gpu = candidate;
                    GpuAvailable = true;
                    break;
                }

                DisposeRecorder(ref candidate);
            }

            IsRunning = true;
            LiveSamplerCount++;
        }

        /// <summary>停止采样并释放所有 recorder（幂等）。</summary>
        public void Stop()
        {
            if (!IsRunning) return;

            DisposeRecorder(ref _drawCalls);
            DisposeRecorder(ref _setPass);
            DisposeRecorder(ref _triangles);
            DisposeRecorder(ref _gpu);

            GpuAvailable = false;
            IsRunning = false;
            LiveSamplerCount--;
        }

        public void Dispose()
        {
            Stop();
        }

        private static ProfilerRecorder StartRecorder(ProfilerCategory category, string marker)
        {
            try
            {
                return ProfilerRecorder.StartNew(category, marker, 1, ProfilerRecorderOptions.Default);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static long Read(ProfilerRecorder recorder)
        {
            if (!recorder.Valid || recorder.Count == 0) return 0;
            long value = recorder.LastValue;
            return value < 0 ? 0 : value;
        }

        private static void DisposeRecorder(ref ProfilerRecorder recorder)
        {
            if (!recorder.Valid) return;
            recorder.Stop();
            recorder.Dispose();
            recorder = default;
        }
    }
}
