using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// ConfigValidator rule coverage: duplicate Id, empty Id and [Range]
    /// violations are each detected; a clean table yields zero violations.
    /// </summary>
    public class ConfigValidatorTests
    {
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
        public void Validate_CleanTable_NoViolations()
        {
            var table = MakeTable(
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "goblin", Hp = 60, Speed = 3.75f, Damage = 12 });

            var violations = ConfigValidator.Validate(table);

            Assert.AreEqual(0, violations.Count, string.Join("; ", violations));
        }

        [Test]
        public void Validate_DuplicateId_IsDetected()
        {
            var table = MakeTable(
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "slime", Hp = 40, Speed = 3f, Damage = 6 });

            var violations = ConfigValidator.Validate(table);

            Assert.AreEqual(1, violations.Count, string.Join("; ", violations));
            StringAssert.Contains("重复", violations[0]);
            StringAssert.Contains("slime", violations[0]);
        }

        [Test]
        public void Validate_EmptyId_IsDetected()
        {
            var table = MakeTable(new EnemyConfigRow { Id = "", Hp = 30, Speed = 2.5f, Damage = 5 });

            var violations = ConfigValidator.Validate(table);

            Assert.AreEqual(1, violations.Count, string.Join("; ", violations));
            StringAssert.Contains("Id 为空", violations[0]);
        }

        [Test]
        public void Validate_RangeOverflow_IsDetected()
        {
            // Hp 合法域 [1, 10000]（EnemyConfigRow 上的 [Range]）。
            var table = MakeTable(new EnemyConfigRow { Id = "titan", Hp = 99999, Speed = 2.5f, Damage = 5 });

            var violations = ConfigValidator.Validate(table);

            Assert.AreEqual(1, violations.Count, string.Join("; ", violations));
            StringAssert.Contains("超出 Range", violations[0]);
            StringAssert.Contains("Hp", violations[0]);
        }

        [Test]
        public void Validate_MultipleViolationKinds_AllReported()
        {
            var table = MakeTable(
                new EnemyConfigRow { Id = "", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "slime", Hp = 30, Speed = 2.5f, Damage = 5 },
                new EnemyConfigRow { Id = "slime", Hp = 40, Speed = 3f, Damage = 6 },
                new EnemyConfigRow { Id = "titan", Hp = 99999, Speed = 200f, Damage = 5 });

            var violations = ConfigValidator.Validate(table);

            Assert.AreEqual(4, violations.Count,
                "空 Id + 重复 Id + 2 处越界应全报: " + string.Join("; ", violations));
        }

        [Test]
        public void ValidateRows_NonGenericCore_WorksOnRawSequences()
        {
            var rows = new List<ConfigRow>
            {
                new EnemyConfigRow { Id = "a", Hp = 1, Speed = 1f, Damage = 1 },
                new EnemyConfigRow { Id = "a", Hp = 1, Speed = 1f, Damage = 1 },
            };

            var violations = ConfigValidator.ValidateRows(rows, typeof(EnemyConfigRow), "RawTable");

            Assert.AreEqual(1, violations.Count);
            StringAssert.Contains("[RawTable]", violations[0]);
        }
    }
}
