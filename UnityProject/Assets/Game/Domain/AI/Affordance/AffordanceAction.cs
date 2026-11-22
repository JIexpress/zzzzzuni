using System;
using Game.Domain.AI.Utility;
using UnityEngine;

namespace Game.Domain.AI.Affordances
{
    /// <summary>
    /// Factory that turns "the world offers a Rest spot" into a standard
    /// <see cref="UtilityAction"/> (TASK-A-AI-03). The action adds itself to a
    /// normal <see cref="UtilityBrain"/> — the brain does not change and does
    /// not know affordances exist; the action just scores differently.
    ///
    /// Score formula (fixed, hand-computable, asserted in tests):
    /// <code>score = needWeight × clamp01(1 − distance / maxDistance) × (available ? 1 : 0)</code>
    /// where distance is the XZ distance to the nearest available affordance
    /// of the kind (the same lookup <c>FindBest</c> does), and needWeight is a
    /// pure function of the pumped <see cref="NPCWorldState"/> using the
    /// EXISTING <see cref="NPCWorldState.Keys"/> channels — no new state
    /// system. No available affordance ⇒ exactly 0 (the availability term).
    ///
    /// Execute re-runs the same lookup and RESERVES the winner through the
    /// registry (TASK-A-AI-05: selection no longer occupies — the agent only
    /// holds the spot against other selectors), recording it in
    /// <see cref="LastClaimed"/> (assertion anchor). Converting the
    /// reservation into a real occupancy claim happens on ARRIVAL, driven by
    /// the view-side seeker; <see cref="ReleaseClaim"/> clears both the
    /// occupancy and the reservation. Re-reserving on consecutive winning
    /// ticks is idempotent by design.
    /// </summary>
    public sealed class AffordanceAction
    {
        /// <summary>Default reach of an affordance action in metres.</summary>
        public const float DefaultMaxDistance = 20f;

        /// <summary>Generated action-name prefix: "Affordance." + kind.</summary>
        public const string DefaultNamePrefix = "Affordance.";

        private readonly AffordanceRegistry _registry;
        private readonly string _agentId;
        private readonly Func<Vector3> _positionProvider;
        private readonly float _maxDistance;

        private AffordanceAction(
            AffordanceRegistry registry,
            AffordanceKind kind,
            string agentId,
            Func<Vector3> positionProvider,
            float maxDistance,
            string name)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (string.IsNullOrEmpty(agentId))
            {
                throw new ArgumentException("AffordanceAction: agentId must not be empty.", nameof(agentId));
            }

            _positionProvider = positionProvider ?? throw new ArgumentNullException(nameof(positionProvider));
            if (float.IsNaN(maxDistance) || float.IsInfinity(maxDistance) || maxDistance <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistance), "AffordanceAction: maxDistance must be finite and > 0.");
            }

            _registry = registry;
            _agentId = agentId;
            _maxDistance = maxDistance;
            Kind = kind;
            Action = new UtilityAction(
                string.IsNullOrEmpty(name) ? DefaultNamePrefix + kind : name,
                Score,
                Execute,
                0f,
                TraitOf(kind));
        }

        /// <summary>Interaction kind served by this action.</summary>
        public AffordanceKind Kind { get; }

        /// <summary>The standard utility action to register on a brain.</summary>
        public UtilityAction Action { get; }

        /// <summary>
        /// Last affordance RESERVED by <see cref="UtilityAction.Execute"/>
        /// (null before the first selection; test anchor). TASK-A-AI-05: the
        /// name keeps the A-AI-03 era identifier for assertion continuity,
        /// but since this task the winner is only reserved on selection —
        /// occupancy starts on arrival (the view-side seeker claims).
        /// </summary>
        public Affordance LastClaimed { get; private set; }

        /// <summary>
        /// Creates the action with the default reach (20 m) and generated name
        /// ("Affordance.Rest"). This is the briefing's
        /// <c>Create(registry, kind, agentId, positionProvider)</c> entry point.
        /// </summary>
        public static UtilityAction Create(
            AffordanceRegistry registry,
            AffordanceKind kind,
            string agentId,
            Func<Vector3> positionProvider)
        {
            return Create(registry, kind, agentId, positionProvider, DefaultMaxDistance, null);
        }

        /// <summary>Creates the action with an explicit reach and name (null name = generated).</summary>
        public static UtilityAction Create(
            AffordanceRegistry registry,
            AffordanceKind kind,
            string agentId,
            Func<Vector3> positionProvider,
            float maxDistance,
            string name)
        {
            return CreateTracked(registry, kind, agentId, positionProvider, maxDistance, name).Action;
        }

        /// <summary>
        /// Creates the TRACKED handle — same wiring as <see cref="Create"/>,
        /// but the caller keeps the instance so it can assert
        /// <see cref="LastClaimed"/> (and release the claim when it owns the
        /// NPC's lifecycle).
        /// </summary>
        public static AffordanceAction CreateTracked(
            AffordanceRegistry registry,
            AffordanceKind kind,
            string agentId,
            Func<Vector3> positionProvider,
            float maxDistance = DefaultMaxDistance,
            string name = null)
        {
            return new AffordanceAction(registry, kind, agentId, positionProvider, maxDistance, name);
        }

        /// <summary>
        /// Frees the last stake — occupancy AND reservation (TASK-A-AI-05
        /// dual clear: registry.Release clears both). Owner teardown /
        /// switching spots. Safe to call when nothing was held; the next
        /// winning tick may reserve again.
        /// </summary>
        public void ReleaseClaim()
        {
            if (LastClaimed != null)
            {
                _registry.Release(LastClaimed, _agentId);
                LastClaimed = null;
            }
        }

        /// <summary>
        /// Need weight of a kind over the pumped world state (0..1). Every input
        /// is an EXISTING <see cref="NPCWorldState.Keys"/> channel:
        /// - Rest ← Fatigue: the more tired, the stronger the pull (absent key
        ///   = 0 — an unpumped need must not create desire);
        /// - Work ← DeviceBroken: a broken device is a full-weight duty
        ///   (0.5 baseline otherwise);
        /// - Social ← Trust: trust raises the pull above a 0.5 baseline;
        /// - Eat ← baseline 0.5: no hunger channel exists yet (A-AI-04 pumps
        ///   the scene's real need) so hunger is a flat basic need, not
        ///   invented state.
        /// </summary>
        public static float NeedWeight(NPCWorldState state, AffordanceKind kind)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            switch (kind)
            {
                case AffordanceKind.Rest:
                    return UtilityCurves.Clamp01(state.GetFloat(NPCWorldState.Keys.Fatigue, 0f));
                case AffordanceKind.Work:
                    return state.TryGet(NPCWorldState.Keys.DeviceBroken, out bool broken) && broken ? 1f : 0.5f;
                case AffordanceKind.Social:
                    return 0.5f + (0.5f * UtilityCurves.Clamp01(state.GetFloat(NPCWorldState.Keys.Trust, 0f)));
                case AffordanceKind.Eat:
                    return 0.5f;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), $"AffordanceAction: unknown kind '{kind}'.");
            }
        }

        /// <summary>
        /// Personality trait tag per kind: resting is self-care (fatigue),
        /// socialising is company (social); Work and Eat are neutral duties
        /// (weight 1.0 — obligations stay obligations).
        /// </summary>
        private static string TraitOf(AffordanceKind kind)
        {
            switch (kind)
            {
                case AffordanceKind.Rest: return UtilityTraits.Fatigue;
                case AffordanceKind.Social: return UtilityTraits.Social;
                default: return null;
            }
        }

        private float Score(NPCWorldState state)
        {
            // Pure by contract: the registry lookup mutates nothing, and the
            // reservation happens only in Execute. TASK-A-AI-05: the lookup is
            // agent-aware — spots reserved by OTHER agents score 0; the
            // agent's own reservation keeps scoring normally ("A 自己仍能选中").
            Vector3 from = _positionProvider();
            Affordance best = _registry.FindBest(from, Kind, _maxDistance, _agentId);
            if (best == null)
            {
                return 0f; // no available affordance ⇒ availability term = 0
            }

            float distance = best.FlatDistanceTo(from);
            float proximity = UtilityCurves.Clamp01(1f - (distance / _maxDistance));
            return UtilityCurves.Clamp01(NeedWeight(state, Kind) * proximity);
        }

        private void Execute()
        {
            // TASK-A-AI-05: selection = Reserve (no capacity slot taken).
            // The reservation stops other agents from selecting/occupying the
            // same spot; converting it into a real occupancy claim happens on
            // ARRIVAL (AffordanceSeeker.Arrive).
            Affordance best = _registry.FindBest(_positionProvider(), Kind, _maxDistance, _agentId);
            if (best == null)
            {
                return; // world changed between score and execute: no reserve, no stale LastClaimed
            }

            if (_registry.Reserve(best, _agentId))
            {
                LastClaimed = best;
            }
        }
    }
}
