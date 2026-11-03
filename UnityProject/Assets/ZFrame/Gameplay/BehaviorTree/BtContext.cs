namespace ZFrame.Gameplay
{
    /// <summary>
    /// Per-tick input handed to every node (TASK-A-R8-03). Carries the shared
    /// <see cref="Blackboard"/>, the tree owner (whatever object the caller
    /// passed — the framework never touches it) and the delta time of this tick.
    ///
    /// The <see cref="BehaviorTreeRunner"/> creates ONE context per tree and
    /// mutates <see cref="Dt"/> in place, so ticking allocates nothing.
    /// </summary>
    public sealed class BtContext
    {
        /// <summary>Creates a context. Blackboard/owner may be null for leaf-only trees.</summary>
        public BtContext(Blackboard blackboard = null, object owner = null, float dt = 0f)
        {
            Blackboard = blackboard;
            Owner = owner;
            Dt = dt;
        }

        /// <summary>Shared typed key-value store pumped by the outside world. May be null.</summary>
        public Blackboard Blackboard { get; }

        /// <summary>Owner object passed at construction (read-only for nodes).</summary>
        public object Owner { get; }

        /// <summary>Delta time of the current tick in seconds (updated by the runner).</summary>
        public float Dt { get; internal set; }
    }
}
