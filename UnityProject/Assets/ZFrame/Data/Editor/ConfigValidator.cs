#if UNITY_EDITOR
using System;
using System.Collections;
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
    /// Authoring-time config validation. Current rule set: duplicate Id,
    /// empty Id, numeric Range violations (via [Range] on row properties).
    /// Returns the violation list (for tests) and shows it in a window.
    /// </summary>
    public static class ConfigValidator
    {
        /// <summary>Validates a typed table (thin generic wrapper over the non-generic core).</summary>
        public static List<string> Validate<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow
        {
            return ValidateRows(table.Rows, typeof(TRow), table.name);
        }

        /// <summary>
        /// Non-generic core: validates any row sequence. Violations are
        /// prefixed with "table/row" so the command-line report is greppable.
        /// </summary>
        public static List<string> ValidateRows(IEnumerable rows, Type rowType, string tableName)
        {
            var violations = new List<string>();
            var seen = new HashSet<string>();
            var rangeProps = CollectRangeProps(rowType);
            var index = 0;
            foreach (var obj in rows)
            {
                index++;
                var row = obj as ConfigRow;
                if (row == null)
                {
                    violations.Add($"[{tableName}] 第 {index} 行: null 或非 {rowType.Name} 行");
                    continue;
                }

                if (string.IsNullOrEmpty(row.Id))
                {
                    violations.Add($"[{tableName}] 第 {index} 行: Id 为空");
                }
                else if (!seen.Add(row.Id))
                {
                    violations.Add($"[{tableName}] 第 {index} 行: Id '{row.Id}' 重复");
                }

                foreach (var (prop, range) in rangeProps)
                {
                    var value = prop.GetValue(row);
                    var d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (d < range.min || d > range.max)
                    {
                        violations.Add(
                            $"[{tableName}] Id '{row.Id}': 字段 {prop.Name}={value} 超出 Range [{range.min}, {range.max}]");
                    }
                }
            }

            return violations;
        }

        /// <summary>Validates every ConfigTable asset under Assets/ (script type filter, then FindRowType).</summary>
        public static List<string> ValidateAllConfigAssets()
        {
            var all = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                var rowType = FindRowType(asset.GetType());
                if (rowType == null)
                {
                    continue;
                }

                var rowsProp = asset.GetType().GetProperty("Rows");
                var rows = (IEnumerable)rowsProp.GetValue(asset);
                all.AddRange(ValidateRows(rows, rowType, asset.name));
            }

            return all;
        }

        /// <summary>Batch entry: validate every config asset, write report, exit 1 on violations.</summary>
        public static void ValidateFromCommandLine()
        {
            var violations = ValidateAllConfigAssets();
            var sb = new StringBuilder();
            sb.AppendLine($"ZFrame 配置校验报告 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"违规数: {violations.Count}");
            sb.AppendLine(violations.Count == 0 ? "结果: PASS" : "结果: FAIL");
            sb.AppendLine(new string('-', 60));
            foreach (var violation in violations)
            {
                sb.AppendLine(violation);
            }

            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs",
                "config_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ConfigValidator] report written to {reportPath}\n{sb}");
            EditorApplication.Exit(violations.Count == 0 ? 0 : 1);
        }

        /// <summary>
        /// Range-attributed numeric properties (declared anywhere in the
        /// hierarchy). Reads both property attributes and backing-field
        /// attributes — <c>[field: Range]</c> on auto-properties lands on
        /// the compiler-generated backing field, not the property.
        /// </summary>
        private static List<(PropertyInfo prop, (double min, double max) range)> CollectRangeProps(Type rowType)
        {
            var result = new List<(PropertyInfo, (double, double))>();
            foreach (var prop in rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead)
                {
                    continue;
                }

                var range = FindRange(prop);
                if (range != null)
                {
                    result.Add((prop, (range.min, range.max)));
                }
            }

            return result;
        }

        private static RangeAttribute FindRange(PropertyInfo prop)
        {
            foreach (var attr in prop.GetCustomAttributes(typeof(RangeAttribute), true))
            {
                if (attr is RangeAttribute onProperty)
                {
                    return onProperty;
                }
            }

            var backing = prop.DeclaringType?.GetField(
                $"<{prop.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            return backing?.GetCustomAttribute<RangeAttribute>();
        }

        private static Type FindRowType(Type tableType)
        {
            for (var t = tableType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ConfigTable<>))
                {
                    return t.GetGenericArguments()[0];
                }
            }

            return null;
        }

        /// <summary>Menu: window listing current violations of every config asset.</summary>
        [MenuItem("ZFrame/配置校验")]
        private static void OpenWindow()
        {
            EditorWindow.GetWindow<ValidatorWindow>("ZFrame 配置校验");
        }

        private sealed class ValidatorWindow : EditorWindow
        {
            private Vector2 _scroll;
            private List<string> _violations;

            private void OnGUI()
            {
                if (GUILayout.Button("校验全部配置表") || _violations == null)
                {
                    _violations = ValidateAllConfigAssets();
                    Repaint();
                }

                EditorGUILayout.HelpBox(
                    _violations.Count == 0 ? "PASS: 无违规" : $"FAIL: {_violations.Count} 处违规", MessageType.None);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var violation in _violations)
                {
                    EditorGUILayout.LabelField(violation);
                }

                EditorGUILayout.EndScrollView();
            }
        }
    }
}
#endif
