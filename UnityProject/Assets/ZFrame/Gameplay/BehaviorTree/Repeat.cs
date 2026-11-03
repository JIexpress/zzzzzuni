using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Decorator that repeats the child (TASK-A-R8-03, course M14-72).
    ///
    /// Two modes:
    ///  - <see cref="Repeat(BtNode, int)"/> with times &gt; 0: bounded — the
    ///    child must succeed <paramref name="times"/> times before the
    ///    decorator succeeds; each child Success in between yields Running. A
    ///    child Failure propagates immediately and the counter restarts (fresh
    ///    entry next tick).
    ///  - <see cref="Repeat(BtNode, int)"/> with a negative count, or
    ///    <see cref="Forever"/>: unbounded — a succeeding child keeps the
    ///    decorator Running forever; only Failure/Running pass through as-is.
    ///
    /// Reset restores the counter and resets the child (re-entrancy guard).
    /// </summary>
    public sealed class Repeat : BtNode
    {
        private readonly BtNode _child;
        private readonly int _times;
        private int _remaining;

        /// <summary>Repeats the child <paramref name="times"/> successes. Zero throws; a negative count means forever (or use <see cref="Forever"/>).</summary>
        public Repeat(BtNode child, int times)
        {
            if (times == 0)
            {
                throw new ArgumentException("Repeat: times must not be 0 (use 1..n, or Forever()).", nameof(times));
            }

            _child = child ?? throw new ArgumentNullException(nameof(child));
            _times = times;
            _remaining = times;
        }

        /// <summary>Creates an unbounded repeat: a succeeding child keeps it Running forever.</summary>
        public static Repeat Forever(BtNode child)
        {
            return new Repeat(child, -1);
        }

        /// <summary>The wrapped child.</summary>
        public BtNode Child => _child;

        /// <summary>True when this is an unbounded repeat.</summary>
        public bool IsForever => _times < 0;

        /// <summary>Successes still required before completion (diagnostics/tests; meaningless when <see cref="IsForever"/>).</summary>
        public int Remaining => _remaining;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            BTStatus status = _child.Tick(ctx);
            if (_times < 0)
            {
                // Forever: success means "one more lap", never completion.
                return status == BTStatus.Success ? BTStatus.Running : status;
            }

            if (status == BTStatus.Running)
            {
                return BTStatus.Running;
            }

            if (status == BTStatus.Failure)
            {
                _remaining = _times; // fresh entry on the next tick
                return BTStatus.Failure;
            }

            _remaining--;
            if (_remaining <= 0)
            {
                _remaining = _times; // reusable: next entry starts a fresh count
                return BTStatus.Success;
            }

            return BTStatus.Running;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            _remaining = _times;
            _child.Reset();
        }
    }
}
