using System;
using Game.Domain.AI.Utility;
using NUnit.Framework;

namespace Game.Domain.Tests
{
    /// <summary>
    /// NPCWorldState tests (TASK-A-TECH-01): typed key-value semantics —
    /// roundtrip, fail-closed wrong-type reads, fallback readers, the well-
    /// known demo keys and lifecycle. The store is what score functions read,
    /// so "same input" starts here.
    /// </summary>
    public sealed class NPCWorldStateTests
    {
        [Test]
        public void Set_TryGet_Roundtrip()
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, 0.75f);
            state.Set(NPCWorldState.Keys.InventoryCount, 3);

            Assert.IsTrue(state.TryGet(NPCWorldState.Keys.Fatigue, out float fatigue));
            Assert.AreEqual(0.75f, fatigue);
            Assert.IsTrue(state.TryGet(NPCWorldState.Keys.InventoryCount, out int inventory));
            Assert.AreEqual(3, inventory);
            Assert.AreEqual(2, state.Count);
        }

        [Test]
        public void WrongTypeRead_FailsClosed()
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, 0.5f);

            Assert.IsFalse(state.TryGet(NPCWorldState.Keys.Fatigue, out string text), "float 键按 string 读必须 false");
            Assert.IsNull(text);
            Assert.IsFalse(state.TryGet(NPCWorldState.Keys.Fatigue, out int number), "float 键按 int 读必须 false（无隐式转换）");
            Assert.AreEqual(0, number);
        }

        [Test]
        public void GetFloat_GetInt_FallBackWhenAbsent()
        {
            var state = new NPCWorldState();
            Assert.AreEqual(0.42f, state.GetFloat(NPCWorldState.Keys.PlayerDistance, 0.42f));
            Assert.AreEqual(7, state.GetInt(NPCWorldState.Keys.InventoryCount, 7));

            state.Set(NPCWorldState.Keys.PlayerDistance, 12f);
            Assert.AreEqual(12f, state.GetFloat(NPCWorldState.Keys.PlayerDistance, 0.42f));
        }

        [Test]
        public void HasDrop_ReflectsDropDistanceKeyOnly()
        {
            var state = new NPCWorldState();
            Assert.IsFalse(state.HasDrop(), "未感知掉落物：键缺席");

            state.Set(NPCWorldState.Keys.DropDistance, 3.5f);
            Assert.IsTrue(state.HasDrop());

            state.Remove(NPCWorldState.Keys.DropDistance);
            Assert.IsFalse(state.HasDrop());
        }

        [Test]
        public void Overwrite_SameKeyDifferentType_IsAllowed()
        {
            var state = new NPCWorldState();
            state.Set("custom", 1);
            state.Set("custom", "one");

            Assert.IsTrue(state.TryGet("custom", out string value));
            Assert.AreEqual("one", value);
        }

        [Test]
        public void NullKey_Throws()
        {
            var state = new NPCWorldState();
            Assert.Throws<ArgumentNullException>(() => state.Set(null, 1f));
            Assert.IsFalse(state.Has(null));
            Assert.IsFalse(state.Remove(null));
        }

        [Test]
        public void Clear_RemovesEverything()
        {
            var state = new NPCWorldState();
            state.Set(NPCWorldState.Keys.Fatigue, 1f);
            state.Set(NPCWorldState.Keys.PlayerDistance, 2f);

            state.Clear();

            Assert.AreEqual(0, state.Count);
            Assert.IsFalse(state.Has(NPCWorldState.Keys.Fatigue));
        }
    }
}
