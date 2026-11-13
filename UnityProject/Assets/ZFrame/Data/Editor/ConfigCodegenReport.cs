#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>Outcome summary of one row-type generation run (errors are collected, never thrown).</summary>
    public sealed class ConfigCodegenReport
    {
        /// <summary>Source table path this report belongs to.</summary>
        public string SourceCsvPath { get; set; }

        /// <summary>All problems found (bad schema, unreadable csv...). Nothing is written when non-empty.</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>True when the generated file content changed and was written this run.</summary>
        public bool WroteFile { get; set; }

        /// <summary>
        /// True when the csv was skipped because its header carries no type
        /// annotation at all — a bare-header table (e.g. npc_personality.csv,
        /// whose row type lives in another assembly) is a legacy CSV-import
        /// table, not a codegen input. Not an error.
        /// </summary>
        public bool Skipped { get; set; }

        /// <summary>True when no errors were collected.</summary>
        public bool Ok
        {
            get { return Errors.Count == 0; }
        }

        /// <summary>Human readable one-line summary.</summary>
        public override string ToString()
        {
            return $"{SourceCsvPath}: wrote={WroteFile}, skipped={Skipped}, errors={Errors.Count}";
        }

        /// <summary>Multi-line report block (used by the batch entry).</summary>
        public string ToReportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(SourceCsvPath);
            if (Skipped)
            {
                sb.AppendLine("  跳过: 表头无类型标注（裸表头 = 仅 CSV↔SO 导入的旧式表，非代码生成输入）");
            }
            else
            {
                sb.AppendLine(WroteFile ? "  写入: 是（内容有变化）" : "  写入: 否（无变化或出错）");
            }

            foreach (var error in Errors)
            {
                sb.AppendLine("  错误: " + error);
            }

            return sb.ToString();
        }
    }
}
#endif
