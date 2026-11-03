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
    /// </summary>
    public static class CsvExporter
    {
        /// <summary>Convention: Assets/Configs/CSV/enemy.csv -> Assets/Configs/CFG_EnemyTable.asset.</summary>
        public static string TableAssetPathFor(string csvPath)
        {
            var dir = Path.GetDirectoryName(csvPath)?.Replace('\\', '/') ?? "Assets/Configs/CSV";
            var csvName = Path.GetFileNameWithoutExtension(csvPath).ToLowerInvariant();
            var pascalName = char.ToUpperInvariant(csvName[0]) + csvName.Substring(1);
            return $"{dir}/../CFG_{pascalName}Table.asset";
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

        /// <summary>Core export: table -> csv text (header + one line per row).</summary>
        public static string BuildCsv<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow
        {
            var props = SerializableProps(typeof(TRow));
            var sb = new StringBuilder();
            var header = new string[props.Count];
            for (int i = 0; i < props.Count; i++)
            {
                header[i] = props[i].Name;
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
    }
}
#endif
