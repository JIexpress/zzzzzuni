using System;
using NUnit.Framework;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>EditMode tests for the pure game-loop pieces: TickQueue, GameTime, Scheduler.</summary>
    public class GameLoopTests
    {
        // ---- TickQueue -------------------------------------------------

        private sealed class StageRecorder : ITickable
        {
            private readonly System.Collections.Generic.List<string> _log;
            private readonly string _name;

            public StageRecorder(System.Collections.Generic.List<string> log, string name)
            {
                _log = log;
                _name = name;
            }

            public void Tick(float dt)
            {
                _log.Add(_name);
            }
        }

        [Test]
        public void TickQueue_InputStageTicksBeforeLogic_WhenDrivenInOrder()
        {
            var queue = new TickQueue();
            var order = new System.Collections.Generic.List<string>();

            queue.Register(new StageRecorder(order, "input"), TickStage.Input);
            queue.Register(new StageRecorder(order, "logic"), TickStage.Logic);
            queue.Register(new StageRecorder(order, "input2"), TickStage.Input);

            // The driver always drives Input first; assert the queue honors stage isolation.
            queue.TickAll(TickStage.Input, 0.016f);
            queue.TickAll(TickStage.Logic, 0.016f);

            CollectionAssert.AreEqual(
                new[] { "input", "input2", "logic" }, order,
                "Input registrants tick in registration order before Logic registrants");
        }

        [Test]
        public void TickQueue_RegistrationDuringTick_IsDeferredToNextRound()
        {
            var queue = new TickQueue();
            var order = new System.Collections.Generic.List<string>();
            var first = new StageRecorder(order, "first");

            queue.Register(new Registrar(queue, first, order), TickStage.Logic);

            queue.TickAll(TickStage.Logic, 1f);
            CollectionAssert.AreEqual(new[] { "first" }, order,
                "registrant added mid-tick must not tick in the same round");

            queue.TickAll(TickStage.Logic, 1f);
            CollectionAssert.AreEqual(new[] { "first", "first", "latecomer" }, order,
                "deferred registrant ticks on the next round, after existing registrants");
            Assert.AreEqual(2, queue.Count(TickStage.Logic));
        }

        private sealed class Registrar : ITickable
        {
            private readonly TickQueue _queue;
            private readonly ITickable _early;
            private readonly System.Collections.Generic.List<string> _log;
            private bool _registered;

            public Registrar(TickQueue queue, ITickable early, System.Collections.Generic.List<string> log)
            {
                _queue = queue;
                _early = early;
                _log = log;
            }

            public void Tick(float dt)
            {
                _log.Add("first");
                if (!_registered)
                {
                    _registered = true;
                    _queue.Register(new StageRecorder(_log, "latecomer"), TickStage.Logic);
                }
            }
        }

        [Test]
        public void TickQueue_Unregister_StopsFutureTicks()
        {
            var queue = new TickQueue();
            var order = new System.Collections.Generic.List<string>();
            var tickable = new StageRecorder(order, "x");

            queue.Register(tickable, TickStage.View);
            queue.TickAll(TickStage.View, 1f);
            queue.Unregister(tickable);
            queue.TickAll(TickStage.View, 1f);

            Assert.AreEqual(1, order.Count, "unregistered tickable must not tick again");
            Assert.AreEqual(0, queue.Count(TickStage.View));
        }

        [Test]
        public void TickQueue_UnregisterDuringTick_IsSafe()
        {
            var queue = new TickQueue();
            var order = new System.Collections.Generic.List<string>();
            var second = new StageRecorder(order, "second");
            queue.Register(new SelfRemover(queue), TickStage.Logic);
            queue.Register(second, TickStage.Logic);

            Assert.DoesNotThrow(() => queue.TickAll(TickStage.Logic, 1f));
            Assert.AreEqual(1, order.Count, "second registrant still ticks after first removes itself");
        }

        private sealed class SelfRemover : ITickable
        {
            private readonly TickQueue _queue;

            public SelfRemover(TickQueue queue)
            {
                _queue = queue;
            }

            public void Tick(float dt)
            {
                _queue.Unregister(this);
            }
        }

        // ---- GameTime --------------------------------------------------

        [Test]
        public void GameTime_Advance_AccumulatesUnscaledAndProducesScaledDelta()
        {
            var time = new GameTime();
            time.Advance(0.5f);
            time.Advance(0.25f);

            Assert.AreEqual(0.25f, time.UnscaledDeltaTime, "unscaled dt is the last pumped value");
            Assert.AreEqual(0.75f, time.TotalUnscaled, 1e-5f, "total accumulates every Advance");
            Assert.AreEqual(0.25f, time.DeltaTime, 1e-5f, "scale 1 keeps dt unchanged");
        }

        [Test]
        public void GameTime_TimeScale_ScalesDeltaTimeOnly()
        {
            var time = new GameTime { TimeScale = 2f };
            time.Advance(0.1f);

            Assert.AreEqual(0.2f, time.DeltaTime, 1e-5f, "DeltaTime = dt * TimeScale");
            Assert.AreEqual(0.1f, time.UnscaledDeltaTime, 1e-5f, "UnscaledDeltaTime ignores TimeScale");
            Assert.AreEqual(0.1f, time.TotalUnscaled, 1e-5f, "TotalUnscaled ignores TimeScale");
        }

        [Test]
        public void GameTime_Pause_ZeroesDeltaTime_ButKeepsTimeFlowing()
        {
            var time = new GameTime { IsPaused = true, TimeScale = 3f };
            time.Advance(0.5f);

            Assert.AreEqual(0f, time.DeltaTime, "paused Logic dt must be 0 regardless of scale");
            Assert.AreEqual(0.5f, time.UnscaledDeltaTime, "real time still flows while paused");
            Assert.AreEqual(0.5f, time.TotalUnscaled);
        }

        // ---- Scheduler -------------------------------------------------

        [Test]
        public void Scheduler_Delay_FiresOnceAtDeadline_NotBeforeNotAfter()
        {
            float now = 0f;
            var scheduler = new Scheduler(() => now);
            int fired = 0;
            scheduler.Delay(2f, () => fired++);

            scheduler.Tick(now = 1f);
            Assert.AreEqual(0, fired, "must not fire before deadline");

            scheduler.Tick(now = 2f);
            Assert.AreEqual(1, fired, "fires exactly at deadline");

            scheduler.Tick(now = 5f);
            Assert.AreEqual(1, fired, "one-shot never fires twice");
            Assert.AreEqual(0, scheduler.Count, "fired one-shot is removed");
        }

        [Test]
        public void Scheduler_Repeat_FiresEveryInterval()
        {
            float now = 0f;
            var scheduler = new Scheduler(() => now);
            int fired = 0;
            scheduler.Repeat(1f, () => fired++);

            scheduler.Tick(now = 0.5f);
            Assert.AreEqual(0, fired, "first fire comes after one full interval");

            scheduler.Tick(now = 1.0f);
            scheduler.Tick(now = 2.0f);
            scheduler.Tick(now = 3.0f);
            Assert.AreEqual(3, fired, "one fire per interval boundary");
        }

        [Test]
        public void Scheduler_Cancel_PreventsFiring()
        {
            float now = 0f;
            var scheduler = new Scheduler(() => now);
            int fired = 0;
            SchedulerHandle handle = scheduler.Delay(1f, () => fired++);

            Assert.IsTrue(handle.IsActive);
            handle.Cancel();
            Assert.IsFalse(handle.IsActive, "cancelled handle is inactive");

            scheduler.Tick(now = 2f);
            Assert.AreEqual(0, fired, "cancelled timer never fires");
            Assert.AreEqual(0, scheduler.Count, "cancelled timer is removed on Tick");
        }

        [Test]
        public void Scheduler_UsesInjectedTimeSource_ForScheduling()
        {
            float now = 7f;
            var scheduler = new Scheduler(() => now);
            int fired = 0;
            scheduler.Delay(1f, () => fired++);

            // Deadline must be 7 + 1 = 8, proving the injected source was consulted.
            scheduler.Tick(7.5f);
            Assert.AreEqual(0, fired);
            scheduler.Tick(8f);
            Assert.AreEqual(1, fired);
        }

        [Test]
        public void Scheduler_Clear_RemovesEverything()
        {
            float now = 0f;
            var scheduler = new Scheduler(() => now);
            scheduler.Delay(1f, () => { });
            scheduler.Repeat(1f, () => { });

            scheduler.Clear();
            Assert.AreEqual(0, scheduler.Count);

            scheduler.Tick(10f); // must be a no-op without firing stale timers
        }

        [Test]
        public void Scheduler_ScheduleFromCallback_IsDeferredToNextTick()
        {
            float now = 0f;
            var scheduler = new Scheduler(() => now);
            int inner = 0;
            scheduler.Delay(1f, () => scheduler.Delay(0f, () => inner++));

            scheduler.Tick(now = 1f);
            Assert.AreEqual(0, inner, "timer scheduled from a callback must not fire in the same pump");

            scheduler.Tick(now = 1f);
            Assert.AreEqual(1, inner, "deferred timer fires on the next Tick");
        }
    }
}
