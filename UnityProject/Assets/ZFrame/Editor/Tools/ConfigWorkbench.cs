using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace ZFrame.Editor
{
    /// <summary>
    /// CSV 配置工作台。选定 ConfigTable 资产后表格化显示全部行（SerializedProperty
    /// 驱动，直接写回资产），支持行内编辑/增删行；按钮：
    ///  - 导出 CSV：复用 A-M1-02 的 <see cref="CsvExporter.Export"/>；
    ///  - 校验当前表：复用 <see cref="ConfigValidator.ValidateRows"/>；
    ///  - 全表扫描：Assets/Configs 下所有配置表逐个校验并汇总。
    /// 批处理入口：<see cref="ValidateAllFromCommandLine"/>（全表扫描，违规 exit 1）。
    /// 窗口菜单：ZFrame/Tools/配置工作台。
    /// </summary>
    public sealed class ConfigWorkbench : EditorWindow
    {
        private const string RowsFieldName = "_rows";
        private const string CsvDir = "Assets/Configs/CSV";

        private ScriptableObject _table;
        private SerializedObject _serialized;
        private Vector2 _scroll;
        private List<string> _violations;
        private string _scanSummary;

        [MenuItem("ZFrame/Tools/配置工作台")]
        private static void Open()
        {
            GetWindow<ConfigWorkbench>("ZFrame 配置工作台");
        }

        private void OnEnable()
        {
            TryBind(Selection.activeObject as ScriptableObject);
        }

        private void OnSelectionChange()
        {
            TryBind(Selection.activeObject as ScriptableObject);
            Repaint();
        }

        private void TryBind(ScriptableObject candidate)
        {
            if (candidate == null || FindRowType(candidate.GetType()) == null)
            {
                return; // 保留当前绑定，避免误选其它资产时丢失编辑上下文
            }

            _table = candidate;
            _serialized = new SerializedObject(_table);
            _violations = null;
            _scanSummary = null;
        }

        private void OnGUI()
        {
            var picked = EditorGUILayout.ObjectField("配置表资产", _table, typeof(ScriptableObject), false)
                as ScriptableObject;
            if (picked != _table)
            {
                if (picked != null && FindRowType(picked.GetType()) == null)
                {
                    EditorUtility.DisplayDialog("配置工作台", "选中的资产不是 ConfigTable 派生类型。", "确定");
                }
                else
                {
                    _table = picked;
                    _serialized = _table != null ? new SerializedObject(_table) : null;
                    _violations = null;
                    _scanSummary = null;
                }
            }

            if (_table == null || _serialized == null)
            {
                EditorGUILayout.HelpBox(
                    "请选择一个 ConfigTable 资产（如 Assets/Configs/CFG_EnemyTable.asset）。\n"
                    + "也可在 Project 窗口选中资产后自动绑定。",
                    MessageType.Info);
                return;
            }

            DrawToolbar();
            GUILayout.Space(6f);
            DrawRowsGrid();
            GUILayout.Space(6f);
            DrawValidationSection();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("导出 CSV", GUILayout.Width(90f)))
            {
                ExportCsv();
            }

            if (GUILayout.Button("校验当前表", GUILayout.Width(100f)))
            {
                _violations = ValidateTable(_table);
                _scanSummary = null;
            }

            if (GUILayout.Button("全表扫描 (Assets/Configs)", GUILayout.Width(180f)))
            {
                RunFullScan();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRowsGrid()
        {
            var rows = _serialized.FindProperty(RowsFieldName);
            if (rows == null)
            {
                EditorGUILayout.HelpBox($"资产上找不到序列化字段 {RowsFieldName}。", MessageType.Warning);
                return;
            }

            var columns = ColumnNames(_table.GetType());
            _scroll = GUILayout.BeginScrollView(_scroll);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(22f);
            foreach (var column in columns)
            {
                GUILayout.Label(column, EditorStyles.boldLabel, GUILayout.MinWidth(80f));
            }

            GUILayout.Width(24f);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < rows.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(i.ToString(), GUILayout.Width(20f));
                var element = rows.GetArrayElementAtIndex(i);
                foreach (var column in columns)
                {
                    var cell = FindCellProperty(element, column);
                    if (cell != null)
                    {
                        var cached = GUI.backgroundColor;
                        EditorGUI.BeginChangeCheck();
                        EditorGUILayout.PropertyField(cell, GUIContent.none, false, GUILayout.MinWidth(80f));
                        if (EditorGUI.EndChangeCheck())
                        {
                            GUI.backgroundColor = Color.yellow; // 有未保存修改的视觉提示
                        }

                        GUI.backgroundColor = cached;
                    }
                    else
                    {
                        GUILayout.Label("-", GUILayout.MinWidth(80f));
                    }
                }

                if (GUILayout.Button("×", GUILayout.Width(24f)))
                {
                    rows.DeleteArrayElementAtIndex(i);
                }

                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ 添加行", GUILayout.Width(90f)))
            {
                rows.arraySize++;
                EditorUtility.SetDirty(_table);
            }

            _serialized.ApplyModifiedProperties();
            GUILayout.EndScrollView();
            EditorGUILayout.HelpBox($"行数: {rows.arraySize}（修改直接写回资产；导出 CSV 会同步到 {CsvDir}/）", MessageType.None);
        }

        private void DrawValidationSection()
        {
            if (!string.IsNullOrEmpty(_scanSummary))
            {
                EditorGUILayout.HelpBox(_scanSummary, MessageType.Info);
            }

            if (_violations == null)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                _violations.Count == 0 ? "PASS: 无违规" : $"FAIL: {_violations.Count} 处违规",
                _violations.Count == 0 ? MessageType.Info : MessageType.Error);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(160f));
            foreach (var violation in _violations)
            {
                EditorGUILayout.LabelField(violation);
            }

            EditorGUILayout.EndScrollView();
        }

        private void ExportCsv()
        {
            var rowType = FindRowType(_table.GetType());
            var assetName = _table.name;
            var csvPath = $"{CsvDir}/{CsvFileNameFor(assetName)}";
            try
            {
                typeof(CsvExporter)
                    .GetMethod(nameof(CsvExporter.Export), BindingFlags.Public | BindingFlags.Static)
                    ?.MakeGenericMethod(rowType)
                    .Invoke(null, new object[] { _table, csvPath });
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("导出 CSV", $"导出失败: {ex.InnerException?.Message ?? ex.Message}", "确定");
                return;
            }

            AssetDatabase.Refresh();
            ShowNotification(new GUIContent($"已导出 {csvPath}"));
        }

        private void RunFullScan()
        {
            var sb = new StringBuilder();
            var total = 0;
            var tableCount = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets/Configs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (asset == null || FindRowType(asset.GetType()) == null)
                {
                    continue;
                }

                tableCount++;
                var rows = asset.GetType().GetProperty("Rows")?.GetValue(asset) as System.Collections.IEnumerable;
                var violations = ConfigValidator.ValidateRows(rows, FindRowType(asset.GetType()), asset.name);
                total += violations.Count;
                sb.AppendLine($"{path}: {violations.Count} 处违规");
                foreach (var violation in violations)
                {
                    sb.AppendLine("  " + violation);
                }
            }

            _scanSummary = $"全表扫描: {tableCount} 张表, 共 {total} 处违规";
            _violations = sb.ToString().Split('\n')
                .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
                .ToList();
            if (_violations.Count == 0)
            {
                _violations = new List<string>();
            }

            Debug.Log($"[ConfigWorkbench] {_scanSummary}\n{sb}");
        }

        /// <summary>Batch entry: full scan of Assets/Configs, write report, exit 1 on violations.</summary>
        public static void ValidateAllFromCommandLine()
        {
            var report = new StringBuilder();
            report.AppendLine($"ZFrame 配置工作台全表扫描 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var total = 0;
            var tableCount = 0;
            var lines = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets/Configs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                var rowType = asset != null ? FindRowType(asset.GetType()) : null;
                if (rowType == null)
                {
                    continue;
                }

                tableCount++;
                var rows = asset.GetType().GetProperty("Rows")?.GetValue(asset) as System.Collections.IEnumerable;
                var violations = ConfigValidator.ValidateRows(rows, rowType, asset.name);
                total += violations.Count;
                lines.AddRange(violations);
            }

            report.AppendLine($"扫描表数: {tableCount}, 违规数: {total}");
            report.AppendLine(total == 0 ? "结果: PASS" : "结果: FAIL");
            report.AppendLine(new string('-', 60));
            foreach (var line in lines)
            {
                report.AppendLine(line);
            }

            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs",
                "workbench_scan_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ConfigWorkbench] report written to {reportPath}\n{report}");
            EditorApplication.Exit(total == 0 ? 0 : 1);
        }

        /// <summary>校验单张表（复用 ConfigValidator 的规则核心）。</summary>
        private static List<string> ValidateTable(ScriptableObject table)
        {
            var rowType = FindRowType(table.GetType());
            if (rowType == null)
            {
                return new List<string> { $"[{table.name}] 不是 ConfigTable 派生类型" };
            }

            var rows = table.GetType().GetProperty("Rows")?.GetValue(table) as System.Collections.IEnumerable;
            return ConfigValidator.ValidateRows(rows, rowType, table.name);
        }

        /// <summary>CFG_EnemyTable -> enemy.csv（CsvExporter.TableAssetPathFor 的逆向约定）。</summary>
        public static string CsvFileNameFor(string assetName)
        {
            var stem = assetName.StartsWith("CFG_", StringComparison.Ordinal)
                ? assetName.Substring(4)
                : assetName;
            if (stem.EndsWith("Table", StringComparison.Ordinal))
            {
                stem = stem.Substring(0, stem.Length - 5);
            }

            return stem.ToLowerInvariant() + ".csv";
        }

        /// <summary>沿类型链向上找 ConfigTable&lt;TRow&gt; 的行类型（与 ConfigValidator 一致的判定）。</summary>
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

        /// <summary>
        /// 列名 = 行类型可读写公共属性（基类 ConfigRow 在前，与 CSV 列序一致）。
        /// 单元格定位：[field: SerializeField] 自动属性序列化为 &lt;Name&gt;k__BackingField。
        /// </summary>
        private static string[] ColumnNames(Type rowType)
        {
            var chain = new List<Type>();
            for (var t = rowType; t != null && t != typeof(object); t = t.BaseType)
            {
                chain.Add(t);
            }

            chain.Reverse();
            var names = new List<string>();
            foreach (var t in chain)
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.CanRead && p.CanWrite)
                    {
                        names.Add(p.Name);
                    }
                }
            }

            return names.ToArray();
        }

        private static SerializedProperty FindCellProperty(SerializedProperty element, string columnName)
        {
            var iterator = element.Copy();
            var end = element.GetEndProperty();
            while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
            {
                var name = iterator.name;
                if (name.StartsWith("<", StringComparison.Ordinal))
                {
                    name = name.Substring(1, name.Length - 1 - "k__BackingField".Length - 1);
                }

                if (name == columnName)
                {
                    return iterator.Copy();
                }
            }

            return null;
        }
    }
}
