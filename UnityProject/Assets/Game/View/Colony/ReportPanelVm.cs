// ReportPanelVm.cs —— TASK-A-COL-05：月度报告面板 VM（MVP 的 M 层，纯 C# 零引擎依赖）。
// 字段口径 = 简报点名：「标题行 / 摘要行 / 异常项高亮行列表 / Raw 行列表 / 当前月份」。
// 视野纪律落在 VM 的构成上（不写"瞒报开关"）：
//   SummaryLines/AnomalyLines 只可能来自 Summary 档 Report（AI 执政官视野，Covert 吃不到）；
//   RawLines 来自诚实口径的 Raw Report（Truth 全量，含 Covert）——「查原始记录」才能看到，
//   Summary 与 Raw 可对不上（AI 瞒报的机制来源，A-COL-04 M2/M3 同口径）。

using System.Collections.Generic;

namespace Game.View.Colony
{
    /// <summary>月度报告面板的一次呈现内容（标题行/摘要行/异常项高亮行/Raw 行/当前月份）。</summary>
    public sealed class ReportPanelVm
    {
        /// <summary>面板标题行（含当前月份；无报告时为"尚未结算"占位）。</summary>
        public string TitleLine { get; }

        /// <summary>Summary 档摘要行（AI 执政官口径——不含 Covert 来源行）。</summary>
        public IReadOnlyList<string> SummaryLines { get; }

        /// <summary>异常项高亮行列表（完整清单，逐行带 "▲ " 前缀；不受 Summary 页数预算截断）。</summary>
        public IReadOnlyList<string> AnomalyLines { get; }

        /// <summary>Raw 档行列表（Truth 全量，含 Covert——「查原始记录」toggle 后才显示）。</summary>
        public IReadOnlyList<string> RawLines { get; }

        /// <summary>当前月份（完成的月序号，1 起；0 = 尚未结算）。</summary>
        public int Month { get; }

        /// <summary>是否显示 Raw 档（false = 默认 Summary 档，视野纪律：主动检查才见真相）。</summary>
        public bool ShowRaw { get; }

        /// <summary>是否已有月度报告（false = 首月结算前的空状态）。</summary>
        public bool HasReport { get; }

        public ReportPanelVm(int month, string titleLine,
            IReadOnlyList<string> summaryLines,
            IReadOnlyList<string> anomalyLines,
            IReadOnlyList<string> rawLines,
            bool showRaw,
            bool hasReport)
        {
            Month = month;
            TitleLine = titleLine;
            SummaryLines = summaryLines;
            AnomalyLines = anomalyLines;
            RawLines = rawLines;
            ShowRaw = showRaw;
            HasReport = hasReport;
        }
    }
}
