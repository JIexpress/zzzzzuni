// MonthlyReport.cs —— TASK-A-COL-04：月度报告三档详细度（指挥代理 §1/§8 三层指挥的审核层）。
// 三档：Summary（≤10 行：全局声望变动 / 各聚落一句话 / 异常项标出）· Standard（政策/抗议/契约/市场/
// 人口流动逐类计数+要点）· Raw（窗口内 Truth 全量事件逐条，含 Covert）。
// 视野纪律（指挥代理 §5 + 异常行为 §4，直接用 A-COL-01 的 EventVisibility 三级）：
//   Summary/Standard 只吃 Public + governor 亲历窗口（LastVisit 落在窗口内的地区）的 Personal；
//   Covert 事件只进 Raw——这就是「Raw 与 Summary 可对不上 = AI 瞒报」的机制来源，不需要写「瞒报开关」。
// 异常项（§8 审核层）：Organization 越阈值 A/B/C、怨气地板上升、具名 NPC 离开、支线回找。
// 反模式警戒（§8）：这里没有「报告永远准确」——报告吃不到的事件就是不存在于报告里。
// 纯 Domain：零引擎依赖（Game.Domain.Colony 只引用 ZFrame.Core，本文件连 ZFrame 也不用）。

using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Player
{
    /// <summary>报告详细度三档（指挥代理 §8）。枚举默认值（byte 0）= Summary，即默认最简。</summary>
    public enum ReportDetail : byte
    {
        Summary,   // ≤10 行：全局声望变动 / 各聚落一句话 / 异常项标出
        Standard,  // 分类明细：政策/抗议/契约/市场/人口流动逐类计数 + 要点
        Raw,       // 窗口内 Truth 全量事件逐条（含 Covert——与 Summary 可对不上）
    }

    /// <summary>
    /// 一份月度报告。Referenced 是本档实际「吃进」的事件集合（视野纪律的精确口径）：
    /// Summary/Standard = Public + 亲历窗口 Personal；Raw = 窗口全量（含 Covert）。
    /// 「Raw − Summary 的差集」与「Raw 事件数 ≥ Standard 引用数」等验收断言以此为据。
    /// </summary>
    public sealed class Report
    {
        public ReportDetail Detail { get; }
        public IReadOnlyList<string> Lines { get; }
        public IReadOnlyList<string> Anomalies { get; }
        public IReadOnlyList<GameEvent> Referenced { get; }

        public Report(ReportDetail detail, List<string> lines, List<string> anomalies, List<GameEvent> referenced)
        {
            Detail = detail;
            Lines = lines;
            Anomalies = anomalies;
            Referenced = referenced;
        }
    }

    /// <summary>月度报告聚合器（纯 Domain）：GameWorld + 月份天数窗口 → 三档 Report。</summary>
    public static class MonthlyReport
    {
        /// <summary>月度窗口长度（30 天 = GameWorld.MonthTick 周期）。</summary>
        public const uint MonthDays = 30;

        /// <summary>Summary 档页数预算（指挥代理 §8：≤10 行）。</summary>
        public const int SummaryMaxLines = 10;

        /// <summary>诚实口径编译（玩家工具/调试对照用）：不做任何自我豁免过滤。</summary>
        public static Report Compile(GameWorld w, ReportDetail detail, uint fromDay, uint days)
            => CompileFor(w, detail, fromDay, days, null);

        /// <summary>便利入口：最近一个月（窗口 [Day-30, Day-1]；Day 不足 30 时为 [1, Day-1]）。</summary>
        public static Report CompileLastMonth(GameWorld w, ReportDetail detail)
        {
            uint from = LastMonthFrom(w);
            return Compile(w, detail, from, w.Day - from);
        }

        /// <summary>最近一个月的起始天（Compose 与测试共用同一口径）。</summary>
        public static uint LastMonthFrom(GameWorld w) => w.Day > MonthDays ? w.Day - MonthDays : 1u;

        /// <summary>
        /// AI 过滤口径（AiSteward.Compose 专用，同程序集 internal）：ownDecisions 是「AI 本月自己施加的
        /// 决策」的事件文本集合——Summary/Standard 完全不提自己的决策（报喜不报忧）；自己的 Raw 里只保留
        /// Public 的（别人也看见了，瞒不住），非 Public 的自有记录被剔除（自我豁免 = 瞒报的第二来源）。
        /// ownDecisions 为 null 时即诚实口径（Compile 的实现）。
        /// </summary>
        internal static Report CompileFor(GameWorld w, ReportDetail detail, uint fromDay, uint days, HashSet<string> ownDecisions)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (days < 1) days = 1;
            uint toDay = fromDay + days - 1;

            // 窗口内 Truth 全量 → 按档位与视野纪律筛出本档「吃进」的事件
            List<GameEvent> eaten;
            if (detail == ReportDetail.Raw)
            {
                eaten = w.Log.All
                    .Where(e => e.Day >= fromDay && e.Day <= toDay)
                    .Where(e => ownDecisions == null || !ownDecisions.Contains(e.Text) || e.Vis == EventVisibility.Public)
                    .ToList();
            }
            else
            {
                var visited = VisitedRegionsInWindow(w, fromDay, toDay);
                eaten = w.Log.All
                    .Where(e => e.Day >= fromDay && e.Day <= toDay)
                    .Where(e => e.Vis == EventVisibility.Public
                                || (e.Vis == EventVisibility.Personal && visited.Contains(e.Where)))
                    .Where(e => ownDecisions == null || !ownDecisions.Contains(e.Text))
                    .ToList();
            }

            List<string> anomalies = DetectAnomalies(w, eaten);
            List<string> lines = detail switch
            {
                ReportDetail.Summary => BuildSummary(w, anomalies, fromDay, toDay),
                ReportDetail.Standard => BuildStandard(w, eaten, anomalies, fromDay, toDay),
                ReportDetail.Raw => BuildRaw(eaten, anomalies, fromDay, toDay),
                _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "未知报告详细度"),
            };
            return new Report(detail, lines, anomalies, eaten);
        }

        /// <summary>亲历窗口：LastVisit 落在报告窗口内的地区才算「亲自在这块地」（§5 在场视野）。</summary>
        private static HashSet<string> VisitedRegionsInWindow(GameWorld w, uint fromDay, uint toDay)
        {
            var visited = new HashSet<string>();
            foreach (var kv in w.Governor.LastVisit)
                if (kv.Value >= fromDay && kv.Value <= toDay) visited.Add(kv.Key);
            return visited;
        }

        // ── 异常项（§8 审核层）──────────────────────────────────────────

        /// <summary>
        /// 异常项检测：① Organization 越阈值 A/B/C（聚落统计口径——报告只标「异常」，不解释成因：
        /// 成因（地下组织者）是 Covert 事件，只进 Raw，这正是 Summary 与 Raw 可对不上的来源之一）；
        /// ② 怨气地板上升（地板只升不降，按期末状态口径列出所有地板 &gt;0 的聚落）；
        /// ③ 具名 NPC 离开（视野纪律内：离开事件是 Personal，亲历过的才进报告）；
        /// ④ 支线回找（延迟后果到账是 Public 事件）。
        /// </summary>
        private static List<string> DetectAnomalies(GameWorld w, List<GameEvent> eaten)
        {
            var anomalies = new List<string>();

            foreach (Settlement s in w.Settlements)
            {
                if (s.Organization > Settlement.THRESHOLD_C)
                    anomalies.Add($"{s.Name}：组织度 {s.Organization:0.00} 越过阈值 C（{Settlement.THRESHOLD_C:0.00}）——大游行");
                else if (s.Organization > Settlement.THRESHOLD_B)
                    anomalies.Add($"{s.Name}：组织度 {s.Organization:0.00} 越过阈值 B（{Settlement.THRESHOLD_B:0.00}）——小规模骚乱");
                else if (s.Organization > Settlement.THRESHOLD_A)
                    anomalies.Add($"{s.Name}：组织度 {s.Organization:0.00} 越过阈值 A（{Settlement.THRESHOLD_A:0.00}）——组织者涌现");
            }

            foreach (Settlement s in w.Settlements)
                if (s.GrievanceFloor > 0f)
                    anomalies.Add($"{s.Name}：怨气地板 {s.GrievanceFloor:0.00}（镇压实绩不可逆——怨气只能降到这里）");

            foreach (GameEvent e in eaten.Where(e => e.Kind == EventKind.Departure))
                anomalies.Add($"{e.Where}：具名 NPC 离开——{e.Text}");

            foreach (GameEvent e in eaten.Where(e => e.Kind == EventKind.Quest && e.Text.Contains("旧账当众翻出")))
                anomalies.Add($"支线回找——{e.Text}");

            return anomalies;
        }

        // ── 三档排版 ─────────────────────────────────────────────────────

        private static List<string> BuildSummary(GameWorld w, List<string> anomalies, uint fromDay, uint toDay)
        {
            var lines = new List<string>
            {
                $"【月度报告 Summary】第 {fromDay}–{toDay} 天",
                PrestigeLine(w),
            };
            foreach (Settlement s in w.Settlements)
                lines.Add($"· {s.Name}：怨气 {s.AvgGrievance:0.00}（地板 {s.GrievanceFloor:0.00}）" +
                          $" / 组织度 {s.Organization:0.00} —— {s.EmergentState()}");

            foreach (string a in anomalies)
            {
                if (lines.Count >= SummaryMaxLines) break;   // 页数预算：异常项装不下时截断（Anomalies 属性仍全量）
                lines.Add("▲ " + a);
            }
            return lines;
        }

        private static List<string> BuildStandard(GameWorld w, List<GameEvent> eaten, List<string> anomalies, uint fromDay, uint toDay)
        {
            var lines = new List<string>
            {
                $"【月度报告 Standard】第 {fromDay}–{toDay} 天（窗口内可见事件 {eaten.Count} 条）",
                PrestigeLine(w),
            };
            foreach (Settlement s in w.Settlements)
                lines.Add($"· {s.Name}：怨气 {s.AvgGrievance:0.00}（地板 {s.GrievanceFloor:0.00}）" +
                          $" / 组织度 {s.Organization:0.00} —— {s.EmergentState()}");

            var covered = new HashSet<EventKind>();
            foreach (var (label, kinds) in Categories)
            {
                var hits = eaten.Where(e => kinds.Contains(e.Kind)).ToList();
                foreach (EventKind k in kinds) covered.Add(k);
                if (hits.Count == 0) continue;
                lines.Add($"· {label}：{hits.Count} 条");
                foreach (GameEvent e in hits.Take(2))
                    lines.Add($"  - [{e.Day}][{e.Where}] {e.Text}");
            }

            var rest = eaten.Where(e => !covered.Contains(e.Kind)).ToList();
            if (rest.Count > 0)
            {
                lines.Add($"· 其他：{rest.Count} 条");
                foreach (GameEvent e in rest.Take(2))
                    lines.Add($"  - [{e.Day}][{e.Where}] {e.Text}");
            }

            if (anomalies.Count > 0)
            {
                lines.Add($"▲ 异常项 {anomalies.Count} 条：");
                lines.AddRange(anomalies.Select(a => "▲ " + a));
            }
            return lines;
        }

        private static List<string> BuildRaw(List<GameEvent> eaten, List<string> anomalies, uint fromDay, uint toDay)
        {
            var lines = new List<string>
            { $"【月度报告 Raw·Truth 全量】第 {fromDay}–{toDay} 天（{eaten.Count} 条，含 Covert）" };
            foreach (GameEvent e in eaten)
                lines.Add($"[{e.Day}d][{e.Vis}][{e.Kind}][{e.Where}] {e.Text}");

            if (anomalies.Count > 0)
            {
                lines.Add($"▲ 异常项 {anomalies.Count} 条：");
                lines.AddRange(anomalies.Select(a => "▲ " + a));
            }
            return lines;
        }

        private static string PrestigeLine(GameWorld w)
        {
            var p = w.Governor.Prestige;
            return $"全局声望 {p.GlobalPrestige:0.00}（{p.Title()}）｜产值 {p.Production:0.00}｜贸易 {p.TradeValue:0.00}" +
                   $"｜信誉 {p.Credibility:0.00}｜军功 {p.MilitaryDeed:0.00}";
        }

        /// <summary>Standard 档的固定五类（指挥代理 §8：政策/抗议/契约/市场/人口流动），其余进「其他」。</summary>
        private static readonly (string Label, EventKind[] Kinds)[] Categories =
        {
            ("政策", new[] { EventKind.Policy }),
            ("抗议", new[] { EventKind.Protest }),
            ("契约", new[] { EventKind.Contract, EventKind.Departure }),
            ("市场", new[] { EventKind.Market }),
            ("人口流动", new[] { EventKind.Migration }),
        };
    }
}
