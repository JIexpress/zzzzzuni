using System;
using System.Collections.Generic;

namespace ZFrame.Core
{
    /// <summary>
    /// Cancellation handle for a scheduled timer. Safe to cancel after the
    /// timer already fired (no-op). Cancelling a Repeat stops future fires.
    /// </summary>
    public sealed class SchedulerHandle
    {
        internal Scheduler.Timer Timer;

        internal SchedulerHandle(Scheduler.Timer timer)
        {
            Timer = timer;
        }

        /// <summary>True when the timer is still live (will fire again or has not fired yet).</summary>
        public bool IsActive => Timer != null && !Timer.Cancelled;

        /// <summary>Cancels the timer. No-op when already fired or cancelled.</summary>
        public void Cancel()
        {
            if (Timer != null)
            {
                Timer.Cancelled = true;
            }
        }
    }

    /// <summary>
    /// Pure C# time-based scheduler. The time source is injected as a
    /// <c>Func&lt;float&gt;</c> (the GameRoot wires it to
    /// <see cref="IGameTime.TotalUnscaled"/>) so tests drive it with plain
    /// numbers: <see cref="Tick"/> receives "now" explicitly.
    ///
    /// Callbacks run in scheduling order. Reentrant scheduling from inside a
    /// callback is deferred to the next Tick (never fires within the same
    /// pump).
    /// </summary>
    public sealed class Scheduler
    {
        internal sealed class Timer
        {
            public Action Callback;
            public float NextFire;
            public float Interval;
            public bool Repeating;
            public bool Cancelled;
        }

        private readonly Func<float> _now;
        private readonly List<Timer> _timers = new List<Timer>();
        private bool _ticking;

        /// <param name="now">Clock source in seconds. Typically () =&gt; gameTime.TotalUnscaled.</param>
        public Scheduler(Func<float> now)
        {
            _now = now ?? throw new ArgumentNullException(nameof(now));
        }

        /// <summary>Number of live timers (debug/tests).</summary>
        public int Count => _timers.Count;

        /// <summary>Schedules <paramref name="callback"/> once, <paramref name="seconds"/> from now.</summary>
        public SchedulerHandle Delay(float seconds, Action callback)
        {
            return AddTimer(seconds, callback, repeating: false);
        }

        /// <summary>Schedules <paramref name="callback"/> every <paramref name="seconds"/>, first fire after one interval.</summary>
        public SchedulerHandle Repeat(float seconds, Action callback)
        {
            return AddTimer(seconds, callback, repeating: true);
        }

        /// <summary>
        /// Pumps the scheduler: fires every timer whose deadline has passed at
        /// <paramref name="now"/>. Pass the current time explicitly — the
        /// injected source is only the default for scheduling.
        /// </summary>
        public void Tick(float now)
        {
            if (_ticking)
            {
                return;
            }

            _ticking = true;
            try
            {
                // Index loop over the live list; fired one-shots are tombstoned
                // and compacted after the loop so callbacks can Cancel safely.
                for (int i = 0; i < _timers.Count; i++)
                {
                    Timer t = _timers[i];
                    if (t.Cancelled || now < t.NextFire)
                    {
                        continue;
                    }

                    if (t.Repeating)
                    {
                        t.NextFire += t.Interval;
                    }
                    else
                    {
                        t.Cancelled = true; // one-shot: will not fire again
                    }

                    t.Callback();
                }
            }
            finally
            {
                _ticking = false;
                _timers.RemoveAll(t => t.Cancelled);
                FlushPending();
            }
        }

        /// <summary>Cancels every timer (scene switch / shutdown).</summary>
        public void Clear()
        {
            _timers.Clear();
        }

        private SchedulerHandle AddTimer(float seconds, Action callback, bool repeating)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var timer = new Timer
            {
                Callback = callback,
                NextFire = _now() + seconds,
                Interval = seconds,
                Repeating = repeating,
            };

            if (_ticking)
            {
                // Reentrant scheduling from a callback: defer insertion so the
                // in-flight index loop is never mutated. NextFire is in the
                // future relative to 'now', so it cannot fire this pump.
                _pendingDuringTick.Add(timer);
            }
            else
            {
                _timers.Add(timer);
            }

            return new SchedulerHandle(timer);
        }

        private readonly List<Timer> _pendingDuringTick = new List<Timer>();

        /// <summary>Flushes timers scheduled from inside a callback into the live list.</summary>
        private void FlushPending()
        {
            if (_pendingDuringTick.Count == 0)
            {
                return;
            }

            var pending = _pendingDuringTick.ToArray();
            _pendingDuringTick.Clear();
            foreach (Timer t in pending)
            {
                if (!t.Cancelled)
                {
                    _timers.Add(t);
                }
            }
        }
    }
}
