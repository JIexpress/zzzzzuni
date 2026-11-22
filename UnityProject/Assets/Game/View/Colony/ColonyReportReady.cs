using System.Collections.Generic;
using Game.Domain.Colony.Player;

namespace Game.View.Colony
{
    /// <summary>
    /// 月度报告就绪事件（TASK-A-COL-05）：ColonyWorldHost 月度结算链
    /// （AiSteward.ApplyMonthlyPolicy → MonthlyReport 编译 → 本事件 Fire）的出口载荷。
    ///
    /// 字段口径（简报点名 + 垂直切片需要的最小扩展）：
    ///  - <see cref="ReportLines"/>/<see cref="Anomalies"/>：当月 Summary 档的行与异常项
    ///    （AI 执政官视野——报喜不报忧），与简报 payload <c>{ Month, ReportLines, Anomalies }</c> 一致；
    ///  - <see cref="Summary"/>/<see cref="Raw"/>：两份 Report 的完整引用。Raw 是**诚实口径**
    ///    （MonthlyReport 的 Truth 全量，含 Covert），在结算时刻快照——玩家稍后点「查原始记录」
    ///    时看到的是结算当月的真相，而不是"今天的最近 30 天"（否则 Summary 与 Raw 的窗口漂移，
    ///    瞒报对照不成立）。快照语义是简报「Raw 档需玩家主动检查」成立的前提。
    /// </summary>
    public sealed class ColonyReportReady
    {
        /// <summary>完成的月序号（1 起：满 30 游戏日 = 第 1 月）。</summary>
        public int Month { get; set; }

        /// <summary>当月 Summary 档报告行（AI 执政官口径）。</summary>
        public IReadOnlyList<string> ReportLines { get; set; }

        /// <summary>当月异常项（组织度越阈 / 怨气地板 / 具名离开 / 支线回找）。</summary>
        public IReadOnlyList<string> Anomalies { get; set; }

        /// <summary>当月 Summary 档 Report（AI 执政官视野，自我豁免过滤后）。</summary>
        public Report Summary { get; set; }

        /// <summary>当月 Raw 档 Report（诚实口径 Truth 全量，含 Covert——结算时刻快照）。</summary>
        public Report Raw { get; set; }
    }
}
