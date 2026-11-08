using System;
using System.Collections.Generic;
using Game.Domain.AI.Coordination;
using NUnit.Framework;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// FormationSlot tests (TASK-A-AI-02) — pure ring math, hand-computed:
    /// 4 slots around (10,20) at radius 2 land exactly on (12,20)/(10,22)/
    /// (8,20)/(10,18); the uniqueness invariant (<see cref="FormationSlot.AreDistinct"/>)
    /// is asserted directly, including the negative case.
    /// </summary>
    public sealed class FormationSlotTests
    {
        [Test]
        public void FourSlots_LandOnHandComputedRingPoints()
        {
            var leader = new Vec2(10f, 20f);
            Vec2 slot0 = FormationSlot.Calculate(leader, 0, 4, 2f);
            Vec2 slot1 = FormationSlot.Calculate(leader, 1, 4, 2f);
            Vec2 slot2 = FormationSlot.Calculate(leader, 2, 4, 2f);
            Vec2 slot3 = FormationSlot.Calculate(leader, 3, 4, 2f);

            Assert.That(slot0.X, Is.EqualTo(12f).Within(1e-4f), "槽 0：角 0 → +X");
            Assert.That(slot0.Y, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(slot1.X, Is.EqualTo(10f).Within(1e-4f), "槽 1：角 π/2 → +Y");
            Assert.That(slot1.Y, Is.EqualTo(22f).Within(1e-4f));
            Assert.That(slot2.X, Is.EqualTo(8f).Within(1e-4f), "槽 2：角 π → −X");
            Assert.That(slot2.Y, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(slot3.X, Is.EqualTo(10f).Within(1e-4f), "槽 3：角 3π/2 → −Y");
            Assert.That(slot3.Y, Is.EqualTo(18f).Within(1e-4f));
        }

        [Test]
        public void TwoSlots_AreOppositeOnTheRing()
        {
            var leader = new Vec2(0f, 0f);
            Vec2 first = FormationSlot.Calculate(leader, 0, 2, 3f);
            Vec2 second = FormationSlot.Calculate(leader, 1, 2, 3f);

            Assert.That(first.X, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(second.X, Is.EqualTo(-3f).Within(1e-4f), "两人编队：对称环位");
            Assert.That(Vec2.Distance(first, second), Is.EqualTo(6f).Within(1e-4f));
        }

        [Test]
        public void StartAngleRotatesTheRing()
        {
            var leader = new Vec2(0f, 0f);
            Vec2 slot = FormationSlot.Calculate(leader, 0, 1, 2f, (float)(Math.PI / 2f));

            Assert.That(slot.X, Is.EqualTo(0f).Within(1e-4f), "起始角 π/2 把单人槽转到 +Y");
            Assert.That(slot.Y, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void RingPositions_ArePairwiseDistinct()
        {
            var leader = new Vec2(5f, 5f);
            var positions = new List<Vec2>();
            for (int i = 0; i < 6; i++)
            {
                positions.Add(FormationSlot.Calculate(leader, i, 6, 2.5f));
            }

            Assert.IsTrue(FormationSlot.AreDistinct(positions), "六个环形槽两两相异（槽唯一不变量）");
        }

        [Test]
        public void AreDistinct_CatchesCollision()
        {
            var collision = new List<Vec2> { new Vec2(1f, 1f), new Vec2(2f, 2f), new Vec2(1f, 1f) };
            Assert.IsFalse(FormationSlot.AreDistinct(collision), "两个重合位置必须被判为冲突");

            var nearButDistinct = new List<Vec2> { new Vec2(1f, 1f), new Vec2(2f, 2f) };
            Assert.IsTrue(FormationSlot.AreDistinct(nearButDistinct));
        }

        [Test]
        public void Calculate_RejectsNonsenseInputs()
        {
            var leader = new Vec2(0f, 0f);
            Assert.Throws<ArgumentOutOfRangeException>(() => FormationSlot.Calculate(leader, 0, 0, 2f), "slotCount 0");
            Assert.Throws<ArgumentOutOfRangeException>(() => FormationSlot.Calculate(leader, -1, 4, 2f), "负索引");
            Assert.Throws<ArgumentOutOfRangeException>(() => FormationSlot.Calculate(leader, 4, 4, 2f), "索引越界");
            Assert.Throws<ArgumentOutOfRangeException>(() => FormationSlot.Calculate(leader, 0, 4, -1f), "负半径");
            Assert.Throws<ArgumentOutOfRangeException>(() => FormationSlot.Calculate(leader, 0, 4, float.NaN), "NaN 半径");
        }
    }
}
