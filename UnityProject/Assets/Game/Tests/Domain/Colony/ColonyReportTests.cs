// ColonyReportTests.cs —— TASK-A-COL-04：月度报告三档详细度 + AI 执政官瞒报分歧（M1-M5）。
// M1 Summary 异常项标出 · M2 Covert 只进 Raw · M3 过度优化分歧（Aggressive/Conservative 对照）
// · M4 Verbosity 缩水 · M5 亲历窗口纪律（LastVisit 落在窗口内的 Personal 才进 Summary）。
// 建局一律走 A-COL-02 的 ColonyConfigAdapter 数据驱动路径（五张 GLM 表资产），零新配置表；
// 视野纪律直接锚定 A-COL-01 的 EventVisibility 三级 + Governor.LastVisit（简报点名）。

using System;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Sim;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Core;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace Game.Domain.Tests
{
    /// <summary>月度报告与 AI 执政官（TASK-A-COL-04）：三档视野纪律、过度优化分歧、Verbosity 缩水。</summary>
    public sealed class ColonyReportTests
    {
        // 与 V1-V7/D1-D4/S1-S5 同种子：随机流（周结/面具裂缝/市场漂移/支线日程）确定性可复现。
        private const ulong Seed = 20261122UL;

        /// <summary>M1/M5 的舞台：places.csv 人口第二「残垣城」（D2 点名）。</summary>
        private const string RepressedRegion = "残垣城";

        /// <summary>M3/M4 的施政舞台：places.csv 人口第一「双河城」（D2 点名）。</summary>
        private const string GoverningRegion = "双河城";

        private static ConfigService _config;
        private static ColonyWorldData _data;

        [OneTimeSetUp]
        public void LoadRealTables()
        {
            _config = new ConfigService();
            LoadTable<NpcsConfigRow>(_config, "npcs");
            LoadTable<PlacesConfigRow>(_config, "places");
            LoadTable<PoliciesConfigRow>(_config, "policies");
            LoadTable<QuestsConfigRow>(_config, "quests");
            LoadTable<DialogueConfigRow>(_config, "dialogue");
            _data = ColonyConfigAdapter.ToWorldData(_config);
        }

        [TearDown]
        public void TearDown()
        {
            // EventBus 是进程级静态：清掉可能残留的排队事件（A-COL-01/02/03 fixture 纪律）。
            EventBus.FlushQueued();
        }

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（照抄 ColonyDataDrivenTests 同款）。</summary>
        private static void LoadTable<TRow>(ConfigService config, string stem) where TRow : ConfigRow
        {
            var csvPath = $"{CsvImporter.CsvRoot}/{stem}.csv";
            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            Assert.IsNotNull(table, $"殖民地表资产缺失：{assetPath}");
            config.Load(table);
        }

        /// <summary>每个用例独立建局（真实数据管线，种子固定 → 断言确定性可复现）。</summary>
        private static GameWorld BuildWorld() => ColonyDataLoader.BuildWorld(Seed, _data);

        // ────────────────────────────── M1-M5 ──────────────────────────────

        /// <summary>
        /// M1：编排镇压×2 + 推进 45 天（残垣城 Organization 越阈值）→ Summary.Anomalies 含越阈项；
        /// 页数预算 ≤10 行；视野纪律正对照——组织者涌现（Covert）只进 Raw。
        /// </summary>
        [Test]
        public void M1_SummaryFlagsAnomalies()
        {
            var w = BuildWorld();
            Settlement repressed = w.SettlementOf(RepressedRegion);
            Assert.IsNotNull(repressed, $"前提：{RepressedRegion} 聚落存在（places.csv 人口第二）");
            repressed.Apply(Decision.Harsh);   // 镇压集会：组织度 +0.30、怨气地板 +0.10
            repressed.Apply(Decision.Harsh);   // 镇压余波
            w.AdvanceDays(45);
            Assert.Greater(repressed.Organization, Settlement.THRESHOLD_B,
                $"前提：两次镇压 + 周结后组织度必须越过阈值 B（{Settlement.THRESHOLD_B:0.00}）");
            Assert.Greater(repressed.GrievanceFloor, 0f, "前提：仇恨不可逆——怨气地板已抬升");

            Report summary = MonthlyReport.Compile(w, ReportDetail.Summary, fromDay: 16, days: 30);
            Assert.AreEqual(ReportDetail.Summary, summary.Detail);
            Assert.LessOrEqual(summary.Lines.Count, MonthlyReport.SummaryMaxLines,
                $"Summary 档页数预算 ≤{MonthlyReport.SummaryMaxLines} 行（指挥代理 §8），实际 {summary.Lines.Count} 行");
            Assert.IsTrue(summary.Anomalies.Any(a => a.Contains(RepressedRegion) && a.Contains("越过阈值")),
                "Summary 异常项必须标出组织度越阈：" + string.Join("｜", summary.Anomalies));
            Assert.IsTrue(summary.Anomalies.Any(a => a.Contains("怨气地板")), "怨气地板上升必须进异常项");
            Assert.IsTrue(summary.Lines.Any(l => l.StartsWith("▲ ")), "异常项必须在 Summary 行内标出");

            // 视野纪律正对照：组织者涌现是 Covert——Summary 吃不到，Raw（Truth 全量）吃得到
            Assert.IsFalse(summary.Referenced.Any(e => e.Vis == EventVisibility.Covert),
                "Summary 不得吃 Covert 事件");
            Report raw = MonthlyReport.Compile(w, ReportDetail.Raw, 16, 30);
            Assert.IsTrue(raw.Referenced.Any(e => e.Kind == EventKind.Uprising && e.Vis == EventVisibility.Covert),
                "窗口内组织者涌现（Covert）必须出现在 Raw");
        }

        /// <summary>
        /// M2：窗口内存在 Covert 事件 → Raw 含之、Summary/Standard 均不含（行与引用集双查）；
        /// Raw 事件数 ≥ Standard 引用数。
        /// </summary>
        [Test]
        public void M2_CovertOnlyInRaw()
        {
            var w = BuildWorld();
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), GoverningRegion, execution: 0.4f);
            GameEvent covert = w.Log.All.FirstOrDefault(e => e.Vis == EventVisibility.Covert);
            Assert.IsNotNull(covert, "前提：颁布挖坑政策必写一条「不满人群」Covert 事件");
            w.AdvanceDays(3);

            Report summary = MonthlyReport.Compile(w, ReportDetail.Summary, 1, 3);
            Report standard = MonthlyReport.Compile(w, ReportDetail.Standard, 1, 3);
            Report raw = MonthlyReport.Compile(w, ReportDetail.Raw, 1, 3);

            Assert.IsFalse(summary.Referenced.Contains(covert), "Summary 不得吃 Covert");
            Assert.IsFalse(standard.Referenced.Contains(covert), "Standard 不得吃 Covert");
            Assert.IsFalse(summary.Lines.Any(l => l.Contains(covert.Text)), "Summary 行不得泄漏 Covert 文本");
            Assert.IsFalse(standard.Lines.Any(l => l.Contains(covert.Text)), "Standard 行不得泄漏 Covert 文本");
            Assert.IsTrue(raw.Referenced.Contains(covert), "Raw 是 Truth 全量：必须含 Covert");
            Assert.IsTrue(raw.Lines.Any(l => l.Contains(covert.Text)), "Raw 行必须逐条呈现 Covert");
            Assert.GreaterOrEqual(raw.Referenced.Count, standard.Referenced.Count,
                $"Raw 事件数（{raw.Referenced.Count}）≥ Standard 引用数（{standard.Referenced.Count}）");

            // 正对照：Public 颁布事件三档都吃得到（防过滤 vacuous）
            GameEvent announce = w.Log.All.First(e => e.Kind == EventKind.Policy && e.Vis == EventVisibility.Public);
            Assert.IsTrue(summary.Referenced.Contains(announce), "Public 颁布必须进 Summary");
            Assert.IsTrue(standard.Referenced.Contains(announce), "Public 颁布必须进 Standard");
            Assert.IsTrue(raw.Referenced.Contains(announce), "Public 颁布必须进 Raw");
        }

        /// <summary>
        /// M3：Aggressive persona 连续 3 个月 ApplyMonthlyPolicy → Summary 里 Production 上升被报喜、
        /// Raw − Summary 的 Public 差集非空（差集里恰有写明怨气代价的施政记录）；自我豁免把 Covert
        /// 暗账从 AI 自己的 Raw 里剔除（但 Truth 全量里有）；Conservative persona 对照：无产量变化。
        /// </summary>
        [Test]
        public void M3_OverOptimizationDivergence()
        {
            // —— 激进：连续 3 个月过度优化 ——
            var w = BuildWorld();
            var steward = new AiSteward(AiPersona.Aggressive());
            for (int month = 0; month < 3; month++)
            {
                AiDecision d = steward.ApplyMonthlyPolicy(w, GoverningRegion);
                Assert.IsNotNull(d, $"第 {month + 1} 个月：激进 AI（RiskTolerance 0.9 > 0.7）必须施加过度优化");
                Assert.AreEqual(0.02f, d.ProductionGain, 1e-5f, "每月产值预支 +0.02（简报钉死）");
                w.AdvanceDays(30);
            }
            Assert.AreEqual(0.06f, w.Governor.Prestige.Production, 1e-5f, "三个月产值预支 +0.02×3");
            Settlement governed = w.SettlementOf(GoverningRegion);
            Assert.Greater(governed.AvgGrievance, 0.35f, "前提：三个月怨气透支真实落在聚落上");

            Report summary = steward.Compose(w, AiPersona.Aggressive(), ReportDetail.Summary);
            Report raw = steward.Compose(w, AiPersona.Aggressive(), ReportDetail.Raw);

            // 报喜：Summary 只报产值上行（声望线含产值），自己的《增产令》绝不主动提
            Assert.IsTrue(summary.Lines.Any(l => l.Contains("产值")),
                "Summary 必须报产值（报喜）：" + string.Join("｜", summary.Lines));
            Assert.IsFalse(summary.Lines.Any(l => l.Contains("增产令")),
                "自我豁免：Summary 行不得主动提自己的《增产令》");
            Assert.IsFalse(summary.Referenced.Any(e => e.Text.Contains("增产令")),
                "自我豁免：Summary 不得引用自己的决策事件");

            // 瞒报：Raw − Summary 的 Public 差集非空——差集里恰有《增产令》施政记录（含怨言字样）
            var publicDiff = raw.Referenced
                .Where(e => e.Vis == EventVisibility.Public && !summary.Referenced.Contains(e))
                .ToList();
            Assert.IsNotEmpty(publicDiff, "Raw − Summary 的 Public 差集必须非空（AI 瞒报的机制证据）");
            Assert.IsTrue(publicDiff.Any(e => e.Text.Contains("增产令")),
                "差集必须含《增产令》施政记录：" + string.Join("｜", publicDiff.Select(e => e.Text)));

            // 暗账：Truth 里有、AI 自己的 Raw 里没有（非 Public 的自有记录被自我豁免剔除）
            uint windowFrom = MonthlyReport.LastMonthFrom(w);
            GameEvent ledger = w.Log.All.FirstOrDefault(e => e.Text.Contains("暗账") && e.Day >= windowFrom);
            Assert.IsNotNull(ledger, "前提：第三个月的过度优化写了 Covert 暗账");
            Assert.IsFalse(raw.Referenced.Contains(ledger), "自我豁免：AI 自己的 Raw 也剔除自己的暗账");
            Report truthRaw = MonthlyReport.Compile(w, ReportDetail.Raw, windowFrom, MonthlyReport.MonthDays);
            Assert.IsTrue(truthRaw.Referenced.Contains(ledger), "诚实聚合器（Truth 全量）必须含暗账——AI 的 Raw ≠ Truth");

            // —— 保守对照：无产量变化 ——
            var w2 = BuildWorld();
            var conservative = new AiSteward(AiPersona.Conservative());
            for (int month = 0; month < 3; month++)
            {
                Assert.IsNull(conservative.ApplyMonthlyPolicy(w2, GoverningRegion), "保守 AI（0.2 ≤ 0.7）不动");
                w2.AdvanceDays(30);
            }
            Assert.AreEqual(0f, w2.Governor.Prestige.Production, 1e-5f, "保守 AI：零产量变化");
            Assert.IsEmpty(conservative.Decisions, "保守 AI：无施政记录");
            Assert.IsFalse(w2.Log.All.Any(e => e.Text.Contains("增产令")), "保守 AI：无施政日志");
            Assert.Less(w2.SettlementOf(GoverningRegion).AvgGrievance, governed.AvgGrievance,
                "激进 AI 的怨气透支必须真实落在聚落上（保守对照更低——随机流同种子可对照）");
        }

        /// <summary>
        /// M4：Verbosity&lt;0.3 的 persona 在 detail=Standard 请求下返回 Summary 档内容
        /// （行数 < 同输入 Standard 的 Standard 档行数）；Verbosity≥0.3 的对照不缩水。
        /// </summary>
        [Test]
        public void M4_VerbosityShrink()
        {
            var w = BuildWorld();
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), GoverningRegion, execution: 0.4f);
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsDarkMedicine), "青石城", execution: 0.9f);
            w.AdvanceDays(20);   // Day=21 → Compose 窗口 [1,20]，含两政策四条事件

            var silent = new AiSteward(AiPersona.Silent());
            Report shrunk = silent.Compose(w, AiPersona.Silent(), ReportDetail.Standard);
            var verbose = new AiSteward(AiPersona.Verbose());
            Report full = verbose.Compose(w, AiPersona.Verbose(), ReportDetail.Standard);

            Assert.AreEqual(ReportDetail.Summary, shrunk.Detail,
                "Verbosity 0.1 < 0.3：Standard 请求必须缩水成 Summary");
            Assert.AreEqual(ReportDetail.Standard, full.Detail, "Verbosity 0.9 ≥ 0.3：Standard 请求保持 Standard");
            Assert.Less(shrunk.Lines.Count, full.Lines.Count,
                $"缩水后的行数（{shrunk.Lines.Count}）必须严格少于同输入 Standard 档（{full.Lines.Count}）");
            Assert.LessOrEqual(shrunk.Lines.Count, MonthlyReport.SummaryMaxLines, "缩水产物是 Summary 档：同样 ≤10 行");
            Assert.Greater(full.Lines.Count, 5, "前提：Standard 档必须有分类明细（政策类 ≥1 组）");
        }

        /// <summary>
        /// M5：亲历窗口纪律——LastVisit 落在窗口内的地区，其 Personal 事件进 Summary；
        /// 未亲历 / 亲历在窗口外都不算（指挥代理 §5「在场视野」的报告侧口径）。
        /// </summary>
        [Test]
        public void M5_VisitWindowPersonalFeedsSummary()
        {
            var w = BuildWorld();
            Settlement target = w.SettlementOf(RepressedRegion);
            Assert.IsNotNull(target, $"前提：{RepressedRegion} 聚落存在");
            Assert.GreaterOrEqual(target.AvgGrievance, 0.35f,
                "前提：残垣城初始怨气 ≥ 0.35（坟山北坡的 GrievanceAbove 触发线）");
            w.AdvanceDays(3);   // 坟山北坡（GrievanceAbove 0.35 @ 残垣城）第 1 天触发，Personal 事件
            GameEvent trigger = w.Log.All.FirstOrDefault(e =>
                e.Kind == EventKind.Quest && e.Text.Contains("坟山北坡") && e.Vis == EventVisibility.Personal);
            Assert.IsNotNull(trigger, "前提：支线触发已写 Personal 事件");

            // ① 没亲历过：Summary 吃不到这条 Personal
            Report noVisit = MonthlyReport.Compile(w, ReportDetail.Summary, 1, 3);
            Assert.IsFalse(noVisit.Referenced.Contains(trigger), "未亲历：Personal 事件不得进 Summary");
            Assert.IsFalse(noVisit.Lines.Any(l => l.Contains("坟山北坡")), "未亲历：Summary 行不得出现 Personal 内容");

            // ② 窗口内亲历（第 2 天到过残垣城）：同一条事件进 Summary
            w.Governor.Visit(RepressedRegion, 2);
            Report visited = MonthlyReport.Compile(w, ReportDetail.Summary, 1, 3);
            Assert.IsTrue(visited.Referenced.Contains(trigger), "窗口内亲历：Personal 事件必须进 Summary");

            // ③ 亲历落在窗口外（第 3 天），窗口取 [1,2]：不算亲历
            w.Governor.Visit(RepressedRegion, 3);
            Report outside = MonthlyReport.Compile(w, ReportDetail.Summary, 1, 2);
            Assert.IsFalse(outside.Referenced.Contains(trigger), "亲历在窗口外：Personal 事件不得进 Summary");
        }
    }
}
