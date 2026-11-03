using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Decorator that flips the child's result (TASK-A-R8-03): Success becomes
    /// Failure and vice versa; <see cref="BTStatus.Running"/> passes through
    /// untouched (an unfinished subtree is unfinished regardless of polarity).
    /// </summary>
    public sealed class Invert : BtNode
    {
        private readonly BtNode _child;

        /// <summary>Wraps a child. Null throws.</summary>
        public Invert(BtNode child)
        {
            _child = child ?? throw new ArgumentNullException(nameof(child));
        }

        /// <summary>The wrapped child.</summary>
        public BtNode Child => _child;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            BTStatus status = _child.Tick(ctx);
            switch (status)
            {
                case BTStatus.Success:
                    return BTStatus.Failure;
                case BTStatus.Failure:
                    return BTStatus.Success;
                default:
                    return BTStatus.Running;
            }
        }

        /// <inheritdoc />
        public override void Reset()
        {
            _child.Reset();
        }
    }
}
