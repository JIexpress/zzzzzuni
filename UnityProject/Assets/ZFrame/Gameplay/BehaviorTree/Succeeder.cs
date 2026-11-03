using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Decorator that reports Success no matter what the child says
    /// (TASK-A-R8-03) — "I don't care if it worked, carry on":
    /// Success/Failure both become Success; <see cref="BTStatus.Running"/>
    /// passes through (an unfinished subtree must stay unfinished, otherwise
    /// the parent would move on while the work is still going).
    /// </summary>
    public sealed class Succeeder : BtNode
    {
        private readonly BtNode _child;

        /// <summary>Wraps a child. Null throws.</summary>
        public Succeeder(BtNode child)
        {
            _child = child ?? throw new ArgumentNullException(nameof(child));
        }

        /// <summary>The wrapped child.</summary>
        public BtNode Child => _child;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            return _child.Tick(ctx) == BTStatus.Running ? BTStatus.Running : BTStatus.Success;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            _child.Reset();
        }
    }
}
