using System;
using System.Collections.Generic;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// An NPC's episodic memory (TASK-A-AI-01): a bounded, rolling list of
    /// <see cref="MemoryEntry"/> plus the pure aggregation the attitude layer
    /// reads. Capacity defaults to 32 (the brief's "容量上限 32，滚动丢弃最旧"):
    /// adding entry 33 silently evicts the OLDEST — forgetting is the design,
    /// not a leak; an NPC's past fades both by expiry (eviction) and by
    /// half-life decay (weighting).
    ///
    /// Determinism contract: no clock and no randomness here — time is a
    /// parameter (<paramref name="now"/>), so the same entries + same now =
    /// same aggregate, asserted in tests.
    /// </summary>
    public sealed class NpcMemory
    {
        /// <summary>Default capacity (the brief's 32-entry rolling window).</summary>
        public const int DefaultCapacity = 32;

        private readonly List<MemoryEntry> _entries;
        private readonly int _capacity;

        /// <summary>Creates a memory. <paramref name="capacity"/> must be > 0.</summary>
        public NpcMemory(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "NpcMemory: capacity must be > 0.");
            }

            _capacity = capacity;
            _entries = new List<MemoryEntry>(capacity);
        }

        /// <summary>Maximum kept episodes (oldest evicted beyond this).</summary>
        public int Capacity => _capacity;

        /// <summary>Kept episodes, oldest first (read-only view).</summary>
        public IReadOnlyList<MemoryEntry> Entries => _entries;

        /// <summary>
        /// Adds one episode; when the store is full the OLDEST entry is
        /// evicted first (rolling window). Returns true when an entry was
        /// evicted to make room (diagnostics/tests).
        /// </summary>
        public bool Add(MemoryEntry entry)
        {
            bool evicted = false;
            if (_entries.Count >= _capacity)
            {
                _entries.RemoveAt(0);
                evicted = true;
            }

            _entries.Add(entry);
            return evicted;
        }

        /// <summary>Drops every episode (same list instance — no re-allocation).</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Pure aggregation for one event type (the brief's
        /// <c>Aggregate(attitudeType)</c>): the time-decay weighted sum of the
        /// valences of all entries OF THAT TYPE —
        /// <c>Σ NpcAttitudeMath.DecayWeight(now − e.Timestamp, halfLife) × e.Valence</c>.
        /// Recent episodes weigh more (decay 1 at age 0, 0.5 at one half-life,
        /// 0.25 at two). Entries of other types are ignored; an empty match is
        /// 0. The SIGN follows the valences (negative for bad episodes); the
        /// attitude layer (<see cref="NpcAttitudeMath.Evaluate"/>) splits that
        /// sign into threat vs goodwill and applies the per-type profile gains.
        /// </summary>
        /// <param name="type">Only episodes of this type aggregate.</param>
        /// <param name="now">Current time in the owner clock domain (seconds).</param>
        /// <param name="halfLifeSeconds">Decay half-life (must be > 0).</param>
        public float Aggregate(MemoryEventType type, float now, float halfLifeSeconds)
        {
            float sum = 0f;
            for (int i = 0; i < _entries.Count; i++)
            {
                MemoryEntry entry = _entries[i];
                if (entry.Type != type)
                {
                    continue;
                }

                float weight = NpcAttitudeMath.DecayWeight(now - entry.Timestamp, halfLifeSeconds);
                sum += weight * entry.Valence;
            }

            return sum;
        }
    }
}
