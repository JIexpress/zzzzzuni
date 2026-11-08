using System;
using System.Collections.Generic;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// Key-value world snapshot a utility brain scores against (TASK-A-TECH-01).
    /// The outside world PUMPS sensor data in (own fatigue, distance to the
    /// player, perceived drop distance, inventory size...) exactly like the
    /// behaviour-tree <c>ZFrame.Gameplay.Blackboard</c> pattern; score
    /// functions read it and never touch engine state. Pure C#: this type is
    /// part of the engine-free logic layer and is deliberately a NEW store
    /// instead of reusing the gameplay blackboard so the utility slice stays
    /// self-contained and extractable later.
    ///
    /// Type safety mirrors the blackboard: <see cref="TryGet{T}"/> only
    /// succeeds when the stored value is exactly assignable to T — a wrong-type
    /// read returns false instead of throwing or converting. Well-known keys
    /// live in <see cref="Keys"/>; custom keys are allowed for experiments.
    /// </summary>
    public sealed class NPCWorldState
    {
        /// <summary>Well-known keys of the utility slice demo (typed channels).</summary>
        public static class Keys
        {
            /// <summary>float 0..1 — how tired the NPC is (1 = exhausted).</summary>
            public const string Fatigue = "npc.fatigue";

            /// <summary>float — metres to the last pumped player position.</summary>
            public const string PlayerDistance = "npc.playerDistance";

            /// <summary>float — metres to the nearest perceived drop; ABSENT means none perceived.</summary>
            public const string DropDistance = "npc.dropDistance";

            /// <summary>int — items already carried (used for diminishing pickup desire).</summary>
            public const string InventoryCount = "npc.inventoryCount";

            /// <summary>
            /// float — metres to the repair toolbox (tool source); ABSENT means
            /// no toolbox perceived (TASK-A-TECH-04 GOAP slice).
            /// </summary>
            public const string ToolboxDistance = "npc.toolboxDistance";

            /// <summary>float — metres to the broken device (repair target); ABSENT means none perceived.</summary>
            public const string DeviceDistance = "npc.deviceDistance";

            /// <summary>bool — true while the device is broken and repair is pending.</summary>
            public const string DeviceBroken = "npc.deviceBroken";

            /// <summary>
            /// float 0..1 — current fear (TASK-A-AI-01 attitude layer; pumped
            /// by <see cref="NpcUtilityAgent"/> from its memory aggregation).
            /// </summary>
            public const string Fear = "npc.fear";

            /// <summary>float 0..1 — current anger (TASK-A-AI-01 attitude layer).</summary>
            public const string Anger = "npc.anger";

            /// <summary>float 0..1 — current trust (TASK-A-AI-01 attitude layer).</summary>
            public const string Trust = "npc.trust";

            /// <summary>
            /// float — metres to the pumped threat source; ABSENT means no
            /// threat is perceived (TASK-A-AI-01).
            /// </summary>
            public const string ThreatDistance = "npc.threatDistance";
        }

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

        /// <summary>Reads a float key, or <paramref name="fallback"/> when absent (score curves prefer this over branching).</summary>
        public float GetFloat(string key, float fallback)
        {
            return TryGet(key, out float value) ? value : fallback;
        }

        /// <summary>Reads an int key, or <paramref name="fallback"/> when absent.</summary>
        public int GetInt(string key, int fallback)
        {
            return TryGet(key, out int value) ? value : fallback;
        }

        /// <summary>True when <see cref="Keys.DropDistance"/> is present (a drop is perceived).</summary>
        public bool HasDrop()
        {
            return Has(Keys.DropDistance);
        }

        /// <summary>True when <see cref="Keys.ThreatDistance"/> is present (a threat is perceived; TASK-A-AI-01).</summary>
        public bool HasThreat()
        {
            return Has(Keys.ThreatDistance);
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
