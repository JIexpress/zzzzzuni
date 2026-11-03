using System;
using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.View.Tests
{
    /// <summary>EditMode tests for the equal-power crossfade clock (TASK-A-M2-02).</summary>
    public class CrossfadeStateTests
    {
        [Test]
        public void Initial_WeightsAreA1_B0()
        {
            var state = new CrossfadeState();
            Assert.IsFalse(state.IsFading);
            Assert.AreEqual(1f, state.CurrentWeights().WeightA, 1e-5f);
            Assert.AreEqual(0f, state.CurrentWeights().WeightB, 1e-5f);
        }

        [Test]
        public void Midpoint_WeightsAreEqual_TargetB()
        {
            var state = new CrossfadeState();
            state.Start(1, 1f);
            (float a, float b) = state.Tick(0.5f);

            // Equal-power midpoint: cos(45°) on both tracks.
            float expected = (float)Math.Cos(Math.PI / 4.0);
            Assert.AreEqual(expected, a, 1e-4f, "track A at midpoint");
            Assert.AreEqual(expected, b, 1e-4f, "track B at midpoint");
        }

        [Test]
        public void Midpoint_WeightsAreEqual_TargetA()
        {
            var state = new CrossfadeState();
            state.Start(1, 0f); // snap to B first
            state.Start(0, 1f); // now fade back to A
            (float a, float b) = state.Tick(0.5f);

            float expected = (float)Math.Cos(Math.PI / 4.0);
            Assert.AreEqual(expected, a, 1e-4f, "track A at midpoint");
            Assert.AreEqual(expected, b, 1e-4f, "track B at midpoint");
        }

        [Test]
        public void FullFade_CompletesAtTarget()
        {
            var state = new CrossfadeState();
            state.Start(1, 2f);

            (float a, float b) = state.Tick(1f);
            Assert.IsTrue(state.IsFading, "fade must still run at half the duration");

            (a, b) = state.Tick(1f);
            Assert.IsFalse(state.IsFading, "fade must complete after the full duration");
            Assert.AreEqual(0f, a, 1e-5f, "track A faded out");
            Assert.AreEqual(1f, b, 1e-5f, "track B faded in");
        }

        [Test]
        public void TickBeyondDuration_ClampsToCompleted()
        {
            var state = new CrossfadeState();
            state.Start(1, 1f);
            state.Tick(5f);
            (float a, float b) = state.Tick(5f);
            Assert.AreEqual(0f, a, 1e-5f);
            Assert.AreEqual(1f, b, 1e-5f);
        }

        [Test]
        public void ZeroDuration_SnapsImmediately()
        {
            var state = new CrossfadeState();
            state.Start(1, 0f);
            Assert.IsFalse(state.IsFading);
            (float a, float b) = state.CurrentWeights();
            Assert.AreEqual(0f, a, 1e-5f);
            Assert.AreEqual(1f, b, 1e-5f);
        }

        [Test]
        public void NegativeDuration_SnapsImmediately()
        {
            var state = new CrossfadeState();
            state.Start(1, -1f);
            Assert.IsFalse(state.IsFading);
            Assert.AreEqual(0f, state.CurrentWeights().WeightA, 1e-5f);
        }

        [Test]
        public void StartTowardCurrentTarget_WhenSettled_IsNoOp()
        {
            var state = new CrossfadeState();
            state.Start(1, 1f);
            state.Tick(1f); // completed: settled on B

            state.Start(1, 1f);
            Assert.IsFalse(state.IsFading, "fading to the settled target must be a no-op");
            (float a, float b) = state.CurrentWeights();
            Assert.AreEqual(0f, a, 1e-5f);
            Assert.AreEqual(1f, b, 1e-5f);
        }

        [Test]
        public void EqualPower_SumOfSquaresIsOneThroughout()
        {
            var state = new CrossfadeState();
            state.Start(1, 1f);

            for (int i = 0; i <= 10; i++)
            {
                (float a, float b) = state.Tick(0.1f);
                Assert.AreEqual(1f, a * a + b * b, 1e-4f,
                    $"equal-power invariant broken at t={i * 0.1f:0.0}");
            }
        }

        [Test]
        public void Start_InvalidTarget_Throws()
        {
            var state = new CrossfadeState();
            Assert.Throws<ArgumentOutOfRangeException>(() => state.Start(2, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.Start(-1, 1f));
        }
    }
}
