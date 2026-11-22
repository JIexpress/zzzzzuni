using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Domain.AI.Affordances
{
    /// <summary>
    /// World-side registry of affordances (TASK-A-AI-03): the piece that lets
    /// an NPC find "something to do" instead of ranking a fixed compile-time
    /// action list. Registration order is preserved (deterministic tie-break:
    /// equal distance → earliest registered wins), lookups are linear scans
    /// with a hard budget so 20+ NPCs polling every tick stay bounded.
    ///
    /// Budget semantics: one <see cref="FindBest"/> call visits at most
    /// <see cref="ScanBudget"/> registered affordances (every visited entry
    /// counts, including wrong-kind and full ones). Exceeding the budget does
    /// NOT throw and does NOT abort empty-handed — the scan stops, counts one
    /// <see cref="BudgetExceededCount"/> and returns the best candidate found
    /// so far. The counter is cumulative telemetry ("budget was too small"),
    /// reset by the owner, never by the scan.
    ///
    /// Pure logic: no MonoBehaviour, no clock, no RNG.
    /// </summary>
    public sealed class AffordanceRegistry
    {
        /// <summary>Default per-scan budget — room for a scene's worth of affordances; 20+ NPCs poll this per tick.</summary>
        public const int DefaultScanBudget = 64;

        private readonly List<Affordance> _affordances = new List<Affordance>();
        private int _scanBudget = DefaultScanBudget;

        /// <summary>Registered affordance count.</summary>
        public int Count => _affordances.Count;

        /// <summary>Registered affordances in registration order (diagnostics/tests; do not mutate).</summary>
        public IReadOnlyList<Affordance> Affordances => _affordances;

        /// <summary>
        /// Maximum affordances one <see cref="FindBest"/> call may visit.
        /// Must be &gt;= 1 (a zero budget would make the world invisible).
        /// </summary>
        public int ScanBudget
        {
            get => _scanBudget;
            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "AffordanceRegistry: ScanBudget must be >= 1.");
                }

                _scanBudget = value;
            }
        }

        /// <summary>How many scans stopped early because they hit <see cref="ScanBudget"/> (cumulative).</summary>
        public int BudgetExceededCount { get; private set; }

        /// <summary>
        /// Registers an affordance. Null throws; registering the SAME instance
        /// twice throws (a duplicate would double-count capacity and let one
        /// spot win twice in a scan).
        /// </summary>
        public void Register(Affordance affordance)
        {
            if (affordance == null)
            {
                throw new ArgumentNullException(nameof(affordance));
            }

            for (int i = 0; i < _affordances.Count; i++)
            {
                if (ReferenceEquals(_affordances[i], affordance))
                {
                    throw new ArgumentException($"AffordanceRegistry: affordance '{affordance.Id}' is already registered.", nameof(affordance));
                }
            }

            _affordances.Add(affordance);
        }

        /// <summary>
        /// Unregisters an affordance. Returns true when it was registered and is
        /// now gone; repeat/unregistered/null returns false and never throws
        /// (teardown paths call this unconditionally). Occupancy slots stay on
        /// the affordance — re-registering the same instance keeps its claims.
        /// </summary>
        public bool Unregister(Affordance affordance)
        {
            return affordance != null && _affordances.Remove(affordance);
        }

        /// <summary>
        /// Takes a slot on <paramref name="affordance"/> for
        /// <paramref name="agentId"/>. Returns true when the agent holds a slot
        /// afterwards: repeating the same claim is idempotent, a full
        /// affordance (or one reserved by ANOTHER agent — TASK-A-AI-05)
        /// returns false. Null affordance / empty agent id throw.
        /// </summary>
        public bool Claim(Affordance affordance, string agentId)
        {
            if (affordance == null)
            {
                throw new ArgumentNullException(nameof(affordance));
            }

            if (string.IsNullOrEmpty(agentId))
            {
                throw new ArgumentException("AffordanceRegistry: agentId must not be empty.", nameof(agentId));
            }

            return affordance.TryAddOccupant(agentId);
        }

        /// <summary>
        /// Reserves <paramref name="affordance"/> for <paramref name="agentId"/>
        /// (TASK-A-AI-05): marks "this agent selected it and is on the way"
        /// WITHOUT taking a capacity slot — others must skip it in
        /// <see cref="FindBest"/> and <see cref="Claim"/> while
        /// <see cref="Affordance.ReservedBy"/> is set, the reserver itself
        /// keeps seeing it as available. Idempotent for the same agent;
        /// returns false when another agent holds the reservation. Null
        /// affordance / empty agent id throw.
        /// </summary>
        public bool Reserve(Affordance affordance, string agentId)
        {
            if (affordance == null)
            {
                throw new ArgumentNullException(nameof(affordance));
            }

            if (string.IsNullOrEmpty(agentId))
            {
                throw new ArgumentException("AffordanceRegistry: agentId must not be empty.", nameof(agentId));
            }

            return affordance.TrySetReservation(agentId);
        }

        /// <summary>
        /// Frees <paramref name="agentId"/>'s stake: occupancy slot AND —
        /// TASK-A-AI-05 — the agent's own reservation, so "give up" always
        /// fully clears both (V3). Returns true when anything was freed;
        /// releasing twice (or a stake never taken) returns false and never
        /// throws. Other agents' reservations/claims are never touched.
        /// </summary>
        public bool Release(Affordance affordance, string agentId)
        {
            if (affordance == null)
            {
                throw new ArgumentNullException(nameof(affordance));
            }

            if (string.IsNullOrEmpty(agentId))
            {
                throw new ArgumentException("AffordanceRegistry: agentId must not be empty.", nameof(agentId));
            }

            bool removed = affordance.RemoveOccupant(agentId);
            bool cleared = affordance.ClearReservation(agentId);
            return removed || cleared;
        }

        /// <summary>
        /// Nearest AVAILABLE affordance of <paramref name="kind"/> within
        /// <paramref name="maxDistance"/> metres on the XZ plane, or null.
        /// Full and farther-than-max entries are skipped.
        /// At most <see cref="ScanBudget"/> entries are visited per call; when
        /// the registry is larger the scan stops, increments
        /// <see cref="BudgetExceededCount"/> and returns the best found so far
        /// (never throws). Ties go to the earliest registered.
        /// </summary>
        public Affordance FindBest(Vector3 from, AffordanceKind kind, float maxDistance)
        {
            return FindBest(from, kind, maxDistance, null);
        }

        /// <summary>
        /// Agent-aware <see cref="FindBest"/> (TASK-A-AI-05): additionally
        /// skips entries reserved by a DIFFERENT agent — the reserver itself
        /// still finds and selects its own reservation (V1). A null/empty
        /// <paramref name="agentId"/> is an anonymous caller: any reservation
        /// blocks it. Without reservations anywhere the scan is bit-identical
        /// to the original occupancy-only behaviour (A-AI-03's 13 assertions
        /// stay green). THE reservation-exclusion check below is the V7
        /// mutation target: removing it must turn the V1 reservation tests
        /// red.
        /// </summary>
        public Affordance FindBest(Vector3 from, AffordanceKind kind, float maxDistance, string agentId)
        {
            if (float.IsNaN(maxDistance) || maxDistance < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistance), "AffordanceRegistry: maxDistance must be >= 0.");
            }

            Affordance best = null;
            float bestDistance = float.PositiveInfinity;
            int visited = 0;
            for (int i = 0; i < _affordances.Count; i++)
            {
                if (visited >= _scanBudget)
                {
                    BudgetExceededCount++;
                    break;
                }

                visited++;
                Affordance candidate = _affordances[i];
                if (candidate.Kind != kind || !candidate.IsAvailableFor(agentId))
                {
                    continue;
                }

                float distance = candidate.FlatDistanceTo(from);
                if (distance > maxDistance || distance >= bestDistance)
                {
                    continue; // too far, or a nearer/earlier candidate already won
                }

                bestDistance = distance;
                best = candidate;
            }

            return best;
        }
    }
}
