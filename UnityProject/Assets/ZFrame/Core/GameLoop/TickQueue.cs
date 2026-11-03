using System.Collections.Generic;

namespace ZFrame.Core
{
    /// <summary>
    /// Execution stages of the central game loop. The underlying int order is
    /// the execution order: Input, then Logic, then View, then Late.
    /// </summary>
    public enum TickStage
    {
        /// <summary>Input sampling — runs first so logic reads fresh state.</summary>
        Input = 0,

        /// <summary>Gameplay simulation. Frozen when the game is paused.</summary>
        Logic = 1,

        /// <summary>View/UI updates — not frozen by pause so menus keep animating.</summary>
        View = 2,

        /// <summary>Post-everything pass, driven from LateUpdate.</summary>
        Late = 3,
    }

    /// <summary>
    /// Pure C# registry of <see cref="ITickable"/>s per <see cref="TickStage"/>.
    /// The <see cref="GameLoopDriver"/> is the only caller of
    /// <see cref="TickAll"/>; everything else only registers/unregisters.
    ///
    /// Registering while a stage is mid-tick is safe: the registration is
    /// deferred to the end of that tick so the iteration is never mutated
    /// in flight. Not thread-safe (main thread only).
    ///
    /// Implements <see cref="ITickQueue"/> (the type consumers resolve from
    /// <see cref="Services"/>); the concrete class stays public because
    /// <see cref="GameLoopDriver"/> needs <see cref="TickAll"/>.
    /// </summary>
    public sealed class TickQueue : ITickQueue
    {
        private readonly List<ITickable>[] _stages =
        {
            new List<ITickable>(),
            new List<ITickable>(),
            new List<ITickable>(),
            new List<ITickable>(),
        };

        private readonly List<(ITickable Item, TickStage Stage)> _pendingRegistrations =
            new List<(ITickable, TickStage)>();

        private readonly HashSet<ITickable> _removedDuringTick = new HashSet<ITickable>();

        private bool _ticking;

        /// <summary>Number of live tickables at <paramref name="stage"/> (debug/tests).</summary>
        public int Count(TickStage stage)
        {
            return _stages[(int)stage].Count;
        }

        /// <summary>
        /// Registers <paramref name="tickable"/> for <paramref name="stage"/>.
        /// Duplicate registrations of the same instance at the same stage are ignored.
        /// If called during <see cref="TickAll"/>, takes effect after the tick finishes.
        /// </summary>
        public void Register(ITickable tickable, TickStage stage)
        {
            if (_ticking)
            {
                _pendingRegistrations.Add((tickable, stage));
                return;
            }

            Add(tickable, stage);
        }

        /// <summary>
        /// Unregisters <paramref name="tickable"/> from every stage.
        /// If called during a tick, the entry is tombstoned so the in-flight
        /// iteration skips it and no other registrant is skipped by the
        /// list-shift side effect.
        /// </summary>
        public void Unregister(ITickable tickable)
        {
            bool removed = false;
            for (int i = 0; i < _stages.Length; i++)
            {
                if (_stages[i].Remove(tickable))
                {
                    removed = true;
                }
            }

            if (removed && _ticking)
            {
                _removedDuringTick.Add(tickable);
            }

            _pendingRegistrations.RemoveAll(p => ReferenceEquals(p.Item, tickable));
        }

        /// <summary>
        /// Ticks all registrants of <paramref name="stage"/> in registration
        /// order with <paramref name="dt"/>. Only one tick may be in flight
        /// at a time.
        /// </summary>
        public void TickAll(TickStage stage, float dt)
        {
            List<ITickable> list = _stages[(int)stage];
            if (_ticking || list.Count == 0)
            {
                return;
            }

            _ticking = true;
            try
            {
                // Snapshot the list: handlers may Remove entries (which would
                // shift indices and skip subsequent registrants) or Register
                // (deferred to _pendingRegistrations). Tombstoned entries —
                // unregistered mid-tick — are skipped.
                ITickable[] snapshot = list.ToArray();
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (_removedDuringTick.Contains(snapshot[i]))
                    {
                        continue;
                    }

                    snapshot[i].Tick(dt);
                }
            }
            finally
            {
                _ticking = false;
                _removedDuringTick.Clear();
                FlushPending();
            }
        }

        /// <summary>Removes all registrants (scene teardown).</summary>
        public void Clear()
        {
            for (int i = 0; i < _stages.Length; i++)
            {
                _stages[i].Clear();
            }

            _pendingRegistrations.Clear();
        }

        private void Add(ITickable tickable, TickStage stage)
        {
            List<ITickable> list = _stages[(int)stage];
            if (!list.Contains(tickable))
            {
                list.Add(tickable);
            }
        }

        private void FlushPending()
        {
            if (_pendingRegistrations.Count == 0)
            {
                return;
            }

            // Copy-then-clear: Add may itself be reached from a nested tick path.
            var pending = _pendingRegistrations.ToArray();
            _pendingRegistrations.Clear();
            foreach (var (item, stage) in pending)
            {
                Add(item, stage);
            }
        }
    }
}
