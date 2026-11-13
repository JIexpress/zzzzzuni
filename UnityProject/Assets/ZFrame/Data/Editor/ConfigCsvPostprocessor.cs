#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// Import hook (TASK-A-CONF-01): whenever a csv under
    /// <see cref="CsvImporter.CsvRoot"/> is (re)imported, regenerate its row
    /// type into <see cref="ConfigCodeGenerator.GeneratedRoot"/>. The write is
    /// content-diffed (identical schema never rewrites, so no recompile
    /// churn); bare-header tables are skipped; a bad annotated header logs
    /// errors and produces no file. New flow: drop an annotated csv in the
    /// folder -> row type appears -> right-click import builds the table.
    /// </summary>
    public sealed class ConfigCsvPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            var csvRootPrefix = CsvImporter.CsvRoot + "/";
            foreach (var path in importedAssets)
            {
                if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!path.StartsWith(csvRootPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var report = ConfigCodeGenerator.GenerateForRowType(path);
                if (report.WroteFile)
                {
                    Debug.Log($"[ConfigCsvPostprocessor] 行类型已重新生成: {report}");
                }

                foreach (var error in report.Errors)
                {
                    Debug.LogError($"[ConfigCsvPostprocessor] {report.SourceCsvPath}: {error}");
                }
            }
        }
    }
}
#endif
