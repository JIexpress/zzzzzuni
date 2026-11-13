using NUnit.Framework;
using UnityEngine;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// CSV import/export core behaviour (pure functions, no asset IO):
    /// create-when-empty, update-by-Id, missing-in-csv reporting, error
    /// collection in one pass, and the export-import roundtrip.
    /// </summary>
    public class CsvImporterTests
    {
        private const string Header = "Id,Hp,Speed,Damage";

        private static EnemyConfigTable MakeTable(params EnemyConfigRow[] rows)
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            foreach (var row in rows)
            {
                table.AddRow(row);
            }

            return table;
        }

        [Test]
        public void ImportRows_EmptyTable_CreatesAllRows()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var csv = Header + "\nslime,30,2.5,5\ngoblin,60,3.75,12\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(2, report.Added);
            Assert.AreEqual(0, report.Updated);
            Assert.AreEqual(2, table.Rows.Count);
            Assert.AreEqual("slime", table.Rows[0].Id);
            Assert.AreEqual(60, table.Rows[1].Hp);
            Assert.AreEqual(3.75f, table.Rows[1].Speed, 1e-5f);
        }

        [Test]
        public void ImportRows_ExistingIds_UpdatesInPlace_NewIdsAppended()
        {
            var table = MakeTable(
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "goblin", Hp = 60, Speed = 3.75f, Damage = 12 });
            // slime 数值被修改；orc 是新增行。
            var csv = Header + "\nslime,99,9.9,50\ngoblin,60,3.75,12\norc,150,1.8,25\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(1, report.Added, "orc 应为新增");
            Assert.AreEqual(2, report.Updated, "slime/goblin 应为按 Id 更新");
            Assert.AreEqual(3, table.Rows.Count);

            var slime = table.Rows[0];
            Assert.AreEqual("slime", slime.Id);
            Assert.AreEqual(99, slime.Hp, "重导入后数值应生效（更新语义）");
            Assert.AreEqual(9.9f, slime.Speed, 1e-5f);
            Assert.AreEqual(50, slime.Damage);
        }

        [Test]
        public void ImportRows_RowMissingInCsv_KeptAndMarkedNotDeleted()
        {
            var table = MakeTable(
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "goblin", Hp = 60, Speed = 3.75f, Damage = 12 });
            var csv = Header + "\nslime,30,2.5,5\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(1, report.MissingInCsv.Count, "CSV 缺失行应被标记");
            Assert.AreEqual("goblin", report.MissingInCsv[0]);
            Assert.AreEqual(2, table.Rows.Count, "缺失行保留，不删除");
        }

        [Test]
        public void ImportRows_BadTypeAndEmptyId_AllErrorsCollectedInOnePass()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            // 第 2 行坏类型（Hp=abc），第 3 行缺 Id —— 一次调用必须两处都报。
            var csv = Header + "\nslime,abc,2.5,5\n,30,2.5,5\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.AreEqual(2, report.Errors.Count,
                "坏类型与空 Id 应在同一趟全部报出: " + string.Join("; ", report.Errors));
            Assert.IsFalse(report.Ok);
            StringAssert.Contains("Hp", report.Errors[0], "第 1 处: 坏类型指明字段");
            StringAssert.Contains("Id 为空", report.Errors[1], "第 2 处: 缺 Id");
        }

        [Test]
        public void ImportRows_UnknownColumn_IsReported()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var csv = "Id,Hp,What\nslime,30,x\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.AreEqual(1, report.Errors.Count);
            StringAssert.Contains("What", report.Errors[0]);
        }

        [Test]
        public void ImportRows_HeaderNotStartingWithId_IsRejected()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var report = CsvImporter.ImportRows("Name,Hp\nslime,30\n", table);

            Assert.IsFalse(report.Ok);
            StringAssert.Contains("Id", report.Errors[0]);
            Assert.AreEqual(0, table.Rows.Count);
        }

        [Test]
        public void Roundtrip_ExportThenImport_DataIdentical()
        {
            var source = MakeTable(
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "goblin", Hp = 60, Speed = 3.75f, Damage = 12 },
                new EnemyConfigRow { Id = "orc", Hp = 150, Speed = 1.8f, Damage = 25 });

            var csv = CsvExporter.BuildCsv(source);
            var roundTripped = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var report = CsvImporter.ImportRows(csv, roundTripped);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(source.Rows.Count, roundTripped.Rows.Count);
            for (int i = 0; i < source.Rows.Count; i++)
            {
                var a = source.Rows[i];
                var b = roundTripped.Rows[i];
                Assert.AreEqual(a.Id, b.Id, $"row {i} Id");
                Assert.AreEqual(a.Hp, b.Hp, $"row {i} Hp");
                Assert.AreEqual(a.Speed, b.Speed, 1e-5f, $"row {i} Speed");
                Assert.AreEqual(a.Damage, b.Damage, $"row {i} Damage");
            }
        }

        [Test]
        public void Roundtrip_SpecialCharacters_SurviveEscapeAndParse()
        {
            var source = ScriptableObject.CreateInstance<EnemyConfigTable>();
            // 引号包裹场景用 string 字段覆盖——Enemy 行无 string 字段，
            // 这里直接测 CsvUtility 的转义/解析往返（基本集：逗号/引号/换行）。
            var fields = new[] { "plain", "with,comma", "with\"quote", "with\nnewline" };
            var line = CsvUtility.JoinLine(fields);
            var parsed = CsvUtility.Parse(line + "\n");

            Assert.AreEqual(1, parsed.Count, "带引号换行的字段是一行逻辑记录");
            Assert.AreEqual(fields, parsed[0]);
        }

        [Test]
        public void TableAssetPathFor_FollowsCfgConvention()
        {
            Assert.AreEqual(
                "Assets/Configs/CSV/../CFG_EnemyTable.asset",
                CsvExporter.TableAssetPathFor("Assets/Configs/CSV/enemy.csv"));
        }

        // ---- 注解表头（TASK-A-CONF-01 代码生成 schema 格式，与裸表头并存）----

        [Test]
        public void ImportRows_AnnotatedHeader_ImportsByStrippedNames()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var csv = "Id:string,Hp:int[1..10000],Speed:float[0..100],Damage:int[0..1000]\nslime,30,2.5,5\n";

            var report = CsvImporter.ImportRows(csv, table);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual("slime", table.Rows[0].Id);
            Assert.AreEqual(30, table.Rows[0].Hp);
            Assert.AreEqual(2.5f, table.Rows[0].Speed, 1e-5f);
            Assert.AreEqual(5, table.Rows[0].Damage);
        }

        [Test]
        public void ImportRows_AnnotatedTypeMismatch_IsReported()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var report = CsvImporter.ImportRows("Id:string,Hp:float\nslime,2.5\n", table);

            Assert.IsFalse(report.Ok);
            StringAssert.Contains("Hp", report.Errors[0]);
            Assert.AreEqual(1, table.Rows.Count, "坏列按既有导入语义收集错误后仍落表（其余列用默认值）");
        }

        [Test]
        public void ImportRows_BadAnnotation_IsReported()
        {
            var table = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var report = CsvImporter.ImportRows("Id,Hp:inat\nslime,30\n", table);

            Assert.IsFalse(report.Ok);
            StringAssert.Contains("Hp", report.Errors[0]);
        }

        [Test]
        public void BuildCsv_EmitsAnnotatedHeader_RoundtripKeepsSchema()
        {
            var source = MakeTable(new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 });

            var csv = CsvExporter.BuildCsv(source);
            StringAssert.StartsWith(
                "Id:string,Hp:int[1..10000],Speed:float[0..100],Damage:int[0..1000]",
                csv,
                "导出的表头就是代码生成 schema（CSV 是唯一 schema 源）");

            var roundTripped = ScriptableObject.CreateInstance<EnemyConfigTable>();
            var report = CsvImporter.ImportRows(csv, roundTripped);

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.AreEqual(30, roundTripped.Rows[0].Hp);
            Assert.AreEqual(2.5f, roundTripped.Rows[0].Speed, 1e-5f);
        }
    }
}
