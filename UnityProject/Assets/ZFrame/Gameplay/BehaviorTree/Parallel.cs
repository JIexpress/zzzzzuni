using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Concurrent composite (TASK-A-R8-03): ticks EVERY child on EVERY tick —
    /// unlike Sequence/Selector it has no resume index, children run side by
    /// side and keep their own memory. Completion follows the
    /// <see cref="ParallelPolicy"/>: RequireAll (AND) or RequireOne (OR).
    ///
    /// Abort discipline (re-entrancy guard): when the parallel COMPLETES — or
    /// aborts early — it Resets all children, so no child is left believing it
    /// is mid-Running when the parallel is re-entered on a later tick. While
    /// the parallel keeps returning Running, children simply keep their own
    /// resume memory (a nested Sequence continues where it left off).
    /// </summary>
    public sealed class Parallel : BtNode
    {
        private readonly BtNode[] _children;
        private readonly ParallelPolicy _policy;

        /// <summary>Creates a parallel with the given completion policy. Null children throw (fail fast at setup).</summary>
        public Parallel(ParallelPolicy policy, params BtNode[] children)
        {
            _policy = policy;
            _children = children != null ? ToArray(children) : new BtNode[0];
        }

        /// <summary>Children ticked concurrently (diagnostics/tests).</summary>
        public IReadOnlyList<BtNode> Children => _children;

        /// <summary>Completion policy of this parallel.</summary>
        public ParallelPolicy Policy => _policy;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            bool anySuccess = false;
            bool anyFailure = false;
            bool anyRunning = false;

            for (int i = 0; i < _children.Length; i++)
            {
                switch (_children[i].Tick(ctx))
                {
                    case BTStatus.Success:
                        anySuccess = true;
                        break;
                    case BTStatus.Failure:
                        anyFailure = true;
                        break;
                    default:
                        anyRunning = true;
                        break;
                }
            }

            BTStatus result;
            if (_policy == ParallelPolicy.RequireAll)
            {
                // AND: any failure aborts (even while others still run); success
                // only when nothing failed AND nothing is still running.
                result = anyFailure ? BTStatus.Failure
                       : anyRunning ? BTStatus.Running
                       : BTStatus.Success; // vacuous case (no children) lands here too
            }
            else
            {
                // OR: any success wins immediately; failure only when nothing
                // succeeded AND nothing is still running.
                result = anySuccess ? BTStatus.Success
                       : anyRunning ? BTStatus.Running
                       : BTStatus.Failure;
            }

            if (result != BTStatus.Running)
            {
                ResetAll();
            }

            return result;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            ResetAll();
        }

        private void ResetAll()
        {
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
                    throw new ArgumentNullException(nameof(children), "Parallel: null child node.");
                }

                list.Add(child);
            }

            return list.ToArray();
        }
    }
}
