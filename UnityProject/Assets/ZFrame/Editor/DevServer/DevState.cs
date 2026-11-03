using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 主线程每帧把 Unity 状态抓成一份不可变快照，后台线程只读快照回应 /health。
    /// 这样即便某一帧主线程被导入/编译占住，/health 也能立刻返回（并如实报出快照年龄）。
    /// <b>所有 Unity API 调用都发生在主线程调用 <see cref="Refresh"/> 时。</b>
    /// </summary>
    internal static class DevState
    {
        public static int DomainReloadCount;

        /// <summary>快照只在主线程整体替换，后台线程整份引用读取，故无需加锁。</summary>
        public static volatile Dictionary<string, object> Snapshot = new Dictionary<string, object>();

        static double _nextRefreshAt;
        static string _unityVersion;
        static int _processId;
        static long _snapshotStampMs;

        static int _idleTicks;
        static double _idleSumUs;
        static double _idlePeakUs;

        public static void Refresh(double now)
        {
            if (now < _nextRefreshAt && Snapshot.Count > 0) return;
            _nextRefreshAt = now + 0.25;

            if (_unityVersion == null)
            {
                _unityVersion = UnityEngine.Application.unityVersion;
                _processId = UnityEngine.Application.isEditor
                    ? System.Diagnostics.Process.GetCurrentProcess().Id
                    : 0;
            }

            Scene scene = SceneManager.GetActiveScene();
            double idleAverageUs = _idleTicks > 0 ? _idleSumUs / _idleTicks : 0.0;

            Snapshot = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["unityVersion"] = _unityVersion,
                ["pid"] = _processId,
                ["compiling"] = EditorApplication.isCompiling,
                ["importing"] = EditorApplication.isUpdating,
                ["playing"] = EditorApplication.isPlaying,
                ["paused"] = EditorApplication.isPaused,
                ["scene"] = scene.IsValid() ? scene.name : string.Empty,
                ["sceneDirty"] = scene.IsValid() && scene.isDirty,
                ["focused"] = EditorWindow.focusedWindow != null,
                ["domainReloadCount"] = DomainReloadCount,
                ["port"] = DevServerBootstrap.Port,
                ["uptimeMs"] = (long)((EditorApplication.timeSinceStartup - DevServerBootstrap.StartedAt) * 1000.0),
                ["pendingRequests"] = DevServerBootstrap.PendingCount,
                ["idlePumpTicks"] = _idleTicks,
                ["idlePumpUsAvg"] = Math.Round(idleAverageUs, 2),
                ["idlePumpUsMax"] = Math.Round(_idlePeakUs, 2),
            };

            _snapshotStampMs = NowMs();
        }

        /// <summary>主线程记录一次"空转泵"开销（没有请求要处理的那一帧）。</summary>
        public static void RecordIdlePump(double microseconds)
        {
            _idleTicks++;
            _idleSumUs += microseconds;
            if (microseconds > _idlePeakUs) _idlePeakUs = microseconds;

            // 每 5000 帧归一，避免长时间运行后平均值失真。
            if (_idleTicks >= 5000)
            {
                _idleTicks = 1;
                _idleSumUs = microseconds;
                _idlePeakUs = microseconds;
            }
        }

        public static long SnapshotAgeMs() => Snapshot.Count == 0 ? -1 : NowMs() - _snapshotStampMs;

        // Unity 的 .NET Standard 2.1 里没有 Environment.TickCount64，用 Stopwatch 单调时钟代替（只需同域内可比）。
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long NowMs() => _clock.ElapsedMilliseconds;
    }
}
