using NUnit.Framework;

namespace ZFrame.Editor.Tests
{
    /// <summary>
    /// AssetNamingRules 纯逻辑单测：前缀表正例/反例/白名单/不适用扩展名。
    /// </summary>
    public sealed class AssetNamingRulesTests
    {
        [TestCase("CHR_Hero.prefab")]
        [TestCase("ENV_Door.prefab")]
        [TestCase("WPN_Sword.prefab")]
        [TestCase("UI_PanelSettings.prefab")]
        [TestCase("CFG_EnemyTable.asset")]
        [TestCase("MAT_Hero_Body.mat")]
        [TestCase("TX_Icon.png")]
        [TestCase("AUD_Click.wav")]
        [TestCase("SCN_Game.unity")]
        [TestCase("MIX_AudioMixer.asset")]
        public void KnownPrefix_Passes(string path)
        {
            var (result, _) = AssetNamingRules.Check(path);
            Assert.AreEqual(NamingCheckResult.Ok, result, path);
        }

        [TestCase("Hero.prefab")]
        [TestCase("icon.png")]
        [TestCase("Cube.asset")]
        [TestCase("Table.mat")]
        [TestCase("MyScene.unity")]
        public void MissingPrefix_Violation(string path)
        {
            var (result, reason) = AssetNamingRules.Check(path);
            Assert.AreEqual(NamingCheckResult.Violation, result, path);
            StringAssert.Contains("无合法前缀", reason);
        }

        [Test]
        public void PrefixRequiresNonEmptySuffix()
        {
            Assert.AreEqual(NamingCheckResult.Violation, AssetNamingRules.Check("CFG_.asset").Result);
            Assert.AreEqual(NamingCheckResult.Violation, AssetNamingRules.Check("CHR_ .prefab").Result);
        }

        [Test]
        public void PrefixIsCaseSensitive()
        {
            Assert.AreEqual(NamingCheckResult.Violation, AssetNamingRules.Check("cfg_EnemyTable.asset").Result);
            Assert.AreEqual(NamingCheckResult.Violation, AssetNamingRules.Check("chr_hero.prefab").Result);
        }

        [TestCase("Scripts/Foo.cs")]
        [TestCase("ZFrame/Editor/ZFrame.Editor.asmdef")]
        [TestCase("Assets.meta")]
        public void NonAssetExtension_NotApplicable(string path)
        {
            var (result, reason) = AssetNamingRules.Check(path);
            Assert.AreEqual(NamingCheckResult.Ok, result, path);
            StringAssert.Contains("不适用", reason);
        }

        [Test]
        public void UiResourcesSnakeCase_Whitelisted()
        {
            var (result, reason) = AssetNamingRules.Check("UI/Resources/ui_main_menu.prefab");
            Assert.AreEqual(NamingCheckResult.Whitelisted, result);
            StringAssert.Contains("资源键", reason);
        }

        [Test]
        public void UiSnakeCaseOutsideResources_Violation()
        {
            Assert.AreEqual(NamingCheckResult.Violation, AssetNamingRules.Check("UI/Editor/ui_debug.prefab").Result);
        }

        [TestCase("Settings/Pipeline/URP_High.asset")]
        [TestCase("Settings/Pipeline/URP_Low_Renderer.asset")]
        [TestCase("Settings/Pipeline/Vol_High.asset")]
        [TestCase("Settings/Pipeline/ZRenderPipeline.asset")]
        [TestCase("Settings/ZRenderPipeline.asset")]
        [TestCase("Settings/UniversalRenderPipelineGlobalSettings.asset")]
        public void PipelineInfra_Whitelisted(string path)
        {
            Assert.AreEqual(NamingCheckResult.Whitelisted, AssetNamingRules.Check(path).Result);
        }

        [Test]
        public void PipelineDirOnly_Whitelisted_PrefixOkInsideStillOk()
        {
            // 白名单目录内的规范资产（CFG_PipelineTiers）应优先判为命中前缀
            var (result, reason) = AssetNamingRules.Check("Settings/Pipeline/CFG_PipelineTiers.asset");
            Assert.AreEqual(NamingCheckResult.Ok, result);
            StringAssert.Contains("CFG_", reason);
        }

        [Test]
        public void UrpGlobalSettings_Whitelisted()
        {
            var (result, _) = AssetNamingRules.Check("UniversalRenderPipelineGlobalSettings.asset");
            Assert.AreEqual(NamingCheckResult.Whitelisted, result);
        }

        [Test]
        public void UrpGlobalSettingsInSubdir_Violation()
        {
            // 整文件名白名单只适用于根目录
            Assert.AreEqual(NamingCheckResult.Violation,
                AssetNamingRules.Check("Game/UniversalRenderPipelineGlobalSettings.asset").Result);
        }

        [TestCase("ZFrame/Tests/Resources/TestCube.prefab")]
        [TestCase("ZFrame/Tests/Resources/TestAsset.asset")]
        public void TestAssets_Whitelisted(string path)
        {
            Assert.AreEqual(NamingCheckResult.Whitelisted, AssetNamingRules.Check(path).Result);
        }

        [Test]
        public void BackslashPath_Normalized()
        {
            Assert.AreEqual(NamingCheckResult.Whitelisted,
                AssetNamingRules.Check(@"UI\Resources\ui_pause_panel.prefab").Result);
        }
    }
}
