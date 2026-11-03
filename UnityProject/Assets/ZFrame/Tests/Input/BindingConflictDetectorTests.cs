using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.View.Input.Tests
{
    /// <summary>
    /// Pure-logic tests of <see cref="BindingConflictDetector"/> (TASK-A-M3-03):
    /// the rule is exercised against hand-built snapshot tables only — NO
    /// Unity object, NO Input System device, NO play mode. This is the proof
    /// that the conflict decision is separable from the engine (the class doc's
    /// five rules are each asserted below).
    /// </summary>
    public sealed class BindingConflictDetectorTests
    {
        private static BindingSnapshot Row(
            string actionName,
            int bindingIndex,
            string effectivePath,
            bool isComposite = false,
            bool isCompositePart = false,
            string mapName = "Player")
        {
            return new BindingSnapshot(
                mapName + "/" + actionName,
                actionName,
                bindingIndex,
                effectivePath,
                isComposite,
                isCompositePart);
        }

        // ---------- rule 1: same effective path conflicts ----------

        [Test]
        public void FindConflict_SamePathInSameMap_ReturnsTheOffendingBinding()
        {
            var map = new List<BindingSnapshot>
            {
                Row("Interact", 0, "<Keyboard>/e"),
                Row("Pause", 0, "<Keyboard>/escape"),
            };

            BindingSnapshot conflict = BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Pause", 0);

            Assert.IsNotNull(conflict, "同 map 内同有效路径必须判定为冲突");
            Assert.AreEqual("Player/Interact", conflict.ActionKey);
            Assert.AreEqual(0, conflict.BindingIndex);
        }

        // ---------- rule 1 (negative): different path is free ----------

        [Test]
        public void FindConflict_DifferentPath_ReturnsNull()
        {
            var map = new List<BindingSnapshot> { Row("Interact", 0, "<Keyboard>/e") };

            Assert.IsNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/f", "Player/Pause", 0));
        }

        // ---------- rule 3: composite headers never participate ----------

        [Test]
        public void FindConflict_CompositeHeader_IsIgnored()
        {
            var map = new List<BindingSnapshot>
            {
                Row("Move", 0, "2DVector", isComposite: true), // header: no device path
                Row("Move", 1, "<Keyboard>/w", isCompositePart: true),
            };

            // Even a captured path equal to the header's "path" must not conflict.
            Assert.IsNull(BindingConflictDetector.FindConflict(map, "2DVector", "Player/Interact", 0));
            Assert.IsFalse(BindingConflictDetector.IsConflictRelevant(map[0]), "composite 头不参与冲突判定");
        }

        // ---------- rule 3 (positive): composite PARTS do participate ----------

        [Test]
        public void FindConflict_CompositePart_DoesParticipate()
        {
            var map = new List<BindingSnapshot>
            {
                Row("Move", 0, "2DVector", isComposite: true),
                Row("Move", 1, "<Keyboard>/w", isCompositePart: true),
            };

            BindingSnapshot conflict = BindingConflictDetector.FindConflict(map, "<Keyboard>/w", "Player/Interact", 0);

            Assert.IsNotNull(conflict, "composite 的部件（WASD 的 W）参与冲突判定");
            Assert.AreEqual("Move", conflict.ActionName);
            Assert.IsTrue(conflict.IsCompositePart);
        }

        // ---------- rule 4: the rebound binding excludes itself ----------

        [Test]
        public void FindConflict_ExcludesTheReboundBindingItself()
        {
            var map = new List<BindingSnapshot> { Row("Interact", 0, "<Keyboard>/e") };

            // Rebinding Interact#0 to the key it already has is not a conflict…
            Assert.IsNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Interact", 0));

            // …but the same path from a DIFFERENT binding index still is.
            Assert.IsNotNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Interact", 1));

            // …and so is the same path from a different action.
            Assert.IsNotNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Pause", 0));
        }

        [Test]
        public void FindConflict_NullExcludeKey_ExcludesNothing()
        {
            var map = new List<BindingSnapshot> { Row("Interact", 0, "<Keyboard>/e") };

            Assert.IsNotNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/e", null, 0));
        }

        // ---------- rule 5: unbound rows never conflict ----------

        [Test]
        public void FindConflict_SkipsRowsWithoutEffectivePath()
        {
            var map = new List<BindingSnapshot>
            {
                Row("Interact", 0, string.Empty),
                Row("Interact", 1, null),
            };

            Assert.IsNull(BindingConflictDetector.FindConflict(map, string.Empty, "Player/Pause", 0));
            Assert.IsNull(BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Pause", 0));
        }

        // ---------- degenerate inputs ----------

        [Test]
        public void FindConflict_EmptyOrNullMap_ReturnsNull()
        {
            Assert.IsNull(BindingConflictDetector.FindConflict(new List<BindingSnapshot>(), "<Keyboard>/e", null, 0));
            Assert.IsNull(BindingConflictDetector.FindConflict(null, "<Keyboard>/e", null, 0));
        }

        [Test]
        public void FindConflict_NullOrEmptyNewPath_ReturnsNull()
        {
            var map = new List<BindingSnapshot> { Row("Interact", 0, "<Keyboard>/e") };

            Assert.IsNull(BindingConflictDetector.FindConflict(map, null, "Player/Pause", 0));
            Assert.IsNull(BindingConflictDetector.FindConflict(map, string.Empty, "Player/Pause", 0));
        }

        // ---------- first match wins (deterministic feedback) ----------

        [Test]
        public void FindConflict_ReturnsTheFirstMatch_InMapOrder()
        {
            var map = new List<BindingSnapshot>
            {
                Row("Interact", 0, "<Keyboard>/e"),
                Row("Pause", 0, "<Keyboard>/e"),
            };

            BindingSnapshot conflict = BindingConflictDetector.FindConflict(map, "<Keyboard>/e", "Player/Move", 0);

            Assert.AreEqual("Player/Interact", conflict.ActionKey, "先出现的行胜出 —— 反馈文案稳定可测");
        }
    }
}
