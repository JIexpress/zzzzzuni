using System;
using System.Collections.Generic;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// Squad coordination (TASK-A-AI-02): a member list around a leader (the
    /// player or a squad captain), a RING FORMATION with unique slots
    /// (<see cref="FormationSlot"/> — 编队槽位唯一), per-member task
    /// exclusivity on a shared <see cref="TaskBoard"/> (成员间任务不冲突:
    /// one member holds at most one task, and the board's claim mutex means
    /// two members can never hold the same one), and defense assignment for
    /// the pumped enemy snapshot (协防) delegated to
    /// <see cref="AttackAssignment"/> so two members never get the same
    /// target.
    ///
    /// Kernel boundary (A-AND-08): the coordinator NEVER reads kernel
    /// bindings itself (the Game.Domain asmdef matrix forbids it). The host
    /// pumps kernel-derived positions in — member states via
    /// <see cref="SetMemberState"/>, crowd/enemy targets via
    /// <see cref="AssignDefense"/>'s argument — and the coordinator only
    /// READS that plain-data snapshot (kernel 数据读——只读).
    ///
    /// Determinism: membership follows join order; slots are assigned in join
    /// order (member i gets ring slot i of n); ties everywhere break to the
    /// earliest member — same input, same formations, same assignments.
    /// </summary>
    public sealed class SquadCoordinator
    {
        /// <summary>Tuning of the squad (properties, overridable via initialiser).</summary>
        public sealed class Options
        {
            /// <summary>Ring radius around the leader position (metres).</summary>
            public float FormationRadius { get; set; } = 2f;

            /// <summary>Ring start angle (radians); 0 points along +X.</summary>
            public float StartAngleRadians { get; set; } = 0f;

            /// <summary>Per-target attacker capacity handed to <see cref="AttackAssignment.Assign"/>.</summary>
            public int MaxAttackersPerTarget { get; set; } = AttackAssignment.DefaultMaxAttackersPerTarget;
        }

        private sealed class MemberState
        {
            public Vec2 Position;
            public float Anger;
            public int SlotIndex = -1;
            public Vec2 FormationPosition;
            public string TaskId;
        }

        private readonly TaskBoard _board;
        private readonly Options _options;
        private readonly List<string> _memberIds = new List<string>();
        private readonly Dictionary<string, MemberState> _members = new Dictionary<string, MemberState>(StringComparer.Ordinal);

        /// <summary>
        /// Creates a squad. <paramref name="board"/> may be null — a squad
        /// without a board still forms up and assigns defense, but
        /// <see cref="TryClaimTask"/> then throws (tasks need a board).
        /// </summary>
        public SquadCoordinator(TaskBoard board = null, Options options = null)
        {
            _board = board;
            _options = options ?? new Options();
            if (_options.MaxAttackersPerTarget < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options), "SquadCoordinator: MaxAttackersPerTarget must be >= 1.");
            }
        }

        /// <summary>The injected task board (null = taskless squad).</summary>
        public TaskBoard Board => _board;

        /// <summary>Member ids in join order (slots and tie-breaks follow this order).</summary>
        public IReadOnlyList<string> MemberIds => _memberIds;

        /// <summary>Current member count (== ring slot count while formed).</summary>
        public int MemberCount => _memberIds.Count;

        /// <summary>The task a member currently holds, or null (成员任务簿).</summary>
        public string TaskOf(string memberId)
        {
            return _members.TryGetValue(memberId, out MemberState state) ? state.TaskId : null;
        }

        /// <summary>A member's assigned ring slot index, or -1 before <see cref="UpdateFormation"/> / after leaving.</summary>
        public int SlotIndexOf(string memberId)
        {
            return _members.TryGetValue(memberId, out MemberState state) ? state.SlotIndex : -1;
        }

        /// <summary>A member's assigned ring position (valid after <see cref="UpdateFormation"/>).</summary>
        public Vec2 FormationPositionOf(string memberId)
        {
            return _members.TryGetValue(memberId, out MemberState state) ? state.FormationPosition : Vec2.Zero;
        }

        /// <summary>
        /// Adds a member. Duplicate id or empty id throws — a squad with two
        /// "npc-a"s could not keep slots or tasks unambiguous.
        /// </summary>
        public void Join(string memberId)
        {
            if (string.IsNullOrEmpty(memberId))
            {
                throw new ArgumentException("SquadCoordinator.Join: memberId must not be empty.", nameof(memberId));
            }

            if (_members.ContainsKey(memberId))
            {
                throw new ArgumentException($"SquadCoordinator.Join: member '{memberId}' already joined.", nameof(memberId));
            }

            _members[memberId] = new MemberState();
            _memberIds.Add(memberId);
        }

        /// <summary>
        /// Removes a member. If it held a task the task is released back to
        /// the board (Open — re-claimable by anyone). Unknown member throws
        /// (a silent no-op would hide a wiring bug).
        /// </summary>
        public void Leave(string memberId)
        {
            if (!_members.TryGetValue(memberId, out MemberState state))
            {
                throw new ArgumentException($"SquadCoordinator.Leave: unknown member '{memberId}'.", nameof(memberId));
            }

            string heldTask = state.TaskId;
            _members.Remove(memberId);
            _memberIds.Remove(memberId);
            if (_board != null && heldTask != null)
            {
                _board.Abandon(heldTask, memberId); // 离队即放生任务：回到 Open，队友可再认领
            }
        }

        /// <summary>
        /// Pumps a member's self-state (position + anger) used by
        /// <see cref="AssignDefense"/>. Positions may come from the agent
        /// itself or from a kernel crowd read done by the host (只读泵入).
        /// Anger is clamped by <see cref="AttackAssignment.AttackerCandidate"/>.
        /// Unknown member throws.
        /// </summary>
        public void SetMemberState(string memberId, Vec2 position, float anger)
        {
            if (!_members.TryGetValue(memberId, out MemberState state))
            {
                throw new ArgumentException($"SquadCoordinator.SetMemberState: unknown member '{memberId}'.", nameof(memberId));
            }

            state.Position = position;
            state.Anger = anger;
        }

        /// <summary>
        /// Forms the ring: member i (join order) gets ring slot i of
        /// <see cref="MemberCount"/> around <paramref name="leaderPosition"/>
        /// (radius/start angle from <see cref="Options"/>). Slots are unique
        /// by construction — <see cref="FormationSlot.AreDistinct"/> over the
        /// returned positions is the invariant test. Safe with zero members
        /// (no-op).
        /// </summary>
        /// <returns>Formation positions in join order (parallel to <see cref="MemberIds"/>).</returns>
        public IReadOnlyList<Vec2> UpdateFormation(Vec2 leaderPosition)
        {
            var positions = new List<Vec2>(_memberIds.Count);
            for (int i = 0; i < _memberIds.Count; i++)
            {
                MemberState state = _members[_memberIds[i]];
                state.SlotIndex = i;
                state.FormationPosition = FormationSlot.Calculate(
                    leaderPosition, i, _memberIds.Count, _options.FormationRadius, _options.StartAngleRadians);
                positions.Add(state.FormationPosition);
            }

            return positions;
        }

        /// <summary>
        /// Claims a board task FOR a member (成员任务不冲突): fails when the
        /// member already holds a task (one task per member) or the board's
        /// claim mutex rejects (already claimed/expired/gone). Success records
        /// the holding; <see cref="CompleteTask"/>/<see cref="Leave"/> release it.
        /// Requires a board (throws when the squad was built without one).
        /// </summary>
        public bool TryClaimTask(string memberId, string taskId, float now)
        {
            if (_board == null)
            {
                throw new InvalidOperationException("SquadCoordinator.TryClaimTask: the squad has no task board.");
            }

            if (!_members.TryGetValue(memberId, out MemberState state))
            {
                throw new ArgumentException($"SquadCoordinator.TryClaimTask: unknown member '{memberId}'.", nameof(memberId));
            }

            if (state.TaskId != null)
            {
                return false; // 成员任务不冲突：一人同时至多一项任务
            }

            if (!_board.Claim(taskId, memberId, now))
            {
                return false; // 板上互斥失败：别人已认领/已到期
            }

            state.TaskId = taskId;
            return true;
        }

        /// <summary>
        /// The member completes its held task: board record removed, member
        /// freed for the next claim. Returns false when the member holds
        /// nothing (or the board rejected — e.g. the task was swept).
        /// </summary>
        public bool CompleteTask(string memberId)
        {
            if (_board == null)
            {
                throw new InvalidOperationException("SquadCoordinator.CompleteTask: the squad has no task board.");
            }

            if (!_members.TryGetValue(memberId, out MemberState state) || state.TaskId == null)
            {
                return false;
            }

            string taskId = state.TaskId;
            state.TaskId = null;
            return _board.Complete(taskId, memberId);
        }

        /// <summary>
        /// Releases the member's held task back to Open (task kept on the
        /// board) without touching membership. Returns false when the member
        /// holds nothing. Used when a member must abort its work.
        /// </summary>
        public bool ReleaseTask(string memberId)
        {
            if (_board == null)
            {
                throw new InvalidOperationException("SquadCoordinator.ReleaseTask: the squad has no task board.");
            }

            if (!_members.TryGetValue(memberId, out MemberState state) || state.TaskId == null)
            {
                return false;
            }

            string taskId = state.TaskId;
            state.TaskId = null;
            return _board.Abandon(taskId, memberId);
        }

        /// <summary>
        /// 协防: assigns the squad's members (positions/anger via
        /// <see cref="SetMemberState"/>) onto the pumped target snapshot via
        /// <see cref="AttackAssignment"/> — with the squad's default capacity
        /// (1), no two members ever share a target. Returns the plan (also
        /// readable through <see cref="AttackAssignment.Plan.Format"/>).
        /// </summary>
        public AttackAssignment.Plan AssignDefense(IReadOnlyList<AttackAssignment.TargetCandidate> targets)
        {
            var attackers = new List<AttackAssignment.AttackerCandidate>(_memberIds.Count);
            for (int i = 0; i < _memberIds.Count; i++)
            {
                MemberState state = _members[_memberIds[i]];
                attackers.Add(new AttackAssignment.AttackerCandidate(_memberIds[i], state.Position, state.Anger));
            }

            return AttackAssignment.Assign(attackers, targets, _options.MaxAttackersPerTarget);
        }
    }
}
