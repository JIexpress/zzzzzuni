using System;
using System.Collections.Generic;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// Shared task board of the coordination layer (TASK-A-AI-02): NPCs (or a
    /// scenario host) PUBLISH work ("pickup at (3,4), expires in 30s") and any
    /// NPC CLAIM a task. The falsifiable core is the CLAIM MUTEX — a task is
    /// held by at most one NPC at a time, so two teammates can never walk to
    /// the same pickup (the second <see cref="Claim"/> returns false, the
    /// loser's brain falls back to the next candidate).
    ///
    /// Determinism contract (asserted in tests):
    /// - Task ids are board-issued "task-N" with a monotonic counter — same
    ///   publish sequence, same ids, no RNG.
    /// - <see cref="OpenTasks"/> lists in publish order (stable iteration for
    ///   candidate building and rankings).
    /// - Expiry is absolute-clock (caller-pumped): <see cref="Claim"/> on an
    ///   expired task fails even before <see cref="SweepExpired"/> runs, and
    ///   <see cref="SweepExpired"/> removes expired tasks (open OR claimed —
    ///   到期回收 frees stale claims too).
    ///
    /// Integration path: <see cref="TaskBoardCandidates"/> turns open tasks
    /// into <c>Game.Domain.AI.Utility.UtilityAction</c> candidates (认领成本=
    /// 距离), and <see cref="TaskBoardGoapBridge"/> feeds them to the
    /// ZFrame.Gameplay GOAP planner as claim→execute two-step chains.
    /// </summary>
    public sealed class TaskBoard
    {
        private readonly List<CoordinationTask> _tasks = new List<CoordinationTask>();
        private readonly Dictionary<string, int> _indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<CoordinationTask> _sweepBuffer = new List<CoordinationTask>();
        private int _nextId;

        /// <summary>Number of tasks currently ON the board (open + claimed).</summary>
        public int Count => _tasks.Count;

        /// <summary>Number of issued task ids over this board's lifetime (diagnostics/tests).</summary>
        public int PublishedCount => _nextId;

        /// <summary>
        /// Publishes a new task and returns its record. <paramref name="kind"/>
        /// must be non-empty; <paramref name="expiresAt"/> must be a positive
        /// instant or <see cref="float.PositiveInfinity"/> — a task born
        /// already-expired (or at a NaN) is a wiring bug and throws.
        /// </summary>
        public CoordinationTask Publish(string kind, Vec2 position, float expiresAt)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException("TaskBoard.Publish: kind must not be empty.", nameof(kind));
            }

            if (float.IsNaN(expiresAt) || expiresAt <= 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expiresAt), "TaskBoard.Publish: expiresAt must be > 0 (use +Infinity for never-expiring).");
            }

            _nextId++;
            string id = "task-" + _nextId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var task = new CoordinationTask(id, kind, position, expiresAt, CoordinationTaskStatus.Open, string.Empty, -1f);
            _indexById[id] = _tasks.Count;
            _tasks.Add(task);
            return task;
        }

        /// <summary>Looks a task up by id. False when the id was never published or has left the board.</summary>
        public bool TryGetTask(string taskId, out CoordinationTask task)
        {
            if (taskId != null && _indexById.TryGetValue(taskId, out int index))
            {
                task = _tasks[index];
                return true;
            }

            task = null;
            return false;
        }

        /// <summary>
        /// Open (unclaimed, on-board) tasks in publish order — the pool the
        /// candidate builders draw from. The list is a fresh snapshot; the
        /// board is not mutated by this call.
        /// </summary>
        public List<CoordinationTask> OpenTasks()
        {
            var open = new List<CoordinationTask>();
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i].Status == CoordinationTaskStatus.Open)
                {
                    open.Add(_tasks[i]);
                }
            }

            return open;
        }

        /// <summary>
        /// Every task currently on the board (open + claimed) in publish
        /// order — the GOAP bridge and diagnostics need claimed tasks too.
        /// Fresh snapshot; the board is not mutated by this call.
        /// </summary>
        public List<CoordinationTask> AllTasks()
        {
            return new List<CoordinationTask>(_tasks);
        }

        /// <summary>
        /// THE claim mutex: atomically moves an open, non-expired task to
        /// claimed-by-<paramref name="claimantId"/>. Returns true only on a
        /// NEW successful claim — false when the task is unknown/expired
        /// already claimed (even by the same claimant: re-claiming what you
        /// hold is not an acquisition; read <see cref="CoordinationTask.ClaimedBy"/>
        /// to check ownership instead).
        /// </summary>
        /// <param name="taskId">Board-issued task id.</param>
        /// <param name="claimantId">Claiming NPC's stable id (null/empty throws — identity).</param>
        /// <param name="now">Current absolute clock (stamp + expiry test).</param>
        public bool Claim(string taskId, string claimantId, float now)
        {
            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoard.Claim: claimantId must not be empty.", nameof(claimantId));
            }

            if (!TryGetTask(taskId, out CoordinationTask task))
            {
                return false;
            }

            if (task.IsExpired(now) || task.Status == CoordinationTaskStatus.Claimed)
            {
                return false;
            }

            _tasks[_indexById[taskId]] = new CoordinationTask(
                task.Id, task.Kind, task.Position, task.ExpiresAt,
                CoordinationTaskStatus.Claimed, claimantId, now);
            return true;
        }

        /// <summary>
        /// The holder completes its task: the record leaves the board. Returns
        /// true only when <paramref name="claimantId"/> is the current holder
        /// and the task is still on the board (a non-holder or a sweeper
        /// completion is a wiring bug, reported as false rather than thrown so
        /// a lost race degrades to "no-op" instead of an exception).
        /// </summary>
        public bool Complete(string taskId, string claimantId)
        {
            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoard.Complete: claimantId must not be empty.", nameof(claimantId));
            }

            if (!TryGetTask(taskId, out CoordinationTask task)
                || task.Status != CoordinationTaskStatus.Claimed
                || !string.Equals(task.ClaimedBy, claimantId, StringComparison.Ordinal))
            {
                return false;
            }

            RemoveAt(_indexById[taskId]);
            return true;
        }

        /// <summary>
        /// The holder releases its task back to Open (e.g. it died or was
        /// re-purposed): re-claimable by anyone. Holder-only, like
        /// <see cref="Complete"/>; false on a non-holder / unknown task.
        /// </summary>
        public bool Abandon(string taskId, string claimantId)
        {
            if (string.IsNullOrEmpty(claimantId))
            {
                throw new ArgumentException("TaskBoard.Abandon: claimantId must not be empty.", nameof(claimantId));
            }

            if (!TryGetTask(taskId, out CoordinationTask task)
                || task.Status != CoordinationTaskStatus.Claimed
                || !string.Equals(task.ClaimedBy, claimantId, StringComparison.Ordinal))
            {
                return false;
            }

            _tasks[_indexById[taskId]] = new CoordinationTask(
                task.Id, task.Kind, task.Position, task.ExpiresAt,
                CoordinationTaskStatus.Open, string.Empty, -1f);
            return true;
        }

        /// <summary>
        /// 到期回收: removes every expired task (open or claimed — a stale
        /// claim frees with the task) and returns them in publish order. An
        /// empty sweep is the normal steady state; the returned records let
        /// the owner clean view state. Pure w.r.t. the caller's clock: nothing
        /// is swept except <see cref="CoordinationTask.IsExpired"/> matches.
        /// </summary>
        public IReadOnlyList<CoordinationTask> SweepExpired(float now)
        {
            _sweepBuffer.Clear();
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                if (_tasks[i].IsExpired(now))
                {
                    _sweepBuffer.Add(_tasks[i]);
                    RemoveAt(i);
                }
            }

            // Reversed removal produced newest-first; restore publish order.
            _sweepBuffer.Reverse();
            return _sweepBuffer.ToArray();
        }

        private void RemoveAt(int index)
        {
            string removedId = _tasks[index].Id;
            _tasks.RemoveAt(index);
            _indexById.Remove(removedId);
            for (int i = index; i < _tasks.Count; i++)
            {
                _indexById[_tasks[i].Id] = i;
            }
        }
    }
}
