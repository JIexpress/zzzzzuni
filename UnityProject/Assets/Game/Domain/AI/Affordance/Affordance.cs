using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Domain.AI.Affordances
{
    /// <summary>
    /// One thing the world offers an NPC (TASK-A-AI-03): a typed spot at a
    /// plane (XZ) position with a radius and a hard capacity. The affordance
    /// OWNS its occupancy slots (agent ids in a set) so that:
    /// - claiming twice with the same agent id is idempotent (a continuous
    ///   behaviour wins several ticks in a row and must not eat capacity);
    /// - <see cref="Occupants"/> is a real count, not a guess;
    /// - <see cref="IsAvailable"/> is the single availability predicate the
    ///   registry's <c>FindBest</c> and the action's score formula share.
    ///
    /// Pure data + pure arithmetic: no engine components, no clock, no RNG.
    /// </summary>
    public sealed class Affordance
    {
        private readonly HashSet<string> _occupants = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Creates an affordance. Throws on nonsense (fail fast): empty id,
        /// capacity &lt; 1, radius &lt; 0 — a slot nobody can occupy or a
        /// negative footprint is a wiring bug, not a runtime state.
        /// </summary>
        /// <param name="id">Stable identity (debug logs, assertions).</param>
        /// <param name="kind">Interaction kind — the need it serves.</param>
        /// <param name="position">World position; only XZ is used for distance.</param>
        /// <param name="radius">Interaction footprint in metres (0 = a point).</param>
        /// <param name="capacity">Maximum simultaneous occupants (&gt;= 1).</param>
        public Affordance(string id, AffordanceKind kind, Vector3 position, float radius, int capacity)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("Affordance: id must not be empty.", nameof(id));
            }

            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), "Affordance: radius must be finite and >= 0.");
            }

            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Affordance: capacity must be >= 1.");
            }

            Id = id;
            Kind = kind;
            Position = position;
            Radius = radius;
            Capacity = capacity;
        }

        /// <summary>Stable identity.</summary>
        public string Id { get; }

        /// <summary>Interaction kind.</summary>
        public AffordanceKind Kind { get; }

        /// <summary>World position (XZ is the decision plane).</summary>
        public Vector3 Position { get; }

        /// <summary>Interaction footprint in metres.</summary>
        public float Radius { get; }

        /// <summary>Maximum simultaneous occupants.</summary>
        public int Capacity { get; }

        /// <summary>Current occupant count (distinct agent ids).</summary>
        public int Occupants => _occupants.Count;

        /// <summary>True while at least one slot is free — the ONLY availability predicate.</summary>
        public bool IsAvailable => _occupants.Count < Capacity;

        /// <summary>Occupying agent ids (diagnostics/tests; set semantics, order not guaranteed).</summary>
        public IReadOnlyCollection<string> OccupantIds => _occupants;

        /// <summary>True when <paramref name="agentId"/> currently holds a slot.</summary>
        public bool IsOccupiedBy(string agentId)
        {
            return agentId != null && _occupants.Contains(agentId);
        }

        /// <summary>
        /// XZ-plane distance — the utility layer lives on the ground plane, so
        /// stair-height differences must not make a bed look farther away.
        /// </summary>
        public float FlatDistanceTo(Vector3 point)
        {
            float dx = Position.x - point.x;
            float dz = Position.z - point.z;
            return (float)Math.Sqrt((dx * dx) + (dz * dz));
        }

        /// <summary>
        /// Takes a slot for <paramref name="agentId"/>. Returns true when the
        /// agent holds a slot afterwards: an existing claim is idempotent
        /// (true, count unchanged), a full affordance returns false.
        /// </summary>
        internal bool TryAddOccupant(string agentId)
        {
            if (_occupants.Contains(agentId))
            {
                return true;
            }

            if (!IsAvailable)
            {
                return false;
            }

            _occupants.Add(agentId);
            return true;
        }

        /// <summary>Frees the agent's slot. Returns true when a slot was actually released (repeat release = false, never throws).</summary>
        internal bool RemoveOccupant(string agentId)
        {
            return agentId != null && _occupants.Remove(agentId);
        }
    }
}
