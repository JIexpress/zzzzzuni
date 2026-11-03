using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Time-window decorator (TASK-A-R8-03): the child may run at most once per
    /// window of <paramref name="seconds"/>; ticks inside the window fail
    /// WITHOUT ticking the child (rate limiting for attacks, taunts, checks).
    ///
    /// Time source: the accumulator is advanced by <see cref="BtContext.Dt"/> —
    /// no engine clock, fully deterministic under tests (pump dt yourself).
    ///
    /// Semantics:
    ///  - starts READY (no artificial delay before the first use);
    ///  - when ready, the child runs; a completed child (Success or Failure)
    ///    restarts the window, a Running child leaves it open;
    ///  - inside the window the decorator returns Failure and only accumulates dt;
    ///  - <see cref="Reset"/> restores readiness and resets the child.
    /// </summary>
    public sealed class Cooldown : BtNode
    {
        private readonly BtNode _child;
        private readonly float _seconds;
        private float _elapsed;

        /// <summary>Creates a cooldown of <paramref name="seconds"/>. Negative values throw.</summary>
        public Cooldown(BtNode child, float seconds)
        {
            if (seconds < 0f)
            {
                throw new ArgumentException("Cooldown: seconds must be >= 0.", nameof(seconds));
            }

            _child = child ?? throw new ArgumentNullException(nameof(child));
            _seconds = seconds;
            _elapsed = seconds; // ready at construction
        }

        /// <summary>The wrapped child.</summary>
        public BtNode Child => _child;

        /// <summary>Window length in seconds.</summary>
        public float Seconds => _seconds;

        /// <summary>Seconds already accumulated inside the current window (diagnostics/tests).</summary>
        public float Elapsed => _elapsed;

        /// <summary>True when the child would run on the next tick (diagnostics/tests).</summary>
        public bool IsReady => _elapsed >= _seconds;

        /// <inheritdoc />
        public override BTStatus Tick(BtContext ctx)
        {
            if (_elapsed < _seconds)
            {
                _elapsed += ctx.Dt;
                return BTStatus.Failure;
            }

            BTStatus status = _child.Tick(ctx);
            if (status != BTStatus.Running)
            {
                _elapsed = 0f; // completed -> window restarts
            }

            return status;
        }

        /// <inheritdoc />
        public override void Reset()
        {
            _elapsed = _seconds;
            _child.Reset();
        }
    }
}
