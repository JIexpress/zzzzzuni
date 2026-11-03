using UnityEngine;
using NUnit.Framework;

namespace ZFrame.Editor.Tests
{
    /// <summary>
    /// UiAnchorRules 纯逻辑单测：拉伸/角落/边中点/中心/非预设点锚。
    /// </summary>
    public sealed class UiAnchorRulesTests
    {
        [Test]
        public void FullStretch_IsPreset()
        {
            Assert.IsTrue(UiAnchorRules.IsPreset(new Vector2(0f, 0f), new Vector2(1f, 1f)));
            Assert.AreEqual("拉伸", UiAnchorRules.PresetName(new Vector2(0f, 0f), new Vector2(1f, 1f)));
        }

        [Test]
        public void PartialStretch_IsPreset()
        {
            // 上下带边距的横条（ui_test_panel 标题条形态）
            Assert.IsTrue(UiAnchorRules.IsPreset(new Vector2(0f, 0.8f), new Vector2(1f, 0.95f)));
            // 居中收窄块（ui_main_menu / ui_pause_panel 形态）
            Assert.IsTrue(UiAnchorRules.IsPreset(new Vector2(0.1f, 0.78f), new Vector2(0.9f, 0.92f)));
        }

        [Test]
        public void Center_IsPreset()
        {
            var p = new Vector2(0.5f, 0.5f);
            Assert.AreEqual("中心", UiAnchorRules.PresetName(p, p));
        }

        [TestCase(0f, 0f)]
        [TestCase(0f, 1f)]
        [TestCase(1f, 0f)]
        [TestCase(1f, 1f)]
        public void Corners_ArePreset(float x, float y)
        {
            var p = new Vector2(x, y);
            Assert.AreEqual("角落", UiAnchorRules.PresetName(p, p));
        }

        [TestCase(0.5f, 0f)]
        [TestCase(0.5f, 1f)]
        [TestCase(0f, 0.5f)]
        [TestCase(1f, 0.5f)]
        public void EdgeMidpoints_ArePreset(float x, float y)
        {
            var p = new Vector2(x, y);
            Assert.AreEqual("边中点", UiAnchorRules.PresetName(p, p));
        }

        [TestCase(0.5f, 0.34f)]
        [TestCase(0.5f, 0.56f)]
        [TestCase(0.25f, 0.75f)]
        [TestCase(0.88f, 0.98f)]
        public void ArbitraryPointAnchors_NotPreset(float x, float y)
        {
            var p = new Vector2(x, y);
            Assert.IsFalse(UiAnchorRules.IsPreset(p, p), $"{x},{y} 应判为非预设");
            Assert.IsNull(UiAnchorRules.PresetName(p, p));
        }
    }
}
