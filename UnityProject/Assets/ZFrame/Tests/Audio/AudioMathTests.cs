using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.View.Tests
{
    /// <summary>EditMode tests for the pure volume→dB mapping (TASK-A-M2-02).</summary>
    public class AudioMathTests
    {
        [Test]
        public void LinearToDb_ZeroIsSilence()
        {
            Assert.AreEqual(AudioMath.MinDb, AudioMath.LinearToDb(0f), 1e-4f);
        }

        [Test]
        public void LinearToDb_FullIsZeroDb()
        {
            Assert.AreEqual(AudioMath.MaxDb, AudioMath.LinearToDb(1f), 1e-4f);
        }

        [Test]
        public void LinearToDb_NegativeClampsToSilence()
        {
            Assert.AreEqual(AudioMath.MinDb, AudioMath.LinearToDb(-0.5f), 1e-4f);
            Assert.AreEqual(AudioMath.MinDb, AudioMath.LinearToDb(float.NegativeInfinity), 1e-4f);
        }

        [Test]
        public void LinearToDb_AboveOneClampsToFull()
        {
            Assert.AreEqual(AudioMath.MaxDb, AudioMath.LinearToDb(1.5f), 1e-4f);
            Assert.AreEqual(AudioMath.MaxDb, AudioMath.LinearToDb(1000f), 1e-4f);
        }

        [Test]
        public void LinearToDb_HalfIsAboutMinus6Db()
        {
            // 20*log10(0.5) = -6.0206 dB.
            Assert.AreEqual(-6.0206f, AudioMath.LinearToDb(0.5f), 1e-3f);
        }

        [Test]
        public void LinearToDb_IsMonotonicOverUnitRange()
        {
            float previous = AudioMath.LinearToDb(0f);
            for (int i = 1; i <= 100; i++)
            {
                float current = AudioMath.LinearToDb(i / 100f);
                Assert.GreaterOrEqual(current, previous - 1e-5f,
                    $"dB curve must be non-decreasing; regression at v={i / 100f}");
                previous = current;
            }
        }

        [Test]
        public void DbToLinear_MinDbIsZero()
        {
            Assert.AreEqual(0f, AudioMath.DbToLinear(AudioMath.MinDb), 1e-4f);
        }

        [Test]
        public void DbToLinear_AboveMaxClampsToOne()
        {
            Assert.AreEqual(1f, AudioMath.DbToLinear(6f), 1e-4f);
        }

        [Test]
        public void RoundTrip_LinearThroughDb_IsStable()
        {
            foreach (float v in new[] { 0.05f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
            {
                Assert.AreEqual(v, AudioMath.DbToLinear(AudioMath.LinearToDb(v)), 1e-4f,
                    $"round trip must be stable at v={v}");
            }
        }
    }
}
