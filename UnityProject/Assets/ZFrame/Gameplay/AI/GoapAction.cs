using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay.AI
{
    /// <summary>
    /// One GOAP operator (TASK-A-TECH-04): a stable name, the atoms that must
    /// all be true to apply (<c>Preconditions</c>), the atoms it makes true
    /// (<c>AddEffects</c>) and the atoms it makes false
    /// (<c>RemoveEffects</c>), plus a <c>Cost</c> carried on the plan.
    ///
    /// Immutable by contract: the planner never mutates an action, it applies
    /// the action to COPIES of the world state. Effects that both add and
    /// remove the same atom are a wiring bug and throw at construction.
    /// </summary>
    public sealed class GoapAction
    {
        /// <summary>Builds an action. Null atom arrays are treated as empty; null/empty names throw.</summary>
        public GoapAction(
            string name,
            IEnumerable<string> preconditions,
            IEnumerable<string> addEffects,
            IEnumerable<string> removeEffects,
            float cost = 1f)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("GoapAction: name must not be empty.", nameof(name));
            }

            if (cost < 0f || float.IsNaN(cost))
            {
                throw new ArgumentOutOfRangeException(nameof(cost), "GoapAction: cost must be a non-negative number.");
            }

            Name = name;
            Cost = cost;
            Preconditions = ToReadOnly(preconditions, nameof(preconditions));
            AddEffects = ToReadOnly(addEffects, nameof(addEffects));
            RemoveEffects = ToReadOnly(removeEffects, nameof(removeEffects));

            foreach (string atom in AddEffects)
            {
                if (Array.IndexOf(RemoveEffects, atom) >= 0)
                {
                    throw new ArgumentException(
                        $"GoapAction '{name}': atom '{atom}' is both added and removed (contradictory effects).",
                        nameof(addEffects));
                }
            }
        }

        /// <summary>Stable identity (plans, logs, assertions).</summary>
        public string Name { get; }

        /// <summary>Atoms that must all be true before this action may apply.</summary>
        public string[] Preconditions { get; }

        /// <summary>Atoms this action makes true.</summary>
        public string[] AddEffects { get; }

        /// <summary>Atoms this action makes false.</summary>
        public string[] RemoveEffects { get; }

        /// <summary>Non-negative planner cost (carried onto <see cref="GoapPlan.TotalCost"/>).</summary>
        public float Cost { get; }

        /// <summary>True when every precondition holds in <paramref name="state"/>.</summary>
        public bool IsApplicableTo(GoapState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            foreach (string atom in Preconditions)
            {
                if (!state.Has(atom))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Applies this action to <paramref name="state"/> IN PLACE (the
        /// caller owns the copy — the planner clones before applying).
        /// Preconditions are NOT re-checked here; gate with
        /// <see cref="IsApplicableTo"/> first.
        /// </summary>
        public void ApplyTo(GoapState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            foreach (string atom in RemoveEffects)
            {
                state.Remove(atom);
            }

            foreach (string atom in AddEffects)
            {
                state.Add(atom);
            }
        }

        /// <summary>True when applying this action would change anything (no-op application is skipped by the planner).</summary>
        public bool ChangesState(GoapState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            foreach (string atom in AddEffects)
            {
                if (!state.Has(atom))
                {
                    return true;
                }
            }

            foreach (string atom in RemoveEffects)
            {
                if (state.Has(atom))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] ToReadOnly(IEnumerable<string> atoms, string paramName)
        {
            if (atoms == null)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>(atoms);
            foreach (string atom in list)
            {
                if (string.IsNullOrEmpty(atom))
                {
                    throw new ArgumentException($"GoapAction: {paramName} contains a null/empty atom.", paramName);
                }
            }

            return list.ToArray();
        }
    }
}
