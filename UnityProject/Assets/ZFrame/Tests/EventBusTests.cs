using System;
using NUnit.Framework;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>EditMode tests for <see cref="EventBus"/>.</summary>
    public class EventBusTests
    {
        private struct DamageEvent
        {
            public int Amount;
            public string Source;
        }

        private struct HealEvent
        {
            public int Amount;
        }

        [TearDown]
        public void TearDown()
        {
            // Drain any leftover queued events so tests stay isolated.
            EventBus.FlushQueued();
        }

        [Test]
        public void Subscribe_And_Fire_DeliversPayload()
        {
            int received = 0;
            var payload = default(DamageEvent);
            using (EventBus.Subscribe<DamageEvent>(e => { received++; payload = e; }))
            {
                EventBus.Fire(new DamageEvent { Amount = 12, Source = "orc" });
            }

            Assert.AreEqual(1, received, "handler should fire exactly once");
            Assert.AreEqual(12, payload.Amount, "payload Amount must survive dispatch");
            Assert.AreEqual("orc", payload.Source, "payload Source must survive dispatch");
        }

        [Test]
        public void TokenDispose_StopsDelivery()
        {
            int received = 0;
            IDisposable token = EventBus.Subscribe<DamageEvent>(_ => received++);
            token.Dispose();

            EventBus.Fire(new DamageEvent { Amount = 1, Source = null });

            Assert.AreEqual(0, received, "no delivery after token dispose");
            Assert.AreEqual(0, EventBus.SubscriberCount<DamageEvent>(), "dictionary entry must be removed after last unsubscribe");
        }

        [Test]
        public void EventScope_DisposeBulkUnsubscribes()
        {
            int a = 0, b = 0;
            using (EventBus.OpenScope())
            {
                EventBus.Subscribe<DamageEvent>(_ => a++);
                EventBus.Subscribe<HealEvent>(_ => b++);
                Assert.AreEqual(1, EventBus.SubscriberCount<DamageEvent>(), "scope must not affect live subscription");
            }

            Assert.AreEqual(0, EventBus.SubscriberCount<DamageEvent>(), "scope dispose must unsubscribe DamageEvent");
            Assert.AreEqual(0, EventBus.SubscriberCount<HealEvent>(), "scope dispose must unsubscribe HealEvent");

            EventBus.Fire(new DamageEvent { Amount = 1, Source = null });
            EventBus.Fire(new HealEvent());
            Assert.AreEqual(0, a, "DamageEvent handler must not run after scope dispose");
            Assert.AreEqual(0, b, "HealEvent handler must not run after scope dispose");
        }

        [Test]
        public void FireQueued_DelaysUntilFlush()
        {
            int received = 0;
            using (EventBus.Subscribe<DamageEvent>(e => received += e.Amount))
            {
                EventBus.FireQueued(new DamageEvent { Amount = 5, Source = null });
                Assert.AreEqual(0, received, "no delivery before FlushQueued");

                EventBus.FlushQueued();
                Assert.AreEqual(5, received, "delivery happens on FlushQueued");

                EventBus.FlushQueued();
                Assert.AreEqual(5, received, "flush must not re-dispatch drained events");
            }
        }

        [Test]
        public void MultipleSubscribers_AllReceive()
        {
            int a = 0, b = 0;
            using (EventBus.Subscribe<DamageEvent>(_ => a++))
            using (EventBus.Subscribe<DamageEvent>(_ => b++))
            {
                Assert.AreEqual(2, EventBus.SubscriberCount<DamageEvent>());
                EventBus.Fire(new DamageEvent { Amount = 1, Source = null });
            }

            Assert.AreEqual(1, a, "first subscriber must receive");
            Assert.AreEqual(1, b, "second subscriber must receive");
        }

        [Test]
        public void DifferentEventTypes_AreIsolated()
        {
            int damage = 0, heal = 0;
            using (EventBus.Subscribe<DamageEvent>(_ => damage++))
            using (EventBus.Subscribe<HealEvent>(_ => heal++))
            {
                EventBus.Fire(new HealEvent { Amount = 3 });
            }

            Assert.AreEqual(0, damage, "DamageEvent must not fire for HealEvent");
            Assert.AreEqual(1, heal, "HealEvent handler must fire");
        }

        [Test]
        public void SubscriberCount_TracksSubscribeAndDispose()
        {
            Assert.AreEqual(0, EventBus.SubscriberCount<HealEvent>(), "baseline count is zero");
            IDisposable t1 = EventBus.Subscribe<HealEvent>(_ => { });
            IDisposable t2 = EventBus.Subscribe<HealEvent>(_ => { });
            Assert.AreEqual(2, EventBus.SubscriberCount<HealEvent>(), "two live subscribers");

            t1.Dispose();
            Assert.AreEqual(1, EventBus.SubscriberCount<HealEvent>(), "one left after first dispose");

            t2.Dispose();
            Assert.AreEqual(0, EventBus.SubscriberCount<HealEvent>(), "zero after last dispose");
        }

        [Test]
        public void UnsubscribeDuringFire_IsSafe()
        {
            int secondReceived = 0;
            IDisposable second = null;
            IDisposable first = EventBus.Subscribe<DamageEvent>(_ => second.Dispose());
            second = EventBus.Subscribe<DamageEvent>(_ => secondReceived++);

            EventBus.Fire(new DamageEvent { Amount = 1, Source = null });
            EventBus.Fire(new DamageEvent { Amount = 1, Source = null });
            first.Dispose();

            Assert.AreEqual(1, secondReceived, "snapshot dispatch must not be corrupted by in-flight unsubscribe");
            Assert.AreEqual(0, EventBus.SubscriberCount<DamageEvent>());
        }
    }
}
