using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.View.Tests
{
    /// <summary>EditMode tests for the per-frame SFX limiter (TASK-A-M2-02).</summary>
    public class SfxLimiterTests
    {
        [Test]
        public void SameIdSameFrame_PlayedOnce()
        {
            var limiter = new SfxLimiter(4);
            limiter.BeginFrame();

            Assert.IsTrue(limiter.TryPlay("hit"), "first play of a frame passes");
            Assert.IsFalse(limiter.TryPlay("hit"), "same id in the same frame must be deduplicated");
        }

        [Test]
        public void DifferentIdsSameFrame_BothPass()
        {
            var limiter = new SfxLimiter(4);
            limiter.BeginFrame();

            Assert.IsTrue(limiter.TryPlay("hit"));
            Assert.IsTrue(limiter.TryPlay("miss"));
        }

        [Test]
        public void FrameBudget_RejectsWhenExhausted()
        {
            var limiter = new SfxLimiter(2);
            limiter.BeginFrame();

            Assert.IsTrue(limiter.TryPlay("a"));
            Assert.IsTrue(limiter.TryPlay("b"));
            Assert.IsFalse(limiter.TryPlay("c"), "budget exhausted");
            Assert.AreEqual(2, limiter.PassedThisFrame);
        }

        [Test]
        public void BeginFrame_ResetsDedupAndBudget()
        {
            var limiter = new SfxLimiter(2);
            limiter.BeginFrame();
            Assert.IsTrue(limiter.TryPlay("a"));
            Assert.IsFalse(limiter.TryPlay("a"), "same frame: id is deduplicated");

            limiter.BeginFrame();

            Assert.IsTrue(limiter.TryPlay("a"), "dedup must reset on the next frame");
            Assert.IsTrue(limiter.TryPlay("b"), "budget must reset on the next frame");
            Assert.IsFalse(limiter.TryPlay("c"), "budget re-exhausts in the new frame");
        }

        [Test]
        public void MaxPerFrame_MinimumIsOne()
        {
            Assert.AreEqual(1, new SfxLimiter(0).MaxPerFrame);
            Assert.AreEqual(1, new SfxLimiter(-5).MaxPerFrame);
            Assert.AreEqual(4, new SfxLimiter(4).MaxPerFrame);
        }

        [Test]
        public void TryPlay_BeforeBeginFrame_StillGatedByBudget()
        {
            // Defensive contract: the limiter starts with a fresh (empty)
            // frame, so plays without an explicit BeginFrame are budgeted.
            var limiter = new SfxLimiter(1);
            Assert.IsTrue(limiter.TryPlay("a"));
            Assert.IsFalse(limiter.TryPlay("b"));
        }
    }
}
