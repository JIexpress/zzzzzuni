using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// AND-composite (TASK-A-R8-03, course M14-72): ticks children in order;
    /// the first Failure fails the sequence (later children are NOT ticked),
    /// Running suspends it.
    ///
    /// Resume memory: while a child returns <see cref="BTStatus.Running"/> the
    /// sequence remembers its index and the NEXT tick continues FROM that child
    /// — earlier children are not re-ticked (interrupted work resumes, it does
    /// not restart). Completing (Success or Failure) clears the memory, so the
    /// next entry starts from the first child.
    ///
    /// Re-entrancy: <see cref="Reset"/> clears the resume index AND resets every
    /// child, so a sequence interrupted mid-Running by a tree-level Reset
    /// restarts from the first child instead of resuming a stale branch.
    /// </summary>
    public sealed class Sequence : BtNode
    {
        private readonly BtNode[] _children;
        private int _current;

        /// <summary>Creates a sequence. Children are ticked in the given order; empty sequences succeed vacuously.</summary>
        public Sequence(params BtNode[] children)
            : this((IEnumerable<BtNode>)children)
        {
        }

        /// <summary>Creates a sequence from a child collection. Null children throw (fail fast at setup).</summary>
        public Sequence(IEnumerable<BtNode> children)
        {
            _children = children != null ? ToArray(children) : new BtNode[0];
        }

        /// <summary>Children in tick order (diagnostics/tests).</summary>
        public IReadOnlyList<BtNode> Children => _children;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            for (int i = _current; i < _children.Length; i++)
            {
                BTStatus status = _children[i].Tick(ctx);
                if (status == BTStatus.Running)
                {
                    _current = i;
                    return BTStatus.Running;
                }

                if (status == BTStatus.Failure)
                {
                    _current = 0;
                    return BTStatus.Failure;
                }
            }

            _current = 0;
            return BTStatus.Success;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            _current = 0;
            for (int i = 0; i < _children.Length; i++)
            {
                _children[i].Reset();
            }
        }

        private static BtNode[] ToArray(IEnumerable<BtNode> children)
        {
            var list = new List<BtNode>();
            foreach (BtNode child in children)
            {
                if (child == null)
                {
                    throw new ArgumentNullException(nameof(children), "Sequence: null child node.");
                }

                list.Add(child);
            }

            return list.ToArray();
        }
    }
}
