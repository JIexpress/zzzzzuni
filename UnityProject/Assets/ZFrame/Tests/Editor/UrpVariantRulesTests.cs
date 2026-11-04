using System.Collections.Generic;
using NUnit.Framework;

namespace ZFrame.Editor.Tests
{
    /// <summary>
    /// TASK-A-R9-16：UrpVariantRules 纯函数单测。
    /// 覆盖：规则命中 / 三档必需不命中 / 白名单保护 / 空关键字 / 大小写 /
    /// 平台分支 / Development 分支 / 逐条禁用 / 自定义规则 / 规则判据完整性。
    ///
    /// 「变异有牙」靶心：EveryStripRule_HitsItsOwnKeyword 与
    /// AdditionalLightsVertex_IsStripped 都硬断言 _ADDITIONAL_LIGHTS_VERTEX
    /// 应 Strip。定向变异把该规则从 UrpVariantRules.StripRules 删掉或改名后，
    /// 这两条必然变红；还原后复绿。
    /// </summary>
    public sealed class UrpVariantRulesTests
    {
        private const string Win = "StandaloneWindows64";
        private const string Android = "Android";

        // --------------------------------------------------------------
        // 命中：规则表每一条都必须能命中自己的关键字（变异靶心之一）
        // --------------------------------------------------------------
        [Test]
        public void EveryStripRule_HitsItsOwnKeyword()
        {
            Assert.Greater(UrpVariantRules.StripRules.Length, 0, "规则表不应为空");
            foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
            {
                var d = UrpVariantRules.Evaluate("Universal Render Pipeline/Lit",
                    new[] { rule.Keyword }, Win, false);
                Assert.IsTrue(d.Strip,
                    "规则 [" + rule.Keyword + "] 应命中，实际=" + d.Reason);
                StringAssert.Contains(rule.Keyword, d.Reason);
            }
        }

        [Test]
        public void AdditionalLightsVertex_IsStripped()
        {
            var d = UrpVariantRules.Evaluate("Universal Render Pipeline/Lit",
                new[] { "_ADDITIONAL_LIGHTS_VERTEX" }, Win, false);
            Assert.IsTrue(d.Strip, "三档都是 PerPixel，PerVertex 变体用不到");
            StringAssert.Contains("命中规则", d.Reason);
        }

        [Test]
        public void DebugDisplay_IsStripped_InReleaseBuild()
        {
            var d = UrpVariantRules.Evaluate("Universal Render Pipeline/Particles/Unlit",
                new[] { "DEBUG_DISPLAY" }, Android, false);
            Assert.IsTrue(d.Strip);
        }

        // --------------------------------------------------------------
        // 不命中：三档 URP 与 Volume 必需变体一律保留
        // --------------------------------------------------------------
        [TestCase("_MAIN_LIGHT_SHADOWS")]
        [TestCase("_MAIN_LIGHT_SHADOWS_CASCADE")]
        [TestCase("_SHADOWS_SOFT")]
        [TestCase("_ADDITIONAL_LIGHTS")]
        [TestCase("_LIGHT_COOKIES")]
        [TestCase("LOD_FADE_CROSSFADE")]
        [TestCase("_ALPHATEST_ON")]
        [TestCase("_FLIPBOOKBLENDING_ON")]
        [TestCase("INSTANCING_ON")]
        [TestCase("FOG_LINEAR")]
        public void RequiredKeyword_Kept(string keyword)
        {
            var d = UrpVariantRules.Evaluate("Universal Render Pipeline/Lit",
                new[] { keyword }, Win, false);
            Assert.IsFalse(d.Strip, keyword + " 不应被剥：" + d.Reason);
        }

        [TestCase("_BLOOM_LQ")]
        [TestCase("_BLOOM_HQ")]
        [TestCase("_BLOOM_LQ_DIRT")]
        [TestCase("_TONEMAP_ACES")]
        [TestCase("_HDR_GRADING")]
        [TestCase("_FILM_GRAIN")]
        [TestCase("_DITHERING")]
        public void VolumeProtectedKeyword_Kept(string keyword)
        {
            var d = UrpVariantRules.Evaluate("Hidden/Universal Render Pipeline/UberPost",
                new[] { keyword }, Win, false);
            Assert.IsFalse(d.Strip, keyword + " 是 Volume/后处理必需：" + d.Reason);
            StringAssert.Contains("白名单保护", d.Reason);
        }

        [Test]
        public void WhitelistBeatsStripRule()
        {
            // 组合同时含 Volume 必需 + 可剥关键字：白名单优先，整条保留（宁可不剥不误伤）
            var d = UrpVariantRules.Evaluate("Hidden/Universal Render Pipeline/UberPost",
                new[] { "_BLOOM_LQ", "_ADDITIONAL_LIGHTS_VERTEX" }, Win, false);
            Assert.IsFalse(d.Strip, d.Reason);
            StringAssert.Contains("白名单保护", d.Reason);
        }

        // --------------------------------------------------------------
        // 边界：空关键字 / 大小写 / 平台分支 / Development 分支
        // --------------------------------------------------------------
        [Test]
        public void EmptyKeywords_Kept()
        {
            Assert.IsFalse(UrpVariantRules.Evaluate("x", null, Win, false).Strip,
                "null 关键字集合不应剥离");

            var d = UrpVariantRules.Evaluate("x", new string[0], Win, false);
            Assert.IsFalse(d.Strip, "空组合不应剥离");
            StringAssert.Contains("空", d.Reason);
        }

        [Test]
        public void NullElementInKeywords_DoesNotThrow()
        {
            var d = UrpVariantRules.Evaluate("x",
                new[] { (string)null, "_ADDITIONAL_LIGHTS_VERTEX" }, Win, false);
            Assert.IsTrue(d.Strip, "null 元素不应影响后续有效关键字的命中");
        }

        [TestCase("_additional_lights_vertex")]
        [TestCase("_ADDITIONAL_LIGHTS_vertex")]
        [TestCase("_additional_LIGHTS_VERTEX")]
        [TestCase("debug_display")]
        public void KeywordMatchIsCaseSensitive(string keyword)
        {
            var d = UrpVariantRules.Evaluate("Universal Render Pipeline/Lit",
                new[] { keyword }, Win, false);
            Assert.IsFalse(d.Strip,
                "Unity 关键字为大写，小写/混写视为不同关键字，不应命中：" + keyword);
        }

        [Test]
        public void PlatformBranch_RulesArePlatformAgnostic()
        {
            // 核对结论：QualitySettings 三档在 Windows x64 与 Android arm64 都打包，
            // 规则刻意不按平台分叉。此用例锁死这一点，防止将来误加平台分支。
            foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
            {
                var onWin = UrpVariantRules.Evaluate("x", new[] { rule.Keyword }, Win, false);
                var onAndroid = UrpVariantRules.Evaluate("x", new[] { rule.Keyword }, Android, false);
                Assert.AreEqual(onWin.Strip, onAndroid.Strip,
                    "规则 " + rule.Keyword + " 在两平台判定应一致");
            }
        }

        [Test]
        public void DevelopmentBuild_KeepsReleaseOnlyRule()
        {
            var dev = UrpVariantRules.Evaluate("x", new[] { "DEBUG_DISPLAY" }, Android, true);
            var release = UrpVariantRules.Evaluate("x", new[] { "DEBUG_DISPLAY" }, Android, false);
            Assert.IsFalse(dev.Strip, "Development 构建需保留 DEBUG_DISPLAY");
            Assert.IsTrue(release.Strip, "正式构建才剥 DEBUG_DISPLAY");
        }

        [Test]
        public void NonReleaseOnlyRule_StillStrips_InDevelopmentBuild()
        {
            var d = UrpVariantRules.Evaluate("x",
                new[] { "_ADDITIONAL_LIGHTS_VERTEX" }, Android, true);
            Assert.IsTrue(d.Strip, "非 ReleaseBuildOnly 规则在 Development 构建仍生效");
        }

        // --------------------------------------------------------------
        // 可配置：逐条禁用 / 自定义规则替换
        // --------------------------------------------------------------
        [Test]
        public void DisabledRuleKeywords_SkipsThatRule()
        {
            var disabled = new HashSet<string> { "_ADDITIONAL_LIGHTS_VERTEX" };
            var d = UrpVariantRules.Evaluate("x",
                new[] { "_ADDITIONAL_LIGHTS_VERTEX" }, Win, false, null, disabled);
            Assert.IsFalse(d.Strip, "被逐条停用的规则不应生效");
        }

        [Test]
        public void CustomRules_ReplaceDefault()
        {
            var custom = new[] { new UrpVariantRules.StripRule("_CUSTOM_X", "测试用自定义规则") };
            Assert.IsTrue(UrpVariantRules.Evaluate("x", new[] { "_CUSTOM_X" }, Win, false, custom).Strip);
            Assert.IsFalse(UrpVariantRules.Evaluate("x",
                new[] { "_ADDITIONAL_LIGHTS_VERTEX" }, Win, false, custom).Strip,
                "自定义规则应完整替换默认规则表");
        }

        // --------------------------------------------------------------
        // 治理：判据完整 / 白名单与规则不重叠 / 核对后推翻候选表
        // --------------------------------------------------------------
        [Test]
        public void EveryRule_HasNonEmptyRationale()
        {
            foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Keyword), "关键字不能为空");
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Rationale),
                    "规则 " + rule.Keyword + " 必须写判据（不许照抄候选表而无依据）");
            }
        }

        [Test]
        public void ProtectedAndStripRules_DoNotOverlap()
        {
            foreach (string p in UrpVariantRules.ProtectedKeywords)
            {
                foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
                {
                    Assert.AreNotEqual(p, rule.Keyword,
                        "白名单关键字不能被列为剥离规则");
                }
            }
        }

        [Test]
        public void MainLightShadowsCascade_NotInStripRules()
        {
            // 简报候选表列了 CASCADE；核对三档后（m_MainLightShadowsSupported=1、
            // Medium/High cascade=2、运行期可切档、URP 源码注明 runtime 可变）推翻，
            // 此用例锁死该结论，防止有人照抄候选表把它加回来。
            foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
            {
                Assert.AreNotEqual("_MAIN_LIGHT_SHADOWS_CASCADE", rule.Keyword);
                Assert.AreNotEqual("_MAIN_LIGHT_SHADOWS", rule.Keyword);
            }
        }

        [Test]
        public void StripRulesCount_Locked()
        {
            // 规则表条数锁死：增删规则必须同步改测试与施工报告（可审计）。
            Assert.AreEqual(21, UrpVariantRules.StripRules.Length);
        }
    }
}
