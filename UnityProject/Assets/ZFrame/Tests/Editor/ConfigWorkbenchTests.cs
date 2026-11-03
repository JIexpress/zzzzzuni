using NUnit.Framework;

namespace ZFrame.Editor.Tests
{
    /// <summary>
    /// ConfigWorkbench 纯逻辑单测：资产名 -> CSV 文件名推导（CsvExporter.TableAssetPathFor 的逆向约定）。
    /// </summary>
    public sealed class ConfigWorkbenchTests
    {
        [Test]
        public void StandardTableName_MapsToSnakeCsv()
        {
            Assert.AreEqual("enemy.csv", ConfigWorkbench.CsvFileNameFor("CFG_EnemyTable"));
        }

        [Test]
        public void WithoutCfgPrefix_StillStripsTableSuffix()
        {
            Assert.AreEqual("hero.csv", ConfigWorkbench.CsvFileNameFor("HeroTable"));
        }

        [Test]
        public void PlainName_LoweredAsIs()
        {
            Assert.AreEqual("items.csv", ConfigWorkbench.CsvFileNameFor("Items"));
        }

        [Test]
        public void TableSuffixOnlyInsideName_IsNotStripped()
        {
            // 只剥离结尾的 Table；词中的 Table 保留
            Assert.AreEqual("tableofcontent.csv", ConfigWorkbench.CsvFileNameFor("CFG_TableOfContent"));
        }
    }
}
