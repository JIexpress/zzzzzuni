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
    /// Header cells may be bare ("Hp", matched to the row property by name)
    /// or annotated ("Hp:int[1..10000]", schema format of the code generator;
    /// the annotation type must match the property). Supported column types:
    /// int / float / bool / string. The header's first column must be "Id".
    /// </summary>
    public static class CsvImporter
    {
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
            if (!ConfigRowSchema.TryParseHeaderCell(header[0], out var idCell, out var idError)
                || !string.Equals(idCell.Name, "Id", StringComparison.Ordinal))
            {
                report.Errors.Add(
                    $"表头首列必须为 Id（或 Id:string），实际为 '{header[0]}'"
                    + (idError != null ? $"（{idError}）" : ""));
                return report;
            }

            // header -> writable property map (skip column 0 = Id).
            var rowType = typeof(TRow);
            var props = new List<PropertyInfo>();
            for (int c = 1; c < header.Length; c++)
            {
                if (!ConfigRowSchema.TryParseHeaderCell(header[c], out var cell, out var cellError))
                {
                    report.Errors.Add($"表头列 '{header[c]}' 非法: {cellError}");
                    continue;
                }

                var prop = rowType.GetProperty(cell.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanRead || !prop.CanWrite)
                {
                    report.Errors.Add($"表头列 '{cell.Name}' 在 {rowType.Name} 上无对应可写字段");
                    continue;
                }

                if (cell.HasType && prop.PropertyType != ConfigColumnTypeUtil.ToClrType(cell.Type))
                {
                    report.Errors.Add(
                        $"表头列 '{cell.Name}' 标注类型为 {ConfigColumnTypeUtil.ToSchemaToken(cell.Type)}"
                        + $"，与 {rowType.Name}.{prop.Name}（{prop.PropertyType.Name}）不一致");
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
        ///
        /// Non-destructive rule (TASK-A-CONF-02 rework): an existing asset is
        /// always updated **in place** (LoadAssetAtPath + rows + SetDirty), never
        /// re-created. DeleteAsset+CreateAsset was the alternative, but it mints a
        /// new GUID and breaks every reference to the table (scene/prefab/inspector
        /// links, and the committed CFG_*.asset's identity), so in-place wins.
        /// If something else already occupies the path, we refuse instead of
        /// overwriting it.
        /// </summary>
        public static CsvImportReport Import<TRow>(string csvPath, string tableAssetPath,
            Func<ConfigTable<TRow>> tableFactory) where TRow : ConfigRow, new()
        {
            var assetPath = CsvExporter.NormalizeAssetPath(tableAssetPath);
            var csvText = File.ReadAllText(csvPath, new UTF8Encoding(false));
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            if (table == null)
            {
                // 目标路径上已有别的东西（类型不符/被占用）时绝不覆盖——旧实现会先建后毁。
                var occupant = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                if (occupant != null)
                {
                    throw new InvalidOperationException(
                        $"表资产 {assetPath} 已存在但类型不是 ConfigTable<{typeof(TRow).Name}>"
                        + $"（实际 {occupant.GetType().Name}），拒绝覆盖");
                }

                table = tableFactory != null ? tableFactory() : null;
                if (table == null)
                {
                    throw new InvalidOperationException(
                        $"表资产 {assetPath} 不存在且未提供 {nameof(tableFactory)}"
                        + "（Unity 无法为泛型 ScriptableObject 直接 CreateInstance）");
                }

                AssetDatabase.CreateAsset(table, assetPath);
                // CreateAsset 在路径冲突等情况下会销毁传入对象；此时对象是 fake-null，
                // 继续 ImportRows/SetDirty 只会得到 "Parameter name: target" 的迷雾异常。
                if (table == null)
                {
                    throw new InvalidOperationException(
                        $"CreateAsset({assetPath}) 后表对象已失效（被 Unity 销毁），导入中止");
                }
            }

            var report = ImportRows(csvText, table);
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CsvImporter] {csvPath} -> {assetPath}: {report}");
            foreach (var error in report.Errors)
            {
                Debug.LogError($"[CsvImporter] {error}");
            }

            return report;
        }

        /// <summary>
        /// Programmatic full-auto import (TASK-A-CONF-02): resolves the table
        /// type from <see cref="ConfigTableRegistry"/> (no hand registry) and
        /// imports the csv, creating the asset at the conventional path when
        /// missing. Returns the import report; throws when the csv stem has no
        /// registered table type.
        /// </summary>
        public static CsvImportReport ImportRegisteredCsv(string csvPath, string tableAssetPath = null)
        {
            if (!ConfigTableRegistry.TryGetTableType(csvPath, out var tableType))
            {
                var key = ConfigTableRegistry.CsvKeyOf(csvPath);
                throw new InvalidOperationException(
                    $"csv '{key}' 没有已注册的表类型（约定：<Pascal>ConfigTable 派生自 ConfigTable<T>）");
            }

            var assetPath = string.IsNullOrEmpty(tableAssetPath)
                ? CsvExporter.TableAssetPathFor(csvPath)
                : tableAssetPath;
            return ImportByTableType(tableType, csvPath, assetPath);
        }

        /// <summary>
        /// Reflection bridge: Import&lt;TRow&gt; needs the TRow at compile time,
        /// the registry only has a Type. Shared by the menu and the programmatic entry.
        /// </summary>
        private static CsvImportReport ImportByTableType(Type tableType, string csvPath, string assetPath)
        {
            var rowType = ConfigTableRegistry.FindRowType(tableType);
            if (rowType == null)
            {
                throw new InvalidOperationException($"{tableType.FullName} 不是 ConfigTable<TRow> 派生类");
            }

            var method = typeof(CsvImporter)
                .GetMethod(nameof(Import), BindingFlags.Public | BindingFlags.Static)
                .MakeGenericMethod(rowType);
            // Func<ConfigTable<TRow>> 工厂：CreateInstance<T>() 绑定到具体表类型。
            // 注意：必须取**泛型**重载 CreateInstance<T>()；按参数表 {typeof(Type)} 取会
            // 命中非泛型 CreateInstance(Type)，MakeGenericMethod 抛
            // "not a generic method definition"（A-CONF-01 的手写注册路径即踩此坑，
            // 当时只读码未实跑，A-CONF-02 首次全链路实测才暴露）。
            var funcType = typeof(Func<>).MakeGenericType(typeof(ConfigTable<>).MakeGenericType(rowType));
            MethodInfo createMethod = null;
            foreach (var candidate in typeof(ScriptableObject).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (candidate.Name == nameof(ScriptableObject.CreateInstance)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 0)
                {
                    createMethod = candidate.MakeGenericMethod(tableType);
                    break;
                }
            }

            if (createMethod == null)
            {
                throw new InvalidOperationException("找不到 ScriptableObject.CreateInstance<T>() 泛型重载");
            }

            var factory = Delegate.CreateDelegate(funcType, createMethod);
            return (CsvImportReport)method.Invoke(null, new object[] { csvPath, assetPath, factory });
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

            if (!ConfigTableRegistry.TryGetTableType(csvPath, out var tableType))
            {
                Debug.LogError(
                    $"[CsvImporter] csv '{ConfigTableRegistry.CsvKeyOf(csvPath)}' 无已注册表类型"
                    + "（约定：生成的 <Pascal>ConfigTable 派生自 ConfigTable<T>）");
                return;
            }

            ImportByTableType(tableType, csvPath, CsvExporter.TableAssetPathFor(csvPath));
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
