using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Typed key-value store for behaviour trees (TASK-A-R8-03): the outside
    /// world PUMPS sensor data in (player position, self position, waypoints...),
    /// conditions read it — conditions never touch the engine, mirroring the
    /// M4-01 FSM data-driven pattern.
    ///
    /// Type safety: <see cref="TryGet{T}"/> only succeeds when the stored value
    /// is exactly assignable to T — a "wrong type" read returns false instead
    /// of throwing or converting. Keys are ordinal strings; the well-known keys
    /// of a tree live as constants next to the tree that owns them (see
    /// Game.Domain.AI.EnemyBehaviorTree.Keys for the worked example).
    /// </summary>
    public sealed class Blackboard
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Number of stored entries (diagnostics/tests).</summary>
        public int Count => _values.Count;

        /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (overwrites, including across types). Null key throws.</summary>
        public void Set<T>(string key, T value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            _values[key] = value;
        }

        /// <summary>
        /// Reads <paramref name="key"/> as <typeparamref name="T"/>. Returns
        /// false when the key is missing, the stored value is null, or the
        /// stored type does not match T (no conversions — fail closed).
        /// </summary>
        public bool TryGet<T>(string key, out T value)
        {
            if (key != null && _values.TryGetValue(key, out object boxed) && boxed is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>True when the key exists (even when its value fails <see cref="TryGet{T}"/>).</summary>
        public bool Has(string key)
        {
            return key != null && _values.ContainsKey(key);
        }

        /// <summary>Removes a key. Returns true when it existed.</summary>
        public bool Remove(string key)
        {
            return key != null && _values.Remove(key);
        }

        /// <summary>Drops every entry (keeps the same dictionary — no re-allocation).</summary>
        public void Clear()
        {
            _values.Clear();
        }
    }
}
