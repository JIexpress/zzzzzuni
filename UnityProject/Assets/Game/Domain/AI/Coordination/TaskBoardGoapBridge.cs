using System;
using System.Collections.Generic;
using ZFrame.Gameplay;
using ZFrame.Gameplay.AI;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// The GOAP integration of the task board (TASK-A-AI-02): expresses board
    /// tasks as STRIPS atoms/operators for <c>ZFrame.Gameplay.AI.GoapPlanner</c>
    /// — 认领=效果达成. Each task contributes TWO operators, which is what
    /// makes the canonical two-step chain: an agent with nothing plans
    /// <c>[ClaimTask:t → ExecuteTask:t]</c> (the claim's precondition
    /// "task open" blocks the execute step until the claim happened), while
    /// an agent that ALREADY holds the task plans the single execute step.
    ///
    /// Atom vocabulary (per task id T):
    /// - <see cref="OpenAtomOf"/> "task:T:open" — true while unclaimed;
    /// - <see cref="ClaimedAtomOf"/> "task:T:claimed" — true while THIS agent
    ///   holds T (per-claimant symbolic world: another agent's claims are
    ///   invisible here, holder identity enforcement stays in the board);
    /// - <see cref="DoneAtomOf"/> "task:T:done" — the planner's goal; the real
    ///   completion is <see cref="TaskBoard.Complete"/>, whose success the
    ///   chain executor asserts step by step.
    ///
    /// Costs: the claim step costs the agent's DISTANCE to the task (认领成本=
    /// 距离, carried into <see cref="GoapPlan.TotalCost"/>); the execute step
    /// costs a fixed <see cref="ExecuteCost"/>. Expired tasks produce no
    /// operators and no atoms (they are gone for planning, exactly as for
    /// claiming). Pure: building atoms/actions never mutates the board — the
    /// chain executor calls <see cref="TaskBoard.Claim"/>/<see cref="TaskBoard.Complete"/>
    /// for the real effects.
    /// </summary>
    public static class TaskBoardGoapBridge
    {
        /// <summary>Cost of the execute step (the claim step costs the distance).</summary>
        public const float ExecuteCost = 1f;

        /// <summary>Atom: task <paramref name="taskId"/> is open (unclaimed).</summary>
        public static string OpenAtomOf(string taskId)
        {
            return "task:" + taskId + ":open";
        }

        /// <summary>Atom: task <paramref name="taskId"/> is claimed by the planning agent.</summary>
        public static string ClaimedAtomOf(string taskId)
        {
            return "task:" + taskId + ":claimed";
        }

        /// <summary>Atom: task <paramref name="taskId"/> is done (the planner's goal).</summary>
        public static string DoneAtomOf(string taskId)
        {
            return "task:" + taskId + ":done";
        }

        /// <summary>GOAP operator name: claim task <paramref name="taskId"/>.</summary>
        public static string ClaimActionNameOf(string taskId)
        {
            return "ClaimTask:" + taskId;
        }

        /// <summary>GOAP operator name: execute task <paramref name="taskId"/>.</summary>
        public static string ExecuteActionNameOf(string taskId)
        {
            return "ExecuteTask:" + taskId;
        }

        /// <summary>
        /// Builds the symbolic world for ONE agent: open atoms for open,
        /// unexpired tasks and claimed atoms ONLY for tasks this
        /// <paramref name="claimantId"/> holds (its own claims — the
        /// per-agent world the planner reasons about).
        /// </summary>
        public static GoapState BuildWorldState(TaskBoard board, string claimantId, float now)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }

            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoardGoapBridge: claimantId must not be empty.", nameof(claimantId));
            }

            var world = new GoapState();
            foreach (CoordinationTask task in board.OpenTasks())
            {
                if (!task.IsExpired(now))
                {
                    world.Add(OpenAtomOf(task.Id));
                }
            }

            AddClaimedAtoms(world, board, claimantId, now);
            return world;
        }

        /// <summary>
        /// Builds the operator set for ONE agent over the board (publish
        /// order; per open task the pair [claim, execute]): the claim
        /// operator's precondition is the open atom and its cost is the
        /// agent's distance to the task position; the execute operator needs
        /// the claimed atom — it is listed UP FRONT (the planner expands a
        /// static operator list) and is gated to applicability by that atom,
        /// which is exactly what produces the canonical two-step chain.
        /// Tasks claimed by OTHER agents produce no operator: the per-claimant
        /// world never contains their claimed atom, so the plans stay grounded
        /// in what THIS agent may really do.
        /// </summary>
        public static List<GoapAction> BuildActions(TaskBoard board, string claimantId, Vec2 agentPosition, float now)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }

            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoardGoapBridge: claimantId must not be empty.", nameof(claimantId));
            }

            var actions = new List<GoapAction>();
            foreach (CoordinationTask task in board.AllTasks())
            {
                if (task.IsExpired(now))
                {
                    continue;
                }

                if (task.Status == CoordinationTaskStatus.Open)
                {
                    actions.Add(new GoapAction(
                        ClaimActionNameOf(task.Id),
                        new[] { OpenAtomOf(task.Id) },
                        new[] { ClaimedAtomOf(task.Id) },
                        new[] { OpenAtomOf(task.Id) },
                        Vec2.Distance(agentPosition, task.Position)));
                    actions.Add(new GoapAction(
                        ExecuteActionNameOf(task.Id),
                        new[] { ClaimedAtomOf(task.Id) },
                        new[] { DoneAtomOf(task.Id) },
                        new[] { ClaimedAtomOf(task.Id) },
                        ExecuteCost));
                }
                else if (string.Equals(task.ClaimedBy, claimantId, StringComparison.Ordinal))
                {
                    actions.Add(new GoapAction(
                        ExecuteActionNameOf(task.Id),
                        new[] { ClaimedAtomOf(task.Id) },
                        new[] { DoneAtomOf(task.Id) },
                        new[] { ClaimedAtomOf(task.Id) },
                        ExecuteCost));
                }
            }

            return actions;
        }

        private static void AddClaimedAtoms(GoapState world, TaskBoard board, string claimantId, float now)
        {
            foreach (CoordinationTask task in board.AllTasks())
            {
                if (!task.IsExpired(now)
                    && task.Status == CoordinationTaskStatus.Claimed
                    && string.Equals(task.ClaimedBy, claimantId, StringComparison.Ordinal))
                {
                    world.Add(ClaimedAtomOf(task.Id));
                }
            }
        }
    }
}
