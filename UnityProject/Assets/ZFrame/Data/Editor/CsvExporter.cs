#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using ZFrame.Data.Config;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// ConfigTable -> CSV exporter. Core serialization is pure
    /// (<see cref="BuildCsv{TRow}"/>); the file wrapper writes UTF-8
    /// (no BOM) with LF line endings — matches the repo file conventions.
    /// The header carries the code-generator schema annotations
    /// (<c>Name:type[min,max]</c>), so export -> import is lossless and the
    /// committed csv stays the single schema source of truth.
    /// </summary>
    public static class CsvExporter
    {
        /// <summary>
        /// Convention: Assets/Configs/CSV/enemy.csv -> Assets/Configs/CFG_EnemyTable.asset.
        /// The result is always the canonical AssetDatabase form (see
        /// <see cref="NormalizeAssetPath"/>): dot segments collapse, so the path
        /// can be handed to <c>AssetDatabase.LoadAssetAtPath</c> as-is.
        /// </summary>
        public static string TableAssetPathFor(string csvPath)
        {
            var csvAssetPath = NormalizeAssetPath(csvPath);
            var dir = Path.GetDirectoryName(csvAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir))
            {
                dir = "Assets";
            }

            var csvName = Path.GetFileNameWithoutExtension(csvAssetPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(csvName))
            {
                throw new ArgumentException($"CSV 路径非法，无法推导表资产路径: '{csvPath}'", nameof(csvPath));
            }

            var pascalName = char.ToUpperInvariant(csvName[0]) + csvName.Substring(1);
            return NormalizeAssetPath($"{dir}/../CFG_{pascalName}Table.asset");
        }

        /// <summary>
        /// Canonical AssetDatabase path: forward slashes, no "." / ".." / doubled
        /// separators, project-root relative (so it starts with "Assets/").
        /// Relative input is resolved against the project root, not the process
        /// CWD, so the result is independent of how Unity was launched.
        ///
        /// Why: <c>TableAssetPathFor</c> used to return
        /// "Assets/Configs/CSV/../CFG_XTable.asset"; <c>LoadAssetAtPath</c> treats
        /// that literally (returns null even for an existing asset), and
        /// <c>CreateAsset</c> then overwrote the real table with an empty object
        /// (TASK-A-CONF-02 rework).
        /// </summary>
        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var projectRoot = ProjectRootFullPath();
            var raw = path.Replace('\\', '/');
            // 相对路径按**项目根**解析，而不是进程 CWD（batchmode 下 CWD 不一定可靠）。
            var full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(projectRoot, raw))
                .Replace('\\', '/');

            if (string.Equals(full, projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (full.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return full.Substring(projectRoot.Length + 1);
            }

            // Outside the project (e.g. a temp path in tests): nothing to relativize.
            return full;
        }

        /// <summary>Absolute project root (".../UnityProject"), forward slashes, no trailing slash.</summary>
        private static string ProjectRootFullPath()
        {
            var root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."))
                .Replace('\\', '/');
            return root.TrimEnd('/');
        }

        /// <summary>
        /// Writable public properties, base type (ConfigRow) first so Id is
        /// always the first column.
        /// </summary>
        internal static List<PropertyInfo> SerializableProps(Type rowType)
        {
            var chain = new List<Type>();
            for (var t = rowType; t != null && t != typeof(object); t = t.BaseType)
            {
                chain.Add(t);
            }

            chain.Reverse(); // base (ConfigRow, holds Id) first
            var props = new List<PropertyInfo>();
            foreach (var t in chain)
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.CanRead && p.CanWrite)
                    {
                        props.Add(p);
                    }
                }
            }

            return props;
        }

        /// <summary>Core export: table -> csv text (annotated header + one line per row).</summary>
        public static string BuildCsv<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow
        {
            var props = SerializableProps(typeof(TRow));
            var sb = new StringBuilder();
            var header = new string[props.Count];
            for (int i = 0; i < props.Count; i++)
            {
                header[i] = AnnotatedHeaderCell(props[i]);
            }

            sb.Append(CsvUtility.JoinLine(header)).Append('\n');

            foreach (var row in table.Rows)
            {
                var cells = new string[props.Count];
                for (int i = 0; i < props.Count; i++)
                {
                    var value = props[i].GetValue(row);
                    cells[i] = value is IFormattable f
                        ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                        : value?.ToString() ?? "";
                }

                sb.Append(CsvUtility.JoinLine(cells)).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>File wrapper: writes the csv (UTF-8 no BOM, LF) to <paramref name="csvPath"/>.</summary>
        public static void Export<TRow>(ConfigTable<TRow> table, string csvPath) where TRow : ConfigRow
        {
            var dir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(csvPath, BuildCsv(table), new UTF8Encoding(false));
            UnityEditor.AssetDatabase.Refresh();
            UnityEngine.Debug.Log($"[CsvExporter] exported {table.name} ({table.Rows.Count} rows) -> {csvPath}");
        }

        /// <summary>
        /// Schema annotation of one header cell, derived from the property:
        /// "Hp" -> "Hp:int", plus "[min..max]" when the property (or its
        /// backing field) carries a [Range]. Unknown CLR types export as
        /// string (the importer treats unlisted types as verbatim text).
        /// </summary>
        private static string AnnotatedHeaderCell(PropertyInfo prop)
        {
            var typeToken = prop.PropertyType == typeof(int) ? "int"
                : prop.PropertyType == typeof(float) ? "float"
                : prop.PropertyType == typeof(bool) ? "bool"
                : "string";
            if ((prop.PropertyType == typeof(int) || prop.PropertyType == typeof(float))
                && ConfigValidator.TryGetRange(prop, out var min, out var max))
            {
                return $"{prop.Name}:{typeToken}[{FormatRangeNumber(min)}..{FormatRangeNumber(max)}]";
            }

            return $"{prop.Name}:{typeToken}";
        }

        /// <summary>Invariant, shortest stable form for a range bound (1 -> "1", 0.5 -> "0.5").</summary>
        private static string FormatRangeNumber(double value)
        {
            return Math.Floor(value) == value
                ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
#endif
