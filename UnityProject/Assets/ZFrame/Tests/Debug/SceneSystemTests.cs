using NUnit.Framework;
using UnityEngine;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    public sealed class SceneProgressMathTests
    {
        [Test]
        public void Remap_ZeroToOne()
        {
            Assert.AreEqual(0f, SceneProgressMath.Remap(0f), 1e-5f);
            Assert.AreEqual(1f, SceneProgressMath.Remap(1f), 1e-5f);
        }

        [Test]
        public void Remap_AtGate_IsExactlyPointNine()
        {
            Assert.AreEqual(0.9f, SceneProgressMath.Remap(SceneProgressMath.ActivationGate), 1e-5f);
        }

        [Test]
        public void Remap_LinearBelowGate()
        {
            // Identity linear segment: f(gate * t) == 0.9 * t for t in [0,1].
            Assert.AreEqual(0.3f, SceneProgressMath.Remap(0.3f), 1e-5f);
            Assert.AreEqual(0.45f, SceneProgressMath.Remap(0.45f), 1e-5f);
            float a = SceneProgressMath.Remap(0.2f);
            float b = SceneProgressMath.Remap(0.5f);
            float c = SceneProgressMath.Remap(0.8f);
            Assert.AreEqual(b - a, c - b, 1e-4f, "slope must be constant below the gate");
        }

        [Test]
        public void Remap_EasedAboveGate()
        {
            // t = 0.25 into the gate->1 segment: smoothstep(0.25) = 0.15625.
            float expected = 0.9f + 0.1f * 0.15625f;
            Assert.AreEqual(expected, SceneProgressMath.Remap(0.925f), 1e-5f);

            // Midpoint is exactly halfway (smoothstep symmetry).
            Assert.AreEqual(0.95f, SceneProgressMath.Remap(0.95f), 1e-5f);
        }

        [Test]
        public void Remap_MonotonicNonRegressing_OverFullSweep()
        {
            float previous = SceneProgressMath.Remap(0f);
            for (int i = 1; i <= 100; i++)
            {
                float current = SceneProgressMath.Remap(i / 100f);
                Assert.GreaterOrEqual(current, previous - 1e-6f, $"regressed at raw={i / 100f}");
                previous = current;
            }
        }

        [Test]
        public void Remap_ClampsOutOfRangeInput()
        {
            Assert.AreEqual(0f, SceneProgressMath.Remap(-5f), 1e-5f);
            Assert.AreEqual(1f, SceneProgressMath.Remap(5f), 1e-5f);
        }

        [Test]
        public void EaseTail_EndpointsAndClamping()
        {
            Assert.AreEqual(0f, SceneProgressMath.EaseTail(0f), 1e-5f);
            Assert.AreEqual(1f, SceneProgressMath.EaseTail(1f), 1e-5f);
            Assert.AreEqual(0.5f, SceneProgressMath.EaseTail(0.5f), 1e-5f);
            Assert.AreEqual(0f, SceneProgressMath.EaseTail(-1f), 1e-5f, "negative time clamps to 0");
            Assert.AreEqual(1f, SceneProgressMath.EaseTail(2f), 1e-5f, "overflow time clamps to 1");
        }

        [Test]
        public void EaseTail_Monotonic()
        {
            float previous = SceneProgressMath.EaseTail(0f);
            for (int i = 1; i <= 20; i++)
            {
                float current = SceneProgressMath.EaseTail(i / 20f);
                Assert.GreaterOrEqual(current, previous, $"regressed at t={i / 20f}");
                previous = current;
            }
        }

        [Test]
        public void NonRegressing_NeverMovesBackwards()
        {
            Assert.AreEqual(0.7f, SceneProgressMath.NonRegressing(0.7f, 0.4f), 1e-5f);
            Assert.AreEqual(0.8f, SceneProgressMath.NonRegressing(0.7f, 0.8f), 1e-5f);
        }
    }

    public sealed class ScenePayloadTests
    {
        [Test]
        public void Set_TryPeek_ReadsWithoutConsuming()
        {
            var payload = new ScenePayload();
            payload.Set("level", "3");
            payload.Set("level", "4"); // overwrite

            Assert.IsTrue(payload.TryPeek("level", out string value));
            Assert.AreEqual("4", value);
            Assert.AreEqual(1, payload.Count, "peek must not consume");
            Assert.IsTrue(payload.TryPeek("level", out value));
            Assert.AreEqual("4", value);
        }

        [Test]
        public void ConsumePayload_ReturnsOnce_ThenRemoves()
        {
            var payload = new ScenePayload();
            payload.Set("from", "menu");

            string first = payload.ConsumePayload("from");
            string second = payload.ConsumePayload("from");

            Assert.AreEqual("menu", first);
            Assert.IsNull(second, "consume must remove the entry (read-once semantics)");
            Assert.AreEqual(0, payload.Count);
            Assert.IsTrue(payload.IsEmpty);
        }

        [Test]
        public void ConsumePayload_MissingKey_ReturnsNull()
        {
            var payload = new ScenePayload();

            Assert.IsNull(payload.ConsumePayload("nope"));
        }

        [Test]
        public void ConsumePayload_NullKey_IsSafe()
        {
            var payload = new ScenePayload();
            payload.Set("k", "v");

            Assert.IsNull(payload.ConsumePayload(null));
            Assert.AreEqual(1, payload.Count, "null key must not disturb existing entries");
        }

        [Test]
        public void Set_NullKey_Throws()
        {
            var payload = new ScenePayload();

            Assert.Throws<System.ArgumentNullException>(() => payload.Set(null, "v"));
        }

        [Test]
        public void InitialDictionary_IsCopied()
        {
            var source = new System.Collections.Generic.Dictionary<string, string> { ["hp"] = "100" };
            var payload = new ScenePayload(source);
            source["hp"] = "1"; // later mutation must not leak in

            Assert.IsTrue(payload.TryPeek("hp", out string value));
            Assert.AreEqual("100", value);
        }

        [Test]
        public void NullInitial_IsEmptyPayload()
        {
            var payload = new ScenePayload(null);

            Assert.IsTrue(payload.IsEmpty);
            Assert.IsNull(payload.ConsumePayload("anything"));
        }

        [Test]
        public void Clear_RemovesEverything()
        {
            var payload = new ScenePayload();
            payload.Set("a", "1");
            payload.Set("b", "2");

            payload.Clear();

            Assert.IsTrue(payload.IsEmpty);
            Assert.IsNull(payload.ConsumePayload("a"));
        }
    }

    public sealed class InputBlockerTests
    {
        [SetUp]
        public void SetUp()
        {
            InputBlocker.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            InputBlocker.Reset();
        }

        [Test]
        public void NotBlocking_ByDefault()
        {
            Assert.IsFalse(InputBlocker.Active);
            Assert.IsFalse(InputBlocker.IsAnyBlocking());
            Assert.AreEqual(0, InputBlocker.Count);
        }

        [Test]
        public void AcquireRelease_Paired_BlocksAndUnblocks()
        {
            InputBlocker.Acquire();

            Assert.IsTrue(InputBlocker.Active);
            Assert.IsTrue(InputBlocker.IsAnyBlocking());
            Assert.AreEqual(1, InputBlocker.Count);

            InputBlocker.Release();

            Assert.IsFalse(InputBlocker.Active);
            Assert.AreEqual(0, InputBlocker.Count);
        }

        [Test]
        public void NestedAcquires_StackAndReleaseInnermostLast()
        {
            InputBlocker.Acquire();
            InputBlocker.Acquire();

            Assert.AreEqual(2, InputBlocker.Count);
            Assert.IsTrue(InputBlocker.IsAnyBlocking(), "still blocked while one acquire remains");

            InputBlocker.Release();
            Assert.IsTrue(InputBlocker.IsAnyBlocking(), "nested count must keep blocking");

            InputBlocker.Release();
            Assert.IsFalse(InputBlocker.IsAnyBlocking());
        }

        [Test]
        public void AcquireToken_DisposeReleases()
        {
            using (InputBlocker.Acquire())
            {
                Assert.IsTrue(InputBlocker.Active);
            }

            Assert.IsFalse(InputBlocker.Active);
        }

        [Test]
        public void OverRelease_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(InputBlocker.Release);

            InputBlocker.Acquire();
            InputBlocker.Release();
            Assert.Throws<System.InvalidOperationException>(InputBlocker.Release);
        }
    }
}
