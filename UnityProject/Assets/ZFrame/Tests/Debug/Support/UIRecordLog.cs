using System.Collections.Generic;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Shared lifecycle call recorder for UI fakes (one ordered list per test, reset in SetUp).</summary>
    public static class UIRecordLog
    {
        private static int _nextId = 1;

        /// <summary>Ordered lifecycle entries like "3.OnPause".</summary>
        public static List<string> Entries { get; } = new List<string>();

        /// <summary>Hands out unique view ids so several fakes can share one log without collisions.</summary>
        public static int NextId()
        {
            return _nextId++;
        }

        public static void Add(string entry)
        {
            Entries.Add(entry);
        }

        public static void Reset()
        {
            Entries.Clear();
            _nextId = 1;
        }
    }
}
