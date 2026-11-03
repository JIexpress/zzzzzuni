using System;
using System.Collections.Generic;
using Game.Domain.Enemy;
using ZFrame.Gameplay;

namespace Game.Domain.AI
{
    /// <summary>
    /// Mode-switching driver for the two enemy brains (TASK-A-R8-03): the
    /// M4-01 FSM (<c>Game.Domain.Enemy.EnemyFsm</c>) and the behaviour tree
    /// (<see cref="EnemyBehaviorTree"/>) behind one uniform, engine-free
    /// surface. The brief keeps both alive as a controlled comparison —
    /// <see cref="EnemyDriveMode.BehaviorTree"/> is the DEFAULT, the FSM is
    /// the control group.
    ///
    /// Semantic differences the modes expose (see task report for details):
    ///  - FSM interrupts its current state the instant <c>Kill</c> runs
    ///    (table-driven Any-style transitions); the tree observes death at the
    ///    next re-evaluation boundary (actions fail fast, one masked tick).
    ///  - FSM has no Attack behaviour and never raises
    ///    <see cref="EnemyAttackRequested"/>; the tree commits swings through
    ///    a cooldown-gated Attack branch.
    ///  - FSM abandons a chase via LoseRange from any chase tick; the tree
    ///    re-evaluates branch conditions whenever no branch is Running, so
    ///    its hysteresis only guards a mid-Running chase.
    ///
    /// Unified exits (TASK-A-R9-09): death is announced identically by both
    /// brains — each raises <c>EnemyStateChanged</c>{Current=Dead, Position}
    /// (C# event + EventBus) and this controller forwards whichever brain is
    /// active through its own <see cref="EnemyStateChanged"/>, the same
    /// forwarding chain <see cref="OnTreeAttackRequested"/> established for
    /// swings. The FSM brain additionally announces its routine transitions
    /// (its brain-level event carries them); the tree keeps those
    /// blackboard-internal and announces death only.
    /// </summary>
    public sealed class EnemyController
    {
        private readonly EnemyDriveMode _mode;
        private readonly EnemyFsm _fsm;
        private readonly EnemyBehaviorTree _behaviorTree;

        /// <summary>
        /// Creates the enemy with the requested brain.
        /// <paramref name="attackRange"/>/<paramref name="attackCooldownSeconds"/>
        /// only apply to <see cref="EnemyDriveMode.BehaviorTree"/> (the FSM has
        /// no attack).
        /// </summary>
        public EnemyController(
            string id,
            Vec2 startPosition,
            IEnumerable<Vec2> waypoints,
            EnemyTuning tuning,
            EnemyDriveMode mode = EnemyDriveMode.BehaviorTree,
            float attackRange = 1.5f,
            float attackCooldownSeconds = 1f)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("EnemyController: id must not be empty.", nameof(id));
            }

            _mode = mode;
            if (mode == EnemyDriveMode.BehaviorTree)
            {
                _behaviorTree = new EnemyBehaviorTree(id, startPosition, waypoints, tuning, attackRange, attackCooldownSeconds);
                _behaviorTree.EnemyAttackRequested += OnTreeAttackRequested;
                _behaviorTree.EnemyStateChanged += OnBrainStateChanged;
            }
            else
            {
                _fsm = new EnemyFsm(id, startPosition, waypoints, tuning);
                // The FSM announces its initial state inside the constructor,
                // i.e. before this line — only later transitions forward.
                _fsm.EnemyStateChanged += OnBrainStateChanged;
            }
        }

        private void OnTreeAttackRequested(EnemyAttackRequested evt)
        {
            EnemyAttackRequested?.Invoke(evt);
        }

        private void OnBrainStateChanged(EnemyStateChanged evt)
        {
            EnemyStateChanged?.Invoke(evt);
        }

        /// <summary>Raised when the behaviour-tree brain commits a swing (never in FSM mode).</summary>
        public event Action<EnemyAttackRequested> EnemyAttackRequested;

        /// <summary>
        /// Raised when the active brain announces a state change (TASK-A-R9-09:
        /// one uniform death exit for both brains — Kill fires
        /// <c>Current=Dead</c> with the enemy's plane position through here in
        /// either mode; the payload is the brain's own
        /// <c>EnemyStateChanged</c>, untouched). In FSM mode this also carries
        /// the brain's routine transitions; the tree brain announces death
        /// only. Subscribe to this OR the brain's event, never both — that
        /// would double-count.
        /// </summary>
        public event Action<EnemyStateChanged> EnemyStateChanged;

        /// <summary>Which brain drives this enemy.</summary>
        public EnemyDriveMode Mode => _mode;

        /// <summary>The FSM brain (null in <see cref="EnemyDriveMode.BehaviorTree"/> mode).</summary>
        public EnemyFsm Fsm => _fsm;

        /// <summary>The behaviour-tree brain (null in <see cref="EnemyDriveMode.Fsm"/> mode).</summary>
        public EnemyBehaviorTree BehaviorTree => _behaviorTree;

        /// <summary>Stable identity.</summary>
        public string Id => _mode == EnemyDriveMode.BehaviorTree ? _behaviorTree.Id : _fsm.Id;

        /// <summary>Current world position (2D plane).</summary>
        public Vec2 Position => _mode == EnemyDriveMode.BehaviorTree ? _behaviorTree.Position : _fsm.Position;

        /// <summary>True once <see cref="Kill"/> ran on the active brain.</summary>
        public bool IsDead => _mode == EnemyDriveMode.BehaviorTree ? _behaviorTree.IsDead : _fsm.IsDead;

        /// <summary>Observable behaviour of the active brain (FSM ids and tree ids share the 0..3 numbering; Attack=4 is tree-only).</summary>
        public EnemyBehaviorState CurrentBehaviour
        {
            get
            {
                if (_mode == EnemyDriveMode.BehaviorTree)
                {
                    return _behaviorTree.CurrentBehaviour;
                }

                switch (_fsm.StateId)
                {
                    case EnemyStateId.Patrol: return EnemyBehaviorState.Patrol;
                    case EnemyStateId.Chase: return EnemyBehaviorState.Chase;
                    case EnemyStateId.Dead: return EnemyBehaviorState.Dead;
                    default: return EnemyBehaviorState.Idle;
                }
            }
        }

        /// <summary>Pumps the player position the AI reacts to.</summary>
        public void SetPlayerPosition(Vec2 playerPosition)
        {
            if (_mode == EnemyDriveMode.BehaviorTree)
            {
                _behaviorTree.SetPlayerPosition(playerPosition);
            }
            else
            {
                _fsm.SetPlayerPosition(playerPosition);
            }
        }

        /// <summary>Advances the AI by <paramref name="deltaTime"/> seconds; non-positive dt is ignored by both brains.</summary>
        public void Tick(float deltaTime)
        {
            if (_mode == EnemyDriveMode.BehaviorTree)
            {
                _behaviorTree.Tick(deltaTime);
            }
            else
            {
                _fsm.Tick(deltaTime);
            }
        }

        /// <summary>Kills the enemy through the active brain.</summary>
        public void Kill()
        {
            if (_mode == EnemyDriveMode.BehaviorTree)
            {
                _behaviorTree.Kill();
            }
            else
            {
                _fsm.Kill();
            }
        }
    }
}
