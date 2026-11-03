using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Engine-free 2D vector used by the gameplay layer (TASK-A-M4-01) — the
    /// logic layer must not depend on UnityEngine.Vector2, so this type needs
    /// its own coverage.
    /// </summary>
    public sealed class Vec2Tests
    {
        [Test]
        public void Distance_IsEuclidean()
        {
            Assert.AreEqual(5f, Vec2.Distance(new Vec2(0f, 0f), new Vec2(3f, 4f)), 1e-5f);
        }

        [Test]
        public void Normalized_ReturnsUnitVector()
        {
            Vec2 unit = new Vec2(3f, 4f).Normalized;

            Assert.AreEqual(1f, unit.Magnitude, 1e-5f);
            Assert.AreEqual(0.6f, unit.X, 1e-5f);
            Assert.AreEqual(0.8f, unit.Y, 1e-5f);
        }

        [Test]
        public void Normalized_OfZero_IsZero()
        {
            Assert.AreEqual(Vec2.Zero, Vec2.Zero.Normalized);
        }

        [Test]
        public void MoveTowards_StopsExactlyOnTarget()
        {
            Vec2 start = new Vec2(0f, 0f);
            Vec2 target = new Vec2(1f, 0f);

            Assert.AreEqual(target, Vec2.MoveTowards(start, target, 5f), "不得越过目标点");
        }

        [Test]
        public void MoveTowards_MovesAtMostMaxDelta()
        {
            Vec2 moved = Vec2.MoveTowards(Vec2.Zero, new Vec2(10f, 0f), 2f);

            Assert.AreEqual(2f, moved.X, 1e-5f);
            Assert.AreEqual(0f, moved.Y, 1e-5f);
        }

        [Test]
        public void MoveTowards_NonPositiveDelta_DoesNotMove()
        {
            Vec2 current = new Vec2(3f, 3f);

            Assert.AreEqual(current, Vec2.MoveTowards(current, new Vec2(9f, 9f), 0f));
            Assert.AreEqual(current, Vec2.MoveTowards(current, new Vec2(9f, 9f), -1f));
        }

        [Test]
        public void Operators_AndEquality_WorkComponentWise()
        {
            var a = new Vec2(1f, 2f);
            var b = new Vec2(3f, 5f);

            Assert.AreEqual(new Vec2(4f, 7f), a + b);
            Assert.AreEqual(new Vec2(-2f, -3f), a - b);
            Assert.AreEqual(new Vec2(2f, 4f), a * 2f);
            Assert.AreEqual(new Vec2(2f, 4f), 2f * a);
            Assert.IsTrue(new Vec2(1f, 2f) == a);
            Assert.IsTrue(a != b);
            Assert.IsTrue(a.Equals((object)new Vec2(1f, 2f)));
            Assert.AreEqual(new Vec2(1f, 2f).GetHashCode(), a.GetHashCode());
        }

        [Test]
        public void SqrMagnitude_IsSquaredLength()
        {
            Assert.AreEqual(25f, new Vec2(3f, 4f).SqrMagnitude, 1e-5f);
        }
    }
}
