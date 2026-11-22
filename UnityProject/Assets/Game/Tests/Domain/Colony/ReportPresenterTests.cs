// ReportPresenterTests.cs —— TASK-A-COL-05：月度报告 MVP 的 P 层纯逻辑 EditMode 用例（P1-P4）。
// 覆盖（简报点名）：Summary 档 VM 不含 Covert 来源行、Raw 档含、异常项标高亮行、
// Raw toggle 切换显示集合、关闭接线、面板开着时新报告就地刷新回默认 Summary。
// P 层不依赖 UnityEngine：视图用裸 ReportPanelView 实例（不调 OnInit 就没有 GameObject），
// Attach → Render 全链路纯 C#。报告输入全部来自真实数据管线建局（A-COL-02 ColonyConfigAdapter
// 同款脚手架 + A-COL-04 M 系口径），不是手搓假行。

using System;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Sim;
using Game.View.Colony;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Core;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace Game.Domain.Tests
{
    /// <summary>月度报告 Presenter（TASK-A-COL-05）：VM 构成、视野纪律、toggle/关闭接线、就地刷新。</summary>
    public sealed class ReportPresenterTests
    {
        // 与 V1-V7/D1-D4/S1-S5/M1-M5 同种子：随机流确定性可复现。
        private const ulong Seed = 20261122UL;

        /// <summary>P1/P3/P4 的施政舞台：places.csv 人口第一「双河城」。</summary>
        private const string GoverningRegion = "双河城";

        /// <summary>P2 的镇压舞台：places.csv 人口第二「残垣城」（M1 同款）。</summary>
        private const string RepressedRegion = "残垣城";

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
            // EventBus 是进程级静态：清掉可能残留的排队事件（A-COL 系列 fixture 纪律）。
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

        /// <summary>
        /// 激进 AI 一个月（P1/P3/P4 舞台）：施政（Public《增产令》+ Covert 暗账）→ 推进 30 天 →
        /// Summary（AI 视野，自我豁免）+ Raw（诚实口径 Truth 全量）。返回 (summary, raw, month)。
        /// </summary>
        private static (Report summary, Report raw, int month) ComposeAiMonth()
        {
            var w = BuildWorld();
            var steward = new AiSteward(AiPersona.Aggressive());
            Assert.IsNotNull(steward.ApplyMonthlyPolicy(w, GoverningRegion),
                "前提：激进 AI（RiskTolerance 0.9 > 0.7）必须施加过度优化");
            w.AdvanceDays(30);
            Report summary = steward.Compose(w, AiPersona.Aggressive(), ReportDetail.Summary);
            Report raw = MonthlyReport.CompileLastMonth(w, ReportDetail.Raw);
            return (summary, raw, 1);
        }

        // ────────────────────────────── P1-P4 ──────────────────────────────

        /// <summary>
        /// P1：Summary 档 VM 不含 Covert 来源行、Raw 档含（简报点名视野纪律断言）。
        /// Covert 暗账只进 Raw；AI 自己的《增产令》（Public）被自我豁免挡在 Summary 外、Raw 里有。
        /// </summary>
        [Test]
        public void P1_SummaryVmHidesCovertRows_RawContainsThem()
        {
            (Report summary, Report raw, int month) = ComposeAiMonth();

            // 前提：当月窗口内确有 Covert 暗账（否则断言 vacuous）
            Assert.IsTrue(raw.Referenced.Any(e => e.Vis == EventVisibility.Covert),
                "前提：施政窗口内必须存在 Covert 暗账（A-COL-04 M3 同款）");

            ReportPanelVm vm = ReportPresenter.BuildVm(summary, raw, month, showRaw: false);
            Assert.IsTrue(vm.HasReport, "已结算月份的 VM 必须非空状态");
            Assert.AreEqual(month, vm.Month, "VM 必须携带当前月份");
            Assert.IsFalse(vm.ShowRaw, "默认 Summary 档");

            // Summary 侧（摘要行 + 异常高亮行）：不得泄漏 Covert 文本
            Assert.IsFalse(vm.SummaryLines.Any(l => l.Contains("暗账")),
                "Summary 摘要行不得含 Covert 来源行：" + string.Join("｜", vm.SummaryLines));
            Assert.IsFalse(vm.AnomalyLines.Any(l => l.Contains("暗账")),
                "Summary 异常高亮行不得含 Covert 来源行");
            Assert.IsFalse(vm.SummaryLines.Any(l => l.Contains("增产令")),
                "自我豁免：Summary 摘要行不得主动提 AI 自己的《增产令》");

            // Raw 侧（Truth 全量）：暗账 + 《增产令》都必须在
            Assert.IsTrue(vm.RawLines.Any(l => l.Contains("暗账")), "Raw 行必须含 Covert 暗账（查原始记录才能看到）");
            Assert.IsTrue(vm.RawLines.Any(l => l.Contains("增产令")), "Raw 行必须含 Public 施政记录");
        }

        /// <summary>
        /// P2：异常项标高亮（简报点名）——VM.AnomalyLines 是完整高亮清单（逐行 "▲ " 前缀，
        /// 不受 Summary 页数预算截断）；Summary 摘要行内也保留预算内的 "▲ " 行；Raw 行数多于 Summary。
        /// </summary>
        [Test]
        public void P2_AnomalyLinesAreCompleteHighlightedRows()
        {
            var w = BuildWorld();
            Settlement repressed = w.SettlementOf(RepressedRegion);
            Assert.IsNotNull(repressed, $"前提：{RepressedRegion} 聚落存在（places.csv 人口第二）");
            repressed.Apply(Decision.Harsh);   // 镇压集会：组织度 +0.30、怨气地板 +0.10（M1 同款舞台）
            repressed.Apply(Decision.Harsh);
            w.AdvanceDays(45);
            Assert.Greater(repressed.Organization, Settlement.THRESHOLD_B, "前提：两次镇压必须让组织度越阈");

            Report summary = MonthlyReport.Compile(w, ReportDetail.Summary, fromDay: 16, days: 30);
            Report raw = MonthlyReport.Compile(w, ReportDetail.Raw, 16, 30);
            ReportPanelVm vm = ReportPresenter.BuildVm(summary, raw, month: 2, showRaw: false);

            Assert.IsNotEmpty(summary.Anomalies, "前提：报告异常项非空（组织度越阈/怨气地板）");
            Assert.IsTrue(summary.Anomalies.Any(a => a.Contains(RepressedRegion) && a.Contains("越过阈值")),
                "前提：越阈异常在列：" + string.Join("｜", summary.Anomalies));

            // 高亮行清单 = 全量异常项 + "▲ " 前缀（逐条对应，不受页数预算截断）
            Assert.AreEqual(summary.Anomalies.Count, vm.AnomalyLines.Count,
                "高亮行数必须等于异常项全量（Summary 行内预算截断不影响高亮清单）");
            Assert.IsTrue(vm.AnomalyLines.All(l => l.StartsWith("▲ ", StringComparison.Ordinal)),
                "高亮行必须逐行带 ▲ 前缀");
            foreach (string anomaly in summary.Anomalies)
            {
                Assert.IsTrue(vm.AnomalyLines.Any(l => l.Contains(anomaly)),
                    $"高亮清单必须含异常项原文：{anomaly}");
            }

            // 摘要行内异常项以 "▲ " 形态出现（视图据此上高亮色）
            Assert.IsTrue(vm.SummaryLines.Any(l => l.StartsWith("▲ ", StringComparison.Ordinal)),
                "Summary 摘要行必须含 ▲ 形态的异常项行");
            // Raw 是 Truth 全量：行数必须多于 Summary（页数预算 ≤10）
            Assert.LessOrEqual(vm.SummaryLines.Count, MonthlyReport.SummaryMaxLines, "Summary 行数守页数预算");
            Assert.Greater(vm.RawLines.Count, vm.SummaryLines.Count, "Raw 行数必须多于 Summary");
        }

        /// <summary>
        /// P3：Attach → Render 全链路（裸视图，零 GameObject）+ Raw toggle 切换显示集合 +
        /// 关闭意图经 P 层转发。P↔V 双方向（P→V：SetRawVisible；V→P：NotifyRawToggleChanged）。
        /// </summary>
        [Test]
        public void P3_AttachRender_ToggleSwitchesDisplayedSet_CloseWiresThrough()
        {
            (Report summary, Report raw, int month) = ComposeAiMonth();

            var view = new ReportPanelView(); // 裸实例：不调 OnInit，无 GameObject（P 层可测的关键）
            var presenter = new ReportPresenter(view);
            bool closed = false;
            presenter.Attach(ReportPanelArgs.Create(month, summary, raw, () => closed = true));

            try
            {
                Assert.IsFalse(view.IsBuilt, "裸视图不得创建 GameObject（P 层纯逻辑测试）");
                Assert.IsNotNull(view.CurrentVm, "Attach 后必须完成首次 Render");
                Assert.IsFalse(view.IsRawDisplayed, "默认 Summary 档");
                Assert.IsTrue(view.CurrentVm.HasReport);
                Assert.AreEqual(month, view.CurrentVm.Month);
                StringAssert.Contains($"第 {month} 月", view.CurrentVm.TitleLine, "标题行必须含当前月份");
                StringAssert.Contains("月度报告", view.CurrentVm.TitleLine, "标题行必须可辨识");

                int summaryCount = view.DisplayedLineCount;
                Assert.Greater(summaryCount, 0, "Summary 显示行数必须非空");
                Assert.AreEqual(summary.Lines.Count, summaryCount, "Summary 显示行数 = 摘要行数");

                // P → V：显式开 Raw
                presenter.SetRawVisible(true);
                Assert.IsTrue(view.IsRawDisplayed, "SetRawVisible(true) 后必须切到 Raw 档");
                Assert.Greater(view.DisplayedLineCount, summaryCount, "Raw 行数必须多于 Summary");

                // V → P：真实 toggle 通知路径（UGUI 回调同款入口）
                view.NotifyRawToggleChanged(false);
                Assert.IsFalse(view.IsRawDisplayed, "toggle 关闭必须切回 Summary");
                Assert.AreEqual(summaryCount, view.DisplayedLineCount, "切回后行数恢复");

                // 关闭接线：视图请求 → P 层转发 → 注入的 Close 意图
                Assert.IsFalse(closed, "未请求关闭前 Close 不得触发");
                view.RequestClose();
                Assert.IsTrue(closed, "Close 意图必须经 P 层转发到注入回调");
            }
            finally
            {
                presenter.Detach();
            }
        }

        /// <summary>
        /// P4：面板开着时新月份结算（ColonyReportReady 经 Attach 作用域订阅送达）→
        /// 就地换到最新报告并回到默认 Summary 视野（Raw 是结算时刻快照，旧 Raw 不再代表"当月"）。
        /// </summary>
        [Test]
        public void P4_LiveReportRefresh_SwapsReportAndResetsToSummary()
        {
            var w = BuildWorld();
            var steward = new AiSteward(AiPersona.Aggressive());
            Assert.IsNotNull(steward.ApplyMonthlyPolicy(w, GoverningRegion), "前提：第 1 个月施政");
            w.AdvanceDays(30);
            Report summary1 = steward.Compose(w, AiPersona.Aggressive(), ReportDetail.Summary);
            Report raw1 = MonthlyReport.CompileLastMonth(w, ReportDetail.Raw);

            var view = new ReportPanelView();
            var presenter = new ReportPresenter(view);
            presenter.Attach(ReportPanelArgs.Create(1, summary1, raw1, null));
            presenter.SetRawVisible(true);
            Assert.IsTrue(view.IsRawDisplayed, "前提：面板正开着 Raw 档");

            // 第 2 个月：施政 → 推进 → 结算广播（面板订阅在 Attach 作用域内，就地收到）
            Assert.IsNotNull(steward.ApplyMonthlyPolicy(w, GoverningRegion), "前提：第 2 个月施政");
            w.AdvanceDays(30);
            Report summary2 = steward.Compose(w, AiPersona.Aggressive(), ReportDetail.Summary);
            Report raw2 = MonthlyReport.CompileLastMonth(w, ReportDetail.Raw);
            EventBus.Fire(new ColonyReportReady
            {
                Month = 2,
                ReportLines = summary2.Lines,
                Anomalies = summary2.Anomalies,
                Summary = summary2,
                Raw = raw2,
            });

            Assert.AreEqual(2, view.CurrentVm.Month, "面板必须就地换到最新月份");
            Assert.IsFalse(view.IsRawDisplayed, "新报告必须回到默认 Summary 视野（视野纪律复位）");
            Assert.AreNotSame(summary1.Lines, view.CurrentVm.SummaryLines, "VM 必须引用新报告的行");

            presenter.Detach();
            EventBus.FlushQueued();
        }
    }
}
