using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Data.Config;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// ConfigService behaviour: Load builds the (Type,id) dictionary,
    /// Get is a hit-or-null O(1) lookup, All returns the row list, and
    /// loading the same table type twice replaces (reload semantics).
    /// Also checks the ZFrameData.Init registration path.
    /// </summary>
    public class ConfigServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            ZFrameData.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            ZFrameData.Shutdown();
        }

        private static EnemyConfigTable MakeEnemyTable()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            table.AddRow(new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 });
            table.AddRow(new EnemyConfigRow { Id = "goblin", Hp = 60, Speed = 3.75f, Damage = 12 });
            return table;
        }

        [Test]
        public void Init_RegistersConfigService_IntoServices()
        {
            ZFrameData.Init();
            Assert.IsTrue(Services.TryGet(out IConfigService _), "ZFrameData.Init 应注册 IConfigService");
        }

        [Test]
        public void Get_AfterLoad_HitsById()
        {
            var service = new ConfigService();
            service.Load(MakeEnemyTable());

            var slime = service.Get<EnemyConfigRow>("slime");
            Assert.NotNull(slime, "已加载的 Id 应命中");
            Assert.AreEqual(30, slime.Hp);
            Assert.AreEqual(2.5f, slime.Speed, 1e-5f);
        }

        [Test]
        public void Get_MissingIdOrUnloadedTable_ReturnsNull()
        {
            var service = new ConfigService();
            Assert.Null(service.Get<EnemyConfigRow>("slime"), "未加载表应返回 null");

            service.Load(MakeEnemyTable());
            Assert.Null(service.Get<EnemyConfigRow>("dragon"), "未录入 Id 应返回 null");
        }

        [Test]
        public void All_ReturnsLoadedRows_NullWhenUnloaded()
        {
            var service = new ConfigService();
            Assert.Null(service.All<EnemyConfigRow>());

            service.Load(MakeEnemyTable());
            Assert.AreEqual(2, service.All<EnemyConfigRow>().Count);
        }

        [Test]
        public void Load_SameTableType_Twice_ReplacesContents()
        {
            var service = new ConfigService();
            service.Load(MakeEnemyTable());
            Assert.AreEqual(2, service.All<EnemyConfigRow>().Count);

            var small = ScriptableObject.CreateInstance<EnemyConfigTable>();
            small.AddRow(new EnemyConfigRow { Id = "orc", Hp = 150, Speed = 1.8f, Damage = 25 });
            service.Load(small);

            Assert.Null(service.Get<EnemyConfigRow>("slime"), "重载后旧 Id 不应残留");
            Assert.AreEqual(1, service.All<EnemyConfigRow>().Count, "重载是替换语义，不是追加");
            Assert.NotNull(service.Get<EnemyConfigRow>("orc"));
        }

        [Test]
        public void Reload_IsAliasOfLoad()
        {
            var service = new ConfigService();
            service.Reload(MakeEnemyTable());
            Assert.NotNull(service.Get<EnemyConfigRow>("slime"));
        }

        [Test]
        public void Load_SkipsEmptyIdRows()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            table.AddRow(new EnemyConfigRow { Id = "", Hp = 1, Speed = 1, Damage = 1 });
            table.AddRow(new EnemyConfigRow { Id = "ok", Hp = 1, Speed = 1, Damage = 1 });

            var service = new ConfigService();
            service.Load(table);

            Assert.AreEqual(1, service.All<EnemyConfigRow>().Count, "空 Id 行应被跳过");
            Assert.Null(service.Get<EnemyConfigRow>(""));
        }
    }
}
