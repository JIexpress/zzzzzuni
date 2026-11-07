using System;
using Game.Domain.AI.Utility;
using NUnit.Framework;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Pure score-curve tests (TASK-A-TECH-01): the curves are the testable
    /// heart of utility AI — endpoint behaviour pinned and monotonicity proven
    /// by sweep, so any composed desire function built on them stays
    /// predictable. No state, no clock: these are total functions.
    /// </summary>
    public sealed class UtilityCurvesTests
    {
        private const float Epsilon = 1e-4f;

        [Test]
        public void Clamp01_PinsEndpoints()
        {
            Assert.AreEqual(0f, UtilityCurves.Clamp01(-5f));
            Assert.AreEqual(0f, UtilityCurves.Clamp01(0f));
            Assert.AreEqual(0.25f, UtilityCurves.Clamp01(0.25f));
            Assert.AreEqual(1f, UtilityCurves.Clamp01(1f));
            Assert.AreEqual(1f, UtilityCurves.Clamp01(42f));
        }

        [Test]
        public void Ramp_PinsEndpoints_AndMidpoint()
        {
            Assert.AreEqual(0f, UtilityCurves.Ramp(0f, 0.35f, 0.9f), Epsilon);
            Assert.AreEqual(0f, UtilityCurves.Ramp(0.35f, 0.35f, 0.9f), Epsilon, "下端点含边界");
            Assert.AreEqual(0.5f, UtilityCurves.Ramp(0.625f, 0.35f, 0.9f), Epsilon, "区间中点=0.5");
            Assert.AreEqual(1f, UtilityCurves.Ramp(0.9f, 0.35f, 0.9f), Epsilon, "上端点含边界");
            Assert.AreEqual(1f, UtilityCurves.Ramp(1f, 0.35f, 0.9f), Epsilon);
        }

        [Test]
        public void Ramp_IsMonotonicIncreasing()
        {
            float previous = 0f;
            for (int i = 0; i <= 100; i++)
            {
                float value = UtilityCurves.Ramp(i / 100f, 0.35f, 0.9f);
                Assert.GreaterOrEqual(value, previous - Epsilon, $"x={i / 100f:0.00} 处必须单调不减");
                previous = value;
            }
        }

        [Test]
        public void Ramp_InvertedRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => UtilityCurves.Ramp(0.5f, 0.9f, 0.35f));
            Assert.Throws<ArgumentOutOfRangeException>(() => UtilityCurves.Ramp(0.5f, 1f, 1f));
        }

        [Test]
        public void Diminishing_SaturatesAtGivenValue()
        {
            Assert.AreEqual(0f, UtilityCurves.Diminishing(0f, 5f));
            Assert.AreEqual(0.2f, UtilityCurves.Diminishing(1f, 5f), Epsilon);
            Assert.AreEqual(1f, UtilityCurves.Diminishing(5f, 5f), "饱和点=1");
            Assert.AreEqual(1f, UtilityCurves.Diminishing(50f, 5f), "超饱和钳到 1");
        }

        [Test]
        public void Diminishing_IsMonotonicIncreasing_AndValidates()
        {
            float previous = 0f;
            for (int i = 0; i <= 20; i++)
            {
                float value = UtilityCurves.Diminishing(i, 5f);
                Assert.GreaterOrEqual(value, previous - Epsilon, $"inventory={i} 处必须单调不减");
                previous = value;
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => UtilityCurves.Diminishing(1f, 0f));
        }

        [Test]
        public void Fall_IsInverseOfRamp()
        {
            Assert.AreEqual(1f, UtilityCurves.Fall(0f, 1.5f, 8f), Epsilon);
            Assert.AreEqual(0f, UtilityCurves.Fall(8f, 1.5f, 8f), Epsilon);
            Assert.AreEqual(0.5f, UtilityCurves.Fall(4.75f, 1.5f, 8f), Epsilon, "区间中点=0.5");
            Assert.Less(UtilityCurves.Fall(5f, 1.5f, 8f), UtilityCurves.Fall(2f, 1.5f, 8f),
                "距离越远掉得越多：Fall 单调递减");
        }
    }
}
