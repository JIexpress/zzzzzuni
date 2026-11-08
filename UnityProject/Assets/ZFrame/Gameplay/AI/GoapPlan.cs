using System;
using System.Collections.Generic;
using System.Text;

namespace ZFrame.Gameplay.AI
{
    /// <summary>
    /// The planner's answer (TASK-A-TECH-04): an ordered chain of actions
    /// that leads from the start state to the goal, plus the summed cost.
    /// An empty plan (zero steps) means the goal was already satisfied.
    /// Immutable once produced.
    /// </summary>
    public sealed class GoapPlan
    {
        private readonly List<GoapAction> _steps;

        /// <summary>Builds a plan. Null steps are treated as an empty chain.</summary>
        public GoapPlan(IEnumerable<GoapAction> steps, float totalCost)
        {
            _steps = steps != null ? new List<GoapAction>(steps) : new List<GoapAction>();
            TotalCost = totalCost;
        }

        /// <summary>Ordered action chain (execution order). Never null.</summary>
        public IReadOnlyList<GoapAction> Steps => _steps;

        /// <summary>Number of actions in the chain (its BFS depth).</summary>
        public int StepCount => _steps.Count;

        /// <summary>Sum of the steps' <see cref="GoapAction.Cost"/>.</summary>
        public float TotalCost { get; }

        /// <summary>Debug form: "TakeTool -> RepairDevice" (or "(empty)").</summary>
        public string Format()
        {
            if (_steps.Count == 0)
            {
                return "(empty)";
            }

            var sb = new StringBuilder();
            for (int i = 0; i < _steps.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" -> ");
                }

                sb.Append(_steps[i].Name);
            }

            return sb.ToString();
        }
    }
}
