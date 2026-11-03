using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the pure Volume crossfade clock (TASK-A-R9-08
    /// design point 5): curve endpoints, the equal-power invariant, the
    /// midpoint, completion settling (停稳), Abort landing on the target, the
    /// start-phase re-base seam and the degenerate-duration snap. The core is
    /// pure C# — no engine objects involved.
    /// </summary>
    public sealed class VolumeCrossfadeStateTests
    {
        private const float HalfSqrt2 = 0.70710678f;

        // ---- endpoints ---------------------------------------------------------

        [Test]
        public void Start_Beginning_IsAtFromEndpoint()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);

            Assert.That(state.IsFading, Is.True);
            Assert.That(state.Progress01, Is.EqualTo(0f).Within(1e-6f));
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(1f).Within(1e-6f), "the fade starts with the from slot fully on");
            Assert.That(wTo, Is.EqualTo(0f).Within(1e-6f), "the fade starts with the to slot silent");
        }

        [Test]
        public void Tick_PastDuration_LandsOnTargetEndpoint_AndStaysSettled()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);

            (float wFrom, float wTo) = state.Tick(2f); // far past the end in one step

            Assert.That(state.IsFading, Is.False, "the fade must complete");
            Assert.That(wFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(1f).Within(1e-6f), "completion settles on the target (briefing: 停在 (0,1))");

            // 停稳: further ticks change nothing, forever.
            for (int i = 0; i < 5; i++)
            {
                (float settledFrom, float settledTo) = state.Tick(0.1f);
                Assert.That(settledFrom, Is.EqualTo(0f).Within(1e-6f));
                Assert.That(settledTo, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(state.IsFading, Is.False);
                Assert.That(state.Progress01, Is.EqualTo(1f).Within(1e-6f));
            }
        }

        // ---- midpoint + equal-power invariant -----------------------------------

        [Test]
        public void Tick_Midpoint_BothWeightsAreCos45()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);
            state.Tick(0.5f);

            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(HalfSqrt2).Within(1e-4f), "midpoint: both slots carry cos(45°)");
            Assert.That(wTo, Is.EqualTo(HalfSqrt2).Within(1e-4f));
        }

        [Test]
        public void Curve_KeepsEqualPowerInvariant_AcrossTheWholeFade()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);

            float previousProgress = -1f;
            for (int i = 1; i <= 20; i++)
            {
                (float wFrom, float wTo) = state.Tick(0.05f);
                Assert.That(wFrom * wFrom + wTo * wTo, Is.EqualTo(1f).Within(1e-5f),
                    $"a²+b² ≡ 1 violated at step {i} (the equal-power invariant)");

                Assert.That(state.Progress01, Is.GreaterThanOrEqualTo(previousProgress),
                    "progress must advance monotonically");
                previousProgress = state.Progress01;
            }
        }

        [Test]
        public void Curve_WeightsMoveMonotonicallyTowardTheTarget()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);

            float previousTo = 0f;
            float previousFrom = 1f;
            for (int i = 0; i < 20; i++)
            {
                (float wFrom, float wTo) = state.Tick(0.05f);
                Assert.That(wTo, Is.GreaterThanOrEqualTo(previousTo - 1e-6f), "wTo grows monotonically");
                Assert.That(wFrom, Is.LessThanOrEqualTo(previousFrom + 1e-6f), "wFrom shrinks monotonically");
                previousTo = wTo;
                previousFrom = wFrom;
            }
        }

        // ---- abort ----------------------------------------------------------------

        [Test]
        public void Abort_MidFade_LandsOnTargetImmediately_AndStaysThere()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);
            state.Tick(0.25f);
            Assert.That(state.IsFading, Is.True, "precondition: mid-fade");
            Assert.That(state.CurrentWeights.WeightFrom, Is.GreaterThan(0.5f), "precondition: not at the endpoint");

            state.Abort();

            Assert.That(state.IsFading, Is.False, "abort settles immediately");
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(1f).Within(1e-6f), "abort commits the TARGET (the intent already switched)");

            (float settledFrom, float settledTo) = state.Tick(0.1f);
            Assert.That(settledFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(settledTo, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Abort_WhenSettled_IsHarmless()
        {
            var state = new VolumeCrossfadeState();
            state.Abort(); // never started

            Assert.That(state.IsFading, Is.False);
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(1f).Within(1e-6f));
        }

        // ---- degenerate durations + start phase -----------------------------------

        [Test]
        public void Start_ZeroOrNegativeDuration_SnapsToTarget()
        {
            var state = new VolumeCrossfadeState();
            state.Start(0f);
            Assert.That(state.IsFading, Is.False);
            (float zeroFrom, float zeroTo) = state.CurrentWeights;
            Assert.That(zeroFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(zeroTo, Is.EqualTo(1f).Within(1e-6f));

            state.Start(-1f);
            Assert.That(state.IsFading, Is.False);
            (float negativeFrom, float negativeTo) = state.CurrentWeights;
            Assert.That(negativeFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(negativeTo, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Start_StartPhase_ReBasesTheClockWithoutATick()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f, 0.5f); // begin mid-curve (interrupt re-base seam)

            Assert.That(state.IsFading, Is.True);
            Assert.That(state.Progress01, Is.EqualTo(0.5f).Within(1e-6f));
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(HalfSqrt2).Within(1e-4f),
                "phase 0.5 == the midpoint of the same curve");
            Assert.That(wTo, Is.EqualTo(HalfSqrt2).Within(1e-4f));

            // The remaining half takes exactly the remaining time.
            (float endFrom, float endTo) = state.Tick(0.5f);
            Assert.That(state.IsFading, Is.False);
            Assert.That(endFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(endTo, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Start_PhasePastOne_SettlesImmediately()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f, 1.5f);

            Assert.That(state.IsFading, Is.False);
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void Start_NegativePhase_ClampsToZero()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f, -0.5f);

            Assert.That(state.Progress01, Is.EqualTo(0f).Within(1e-6f));
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void Restart_WhileFading_RestartsFromTheRequestedPhase()
        {
            var state = new VolumeCrossfadeState();
            state.Start(1f);
            state.Tick(0.5f);
            Assert.That(state.Progress01, Is.EqualTo(0.5f).Within(1e-6f));

            state.Start(2f, 0f); // fresh fade, new duration

            Assert.That(state.IsFading, Is.True);
            Assert.That(state.Progress01, Is.EqualTo(0f).Within(1e-6f));
            (float wFrom, float wTo) = state.CurrentWeights;
            Assert.That(wFrom, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(wTo, Is.EqualTo(0f).Within(1e-6f));

            (float endFrom, float endTo) = state.Tick(2f);
            Assert.That(state.IsFading, Is.False);
            Assert.That(endFrom, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(endTo, Is.EqualTo(1f).Within(1e-6f));
        }
    }
}
