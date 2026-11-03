using System;
using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// Per-scene key/value bag passed through
    /// <see cref="SceneLoader.LoadSceneAsync(string, IReadOnlyDictionary{string,string})"/>.
    /// Deliberately string-typed (simple, serializable, cross-domain safe);
    /// after the M3 UI framework lands this can grow a typed wrapper — call
    /// sites then migrate without changing the transport.
    ///
    /// Consume semantics: <see cref="ConsumePayload"/> returns the value AND
    /// removes it, so a value is read exactly once per scene (no stale
    /// re-reads on the next visit to the same scene).
    /// </summary>
    public sealed class ScenePayload
    {
        private readonly Dictionary<string, string> _data;

        /// <summary>Creates an empty payload.</summary>
        public ScenePayload()
        {
            _data = new Dictionary<string, string>();
        }

        /// <summary>Creates a payload seeded with a copy of <paramref name="initial"/> (null tolerated = empty).</summary>
        public ScenePayload(IReadOnlyDictionary<string, string> initial)
        {
            _data = new Dictionary<string, string>();
            if (initial != null)
            {
                foreach (var pair in initial)
                {
                    _data[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>Number of entries not yet consumed.</summary>
        public int Count => _data.Count;

        /// <summary>True when nothing is left to consume.</summary>
        public bool IsEmpty => _data.Count == 0;

        /// <summary>Stores (or overwrites) <paramref name="key"/>. Null key throws — fail fast.</summary>
        public void Set(string key, string value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            _data[key] = value;
        }

        /// <summary>Reads without consuming.</summary>
        public bool TryPeek(string key, out string value)
        {
            if (key == null)
            {
                value = null;
                return false;
            }

            return _data.TryGetValue(key, out value);
        }

        /// <summary>
        /// Returns the value for <paramref name="key"/> and removes it
        /// (consume semantics). Returns null when the key is absent or the
        /// key itself is null — callers treat "no payload entry" as a plain
        /// null, never an exception.
        /// </summary>
        public string ConsumePayload(string key)
        {
            if (key == null || !_data.TryGetValue(key, out string value))
            {
                return null;
            }

            _data.Remove(key);
            return value;
        }

        /// <summary>Removes every entry. Used when a new load supersedes the previous scene's payload.</summary>
        public void Clear()
        {
            _data.Clear();
        }
    }
}
