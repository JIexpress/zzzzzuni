using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Owns a behaviour tree root and pumps it (TASK-A-R8-03) — the BT
    /// counterpart of <see cref="Fsm{TOwner}"/>: engine-free, the caller drives
    /// <see cref="Tick"/> from its own loop, nothing coroutine/MonoBehaviour.
    ///
    /// Zero-allocation tick path: ONE <see cref="BtContext"/> is created at
    /// construction and its <see cref="BtContext.Dt"/> is mutated in place on
    /// every tick — no per-tick context or delegate allocation.
    ///
    /// Discipline (matching Fsm): non-positive dt is ignored (the tree does not
    /// advance), and <see cref="Reset"/> propagates a tree-wide re-entrancy
    /// clear (every node drops its Running memory).
    /// </summary>
    public sealed class BehaviorTreeRunner
    {
        private readonly BtNode _root;
        private readonly BtContext _context;

        /// <summary>Creates a runner. A null root throws; blackboard/owner are optional and reach nodes via the context.</summary>
        public BehaviorTreeRunner(BtNode root, Blackboard blackboard = null, object owner = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _context = new BtContext(blackboard, owner);
        }

        /// <summary>The pumped root node (diagnostics/tests).</summary>
        public BtNode Root => _root;

        /// <summary>The single context reused by every tick (zero-alloc contract; tests may hold it).</summary>
        public BtContext Context => _context;

        /// <summary>Root status of the last executed tick; null before the first tick.</summary>
        public BTStatus? LastStatus { get; private set; }

        /// <summary>
        /// Optional observer (TASK-A-R8-03 brief: "BehaviorTreeRooted"): fired
        /// after EVERY tick with the root's status — HUD/debug hook for "what
        /// did the tree decide this frame". Never fired for ignored ticks.
        /// </summary>
        public event Action<BTStatus> BehaviorTreeRooted;

        /// <summary>Advances the tree by <paramref name="dt"/> seconds; non-positive dt is ignored and returns the last status.</summary>
        public BTStatus Tick(float dt)
        {
            if (dt <= 0f)
            {
                return LastStatus ?? BTStatus.Failure;
            }

            _context.Dt = dt;
            BTStatus status = _root.Tick(_context);
            LastStatus = status;
            BehaviorTreeRooted?.Invoke(status);
            return status;
        }

        /// <summary>Tree-wide re-entrancy clear: every node drops its Running memory (root.Reset, propagated).</summary>
        public void Reset()
        {
            _root.Reset();
        }
    }
}
