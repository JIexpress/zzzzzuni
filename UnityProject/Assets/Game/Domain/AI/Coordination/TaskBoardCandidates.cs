using System;
using System.Collections.Generic;
using Game.Domain.AI.Utility;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// The UtilityBrain integration of the task board (TASK-A-AI-02): turns
    /// the board's OPEN tasks into <see cref="UtilityAction"/> candidates so a
    /// utility brain RANKS real teamwork alongside its private desires.
    /// 认领成本=距离: the score decays linearly with the distance to the task
    /// position (<see cref="UtilityCurves.Fall"/>), so the nearer task wins
    /// and a task beyond <see cref="TaskClaimOptions.OuterDistance"/> is not
    /// worth walking to (score 0 — the brain will never select it).
    ///
    /// Per-task perception key: the owner pumps each open task's distance
    /// into its <see cref="NPCWorldState"/> under
    /// <see cref="DistanceStateKey"/> before ticking; a key that was never
    /// pumped (task not perceived) scores 0 — desire needs perception, the
    /// same rule as the attitude layer's threat gating.
    ///
    /// Lifetime contract: the returned actions are valid for ONE tick — they
    /// close over the claim instant and the task snapshot taken at build
    /// time, so the integration agent rebuilds the candidate set each tick
    /// (task sets are dynamic: claims expire, tasks get swept). This is the
    /// documented deviation from the "build once" rule that the STATIC
    /// candidates in <see cref="NpcUtilityAgent"/> follow; the brain's
    /// determinism is untouched (same open-task snapshot → same registration
    /// order → same ranking).
    ///
    /// The claim mutex lives in the board: the execute delegate calls
    /// <see cref="TaskBoard.Claim"/>, whose false means someone else won —
    /// the losing agent simply re-ranks next tick against the updated board.
    /// </summary>
    public static class TaskBoardCandidates
    {
        /// <summary>UtilityAction name prefix for task claims (ranking anchors: "task:task-1").</summary>
        public const string ActionPrefix = "task:";

        /// <summary>NPCWorldState key prefix for per-task distance perception.</summary>
        public const string DistanceKeyPrefix = "npc.task.";

        /// <summary>NPCWorldState key suffix for per-task distance perception.</summary>
        public const string DistanceKeySuffix = ".distance";

        /// <summary>Claim-candidate tuning (properties, overridable via initialiser).</summary>
        public sealed class TaskClaimOptions
        {
            /// <summary>Desire at point-blank range (multiplied by the distance fall).</summary>
            public float BaseDesire { get; set; } = 0.9f;

            /// <summary>Distance at/below which the desire is full.</summary>
            public float InnerDistance { get; set; } = 1f;

            /// <summary>Distance at/beyond which the desire hits 0 (not worth the walk).</summary>
            public float OuterDistance { get; set; } = 10f;
        }

        /// <summary>The perception key for one task's pumped distance.</summary>
        public static string DistanceStateKey(string taskId)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                throw new ArgumentException("TaskBoardCandidates: taskId must not be empty.", nameof(taskId));
            }

            return DistanceKeyPrefix + taskId + DistanceKeySuffix;
        }

        /// <summary>One task claim candidate's action name (brain-unique by task id).</summary>
        public static string ActionNameOf(string taskId)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                throw new ArgumentException("TaskBoardCandidates: taskId must not be empty.", nameof(taskId));
            }

            return ActionPrefix + taskId;
        }

        /// <summary>
        /// Builds one claim candidate per OPEN task (publish order — the
        /// brain's tie-break order follows the board's). Score = pure distance
        /// decay over the pumped perception key; Execute = board claim by
        /// <paramref name="claimantId"/> at <paramref name="now"/> (one-tick
        /// validity, see type doc). Throws on a null board or an unusable
        /// options pair (outer must exceed inner).
        /// </summary>
        public static List<UtilityAction> CreateClaimActions(
            TaskBoard board,
            string claimantId,
            float now,
            TaskClaimOptions options = null)
        {
            if (board == null)
            {
                throw new ArgumentNullException(nameof(board));
            }

            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoardCandidates: claimantId must not be empty.", nameof(claimantId));
            }

            var effective = options ?? new TaskClaimOptions();
            if (effective.OuterDistance <= effective.InnerDistance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options), "TaskBoardCandidates: OuterDistance must be > InnerDistance.");
            }

            List<CoordinationTask> open = board.OpenTasks();
            var actions = new List<UtilityAction>(open.Count);
            for (int i = 0; i < open.Count; i++)
            {
                CoordinationTask task = open[i];
                string key = DistanceStateKey(task.Id);
                actions.Add(new UtilityAction(
                    ActionNameOf(task.Id),
                    state => ScoreClaim(state, key, effective),
                    () => board.Claim(task.Id, claimantId, now)));
            }

            return actions;
        }

        /// <summary>
        /// Pure score of one claim candidate (hand-computable: distance 2,
        /// inner 1, outer 10, base 0.9 ⇒ (1 − 1/9) × 0.9 = 0.8). Unperceived
        /// (missing key) ⇒ 0.
        /// </summary>
        public static float ScoreClaim(NPCWorldState state, string distanceKey, TaskClaimOptions options)
        {
            float distance = state.GetFloat(distanceKey, float.PositiveInfinity);
            return options.BaseDesire * UtilityCurves.Fall(distance, options.InnerDistance, options.OuterDistance);
        }
    }
}
