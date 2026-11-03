using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// OR-composite / priority fallback (TASK-A-R8-03, course M14-72): ticks
    /// children in order; the first Success wins immediately (later children
    /// are NOT ticked — the short-circuit IS the priority semantics), the first
    /// Running suspends it.
    ///
    /// Resume memory: like <see cref="Sequence"/>, a Running child is resumed
    /// directly on the next tick (earlier, already-failed children are not
    /// re-ticked until the branch completes). Completing clears the memory.
    ///
    /// Re-entrancy: <see cref="Reset"/> clears the resume index AND resets every
    /// child, so a tree-level Reset while a low-priority branch is Running
    /// makes the next tick re-evaluate from the HIGHEST priority branch.
    /// </summary>
    public sealed class Selector : BtNode
    {
        private readonly BtNode[] _children;
        private int _current;

        /// <summary>Creates a selector. Children are tried in the given order (first = highest priority); empty selectors fail vacuously.</summary>
        public Selector(params BtNode[] children)
            : this((IEnumerable<BtNode>)children)
        {
        }

        /// <summary>Creates a selector from a child collection. Null children throw (fail fast at setup).</summary>
        public Selector(IEnumerable<BtNode> children)
        {
            _children = children != null ? ToArray(children) : new BtNode[0];
        }

        /// <summary>Children in priority order (diagnostics/tests).</summary>
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

                if (status == BTStatus.Success)
                {
                    _current = 0;
                    return BTStatus.Success;
                }
            }

            _current = 0;
            return BTStatus.Failure;
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
                    throw new ArgumentNullException(nameof(children), "Selector: null child node.");
                }

                list.Add(child);
            }

            return list.ToArray();
        }
    }
}
