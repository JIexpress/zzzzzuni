namespace ZFrame.Gameplay
{
    /// <summary>
    /// Base class of every behaviour tree node (TASK-A-R8-03). Pure C# — no
    /// engine types; the owner pumps <see cref="Tick"/> through a
    /// <see cref="BehaviorTreeRunner"/> (same discipline as <see cref="Fsm{TOwner}"/>).
    ///
    /// The two-method contract:
    ///  - <see cref="Tick"/>: run one slice of the node's behaviour.
    ///  - <see cref="Reset"/>: clear ALL internal "I am mid-Running" memory so
    ///    the next <see cref="Tick"/> starts from scratch. This is the classic
    ///    course M14 pitfall: a tree that is Reset while a branch is Running
    ///    must NOT resume that branch on the next tick (re-entrancy guard).
    ///    Composites propagate Reset to children; stateless leaves may keep
    ///    the no-op base implementation.
    /// </summary>
    public abstract class BtNode
    {
        /// <summary>Runs one slice. Must not throw on ordinary data; returns <see cref="BTStatus.Running"/> when unfinished.</summary>
        public abstract BTStatus Tick(BtContext ctx);

        /// <summary>
        /// Clears internal Running memory (resumes-from-here indices, repeat
        /// counters, cooldown timers...). After Reset the node behaves exactly
        /// like a freshly constructed one. Base implementation is a no-op for
        /// stateless leaves; any node that stores tick-to-tick state MUST override.
        /// </summary>
        public virtual void Reset()
        {
        }
    }
}
