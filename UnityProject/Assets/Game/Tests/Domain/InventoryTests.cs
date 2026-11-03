using System;
using Game.Domain.Interaction;
using NUnit.Framework;

namespace Game.Domain.Tests
{
    /// <summary>Flat item-count store used by the pickup template (TASK-A-M4-01).</summary>
    public sealed class InventoryTests
    {
        [Test]
        public void Add_ReturnsRunningTotal()
        {
            var inventory = new Inventory();

            Assert.AreEqual(3, inventory.Add("gold", 3));
            Assert.AreEqual(5, inventory.Add("gold", 2));
            Assert.AreEqual(5, inventory.Count("gold"));
        }

        [Test]
        public void Count_UnknownItem_IsZero()
        {
            var inventory = new Inventory();

            Assert.AreEqual(0, inventory.Count("gold"));
            Assert.AreEqual(0, inventory.Count(null));
        }

        [Test]
        public void TotalAndDistinct_TrackAllStacks()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 3);
            inventory.Add("potion", 2);

            Assert.AreEqual(5, inventory.TotalCount);
            Assert.AreEqual(2, inventory.DistinctCount);
            Assert.AreEqual(2, inventory.Items.Count);
        }

        [Test]
        public void TryGetCount_ReportsPresence()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 3);

            Assert.IsTrue(inventory.TryGetCount("gold", out int gold));
            Assert.AreEqual(3, gold);
            Assert.IsFalse(inventory.TryGetCount("potion", out int potion));
            Assert.AreEqual(0, potion);
        }

        [Test]
        public void Clear_EmptiesEverything()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 3);

            inventory.Clear();

            Assert.AreEqual(0, inventory.TotalCount);
            Assert.AreEqual(0, inventory.DistinctCount);
        }

        [Test]
        public void Add_RejectsInvalidArguments()
        {
            var inventory = new Inventory();

            Assert.Throws<ArgumentException>(() => inventory.Add(string.Empty, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => inventory.Add("gold", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => inventory.Add("gold", -1));
        }
    }
}
