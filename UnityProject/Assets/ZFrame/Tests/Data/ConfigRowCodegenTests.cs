using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Data.ConfigEditor;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// TASK-A-CONF-01 codegen: schema parsing (type/range/identifier rules),
    /// emitted C# shape, the "bad header => error, no file" discipline, and
    /// the committed-artifact parity of the generated EnemyConfigRow.
    /// </summary>
    public class ConfigRowCodegenTests
    {
        private const string EnemyHeader = "Id:string,Hp:int[1..10000],Speed:float[0..100],Damage:int[0..1000]";

        private readonly List<string> _tempDirs = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in _tempDirs)
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }

            _tempDirs.Clear();
        }

        private static ConfigRowSchema ParseHeader(string header, string rowClassName = "EnemyConfigRow",
            string sourcePath = "Assets/Configs/CSV/enemy.csv")
        {
            return ConfigRowSchema.Parse(CsvUtility.Parse(header + "\n")[0], sourcePath, rowClassName);
        }

        private string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zframe-codegen-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        [Test]
        public void ParseSchema_AnnotatedHeader_ParsesColumnsTypesAndRanges()
        {
            var schema = ParseHeader(EnemyHeader);

            Assert.IsTrue(schema.IsValid, string.Join("; ", schema.Errors));
            Assert.AreEqual(3, schema.Columns.Count);
            Assert.AreEqual("Hp", schema.Columns[0].Name);
            Assert.AreEqual(ConfigColumnType.Int, schema.Columns[0].Type);
            Assert.IsTrue(schema.Columns[0].HasRange);
            Assert.AreEqual(1, schema.Columns[0].RangeMin);
            Assert.AreEqual(10000, schema.Columns[0].RangeMax);
            Assert.AreEqual(ConfigColumnType.Float, schema.Columns[1].Type);
            Assert.AreEqual(ConfigColumnType.Int, schema.Columns[2].Type);
        }

        [Test]
        public void ParseSchema_SupportsAllFourTypes()
        {
            var schema = ParseHeader("Id:string,Name:string,Age:int,Ratio:float,Active:bool", "MixedRow");

            Assert.IsTrue(schema.IsValid, string.Join("; ", schema.Errors));
            Assert.AreEqual(4, schema.Columns.Count);
            Assert.AreEqual(ConfigColumnType.String, schema.Columns[0].Type);
            Assert.AreEqual(ConfigColumnType.Int, schema.Columns[1].Type);
            Assert.AreEqual(ConfigColumnType.Float, schema.Columns[2].Type);
            Assert.AreEqual(ConfigColumnType.Bool, schema.Columns[3].Type);
        }

        [Test]
        public void ParseSchema_FirstColumnMustBeId()
        {
            Assert.That(ParseHeader("Name:string,Hp:int").Errors[0], Does.Contain("Id"));
        }

        [Test]
        public void ParseSchema_IdOnlyAcceptsStringAnnotation()
        {
            Assert.That(ParseHeader("Id:int,Hp:int").Errors[0], Does.Contain("string"));
        }

        [Test]
        public void ParseSchema_UnknownType_IsError()
        {
            Assert.That(ParseHeader("Id:string,Hp:double").Errors[0], Does.Contain("double"));
        }

        [Test]
        public void ParseSchema_MissingAnnotation_IsError()
        {
            Assert.That(ParseHeader("Id:string,Hp").Errors[0], Does.Contain("类型标注"));
        }

        [Test]
        public void ParseSchema_DuplicateColumn_IgnoringCase_IsError()
        {
            Assert.That(ParseHeader("Id:string,Hp:int,hp:int").Errors[0], Does.Contain("重复"));
        }

        [Test]
        public void ParseSchema_InvalidIdentifier_IsError()
        {
            Assert.That(ParseHeader("Id:string,1Hp:int").Errors[0], Does.Contain("1Hp"));
        }

        [Test]
        public void ParseSchema_CSharpKeyword_IsError()
        {
            Assert.That(ParseHeader("Id:string,class:int").Errors[0], Does.Contain("关键字"));
        }

        [Test]
        public void ParseSchema_RangeOnString_IsError()
        {
            Assert.That(ParseHeader("Id:string,Name:string[0..5]").Errors[0], Does.Contain("Range"));
        }

        [Test]
        public void ParseSchema_ReversedRange_IsError()
        {
            Assert.That(ParseHeader("Id:string,Hp:int[100..1]").Errors[0], Does.Contain("下限"));
        }

        [Test]
        public void ParseSchema_FractionalIntRange_IsError()
        {
            Assert.That(ParseHeader("Id:string,Hp:int[1.5..100]").Errors[0], Does.Contain("整数"));
        }

        [Test]
        public void ParseSchema_EmptyHeader_IsError()
        {
            var schema = ConfigRowSchema.Parse(new string[0], "x.csv", "XRow");
            Assert.IsFalse(schema.IsValid);
        }

        [Test]
        public void RowClassNameFor_FollowsPascalConfigRowConvention()
        {
            Assert.AreEqual("EnemyConfigRow", ConfigCodeGenerator.RowClassNameFor("Assets/Configs/CSV/enemy.csv"));
            Assert.AreEqual("NpcPersonalityConfigRow", ConfigCodeGenerator.RowClassNameFor("npcPersonality.csv"));
        }

        [Test]
        public void RowClassNameFor_InvalidIdentifier_ReturnsNull()
        {
            Assert.IsNull(ConfigCodeGenerator.RowClassNameFor("2020event.csv"));
        }

        [Test]
        public void Generate_EmitsAutoGeneratedHeaderSourcePathAndProperties()
        {
            var code = ConfigRowCodegen.Generate(ParseHeader(EnemyHeader));

            StringAssert.Contains("// <auto-generated>", code);
            StringAssert.Contains("// 源表: Assets/Configs/CSV/enemy.csv", code);
            StringAssert.Contains("namespace ZFrame.Data.Config", code);
            StringAssert.Contains("public class EnemyConfigRow : ConfigRow", code);
            StringAssert.Contains("[field: UnityEngine.Range(1, 10000)]", code);
            StringAssert.Contains("[field: UnityEngine.Range(0f, 100f)]", code);
            StringAssert.Contains("[field: UnityEngine.SerializeField]", code);
            StringAssert.Contains("public int Hp { get; set; }", code);
            StringAssert.Contains("public float Speed { get; set; }", code);
            StringAssert.DoesNotContain("public string Id", code, "Id 由基类继承，不得重复生成");
            StringAssert.DoesNotContain("\r", code, "LF 换行");
            Assert.IsTrue(code.EndsWith("\n"), "文件以换行结尾");
        }

        [Test]
        public void Generate_InvalidSchema_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConfigRowCodegen.Generate(ParseHeader("Id:string,Hp:double")));
        }

        [Test]
        public void CommittedEnemyCsv_GeneratedArtifact_IsInSync()
        {
            // 核心闭环：入库 CSV 的表头 + 入库生成产物必须与生成器输出逐字节一致。
            var csvText = File.ReadAllText($"{Application.dataPath}/Configs/CSV/enemy.csv");
            var header = CsvUtility.Parse(csvText)[0];
            var schema = ConfigRowSchema.Parse(header, "Assets/Configs/CSV/enemy.csv", "EnemyConfigRow");

            Assert.IsTrue(schema.IsValid, string.Join("; ", schema.Errors));
            var expected = ConfigRowCodegen.Generate(schema);
            var committed = File.ReadAllText(
                $"{Application.dataPath}/ZFrame/Data/Config/Generated/EnemyConfigRow.cs", new UTF8Encoding(false));
            Assert.AreEqual(expected, committed, "Generated/EnemyConfigRow.cs 与 enemy.csv 不同步——请重新生成行类型");
        }

        [Test]
        public void GenerateForRowType_WritesFileFromAnnotatedCsv()
        {
            var dir = NewTempDir();
            var csvPath = Path.Combine(dir, "sword.csv");
            File.WriteAllText(csvPath, "Id:string,Hp:int[1..100]\nslime,30\n", new UTF8Encoding(false));

            var report = ConfigCodeGenerator.GenerateForRowType(csvPath, Path.Combine(dir, "Generated"));

            Assert.IsTrue(report.Ok, string.Join("; ", report.Errors));
            Assert.IsTrue(report.WroteFile);
            var outPath = Path.Combine(dir, "Generated", "SwordConfigRow.cs");
            Assert.IsTrue(File.Exists(outPath));
            var code = File.ReadAllText(outPath);
            StringAssert.Contains("// <auto-generated>", code);
            StringAssert.Contains("public class SwordConfigRow : ConfigRow", code);
            StringAssert.Contains("public int Hp { get; set; }", code);
        }

        [Test]
        public void GenerateForRowType_SecondRun_UnchangedFile_NoRewrite()
        {
            var dir = NewTempDir();
            var csvPath = Path.Combine(dir, "sword.csv");
            File.WriteAllText(csvPath, "Id:string,Hp:int\n", new UTF8Encoding(false));
            var outputRoot = Path.Combine(dir, "Generated");

            var first = ConfigCodeGenerator.GenerateForRowType(csvPath, outputRoot);
            var second = ConfigCodeGenerator.GenerateForRowType(csvPath, outputRoot);

            Assert.IsTrue(first.Ok, string.Join("; ", first.Errors));
            Assert.IsTrue(first.WroteFile);
            Assert.IsFalse(second.WroteFile, "内容不变时不得重写（导入钩子防抖）");
        }

        [Test]
        public void GenerateForRowType_BadHeader_ErrorsAndWritesNothing()
        {
            var dir = NewTempDir();
            var csvPath = Path.Combine(dir, "bad.csv");
            File.WriteAllText(csvPath, "Id:string,Hp:inat\nslime,30\n", new UTF8Encoding(false));

            var report = ConfigCodeGenerator.GenerateForRowType(csvPath, Path.Combine(dir, "Generated"));

            Assert.IsFalse(report.Ok);
            Assert.GreaterOrEqual(report.Errors.Count, 1);
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "Generated")), "坏表头不得产出任何文件");
        }

        [Test]
        public void GenerateForRowType_BadHeader_DoesNotOverwriteExistingFile()
        {
            var dir = NewTempDir();
            var outputRoot = Path.Combine(dir, "Generated");
            Directory.CreateDirectory(outputRoot);
            var existingPath = Path.Combine(outputRoot, "BadConfigRow.cs");
            File.WriteAllText(existingPath, "sentinel", new UTF8Encoding(false));
            var csvPath = Path.Combine(dir, "bad.csv");
            File.WriteAllText(csvPath, "Id:string,Hp:int[100..1]\n", new UTF8Encoding(false));

            var report = ConfigCodeGenerator.GenerateForRowType(csvPath, outputRoot);

            Assert.IsFalse(report.Ok);
            Assert.AreEqual("sentinel", File.ReadAllText(existingPath), "出错时既有文件不得被触碰");
        }
    }
}
