using System;
using System.Collections.Generic;
using Game.Domain.Enemy;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.AI
{
    /// <summary>
    /// Behaviour-tree enemy brain (TASK-A-R8-03) — the upgraded counterpart of
    /// <c>Game.Domain.Enemy.EnemyFsm</c>, built on
    /// <c>ZFrame.Gameplay.BehaviorTree</c> and kept purely data-driven like the
    /// M4-01 pattern: the outside world pumps the player position
    /// (<see cref="SetPlayerPosition"/>), the tree reads everything else from
    /// the shared <see cref="Blackboard"/>, and the owner pumps
    /// <see cref="Tick"/>. No physics, no transforms, no engine types.
    ///
    /// Tree shape (brief: Selector[ Dead → Chase(视野内) → Attack(攻击距离) → Patrol(路点) ]):
    /// <code>
    /// Selector
    /// ├─ Sequence( Cond(Dead)                      , Act(EnterDead) )
    /// ├─ Sequence( Cond(PlayerVisible)             , Act(Chase)     )
    /// ├─ Sequence( Cond(InAttackRange &amp;&amp; !Dead) , Succeeder(Cooldown(cd, Act(Attack))) )
    /// └─ Act(Patrol)
    /// </code>
    /// Why the plain Selector order still reaches Attack: a Selector returns on
    /// the first SUCCESS, so the chase branch must not succeed in position —
    /// the chase action instead YIELDS (Failure) once the player is within
    /// attack range ("this branch no longer owns the behaviour"), and the very
    /// same tick the Selector falls through to the Attack branch. While closing
    /// in the chase action returns Running (the Selector resumes it next tick),
    /// and it fails when the player slips beyond LoseRange — the FSM version's
    /// hysteresis lives inside the action and guards a mid-Running chase;
    /// whenever no branch is Running the conditions are re-evaluated from the
    /// top (documented semantic difference to the FSM version).
    ///
    /// Death semantics (difference #2, narrowed by TASK-A-R9-09): the FSM
    /// version interrupts any state via its transition table the moment Kill
    /// runs; the plain Selector resumes a mid-Running branch without
    /// re-checking the Dead watch, so the actions all fail fast on the IsDead
    /// blackboard flag and the Dead branch wins on the next re-evaluation —
    /// <see cref="CurrentBehaviour"/> masks the at most one stale tick by
    /// reporting Dead as soon as Kill ran. The ANNOUNCEMENT is no longer a
    /// difference: <see cref="Kill"/> now raises the same
    /// <c>EnemyStateChanged</c>{Current=Dead, Position} the FSM brain raises
    /// (once, on the first call; identical exit chain — C# event + EventBus).
    ///
    /// Blackboard keys (<see cref="Keys"/>): conditions read ONLY blackboard
    /// data — positions, the waypoint table and the range thresholds are all
    /// pumped in (construction writes the thresholds; every tick writes the
    /// sensor data), so a test can retune the tree at runtime by writing keys.
    /// </summary>
    public sealed class EnemyBehaviorTree
    {
        /// <summary>Well-known blackboard keys of the enemy tree.</summary>
        public static class Keys
        {
            /// <summary>bool — set by <see cref="Kill"/>, read by every branch.</summary>
            public const string IsDead = "enemy.isDead";

            /// <summary>Vec2 — last pumped player position.</summary>
            public const string PlayerPosition = "enemy.playerPosition";

            /// <summary>Vec2 — the enemy's own position, written each tick.</summary>
            public const string SelfPosition = "enemy.selfPosition";

            /// <summary>IReadOnlyList&lt;Vec2&gt; — the patrol loop.</summary>
            public const string Waypoints = "enemy.waypoints";

            /// <summary>float — aggro distance (from <see cref="Game.Domain.Enemy.EnemyTuning.ChaseRange"/>).</summary>
            public const string ChaseRange = "enemy.chaseRange";

            /// <summary>float — give-up distance (from <see cref="Game.Domain.Enemy.EnemyTuning.LoseRange"/>).</summary>
            public const string LoseRange = "enemy.loseRange";

            /// <summary>float — attack trigger distance.</summary>
            public const string AttackRange = "enemy.attackRange";
        }

        private readonly string _id;
        private readonly EnemyTuning _tuning;
        private readonly List<Vec2> _waypoints;
        private readonly float _attackRange;
        private readonly float _attackCooldownSeconds;
        private readonly Blackboard _blackboard;
        private readonly BtNode _root;
        private readonly BehaviorTreeRunner _runner;
        private Vec2 _position;
        private Vec2 _playerPosition;
        private int _waypointIndex;
        private bool _isDead;
        private EnemyBehaviorState _currentBehaviour;

        /// <summary>Creates the brain and builds its tree. Throws on nonsense setup (fail fast).</summary>
        /// <param name="id">Stable identity, e.g. "enemy-1".</param>
        /// <param name="startPosition">Initial world position (2D plane).</param>
        /// <param name="waypoints">Patrol loop; empty means the enemy idles instead of patrolling.</param>
        /// <param name="tuning">Shared behaviour thresholds (movement speeds, aggro/give-up ranges).</param>
        /// <param name="attackRange">Distance at or below which the Attack branch fires. Must be &gt;= 0 and &lt;= tuning.ChaseRange.</param>
        /// <param name="attackCooldownSeconds">Minimum seconds between two attack swings. Must be &gt;= 0.</param>
        public EnemyBehaviorTree(
            string id,
            Vec2 startPosition,
            IEnumerable<Vec2> waypoints,
            EnemyTuning tuning,
            float attackRange = 1.5f,
            float attackCooldownSeconds = 1f)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("EnemyBehaviorTree: id must not be empty.", nameof(id));
            }

            if (attackRange < 0f || attackRange > tuning.ChaseRange)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attackRange),
                    "EnemyBehaviorTree: AttackRange must be 0..ChaseRange (an enemy cannot strike farther than it sees).");
            }

            if (attackCooldownSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(attackCooldownSeconds), "EnemyBehaviorTree: cooldown must be >= 0.");
            }

            _id = id;
            _position = startPosition;
            _playerPosition = startPosition;
            _tuning = tuning;
            _attackRange = attackRange;
            _attackCooldownSeconds = attackCooldownSeconds;
            _waypoints = waypoints != null ? new List<Vec2>(waypoints) : new List<Vec2>();
            _currentBehaviour = EnemyBehaviorState.Idle;

            // Pump the static half of the blackboard once; the sensor half is
            // rewritten at the top of every Tick (see PumpBlackboard).
            _blackboard = new Blackboard();
            _blackboard.Set(Keys.IsDead, false);
            _blackboard.Set(Keys.PlayerPosition, _playerPosition);
            _blackboard.Set(Keys.SelfPosition, _position);
            _blackboard.Set(Keys.Waypoints, _waypoints);
            _blackboard.Set(Keys.ChaseRange, tuning.ChaseRange);
            _blackboard.Set(Keys.LoseRange, tuning.LoseRange);
            _blackboard.Set(Keys.AttackRange, _attackRange);

            _root = BuildTree();
            _runner = new BehaviorTreeRunner(_root, _blackboard, this);
        }

        /// <summary>Raised on every committed swing (also fired on the <see cref="EventBus"/>).</summary>
        public event Action<EnemyAttackRequested> EnemyAttackRequested;

        /// <summary>
        /// Raised once when <see cref="Kill"/> first takes effect — the tree
        /// brain's counterpart of <c>Game.Domain.Enemy.EnemyFsm.EnemyStateChanged</c>
        /// (TASK-A-R9-09: "Kill 即 Dead 事件" in both brains). Same exit chain
        /// as the FSM brain: the C# event first, then the same payload on the
        /// <see cref="EventBus"/> (that is where the death burst listens).
        /// The tree announces death only; routine branch switches stay
        /// blackboard-internal.
        /// </summary>
        public event Action<EnemyStateChanged> EnemyStateChanged;

        /// <summary>Stable identity.</summary>
        public string Id => _id;

        /// <summary>Current world position (2D plane; the View maps it to XZ).</summary>
        public Vec2 Position => _position;

        /// <summary>Last pumped player position.</summary>
        public Vec2 PlayerPosition => _playerPosition;

        /// <summary>Behaviour thresholds (movement speeds, aggro/give-up ranges).</summary>
        public EnemyTuning Tuning => _tuning;

        /// <summary>Attack trigger distance.</summary>
        public float AttackRange => _attackRange;

        /// <summary>Minimum seconds between two swings.</summary>
        public float AttackCooldownSeconds => _attackCooldownSeconds;

        /// <summary>Patrol loop (read-only; the same list the blackboard exposes).</summary>
        public IReadOnlyList<Vec2> Waypoints => _waypoints;

        /// <summary>Index of the waypoint currently being walked toward.</summary>
        public int WaypointIndex => _waypointIndex;

        /// <summary>True once <see cref="Kill"/> ran (terminal; takes effect from the next tick on).</summary>
        public bool IsDead => _isDead;

        /// <summary>Distance to the last pumped player position.</summary>
        public float DistanceToPlayer => Vec2.Distance(_position, _playerPosition);

        /// <summary>Behaviour that owned the last tick (Dead as soon as Kill ran).</summary>
        public EnemyBehaviorState CurrentBehaviour => _isDead ? EnemyBehaviorState.Dead : _currentBehaviour;

        /// <summary>The shared blackboard (tests may retune keys at runtime).</summary>
        public Blackboard Blackboard => _blackboard;

        /// <summary>The pumped tree root (tests: delegate-cache identity, structure checks).</summary>
        public BtNode Root => _root;

        /// <summary>The runner owning the root (tests: context reuse, tick events).</summary>
        public BehaviorTreeRunner Runner => _runner;

        /// <summary>Status the tree returned on the last executed tick (null before the first tick).</summary>
        public BTStatus? LastStatus => _runner.LastStatus;

        /// <summary>Pumps the player position the AI reacts to (called by the driver each frame).</summary>
        public void SetPlayerPosition(Vec2 playerPosition)
        {
            _playerPosition = playerPosition;
            _blackboard.Set(Keys.PlayerPosition, playerPosition);
        }

        /// <summary>Advances the AI by <paramref name="deltaTime"/> seconds; non-positive dt is ignored.</summary>
        public BTStatus Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return _runner.LastStatus ?? BTStatus.Failure;
            }

            PumpBlackboard();
            return _runner.Tick(deltaTime);
        }

        /// <summary>
        /// Kills the enemy (terminal). The Dead branch owns every tick from the
        /// next one on. Idempotent: the first call performs the kill AND fires
        /// the <see cref="EnemyStateChanged"/> death announcement (payload
        /// identical to the FSM brain's — <c>EnemyFsm.OnStateChanged</c>:
        /// previous behaviour, <see cref="EnemyStateId.Dead"/>, current plane
        /// position; TASK-A-R9-09); every later call is a no-op, so the death
        /// event is raised exactly once.
        /// </summary>
        public void Kill()
        {
            if (_isDead)
            {
                return; // terminal: the announcement fired on the first call
            }

            EnemyStateId previous = ToStateId(_currentBehaviour);
            _isDead = true;
            _blackboard.Set(Keys.IsDead, true);

            var evt = new EnemyStateChanged(_id, previous, EnemyStateId.Dead, _position);
            EnemyStateChanged?.Invoke(evt);
            EventBus.Fire(evt);
        }

        private void PumpBlackboard()
        {
            _blackboard.Set(Keys.SelfPosition, _position);
            _blackboard.Set(Keys.IsDead, _isDead);
        }

        /// <summary>
        /// Builds the tree of the class doc comment. Delegates are created ONCE
        /// here (method-group conversions — no per-tick closures); conditions
        /// read only the blackboard, actions write position/behaviour and read
        /// the blackboard.
        /// </summary>
        private BtNode BuildTree()
        {
            return new Selector(
                new Sequence(
                    new BtCondition(IsDeadCondition),
                    new BtAction(EnterDeadAction)),
                new Sequence(
                    new BtCondition(PlayerVisibleCondition),
                    new BtAction(ChaseAction)),
                new Sequence(
                    new BtCondition(InAttackRangeCondition),
                    new Succeeder(
                        new Cooldown(
                            new BtAction(AttackAction),
                            _attackCooldownSeconds))),
                new BtAction(PatrolAction));
        }

        // ---- conditions: blackboard readers only --------------------------

        private bool IsDeadCondition(BtContext ctx)
        {
            return ctx.Blackboard != null
                   && ctx.Blackboard.TryGet(Keys.IsDead, out bool dead)
                   && dead;
        }

        private bool PlayerVisibleCondition(BtContext ctx)
        {
            return TryReadRange(ctx, Keys.ChaseRange, out float chaseRange)
                   && TryReadPositions(ctx, out Vec2 self, out Vec2 player)
                   && Vec2.Distance(self, player) <= chaseRange;
        }

        private bool InAttackRangeCondition(BtContext ctx)
        {
            return !IsDeadCondition(ctx)
                   && TryReadRange(ctx, Keys.AttackRange, out float attackRange)
                   && TryReadPositions(ctx, out Vec2 self, out Vec2 player)
                   && Vec2.Distance(self, player) <= attackRange;
        }

        // ---- actions: the only place the world changes --------------------

        private BTStatus EnterDeadAction(BtContext ctx)
        {
            _currentBehaviour = EnemyBehaviorState.Dead;
            return BTStatus.Success;
        }

        private BTStatus ChaseAction(BtContext ctx)
        {
            if (IsDeadCondition(ctx) || !TryReadPositions(ctx, out Vec2 self, out Vec2 player))
            {
                return BTStatus.Failure; // dead mid-chase: fail fast, Selector re-evaluates from the top next tick
            }

            float distance = Vec2.Distance(self, player);
            if (!TryReadRange(ctx, Keys.LoseRange, out float loseRange) || distance > loseRange)
            {
                return BTStatus.Failure; // gave up (LoseRange hysteresis) — back to patrol/idle
            }

            if (distance <= _attackRange)
            {
                // 在攻击距离内：追击分支让位（Failure），Selector 同一 tick 落到 Attack 分支。
                // 不能返回 Success——Selector 见 Success 即短路，Attack 永远轮不到。
                return BTStatus.Failure;
            }

            _currentBehaviour = EnemyBehaviorState.Chase;
            _position = Vec2.MoveTowards(self, player, _tuning.ChaseSpeed * ctx.Dt);
            return BTStatus.Running;
        }

        private BTStatus AttackAction(BtContext ctx)
        {
            _currentBehaviour = EnemyBehaviorState.Attack;
            var swing = new EnemyAttackRequested(_id, _position, _playerPosition);
            EnemyAttackRequested?.Invoke(swing);
            EventBus.Fire(swing);
            return BTStatus.Success;
        }

        private BTStatus PatrolAction(BtContext ctx)
        {
            if (IsDeadCondition(ctx))
            {
                return BTStatus.Failure; // never caught dead patrolling past this tick
            }

            if (_waypoints.Count == 0)
            {
                _currentBehaviour = EnemyBehaviorState.Idle; // nothing to walk: stand still
                return BTStatus.Success;
            }

            _currentBehaviour = EnemyBehaviorState.Patrol;
            Vec2 target = _waypoints[_waypointIndex];
            _position = Vec2.MoveTowards(_position, target, _tuning.PatrolSpeed * ctx.Dt);
            if (Vec2.Distance(_position, target) <= _tuning.ArriveEpsilon)
            {
                _waypointIndex = (_waypointIndex + 1) % _waypoints.Count;
            }

            return BTStatus.Success;
        }

        // ---- blackboard helpers -------------------------------------------

        /// <summary>
        /// Translates the pre-death behaviour into the FSM id vocabulary so the
        /// tree's death payload speaks the same <see cref="EnemyStateChanged"/>
        /// dialect as the FSM brain. <see cref="EnemyBehaviorState.Attack"/>
        /// has no FSM state: an enemy within attack distance is still
        /// <see cref="EnemyStateId.Chase"/> in the FSM world (its transition
        /// table has no attack state), so that is the faithful translation.
        /// </summary>
        private static EnemyStateId ToStateId(EnemyBehaviorState behaviour)
        {
            switch (behaviour)
            {
                case EnemyBehaviorState.Patrol: return EnemyStateId.Patrol;
                case EnemyBehaviorState.Chase: return EnemyStateId.Chase;
                case EnemyBehaviorState.Attack: return EnemyStateId.Chase;
                case EnemyBehaviorState.Dead: return EnemyStateId.Dead;
                default: return EnemyStateId.Idle;
            }
        }

        private static bool TryReadPositions(BtContext ctx, out Vec2 self, out Vec2 player)
        {
            self = Vec2.Zero;
            player = Vec2.Zero;
            return ctx.Blackboard != null
                   && ctx.Blackboard.TryGet(Keys.SelfPosition, out self)
                   && ctx.Blackboard.TryGet(Keys.PlayerPosition, out player);
        }

        private static bool TryReadRange(BtContext ctx, string key, out float range)
        {
            range = 0f;
            return ctx.Blackboard != null && ctx.Blackboard.TryGet(key, out range);
        }
    }
}
