using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// Console 环形缓冲。用 logMessageReceivedThreaded（回调可能来自任意线程），内部加锁 + 自增 seq。
    /// 只在服务启用时才挂 hook，服务关闭后完全卸载，行为与没有本服务时一致。
    /// </summary>
    internal sealed class DevConsoleRing
    {
        public sealed class Entry
        {
            public long Seq;
            public string Type;
            public string Message;
            public string Stack;
        }

        const int Capacity = 2000;

        /// <summary>domain reload 会重建缓冲；seq 起点存 SessionState，保证客户端 since= 增量拉取不会错位。</summary>
        const string SeqKey = "ZFrame.DevServer.ConsoleSeq";

        static DevConsoleRing _instance;

        readonly Entry[] _buffer;
        readonly object _gate = new object();
        int _count;
        int _next;
        long _seq;

        DevConsoleRing()
        {
            _buffer = new Entry[Capacity];
        }

        public static void Install()
        {
            if (_instance != null) return;
            _instance = new DevConsoleRing();
            _instance._seq = SessionState.GetInt(SeqKey, 0);
            Application.logMessageReceivedThreaded += _instance.OnLog;
        }

        public static void Uninstall()
        {
            DevConsoleRing instance = _instance;
            _instance = null;
            if (instance == null) return;

            SessionState.SetInt(SeqKey, (int)System.Math.Min(instance.SeqLocked(), int.MaxValue));
            Application.logMessageReceivedThreaded -= instance.OnLog;
        }

        public static bool IsInstalled => _instance != null;

        public static long CurrentSeq
        {
            get { return _instance != null ? _instance.SeqLocked() : 0; }
        }

        public static List<Entry> Read(long since, int max, out long next)
        {
            DevConsoleRing instance = _instance;
            if (instance == null)
            {
                next = 0;
                return new List<Entry>();
            }

            return instance.ReadLocked(since, max, out next);
        }

        long SeqLocked()
        {
            lock (_gate) return _seq;
        }

        List<Entry> ReadLocked(long since, int max, out long next)
        {
            var result = new List<Entry>();
            lock (_gate)
            {
                next = _seq;
                int available = _count;
                int start = (_next - available + _buffer.Length * 2) % _buffer.Length;
                for (int i = 0; i < available; i++)
                {
                    Entry entry = _buffer[(start + i) % _buffer.Length];
                    if (entry == null || entry.Seq <= since) continue;
                    result.Add(entry);
                    if (result.Count >= max) break;
                }
            }

            return result;
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (_gate)
            {
                _seq++;
                _buffer[_next] = new Entry
                {
                    Seq = _seq,
                    Type = MapType(type),
                    Message = condition,
                    Stack = stackTrace,
                };
                _next = (_next + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }
        }

        static string MapType(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                case LogType.Exception: return "exception";
                default: return "log";
            }
        }
    }
}
