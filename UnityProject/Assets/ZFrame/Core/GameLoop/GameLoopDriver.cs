using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>
    /// Thin MonoBehaviour adapter that owns the single engine Update of the
    /// framework. Everything else subscribes via <see cref="ITickable"/> on
    /// the shared <see cref="TickQueue"/> — no MonoBehaviour in gameplay code
    /// should ever implement Update/LateUpdate.
    ///
    /// Frame order (design doc §3.2):
    ///   Input → Logic → Scheduler → EventBus.FlushQueued → View,
    ///   LateUpdate → Late.
    ///
    /// Pause policy: <see cref="IGameTime.IsPaused"/> freezes only the Logic
    /// stage and the Scheduler (gameplay timers). Input/View/Late keep
    /// ticking so UI and menus stay responsive.
    ///
    /// Construction/wiring is done by <see cref="GameRoot"/>; call
    /// <see cref="Init"/> exactly once after AddComponent.
    /// </summary>
    public sealed class GameLoopDriver : MonoBehaviour
    {
        private GameTime _time;
        private TickQueue _queue;
        private Scheduler _scheduler;

        private bool _initialized;

        /// <summary>Wires the shared loop objects. Must be called once, before the first engine Update.</summary>
        public void Init(GameTime time, TickQueue queue, Scheduler scheduler)
        {
            _time = time;
            _queue = queue;
            _scheduler = scheduler;
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            // Pump the clock first so every stage this frame reads the same dt.
            _time.Advance(Time.unscaledDeltaTime);

            _queue.TickAll(TickStage.Input, _time.DeltaTime);

            if (!_time.IsPaused)
            {
                // Logic + gameplay timers are frozen together on pause.
                _queue.TickAll(TickStage.Logic, _time.DeltaTime);
                _scheduler.Tick(_time.TotalUnscaled);
            }

            // End of frame: deliver FireQueued events before views react to them.
            EventBus.FlushQueued();

            // Views are never paused (menus keep animating while paused).
            _queue.TickAll(TickStage.View, _time.DeltaTime);
        }

        private void LateUpdate()
        {
            if (!_initialized)
            {
                return;
            }

            _queue.TickAll(TickStage.Late, _time.UnscaledDeltaTime);
        }
    }
}
