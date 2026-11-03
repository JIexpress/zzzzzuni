using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>Severity levels for <see cref="ZLog"/>, ordered by importance.</summary>
    public enum LogLevel
    {
        Info = 0,
        Warn = 1,
        Error = 2,
    }

    /// <summary>
    /// Event fired on the <see cref="EventBus"/> whenever ZLog processes an
    /// Error-level message (immediate dispatch). Lets the future debug panel
    /// / crash reporter observe errors without wrapping every call site.
    /// </summary>
    public struct LogErrorEvent
    {
        public string Channel;
        public string Message;
    }

    /// <summary>
    /// Channel-filtered logging facade. All runtime output funnels through
    /// here and lands in UnityEngine.Debug.Log*.
    ///
    /// Filtering: a message is emitted when its channel is in the enabled
    /// whitelist AND its level is at or above <see cref="MinimumLevel"/>.
    /// Filters are global and reset by <see cref="Reset"/>; toggling
    /// channels at runtime from the debug panel is the intended workflow.
    ///
    /// Error-level messages additionally fire <see cref="LogErrorEvent"/>
    /// on the EventBus regardless of filtering (errors should never vanish
    /// silently).
    /// </summary>
    public static class ZLog
    {
        private static readonly HashSet<string> EnabledChannels = new HashSet<string>();

        /// <summary>
        /// Output sink, injectable for tests. Default wraps UnityEngine.Debug.
        /// Signature: (level, channel, message, formattedLine).
        /// </summary>
        private static Action<LogLevel, string, string, string> _sink = UnitySink;

        /// <summary>Messages below this level are dropped. Default Info (no level filtering).</summary>
        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        /// <summary>
        /// Mount point for log listeners (debug panel ring buffer, crash
        /// reporter, ...). Fired for every Write call BEFORE channel/level
        /// filtering, so listeners capture everything. Defined here in Core;
        /// subscribers (e.g. the View-side debug panel) attach from their own
        /// assembly — no reverse dependency. Signature: (level, channel, message).
        /// </summary>
        public static event Action<LogLevel, string, string> OnLog;

        /// <summary>
        /// Replaces the output sink. Pass null to restore the UnityEngine.Debug
        /// sink. Returns the previous sink so tests can restore it.
        /// </summary>
        public static Action<LogLevel, string, string, string> SetSink(
            Action<LogLevel, string, string, string> sink)
        {
            Action<LogLevel, string, string, string> previous = _sink;
            _sink = sink ?? UnitySink;
            return previous;
        }

        /// <summary>Enables a channel. Only enabled channels produce output.</summary>
        public static void EnableChannel(string channel)
        {
            EnabledChannels.Add(channel);
        }

        /// <summary>Disables a channel.</summary>
        public static void DisableChannel(string channel)
        {
            EnabledChannels.Remove(channel);
        }

        /// <summary>True when a message at (channel, level) would be emitted.</summary>
        public static bool WouldLog(string channel, LogLevel level)
        {
            return EnabledChannels.Contains(channel) && level >= MinimumLevel;
        }

        /// <summary>Info-level message on <paramref name="channel"/>.</summary>
        public static void Info(string channel, string msg)
        {
            Write(LogLevel.Info, channel, msg);
        }

        /// <summary>Warning-level message on <paramref name="channel"/>.</summary>
        public static void Warn(string channel, string msg)
        {
            Write(LogLevel.Warn, channel, msg);
        }

        /// <summary>
        /// Error-level message on <paramref name="channel"/>. Always fires
        /// <see cref="LogErrorEvent"/> on the EventBus, even when filtered
        /// from the console.
        /// </summary>
        public static void Error(string channel, string msg)
        {
            Write(LogLevel.Error, channel, msg);
            EventBus.Fire(new LogErrorEvent { Channel = channel, Message = msg });
        }

        /// <summary>Resets filters, minimum level and sink to pristine state (shutdown/tests).</summary>
        public static void Reset()
        {
            EnabledChannels.Clear();
            MinimumLevel = LogLevel.Info;
            _sink = UnitySink;
        }

        private static void Write(LogLevel level, string channel, string msg)
        {
            OnLog?.Invoke(level, channel, msg);

            if (!WouldLog(channel, level))
            {
                return;
            }

            _sink(level, channel, msg, $"[{channel}] {msg}");
        }

        private static void UnitySink(LogLevel level, string channel, string msg, string line)
        {
            switch (level)
            {
                case LogLevel.Error:
                    Debug.LogError(line);
                    break;
                case LogLevel.Warn:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }
    }
}
