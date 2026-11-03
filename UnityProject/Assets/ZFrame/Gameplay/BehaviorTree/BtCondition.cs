using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Leaf that asks a yes/no question (TASK-A-R8-03): "is the player in
    /// vision?", "is the enemy dead?" — Success/Failure, never Running.
    ///
    /// The predicate delegate is cached at CONSTRUCTION into a readonly field
    /// (course M14 GC pitfall): Tick only invokes the stored reference, so a
    /// tree reused for thousands of ticks allocates zero delegates per tick.
    /// Build conditions once in the tree constructor; never wrap them in a
    /// per-tick closure.
    /// </summary>
    public sealed class BtCondition : BtNode
    {
        private readonly Func<BtContext, bool> _predicate;

        /// <summary>Creates a condition. The delegate is stored as-is (reference preserved).</summary>
        public BtCondition(Func<BtContext, bool> predicate)
        {
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        }

        /// <summary>The cached predicate (exposed so tests can assert reference equality across ticks).</summary>
        public Func<BtContext, bool> Predicate => _predicate;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            return _predicate(ctx) ? BTStatus.Success : BTStatus.Failure;
        }
    }
}
