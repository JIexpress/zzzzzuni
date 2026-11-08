using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay.AI
{
    /// <summary>
    /// Minimal GOAP planner (TASK-A-TECH-04): breadth-first forward search
    /// from the world state toward the goal, expanding every applicable
    /// action in list order. The first goal-satisfying state reached wins —
    /// BFS guarantees the FEWEST-ACTIONS chain, and registration order makes
    /// ties deterministic (same input → same plan, asserted in tests).
    ///
    /// Cost model: <see cref="GoapPlan.TotalCost"/> carries the summed
    /// <see cref="GoapAction.Cost"/> for reporting/future A*; this minimal
    /// version optimizes step count (briefing allows BFS as the minimal
    /// implementation).
    ///
    /// Termination: a visited set of canonical state keys
    /// (<see cref="GoapState.ToKey"/>) prevents re-expansion, so cycles
    /// (mutually undoing actions) cannot loop; <paramref name="maxDepth"/>
    /// caps the chain length — deeper chains are not generated. No plan
    /// within the depth cap → returns null (explicit failure, never throws
    /// for unreachable goals).
    /// </summary>
    public static class GoapPlanner
    {
        /// <summary>Default lookahead cap (briefing: depth 3).</summary>
        public const int DefaultMaxDepth = 3;

        private sealed class Node
        {
            public readonly GoapState State;
            public readonly List<GoapAction> Chain;
            public readonly float Cost;

            public Node(GoapState state, List<GoapAction> chain, float cost)
            {
                State = state;
                Chain = chain;
                Cost = cost;
            }
        }

        /// <summary>
        /// Plans an action chain from <paramref name="worldState"/> to
        /// <paramref name="goal"/>. Returns the chain (fewest steps, earliest
        /// registered on ties) or <c>null</c> when the goal is unreachable
        /// within <paramref name="maxDepth"/>. Neither state is mutated.
        /// </summary>
        /// <param name="worldState">Current symbolic world (atoms true now).</param>
        /// <param name="goal">Atoms that must all be true at the end.</param>
        /// <param name="actions">Candidate operators, registration order = tie-break priority.</param>
        /// <param name="maxDepth">Maximum chain length; must be >= 1 (0 or less is a wiring bug and throws).</param>
        public static GoapPlan Plan(GoapState worldState, GoapState goal, IReadOnlyList<GoapAction> actions, int maxDepth = DefaultMaxDepth)
        {
            if (worldState == null)
            {
                throw new ArgumentNullException(nameof(worldState));
            }

            if (goal == null)
            {
                throw new ArgumentNullException(nameof(goal));
            }

            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            if (maxDepth < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDepth), "GoapPlanner: maxDepth must be >= 1.");
            }

            // Goal already true: an empty plan is the honest answer.
            if (worldState.Satisfies(goal))
            {
                return new GoapPlan(Array.Empty<GoapAction>(), 0f);
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { worldState.ToKey() };
            var frontier = new Queue<Node>();
            frontier.Enqueue(new Node(worldState.Clone(), new List<GoapAction>(), 0f));

            while (frontier.Count > 0)
            {
                Node node = frontier.Dequeue();
                if (node.Chain.Count >= maxDepth)
                {
                    continue; // depth cap reached: this node is expanded no further
                }

                for (int i = 0; i < actions.Count; i++)
                {
                    GoapAction action = actions[i];
                    if (!action.IsApplicableTo(node.State) || !action.ChangesState(node.State))
                    {
                        continue;
                    }

                    GoapState next = node.State.Clone();
                    action.ApplyTo(next);

                    // Building the candidate chain FIRST lets the goal test
                    // and the visited guard share one code path.
                    var chain = new List<GoapAction>(node.Chain) { action };
                    float cost = node.Cost + action.Cost;

                    if (next.Satisfies(goal))
                    {
                        return new GoapPlan(chain, cost);
                    }

                    if (visited.Add(next.ToKey()))
                    {
                        frontier.Enqueue(new Node(next, chain, cost));
                    }
                }
            }

            return null; // unreachable within maxDepth — explicit, tested failure
        }
    }
}
