// ReportPanelVm.cs —— TASK-A-COL-05：月度报告面板 VM（MVP 的 M 层，纯 C# 零引擎依赖）。
// 字段口径 = 简报点名：「标题行 / 摘要行 / 异常项高亮行列表 / Raw 行列表 / 当前月份」。
// 视野纪律落在 VM 的构成上（不写"瞒报开关"）：
//   SummaryLines/AnomalyLines 只可能来自 Summary 档 Report（AI 执政官视野，Covert 吃不到）；
//   RawLines 来自诚实口径的 Raw Report（Truth 全量，含 Covert）——「查原始记录」才能看到，
//   Summary 与 Raw 可对不上（AI 瞒报的机制来源，A-COL-04 M2/M3 同口径）。
//
// TASK-A-COL-07 迭代（滚动 + 异常项跳转 + 情报点买 Raw）新增四块，全部纯 C#：
//   · IntelPoints/RawAffordable —— 情报点余额与「能否开 Raw」的禁用态（null 余额 = 调用方
//     未接入情报点（A-COL-05 兼容口径），Raw 恒可开）；
//   · RawKindFilter —— 异常项跳转的过滤 Kind（null = 全量）；
//   · RawDisplayLines —— Raw 模式实际显示行：未过滤 = RawLines 本体（C4 契约不变），
//     过滤态 = Raw 头行 + 命中行（行数必然 < 全量——C6 断言口径）；
//   · RawHitFlags —— RawDisplayLines 逐行命中标记（true = 视图上高亮色，N1/C6 口径）。

using System;
using System.Collections.Generic;
using Game.Domain.Colony.Sim;

namespace Game.View.Colony
{
    /// <summary>月度报告面板的一次呈现内容（标题行/摘要行/异常项高亮行/Raw 行/当前月份/情报点/过滤态）。</summary>
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

        /// <summary>
        /// 情报点余额（TASK-A-COL-07）。null = 调用方未接入情报点（A-COL-05 旧调用方兼容，
        /// Raw 为免费通道）；接入时 ≤0 即「查不了 Raw」的禁用态。
        /// </summary>
        public int? IntelPoints { get; }

        /// <summary>能否打开 Raw：未接入情报点恒 true；接入时 = 余额 &gt; 0（按钮置灰/提示行的数据源）。</summary>
        public bool RawAffordable { get; }

        /// <summary>Raw 过滤 Kind（异常项跳转）：null = 全量；非 null = 只显示该 Kind 的命中行（+Raw 头行）。</summary>
        public EventKind? RawKindFilter { get; }

        /// <summary>
        /// Raw 模式实际显示行：未过滤 = <see cref="RawLines"/> 本体；过滤态 = Raw 头行 + 命中行
        /// （Display 行数 &lt; RawLines.Count——C6「过滤视图行数 &lt; 全量 Raw 行数」的口径）。
        /// </summary>
        public IReadOnlyList<string> RawDisplayLines { get; }

        /// <summary>RawDisplayLines 逐行命中标记：true = 命中过滤 Kind（视图高亮色），false = 头行/未过滤行。</summary>
        public IReadOnlyList<bool> RawHitFlags { get; }

        public ReportPanelVm(int month, string titleLine,
            IReadOnlyList<string> summaryLines,
            IReadOnlyList<string> anomalyLines,
            IReadOnlyList<string> rawLines,
            bool showRaw,
            bool hasReport,
            int? intelPoints = null,
            EventKind? rawKindFilter = null,
            IReadOnlyList<string> rawDisplayLines = null,
            IReadOnlyList<bool> rawHitFlags = null)
        {
            if (rawDisplayLines == null) rawDisplayLines = rawLines ?? Array.Empty<string>();
            if (rawHitFlags == null) rawHitFlags = new bool[rawDisplayLines.Count];

            Month = month;
            TitleLine = titleLine;
            SummaryLines = summaryLines;
            AnomalyLines = anomalyLines;
            RawLines = rawLines;
            ShowRaw = showRaw;
            HasReport = hasReport;
            IntelPoints = intelPoints;
            RawAffordable = intelPoints == null || intelPoints.Value > 0;
            RawKindFilter = rawKindFilter;
            RawDisplayLines = rawDisplayLines;
            RawHitFlags = rawHitFlags;
        }
    }
}
