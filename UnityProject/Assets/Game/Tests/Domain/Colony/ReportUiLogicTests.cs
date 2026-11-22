// ReportUiLogicTests.cs —— TASK-A-COL-07：报告面板迭代（滚动 + 异常项跳转 + 情报点买 Raw）
// 的 presenter/VM/Domain 纯逻辑用例（N1-N5）。
// N1 异常项点击 → VM 切 Raw + 过滤 Kind + 命中标记 · N2 情报点 0 → Raw 请求被拒且给出禁用态
// · N3 消费成功扣 1 点（每次打开各扣 1 点）· N4 MonthTick 后 +1（月度维持费）
// · N5 情报点存档往返不丢点数（简报括号项「或新增」口径——S1/S5 另有同名断言）。
// 报告输入沿用 A-COL-04/05 的真实数据管线舞台（五张 GLM 表 + P2/M1 同款镇压编排），
// 钱包用闭包假体（余额/扣点意图的活查询，与 ColonyWorldHost 注入 Governor 的生产口径同构）。

using System;
using System.Linq;
using Game.Domain.Colony;
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
    /// <summary>报告面板 UI 纯逻辑（TASK-A-COL-07）：异常项跳转、情报点闸门与月度维持费、存档往返。</summary>
    public sealed class ReportUiLogicTests
    {
        // 与 V/D/S/M/P 全系同种子：随机流确定性可复现。
        private const ulong Seed = 20261122UL;

        /// <summary>镇压舞台：places.csv 人口第二「残垣城」（M1/P2 同款）。</summary>
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

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（照抄 M/P 系同款）。</summary>
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
        /// 异常项舞台（M1/P2 同款）：残垣城双镇压 → 组织度越阈（异常项必列）→ 推进 45 天
        /// → 窗口 [16,45] 内有自主行为 Uprising 事件（Raw 里折叠行可被过滤命中）。
        /// 返回 (summary, raw, month)。
        /// </summary>
        private static (Report summary, Report raw, int month) ComposeAnomalyMonth()
        {
            var w = BuildWorld();
            Settlement repressed = w.SettlementOf(RepressedRegion);
            Assert.IsNotNull(repressed, $"前提：{RepressedRegion} 聚落存在（places.csv 人口第二）");
            repressed.Apply(Decision.Harsh);   // 组织度 +0.30、怨气地板 +0.10（M1 同款）
            repressed.Apply(Decision.Harsh);
            w.AdvanceDays(45);
            Assert.Greater(repressed.Organization, Settlement.THRESHOLD_B, "前提：双镇压后组织度越阈");

            Report summary = MonthlyReport.Compile(w, ReportDetail.Summary, fromDay: 16, days: 30);
            Report raw = MonthlyReport.Compile(w, ReportDetail.Raw, 16, 30);
            Assert.IsNotEmpty(summary.Anomalies, "前提：报告异常项非空（组织度越阈/怨气地板）");
            return (summary, raw, 2);
        }

        /// <summary>钱包假体：与 ColonyWorldHost 注入生产口径同构（活余额查询 + 不足不扣的扣点意图）。</summary>
        private static (Func<int> balance, Func<bool> spend) MakeWallet(int[] holder)
        {
            return (
                () => holder[0],
                () =>
                {
                    if (holder[0] <= 0) return false;
                    holder[0]--;
                    return true;
                });
        }

        // ────────────────────────────── N1-N5 ──────────────────────────────

        /// <summary>
        /// N1（简报点名）：异常项点击 → VM 切 Raw + 过滤 Kind + 命中标记。组织度越阈异常
        /// 映射 Uprising；过滤态显示行 = 头行 + 命中行（行数 &lt; 全量）；Summary → Raw 过渡
        /// 消耗 1 情报点。
        /// </summary>
        [Test]
        public void N1_AnomalyClick_SwitchesToRawWithKindFilterAndHits()
        {
            (Report summary, Report raw, int month) = ComposeAnomalyMonth();

            var view = new ReportPanelView();   // 裸实例：不建 GameObject（P 层纯逻辑同款）
            var presenter = new ReportPresenter(view);
            int[] holder = { 2 };
            (Func<int> balanceFn, Func<bool> spendFn) = MakeWallet(holder);
            presenter.Attach(ReportPanelArgs.Create(month, summary, raw, null, balanceFn, spendFn));

            try
            {
                Assert.IsFalse(view.IsRawDisplayed, "前提：默认 Summary 档");

                int orgIndex = view.CurrentVm.AnomalyLines
                    .Select((line, index) => (line, index))
                    .First(t => t.line.Contains("越过阈值")).index;
                Assert.GreaterOrEqual(orgIndex, 0, "前提：组织度越阈异常在高亮清单中");

                view.NotifyAnomalyClicked(orgIndex);   // 用户路径同款入口（行按钮回调走同一事件）

                Assert.IsTrue(view.IsRawDisplayed, "异常项点击必须切到 Raw 视图");
                Assert.AreEqual(EventKind.Uprising, view.CurrentVm.RawKindFilter,
                    "组织度越阈异常必须映射 Uprising 过滤：" + view.CurrentVm.RawKindFilter?.ToString());
                Assert.Greater(view.CurrentVm.RawHitFlags.Count(h => h), 0, "命中行标记必须存在");
                Assert.AreEqual(view.CurrentVm.RawDisplayLines.Count, view.CurrentVm.RawHitFlags.Count,
                    "命中标记必须与显示行逐行对应");
                Assert.Greater(view.DisplayedLineCount, 0, "过滤视图仍显示头行 + 命中行");
                Assert.Less(view.DisplayedLineCount, view.CurrentVm.RawLines.Count,
                    $"过滤视图行数（{view.DisplayedLineCount}）必须 < 全量 Raw 行数（{view.CurrentVm.RawLines.Count}）");
                Assert.AreEqual(1, balanceFn(), "Summary → Raw（含过滤态）过渡必须消耗 1 情报点");
                Assert.AreEqual(1, view.CurrentVm.IntelPoints, "VM 必须显示扣后余额");
            }
            finally
            {
                presenter.Detach();
            }
        }

        /// <summary>
        /// N2（简报点名）：情报点 0 → Raw 请求被拒（停在 Summary、不扣点）且给出禁用态
        /// （VM.RawAffordable=false——视图侧按钮置灰 + 提示行由该位驱动，PlayMode C7 亲证 UGUI 态）。
        /// </summary>
        [Test]
        public void N2_ZeroIntel_RawRequestRefusedWithDisabledState()
        {
            (Report summary, Report raw, int month) = ComposeAnomalyMonth();

            var view = new ReportPanelView();
            var presenter = new ReportPresenter(view);
            int[] holder = { 0 };
            (Func<int> balanceFn, Func<bool> spendFn) = MakeWallet(holder);
            presenter.Attach(ReportPanelArgs.Create(month, summary, raw, null, balanceFn, spendFn));

            try
            {
                Assert.IsFalse(view.IsRawDisplayed, "前提：默认 Summary 档");

                view.NotifyRawToggleChanged(true);   // 用户路径：请求开 Raw

                Assert.IsFalse(view.IsRawDisplayed, "情报点 0：Raw 请求必须被拒（停在 Summary）");
                Assert.IsFalse(view.CurrentVm.ShowRaw, "VM 不得进入 Raw 档");
                Assert.AreEqual(0, view.CurrentVm.IntelPoints, "VM 显示余额 0");
                Assert.IsFalse(view.CurrentVm.RawAffordable, "禁用态必须给出（余额 0 → RawAffordable=false）");
                Assert.AreEqual(0, balanceFn(), "被拒不得扣点");

                // 异常项跳转路径同样被拒（付费闸门对两条入口一致）
                view.NotifyAnomalyClicked(0);
                Assert.IsFalse(view.IsRawDisplayed, "异常项跳转在情报点 0 时同样被拒");
                Assert.AreEqual(0, balanceFn(), "被拒不得扣点（异常项路径）");
            }
            finally
            {
                presenter.Detach();
            }
        }

        /// <summary>
        /// N3（简报点名）：消费成功扣 1 点；「Raw 每次打开消耗 1 点」——合上再开再扣。
        /// 已在 Raw 态内的重复开启（重复 isOn=true）不重复扣。
        /// </summary>
        [Test]
        public void N3_RawOpen_ConsumesOneIntelPointPerOpen()
        {
            (Report summary, Report raw, int month) = ComposeAnomalyMonth();

            var view = new ReportPanelView();
            var presenter = new ReportPresenter(view);
            int[] holder = { 2 };
            (Func<int> balanceFn, Func<bool> spendFn) = MakeWallet(holder);
            presenter.Attach(ReportPanelArgs.Create(month, summary, raw, null, balanceFn, spendFn));

            try
            {
                view.NotifyRawToggleChanged(true);
                Assert.IsTrue(view.IsRawDisplayed, "余额 2 → 开 Raw 成功");
                Assert.AreEqual(1, balanceFn(), "消费成功扣 1 点");
                Assert.AreEqual(1, view.CurrentVm.IntelPoints, "VM 显示扣后余额");
                Assert.IsTrue(view.CurrentVm.RawAffordable, "余额 1 仍可再开");

                // 已在 Raw：重复开启（UI 上 isOn 已 true 不会发生，程序路径防御）不重复扣
                view.NotifyRawToggleChanged(true);
                Assert.AreEqual(1, balanceFn(), "已在 Raw 态内的重复开启不重复扣点");

                // 合上再开：每次打开各扣 1 点（简报「Raw 每次打开消耗 1 点」）
                view.NotifyRawToggleChanged(false);
                Assert.IsFalse(view.IsRawDisplayed, "合上回到 Summary");
                Assert.AreEqual(1, balanceFn(), "合上不扣点");
                view.NotifyRawToggleChanged(true);
                Assert.IsTrue(view.IsRawDisplayed, "第二次打开成功");
                Assert.AreEqual(0, balanceFn(), "第二次打开再扣 1 点");
                Assert.AreEqual(0, view.CurrentVm.IntelPoints, "余额归零后 VM 显示 0");
                Assert.IsFalse(view.CurrentVm.RawAffordable, "余额 0 → 禁用态");
            }
            finally
            {
                presenter.Detach();
            }
        }

        /// <summary>
        /// N4（简报点名）：MonthTick 后情报点 +1（情报渠道的月度维持费）；0 点时花费被拒（闸门在 Domain）。
        /// </summary>
        [Test]
        public void N4_MonthTickGrantsIntelPoint()
        {
            var w = BuildWorld();
            Assert.AreEqual(2, w.Governor.IntelPoints, "初始情报点 2（简报钉死）");

            Assert.IsTrue(w.Governor.SpendIntelPoint(), "余额 2 可扣");
            Assert.IsTrue(w.Governor.SpendIntelPoint(), "余额 1 可扣");
            Assert.AreEqual(0, w.Governor.IntelPoints);
            Assert.IsFalse(w.Governor.SpendIntelPoint(), "0 点时花费必须被拒不扣（Domain 闸门）");
            Assert.AreEqual(0, w.Governor.IntelPoints, "被拒后余额不变");

            w.AdvanceDays(30);   // 第 30 天月结
            Assert.AreEqual(1, w.Governor.IntelPoints, "MonthTick 每月 +1（月度维持费）");
            w.AdvanceDays(30);
            Assert.AreEqual(2, w.Governor.IntelPoints, "第二个月再 +1");
            Assert.IsTrue(w.Governor.SpendIntelPoint(), "补给后可继续消费");
        }

        /// <summary>
        /// N5（简报括号项「或新增」口径）：情报点随 Capture→Restore 往返不丢点数（花钱与月度
        /// 维持费的余额都随档）；读档世界可继续消费。S1/S5 另有同字段断言（既有用例内补一条）。
        /// </summary>
        [Test]
        public void N5_IntelPoints_SurviveSaveRoundTrip()
        {
            var w = BuildWorld();
            Assert.IsTrue(w.Governor.SpendIntelPoint(), "前提：花 1 点（2→1）");
            w.AdvanceDays(30);   // 第 30 天月结：月度维持费 +1（1→2）
            Assert.AreEqual(2, w.Governor.IntelPoints, "前提：花费 + 月度维持费后的余额");

            var before = ColonySaveCapture.Capture(w);
            Assert.AreEqual(2, before.Governor.IntelPoints, "Capture 必须带走情报点");
            var restored = ColonySaveRestore.Restore(before);
            Assert.AreEqual(w.Governor.IntelPoints, restored.Governor.IntelPoints, "存档往返不丢点数");
            Assert.AreEqual(2, restored.Governor.IntelPoints);

            Assert.IsTrue(restored.Governor.SpendIntelPoint(), "读档世界可继续消费情报点");
            Assert.AreEqual(1, restored.Governor.IntelPoints, "读档后消费正常入账");

            var after = ColonySaveCapture.Capture(restored);
            Assert.AreEqual(restored.Governor.IntelPoints, after.Governor.IntelPoints,
                "读档后消费的新余额必须如实入档（Restore→Capture 二次往返）");
        }
    }
}
