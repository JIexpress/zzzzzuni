using System;
using System.Collections.Generic;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>One captured log entry.</summary>
    public readonly struct ZLogEntry
    {
        /// <summary>Timestamp in seconds; supplied by the recording side (e.g. Time.realtimeSinceStartupAsDouble).</summary>
        public readonly double Time;

        /// <summary>Severity.</summary>
        public readonly LogLevel Level;

        /// <summary>Channel name.</summary>
        public readonly string Channel;

        /// <summary>Message body.</summary>
        public readonly string Message;

        public ZLogEntry(double time, LogLevel level, string channel, string message)
        {
            Time = time;
            Level = level;
            Channel = channel;
            Message = message;
        }
    }

    /// <summary>
    /// Fixed-capacity ring buffer of recent ZLog output. Pure C# (no
    /// UnityEngine): timestamps come from an injectable provider so the
    /// class stays testable. The debug panel subscribes
    /// <see cref="Enqueue(LogLevel, string, string)"/> directly to
    /// <see cref="ZLog.OnLog"/>.
    /// </summary>
    public sealed class ZLogRingBuffer
    {
        private readonly ZLogEntry[] entries;
        private int head;
        private int count;

        /// <summary>Injectable clock (seconds). Defaults to a monotonic fake clock at 0.</summary>
        public Func<double> TimestampProvider { get; set; } = () => 0;

        /// <param name="capacity">Maximum retained entries; oldest are overwritten.</param>
        public ZLogRingBuffer(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            entries = new ZLogEntry[capacity];
        }

        /// <summary>Entries currently retained.</summary>
        public int Count => count;

        /// <summary>Capacity (never grows).</summary>
        public int Capacity => entries.Length;

        /// <summary>Signature-compatible with ZLog.OnLog — usable as a direct subscriber.</summary>
        public void Enqueue(LogLevel level, string channel, string message)
        {
            Enqueue(TimestampProvider(), level, channel, message);
        }

        /// <summary>Appends an entry, overwriting the oldest when full.</summary>
        public void Enqueue(double time, LogLevel level, string channel, string message)
        {
            entries[head] = new ZLogEntry(time, level, channel, message);
            head = (head + 1) % entries.Length;
            if (count < entries.Length)
            {
                count++;
            }
        }

        /// <summary>All retained entries, oldest first.</summary>
        public IEnumerable<ZLogEntry> Entries()
        {
            var start = (head - count + entries.Length) % entries.Length;
            for (int i = 0; i < count; i++)
            {
                yield return entries[(start + i) % entries.Length];
            }
        }

        /// <summary>Retained entries at or above <paramref name="minLevel"/>, oldest first.</summary>
        public IEnumerable<ZLogEntry> Entries(LogLevel minLevel)
        {
            foreach (var entry in Entries())
            {
                if (entry.Level >= minLevel)
                {
                    yield return entry;
                }
            }
        }

        /// <summary>Entry at <paramref name="index"/> counted from the newest (0 = latest), or null.</summary>
        public ZLogEntry? Latest(int index)
        {
            if (index < 0 || index >= count)
            {
                return null;
            }

            var pos = (head - 1 - index + entries.Length) % entries.Length;
            return entries[pos];
        }

        /// <summary>Drops all retained entries.</summary>
        public void Clear()
        {
            Array.Clear(entries, 0, entries.Length);
            head = 0;
            count = 0;
        }
    }
}
