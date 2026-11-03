using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Leaf that DOES something (TASK-A-R8-03): move toward a target, fire an
    /// event, flip a blackboard flag — the only node allowed to change the
    /// world (conditions only read).
    ///
    /// Like <see cref="BtCondition"/>, the delegate is cached at construction
    /// and reused verbatim on every tick (no per-tick closure allocation).
    /// The delegate's return value is passed through, so actions drive
    /// one-shot (Success), failed (Failure) and continuous (Running) behaviour.
    /// </summary>
    public sealed class BtAction : BtNode
    {
        private readonly Func<BtContext, BTStatus> _action;

        /// <summary>Creates an action. The delegate is stored as-is (reference preserved).</summary>
        public BtAction(Func<BtContext, BTStatus> action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        /// <summary>The cached action (exposed so tests can assert reference equality across ticks).</summary>
        public Func<BtContext, BTStatus> Action => _action;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            return _action(ctx);
        }
    }
}
