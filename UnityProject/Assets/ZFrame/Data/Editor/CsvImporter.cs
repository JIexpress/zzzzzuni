#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using ZFrame.Data.Config;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// CSV -> ConfigTable importer. Core conversion is pure
    /// (<see cref="ImportRows{TRow}"/>, unit-testable without assets); the
    /// file/asset wrapper (<see cref="Import{TRow}"/>) creates or updates
    /// the ScriptableObject. Errors are collected and reported once at the
    /// end — a bad row never aborts the pass.
    ///
    /// Supported column types: int / float / bool / string. The header's
    /// first column must be "Id".
    /// </summary>
    public static class CsvImporter
    {
        /// <summary>
        /// Maps csv file name (lower case, no extension) to its concrete
        /// table type, so a right-click import knows what to build.
        /// Extend as tables are added.
        /// </summary>
        private static readonly Dictionary<string, Type> TableByCsvName =
            new Dictionary<string, Type>
            {
                ["enemy"] = typeof(EnemyConfigTable),
            };

        /// <summary>Default asset location for a csv under Assets/Configs/CSV/x.csv.</summary>
        public const string CsvRoot = "Assets/Configs/CSV";

        /// <summary>Core import: csv text into an in-memory table (no file/asset IO).</summary>
        public static CsvImportReport ImportRows<TRow>(string csvText, ConfigTable<TRow> table)
            where TRow : ConfigRow, new()
        {
            var report = new CsvImportReport();
            var parsed = CsvUtility.Parse(csvText);
            if (parsed.Count == 0)
            {
                report.Errors.Add("CSV 为空（无表头）");
                return report;
            }

            var header = parsed[0];
            if (!string.Equals(header[0], "Id", StringComparison.Ordinal))
            {
                report.Errors.Add($"表头首列必须为 Id，实际为 '{header[0]}'");
                return report;
            }

            // header -> writable property map (skip column 0 = Id).
            var rowType = typeof(TRow);
            var props = new List<PropertyInfo>();
            for (int c = 1; c < header.Length; c++)
            {
                var prop = rowType.GetProperty(header[c],
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanRead || !prop.CanWrite)
                {
                    report.Errors.Add($"表头列 '{header[c]}' 在 {rowType.Name} 上无对应可写字段");
                    continue;
                }

                props.Add(prop);
            }

            // Index existing rows by Id for update semantics.
            var byId = new Dictionary<string, TRow>();
            foreach (var row in table.Rows)
            {
                byId[row.Id] = row;
            }

            var seenIds = new HashSet<string>();
            for (int r = 1; r < parsed.Count; r++)
            {
                var cells = parsed[r];
                var line = r + 1; // human friendly (header is line 1)

                var id = cells.Length > 0 ? cells[0] : "";
                if (string.IsNullOrEmpty(id))
                {
                    report.Errors.Add($"第 {line} 行: Id 为空");
                    continue;
                }

                if (!seenIds.Add(id))
                {
                    report.Errors.Add($"第 {line} 行: Id '{id}' 在 CSV 内重复");
                    continue;
                }

                var isNew = false;
                if (!byId.TryGetValue(id, out var row))
                {
                    row = new TRow { Id = id };
                    byId[id] = row;
                    isNew = true;
                }

                for (int c = 0; c < props.Count; c++)
                {
                    var prop = props[c];
                    var column = c + 1; // cell 0 is Id
                    var raw = column < cells.Length ? cells[column] : "";
                    if (!TryConvert(raw, prop.PropertyType, out var value))
                    {
                        report.Errors.Add(
                            $"第 {line} 行 Id '{id}': 字段 '{prop.Name}' 值 '{raw}' 无法转换为 {prop.PropertyType.Name}");
                        continue;
                    }

                    prop.SetValue(row, value);
                }

                if (isNew)
                {
                    table.AddRow(row);
                    report.Added++;
                }
                else
                {
                    report.Updated++;
                }
            }

            // Rows kept in the table but absent from the csv: reported, not deleted.
            foreach (var existing in table.Rows)
            {
                if (!seenIds.Contains(existing.Id))
                {
                    report.MissingInCsv.Add(existing.Id);
                }
            }

            return report;
        }

        /// <summary>
        /// File/asset wrapper: reads <paramref name="csvPath"/>, creates the
        /// table asset at <paramref name="tableAssetPath"/> when missing, or
        /// updates it by Id, then saves. When the asset is missing, Unity
        /// cannot CreateInstance a generic ScriptableObject, so
        /// <paramref name="tableFactory"/> must build the concrete table
        /// type (e.g. <c>() =&gt; CreateInstance&lt;EnemyConfigTable&gt;()</c>).
        /// </summary>
        public static CsvImportReport Import<TRow>(string csvPath, string tableAssetPath,
            Func<ConfigTable<TRow>> tableFactory) where TRow : ConfigRow, new()
        {
            var csvText = File.ReadAllText(csvPath, new UTF8Encoding(false));
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(tableAssetPath);
            if (table == null)
            {
                table = tableFactory != null ? tableFactory() : null;
                if (table == null)
                {
                    throw new InvalidOperationException(
                        $"表资产 {tableAssetPath} 不存在且未提供 {nameof(tableFactory)}"
                        + "（Unity 无法为泛型 ScriptableObject 直接 CreateInstance）");
                }

                AssetDatabase.CreateAsset(table, tableAssetPath);
            }

            var report = ImportRows(csvText, table);
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CsvImporter] {csvPath} -> {tableAssetPath}: {report}");
            foreach (var error in report.Errors)
            {
                Debug.LogError($"[CsvImporter] {error}");
            }

            return report;
        }

        /// <summary>Right-click a csv in the Project window: Assets/ZFrame/Import as Config Table.</summary>
        [MenuItem("Assets/ZFrame/Import as Config Table")]
        private static void ImportSelectedCsv()
        {
            var csvPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(csvPath) || !csvPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[CsvImporter] 请先选中一个 .csv 文件");
                return;
            }

            var csvName = Path.GetFileNameWithoutExtension(csvPath).ToLowerInvariant();
            if (!TableByCsvName.TryGetValue(csvName, out var tableType))
            {
                Debug.LogError($"[CsvImporter] csv '{csvName}' 未注册表类型（见 {nameof(CsvImporter)}.TableByCsvName）");
                return;
            }

            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var rowType = tableType.GetGenericArguments()[0];
            var method = typeof(CsvImporter).GetMethod(nameof(Import), BindingFlags.Public | BindingFlags.Static)
                .MakeGenericMethod(rowType);
            // Func<ConfigTable<TRow>> 工厂：CreateInstance<T> 绑定到具体表类型。
            var funcType = typeof(Func<>).MakeGenericType(tableType);
            var createMethod = typeof(ScriptableObject)
                .GetMethod(nameof(ScriptableObject.CreateInstance), new[] { typeof(Type) })
                .MakeGenericMethod(tableType);
            var factory = Delegate.CreateDelegate(funcType, createMethod);
            method.Invoke(null, new object[] { csvPath, assetPath, factory });
        }

        [MenuItem("Assets/ZFrame/Import as Config Table", true)]
        private static bool ImportSelectedCsvValid()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>int / float / bool / string conversion with invariant culture.</summary>
        internal static bool TryConvert(string raw, Type type, out object value)
        {
            try
            {
                if (type == typeof(string))
                {
                    value = raw;
                    return true;
                }

                value = Convert.ChangeType(string.IsNullOrEmpty(raw) ? null : raw, type,
                    System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }
    }
}
#endif
