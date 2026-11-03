using System.IO;
using NUnit.Framework;
using ZFrame.Editor;

namespace ZFrame.Debug.Tests
{
    public sealed class ArchitectureValidatorTests
    {
        [Test]
        public void ValidateAll_CurrentProject_HasZeroViolations()
        {
            Assert.IsTrue(ArchitectureValidator.ValidateAll(), ArchitectureValidator.LastReport);
            Assert.AreEqual(0, ArchitectureValidator.LastViolations.Count);
        }

        [Test]
        public void ExpectedMatrix_MatchesActualAsmdefCount()
        {
            // Tests asmsemblies are exempt, so actual = total asmdefs - test asmdefs.
            var all = Directory.GetFiles("Assets", "*.asmdef", SearchOption.AllDirectories);
            var testAsmdefs = 0;
            foreach (var path in all)
            {
                var json = File.ReadAllText(path);
                var nameStart = json.IndexOf("\"name\"");
                var name = json.Substring(json.IndexOf('"', nameStart + 6) + 1);
                name = name.Substring(0, name.IndexOf('"'));
                if (name.EndsWith(".Tests") || name.Contains(".Tests."))
                {
                    testAsmdefs++;
                }
            }

            Assert.AreEqual(all.Length - testAsmdefs, ArchitectureValidator.ExpectedMatrixCount,
                "期望矩阵条目数应与工程内非测试 asmdef 数一致（新增程序集须同步更新期望矩阵）");
        }
    }
}
