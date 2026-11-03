using System;
using System.Collections.Generic;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Enemy
{
    /// <summary>
    /// Reference enemy AI built on the framework's <see cref="Fsm{TOwner}"/>
    /// (TASK-A-M4-01) — the worked example that proves the pattern library is
    /// reusable outside the framework assembly.
    ///
    /// States: Idle → Patrol → Chase → Dead (Dead is terminal).
    /// The machine is purely data-driven: the View pumps the player position
    /// (<see cref="SetPlayerPosition"/>), the enemy position lives here, and
    /// the owner ticks it. No physics, no transforms, no engine types.
    ///
    /// Transitions (registered in the FSM, so anything else is rejected):
    ///   Idle   → Patrol (idle timer elapsed and waypoints exist) / Chase / Dead
    ///   Patrol → Idle (no waypoints) / Chase (player in ChaseRange) / Dead
    ///   Chase  → Patrol (player beyond LoseRange, waypoints exist) / Idle / Dead
    /// </summary>
    public sealed class EnemyFsm
    {
        private readonly Fsm<EnemyFsm> _fsm;
        private readonly List<Vec2> _waypoints;
        private readonly EnemyIdleState _idle;
        private readonly EnemyPatrolState _patrol;
        private readonly EnemyChaseState _chase;
        private readonly EnemyDeadState _dead;
        private int _waypointIndex;

        /// <summary>Creates and immediately starts an enemy in <see cref="EnemyStateId.Idle"/>.</summary>
        /// <param name="id">Stable identity, e.g. "enemy-1".</param>
        /// <param name="startPosition">Initial world position (2D plane).</param>
        /// <param name="waypoints">Patrol loop; empty means the enemy never patrols.</param>
        /// <param name="tuning">Behaviour thresholds.</param>
        public EnemyFsm(string id, Vec2 startPosition, IEnumerable<Vec2> waypoints, EnemyTuning tuning)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("EnemyFsm: id must not be empty.", nameof(id));
            }

            Id = id;
            Position = startPosition;
            Tuning = tuning;
            _waypoints = waypoints != null ? new List<Vec2>(waypoints) : new List<Vec2>();

            _idle = new EnemyIdleState();
            _patrol = new EnemyPatrolState();
            _chase = new EnemyChaseState();
            _dead = new EnemyDeadState();

            _fsm = new Fsm<EnemyFsm>(this);
            _fsm.AddState(_idle);
            _fsm.AddState(_patrol);
            _fsm.AddState(_chase);
            _fsm.AddState(_dead);

            _fsm.AddTransition<EnemyIdleState, EnemyPatrolState>();
            _fsm.AddTransition<EnemyIdleState, EnemyChaseState>();
            _fsm.AddTransition<EnemyIdleState, EnemyDeadState>();
            _fsm.AddTransition<EnemyPatrolState, EnemyIdleState>();
            _fsm.AddTransition<EnemyPatrolState, EnemyChaseState>();
            _fsm.AddTransition<EnemyPatrolState, EnemyDeadState>();
            _fsm.AddTransition<EnemyChaseState, EnemyPatrolState>();
            _fsm.AddTransition<EnemyChaseState, EnemyIdleState>();
            _fsm.AddTransition<EnemyChaseState, EnemyDeadState>();

            _fsm.StateChanged += OnStateChanged;
            _fsm.Start<EnemyIdleState>();
        }

        /// <summary>Raised on every transition (initial Idle included). Also fired on the <see cref="EventBus"/>.</summary>
        public event Action<EnemyStateChanged> EnemyStateChanged;

        /// <summary>Stable identity.</summary>
        public string Id { get; }

        /// <summary>Current world position (2D plane; the View maps it to XZ).</summary>
        public Vec2 Position { get; private set; }

        /// <summary>Last pumped player position.</summary>
        public Vec2 PlayerPosition { get; private set; }

        /// <summary>Behaviour thresholds.</summary>
        public EnemyTuning Tuning { get; }

        /// <summary>Patrol loop (read-only).</summary>
        public IReadOnlyList<Vec2> Waypoints => _waypoints;

        /// <summary>Index of the waypoint currently being walked toward.</summary>
        public int WaypointIndex => _waypointIndex;

        /// <summary>
        /// Seconds spent in <see cref="EnemyStateId.Idle"/> since entering it.
        /// Owned by the FSM owner (not the state) so state instances stay
        /// stateless — see <see cref="IState{TOwner}"/>.
        /// </summary>
        internal float IdleElapsed { get; set; }

        /// <summary>Current state id.</summary>
        public EnemyStateId StateId => ((EnemyStateBase)_fsm.Current).StateId;

        /// <summary>Distance to the last pumped player position.</summary>
        public float DistanceToPlayer => Vec2.Distance(Position, PlayerPosition);

        /// <summary>True once <see cref="Kill"/> ran (terminal).</summary>
        public bool IsDead => StateId == EnemyStateId.Dead;

        /// <summary>Pumps the player position the AI reacts to (called by the View each frame).</summary>
        public void SetPlayerPosition(Vec2 playerPosition)
        {
            PlayerPosition = playerPosition;
        }

        /// <summary>Advances the AI by <paramref name="deltaTime"/> seconds.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            _fsm.Tick(deltaTime);
        }

        /// <summary>Requests a transition by id. Returns false when the FSM rejects it.</summary>
        public bool RequestState(EnemyStateId next)
        {
            switch (next)
            {
                case EnemyStateId.Idle:
                    return _fsm.ChangeState(_idle);
                case EnemyStateId.Patrol:
                    return _fsm.ChangeState(_patrol);
                case EnemyStateId.Chase:
                    return _fsm.ChangeState(_chase);
                case EnemyStateId.Dead:
                    return _fsm.ChangeState(_dead);
                default:
                    return false;
            }
        }

        /// <summary>Kills the enemy (terminal Dead state). No-op when already dead.</summary>
        public bool Kill()
        {
            return RequestState(EnemyStateId.Dead);
        }

        /// <summary>Moves toward <paramref name="target"/> by at most speed·dt. Used by the states.</summary>
        internal void MoveToward(Vec2 target, float speed, float deltaTime)
        {
            Position = Vec2.MoveTowards(Position, target, speed * deltaTime);
        }

        /// <summary>True when the current waypoint has been reached.</summary>
        internal bool HasReachedWaypoint()
        {
            return _waypoints.Count == 0 ||
                   Vec2.Distance(Position, _waypoints[_waypointIndex]) <= Tuning.ArriveEpsilon;
        }

        /// <summary>Advances to the next waypoint (wraps around the loop).</summary>
        internal void AdvanceWaypoint()
        {
            if (_waypoints.Count == 0)
            {
                return;
            }

            _waypointIndex = (_waypointIndex + 1) % _waypoints.Count;
        }

        /// <summary>Target position of the current waypoint (Position when there is none).</summary>
        internal Vec2 CurrentWaypoint => _waypoints.Count == 0 ? Position : _waypoints[_waypointIndex];

        private void OnStateChanged(IState<EnemyFsm> previous, IState<EnemyFsm> next)
        {
            var previousId = previous as EnemyStateBase;
            var nextId = (EnemyStateBase)next;
            // TASK-A-R9-07: the event carries the enemy's current plane position
            // so View layers can place feedback (e.g. the death burst) without a
            // position lookup. Same minimal-extension precedent as ItemPickedUp.
            var evt = new EnemyStateChanged(Id, previousId?.StateId ?? nextId.StateId, nextId.StateId, Position);
            EnemyStateChanged?.Invoke(evt);
            EventBus.Fire(evt);
        }
    }
}
