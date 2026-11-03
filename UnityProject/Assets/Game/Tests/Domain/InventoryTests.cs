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

        // ---- TASK-A-R9-12: the rollback primitive behind pickup undo ----

        [Test]
        public void TryRemove_TakesPartialStackOut()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 5);

            Assert.IsTrue(inventory.TryRemove("gold", 2));
            Assert.AreEqual(3, inventory.Count("gold"));
            Assert.AreEqual(1, inventory.DistinctCount);
        }

        [Test]
        public void TryRemove_ToZero_RemovesTheKey_ExactlyLikePreAdd()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 3);
            inventory.Add("wood", 1);

            Assert.IsTrue(inventory.TryRemove("gold", 3));
            Assert.AreEqual(0, inventory.Count("gold"));
            Assert.AreEqual(1, inventory.DistinctCount, "only wood remains — undo can restore the exact pre-add shape");
            Assert.IsFalse(inventory.Items.ContainsKey("gold"));
        }

        [Test]
        public void TryRemove_MoreThanHeld_ReturnsFalse_WithoutMutating()
        {
            var inventory = new Inventory();
            inventory.Add("gold", 3);

            Assert.IsFalse(inventory.TryRemove("gold", 4));
            Assert.IsFalse(inventory.TryRemove("potion", 1));
            Assert.AreEqual(3, inventory.Count("gold"), "nothing was taken out");
            Assert.AreEqual(3, inventory.TotalCount, "no phantom negative stack");
        }

        [Test]
        public void TryRemove_RejectsInvalidArguments()
        {
            var inventory = new Inventory();

            Assert.Throws<ArgumentException>(() => inventory.TryRemove(string.Empty, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryRemove("gold", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryRemove("gold", -1));
        }
    }
}
