#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;


namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// CSV -> C# row-type code generator (TASK-A-CONF-01). For a csv under
    /// <see cref="CsvImporter.CsvRoot"/> the annotated header row defines the
    /// schema (<c>Name:type[min..max]</c>, types string/int/float/bool); the
    /// generator emits the row class into <see cref="GeneratedRoot"/>. A bad
    /// header is reported and produces NO file — never broken code.
    ///
    /// Triggers: menu (<see cref="GenerateAllMenu"/> / <see cref="GenerateSelectedCsv"/>),
    /// import hook (<see cref="ConfigCsvPostprocessor"/>) and batch:
    ///   -executeMethod ZFrame.Data.ConfigEditor.ConfigCodeGenerator.GenerateAllFromCommandLine
    /// </summary>
    public static class ConfigCodeGenerator
    {
        /// <summary>Convention: generated row types live here (source-controlled artifacts).</summary>
        public const string GeneratedRoot = "Assets/ZFrame/Data/Config/Generated";

        /// <summary>
        /// Row class name for a csv: Pascal stem + "ConfigRow"
        /// (enemy.csv -> EnemyConfigRow, matching the hand-migrated sample).
        /// Returns null when the stem cannot produce a valid C# identifier.
        /// </summary>
        public static string RowClassNameFor(string csvPath)
        {
            var stem = Path.GetFileNameWithoutExtension(csvPath ?? "");
            if (string.IsNullOrEmpty(stem))
            {
                return null;
            }

            var pascal = char.ToUpperInvariant(stem[0]) + stem.Substring(1);
            var name = pascal + "ConfigRow";
            return ConfigRowSchema.IsValidIdentifier(name) ? name : null;
        }

        /// <summary>Default asset path of the generated row type for a csv.</summary>
        public static string RowSourcePathFor(string csvPath)
        {
            return $"{GeneratedRoot}/{RowClassNameFor(csvPath)}.cs";
        }

        /// <summary>
        /// Generates the row type for one csv. Reads the header, validates the
        /// schema and writes <see cref="GeneratedRoot"/>/{Class}.cs only when
        /// content changed. Errors => report only, nothing written.
        /// <paramref name="outputRoot"/> overrides the destination folder
        /// (asset path form; tests use it to stay out of Assets).
        /// </summary>
        public static ConfigCodegenReport GenerateForRowType(string csvPath, string outputRoot = null)
        {
            var report = new ConfigCodegenReport { SourceCsvPath = ToAssetPath(csvPath) };
            try
            {
                GenerateForRowTypeCore(csvPath, outputRoot, report);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"生成失败: {ex.Message}");
            }

            return report;
        }

        /// <summary>Generates row types for every csv under <see cref="CsvImporter.CsvRoot"/>.</summary>
        public static List<ConfigCodegenReport> GenerateAll()
        {
            var reports = new List<ConfigCodegenReport>();
            var csvDir = ToAbsolute(CsvImporter.CsvRoot);
            if (!Directory.Exists(csvDir))
            {
                var report = new ConfigCodegenReport { SourceCsvPath = CsvImporter.CsvRoot };
                report.Errors.Add($"CSV 目录不存在: {CsvImporter.CsvRoot}");
                reports.Add(report);
                return reports;
            }

            foreach (var fullPath in Directory.GetFiles(csvDir, "*.csv"))
            {
                reports.Add(GenerateForRowType(ToAssetPath(fullPath)));
            }

            return reports;
        }

        private static void GenerateForRowTypeCore(string csvPath, string outputRoot, ConfigCodegenReport report)
        {
            if (string.IsNullOrEmpty(csvPath))
            {
                report.Errors.Add("csvPath 为空");
                return;
            }

            var className = RowClassNameFor(csvPath);
            if (className == null)
            {
                report.Errors.Add($"csv 文件名 '{Path.GetFileName(csvPath)}' 无法推导出合法 C# 行类名");
                return;
            }

            if (!File.Exists(csvPath))
            {
                report.Errors.Add($"csv 不存在: {csvPath}");
                return;
            }

            var csvText = File.ReadAllText(csvPath, new UTF8Encoding(false));
            var parsed = CsvUtility.Parse(csvText);
            if (parsed.Count == 0)
            {
                report.Errors.Add("CSV 为空（无表头）");
                return;
            }

            if (!HasAnyTypeAnnotation(parsed[0]))
            {
                // 裸表头 = 旧式仅导入表（如 npc_personality.csv，行类型在
                // Game.Domain 且不按 Generated 约定命名）——跳过，不算错误；
                // 一旦表头带注解则按代码生成表从严校验。
                report.Skipped = true;
                return;
            }

            var schema = ConfigRowSchema.Parse(parsed[0], report.SourceCsvPath, className);
            if (!schema.IsValid)
            {
                report.Errors.AddRange(schema.Errors);
                return; // 坏表头：报错，不产出坏文件
            }

            var code = ConfigRowCodegen.Generate(schema);
            var root = string.IsNullOrEmpty(outputRoot) ? GeneratedRoot : outputRoot;
            var fullPath = ToAbsolute($"{root}/{className}.cs");
            report.WroteFile = ConfigRowCodegen.WriteIfChanged(fullPath, code);
        }

        [MenuItem("ZFrame/Config/生成行类型(全部 CSV)")]
        private static void GenerateAllMenu()
        {
            var reports = GenerateAll();
            var failed = 0;
            foreach (var report in reports)
            {
                if (report.WroteFile)
                {
                    Debug.Log($"[ConfigCodeGenerator] 行类型已重新生成: {report.SourceCsvPath}");
                }

                foreach (var error in report.Errors)
                {
                    failed++;
                    Debug.LogError($"[ConfigCodeGenerator] {report.SourceCsvPath}: {error}");
                }
            }

            if (failed == 0)
            {
                Debug.Log($"[ConfigCodeGenerator] 全部 {reports.Count} 张表行类型生成完成（0 错误）");
            }

            AssetDatabase.Refresh();
        }

        [MenuItem("Assets/ZFrame/Generate Config Row Type")]
        private static void GenerateSelectedCsv()
        {
            var csvPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(csvPath) || !csvPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[ConfigCodeGenerator] 请先选中一个 .csv 文件");
                return;
            }

            var report = GenerateForRowType(csvPath);
            if (report.WroteFile)
            {
                Debug.Log($"[ConfigCodeGenerator] 行类型已生成: {report}");
            }

            foreach (var error in report.Errors)
            {
                Debug.LogError($"[ConfigCodeGenerator] {error}");
            }

            AssetDatabase.Refresh();
        }

        [MenuItem("Assets/ZFrame/Generate Config Row Type", true)]
        private static bool GenerateSelectedCsvValid()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Batch entry: generate all, write report, exit 1 on any error. Never Refreshes (batch discipline).</summary>
        public static void GenerateAllFromCommandLine()
        {
            var reports = GenerateAll();
            var failed = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"ZFrame 行类型生成报告 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var report in reports)
            {
                if (!report.Ok)
                {
                    failed++;
                }

                sb.Append(report.ToReportText());
            }

            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"表数: {reports.Count}, 错误表数: {failed}");
            sb.AppendLine(failed == 0 ? "结果: PASS" : "结果: FAIL");
            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs",
                "config_codegen_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ConfigCodeGenerator] report written to {reportPath}\n{sb}");
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        /// <summary>True when at least one header cell carries a :type annotation (codegen-table marker).</summary>
        private static bool HasAnyTypeAnnotation(IReadOnlyList<string> headerCells)
        {
            foreach (var cell in headerCells)
            {
                if (ConfigRowSchema.TryParseHeaderCell(cell, out var parsed, out _) && parsed.HasType)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Asset-relative path ("Assets/...") from an absolute or asset-relative input.</summary>
        private static string ToAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var normalized = path.Replace('\\', '/');
            var root = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return "Assets/" + normalized.Substring(root.Length + 1);
            }

            return normalized;
        }

        /// <summary>Absolute filesystem path of an asset-relative path.</summary>
        private static string ToAbsolute(string assetPath)
        {
            if (Path.IsPathRooted(assetPath))
            {
                return assetPath;
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }
    }
}
#endif
