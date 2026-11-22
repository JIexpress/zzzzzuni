// ColonyVerticalSliceTests.cs —— TASK-A-COL-05：垂直切片场景级证据（C1-C4）。
// 口径（简报）：① 场景加载后 ColonyWorldHost 存在且 Day 随真实时间推进 ② 模拟 31+ 游戏日收到
// ColonyReportReady 且 Report 非空 ③ ReportPanel 可开合且显示当月 Summary ④ Raw 切换后行数 > Summary。
//
// 取证隔离纪律（HANDOFF 记录过 fixture 场景泄漏坑，B-PLANET-15 教训同源）：
//  - 每条用例显式单模式加载 SCN_Game（宿主是 DontDestroyOnLoad——前序用例残留必先销毁，
//    保证世界从 Day=1 干净开始）；
//  - finally 清理：面板 Pop + 宿主销毁 + EventBus.FlushQueued；
//  - 清理断言：宿主必须已解除静态引用（环境不干净必须红，绝不静默取证）。
//  宿主自身零 Renderer/零可见物（同 SurvivalLoop 的"默认零影响"口径），不碰像素类用例的环境。

using System;
using System.Collections;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;
using Game.View.Colony;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.PlayMode.Tests
{
    /// <summary>垂直切片 C1-C4：真实时钟推进、月度结算广播、报告面板栈开合与 Raw 检查。</summary>
    public sealed class ColonyVerticalSliceTests
    {
        private IDisposable _eventToken;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _eventToken?.Dispose();
            _eventToken = null;

            // 清理顺序：面板（若在栈上）→ 宿主 → 一帧（Destroy 延迟到帧末）→ 清理断言。
            if (UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>())
            {
                UIManager.Instance.Pop(ColonyWorldHost.ReportPanelConfig);
            }

            if (ColonyWorldHost.Instance != null)
            {
                UnityEngine.Object.Destroy(ColonyWorldHost.Instance.gameObject);
            }

            yield return null;

            Assert.IsNull(ColonyWorldHost.Instance, "清理断言：宿主必须已销毁（fixture 不泄漏）");
            if (UIManager.Instance != null)
            {
                Assert.IsFalse(UIManager.Instance.IsOnStack<ReportPanelView>(), "清理断言：报告面板必须已回栈");
            }

            EventBus.FlushQueued();
        }

        // ────────────────────────────── C1-C4 ──────────────────────────────

        /// <summary>
        /// C1：SCN_Game 加载后宿主自动挂载（数据驱动建局），DaysPerRealSecond=0 停表、
        /// 默认 1.0 时 Day 随真实时钟推进（等待 ~2s 断言 ≥1 游戏日）+ Day/SimulatedDays 恒等式。
        /// </summary>
        [UnityTest]
        public IEnumerator C1_SceneLoad_MountsHost_DayAdvancesWithRealTime()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;

            Assert.IsTrue(host.IsWorldReady, "宿主必须完成数据驱动建局（真实五表 + 固定种子）");
            Assert.IsNotNull(host.World, "World 聚合根必须在位");
            Assert.AreEqual(3, host.World.Regions.Length, "真实数据建局：人口前三 = 3 地区（A-COL-02 D2 口径）");
            Assert.AreEqual(100, host.World.Named.Count, "真实数据建局：100 张 GLM 卡全部注册（D2 口径）");
            Assert.AreEqual(ColonyWorldHost.DefaultDaysPerRealSecond, host.DaysPerRealSecond,
                "默认时间比例必须是 1.0（1 真实秒 = 1 游戏日）");

            // 可配置性正对照：比例 0 = 停表（推进与真实时间脱钩的能力来源）
            uint daysBeforeFreeze = host.SimulatedDays;
            host.DaysPerRealSecond = 0f;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.AreEqual(daysBeforeFreeze, host.SimulatedDays,
                "比例 0 时不得推进任何游戏日（停表）");

            // 默认比例：等待 ~2s（简报口径）→ 至少推进 1 游戏日
            host.DaysPerRealSecond = ColonyWorldHost.DefaultDaysPerRealSecond;
            uint before = host.SimulatedDays;
            yield return new WaitForSecondsRealtime(2.2f);
            Assert.GreaterOrEqual(host.SimulatedDays - before, 1u,
                "等待 ~2s 必须按真实时钟推进 ≥1 游戏日（DaysPerRealSecond=1.0）");
            Assert.AreEqual(1u + host.SimulatedDays, host.Day,
                "恒等式：Day 必须等于建局 Day(1) + 推进日数");
        }

        /// <summary>
        /// C2：模拟 31+ 游戏日（DebugFastForward 加速钩子）→ 收到 ColonyReportReady 且 Report 非空；
        /// 施政先行（Aggressive 执政官《增产令》已入 Truth 日志）；宿主留存最近月报。
        /// </summary>
        [UnityTest]
        public IEnumerator C2_MonthlySettlement_FiresColonyReportReady_WithNonEmptyReport()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;

            ColonyReportReady received = null;
            _eventToken = EventBus.Subscribe<ColonyReportReady>(e => received = e);

            Assert.IsNull(host.LatestReport, "首月结算前不得有月报");
            host.DebugFastForward(31); // 逐日走宿主同一条推进路径（月结照常触发）

            Assert.IsNotNull(received, "跨过第 30 游戏日必须收到 ColonyReportReady");
            Assert.AreEqual(1, received.Month, "满 30 日 = 第 1 月");
            Assert.IsNotNull(received.ReportLines, "ReportLines 不得为 null");
            Assert.IsNotEmpty(received.ReportLines, "当月 Report 行必须非空");
            Assert.IsNotNull(received.Summary, "Summary 档 Report 必须随载荷携带");
            Assert.IsNotNull(received.Raw, "Raw 档 Report 必须随载荷携带（结算时刻快照）");
            Assert.IsNotEmpty(received.Summary.Lines, "Summary 报告行必须非空");
            Assert.IsNotEmpty(received.Raw.Lines, "Raw 报告行必须非空（Truth 全量）");
            Assert.AreSame(received, host.LatestReport, "宿主必须留存最近一次月报载荷");
            Assert.IsTrue(host.World.Log.All.Any(e => e.Text != null && e.Text.Contains("增产令")),
                "月结前执政官必须已施加《增产令》（施政先于月结的日志证据）");
        }

        /// <summary>
        /// C3：报告面板可开合——经 UIManager 面板栈注册（pure-view 代码构建 UGUI），
        /// 默认 Summary 档且显示当月月份（视野纪律：Raw 需主动检查）。
        /// </summary>
        [UnityTest]
        public IEnumerator C3_ReportPanel_OpensOnStack_ShowsCurrentMonthSummary()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;
            host.DebugFastForward(31); // 确保已有当月报告

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");

            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view), "面板栈上必须能取回视图");
            Assert.IsTrue(view.IsBuilt, "运行时装配：UGUI 层级必须已代码构建（无 prefab 资产）");
            Assert.IsTrue(view.IsVisible, "面板打开后必须处于显示状态");
            Assert.IsNotNull(view.RawToggle, "运行时装配：「查原始记录」toggle 必须存在");

            Assert.IsNotNull(view.CurrentVm, "打开后必须已 Render VM");
            Assert.AreEqual(1, view.CurrentVm.Month, "必须显示当月（第 1 月）报告");
            StringAssert.Contains("第 1 月", view.CurrentVm.TitleLine, "标题行必须含当前月份");
            Assert.IsFalse(view.IsRawDisplayed, "默认 Summary 档（视野纪律：Raw 需玩家主动检查）");
            Assert.Greater(view.DisplayedLineCount, 0, "Summary 显示行数必须非空");
        }

        /// <summary>
        /// C4：「查原始记录」真实 UGUI toggle 切换 → Raw 行数 > Summary（Truth 全量 vs 页数预算）；
        /// 关闭回栈（Pop 后面板离栈且不再显示）。
        /// </summary>
        [UnityTest]
        public IEnumerator C4_RawToggle_ShowsMoreLines_ClosePopsPanel()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;
            host.DebugFastForward(31);

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");
            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view));

            int summaryCount = view.DisplayedLineCount;
            Assert.Greater(summaryCount, 0, "前提：Summary 显示行数非空");

            // 真实控件路径：UGUI Toggle.isOn 触发 onValueChanged → 视图事件 → P 层重渲染
            view.RawToggle.isOn = true;
            yield return null;
            Assert.IsTrue(view.IsRawDisplayed, "toggle 开启后必须切到 Raw 档");
            Assert.Greater(view.DisplayedLineCount, summaryCount,
                $"Raw 行数（{view.DisplayedLineCount}）必须多于 Summary（{summaryCount}）");

            // 关闭回栈：按钮同款入口 → 宿主 Pop → 面板离栈
            view.RequestClose();
            yield return null;
            Assert.IsFalse(UIManager.Instance.IsOnStack<ReportPanelView>(), "关闭后面板必须离栈");
            Assert.IsFalse(view.IsVisible, "关闭后面板必须不再显示");
        }

        /// <summary>
        /// C5（TASK-A-COL-07）：Raw 内容超屏时 ScrollRect 可滚——content 高 &gt; viewport 高
        /// （简报口径）。行数保障：开局对全部具名 NPC OnViolation → 第 31 天确定性涌入 100 条
        /// 「沉默期开始」契约行（月 2 窗口 [31,60] 内），Raw 行数远超屏；toggle 开 Raw 走付费
        /// 闸门（情报点 2+月结 1 → 扣 1 成功）。
        /// </summary>
        [UnityTest]
        public IEnumerator C5_RawScroll_ContentExceedsViewport()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;

            // 行数编排：全具名 NPC 违约 → 第 31 天（_daysSinceViolation=31>30）批量滑入沉默期，
            // 每人一条 Personal Contract 行，全部落在月 2 窗口 [31,60]（非自主行为，不折叠）。
            foreach (NamedNpc n in host.World.Named)
            {
                n.Contract.OnViolation();
            }
            host.DebugFastForward(61);   // 月 2 结算（第 60 天收尾后触发，窗口 [31,60]）

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");
            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view));
            Assert.IsNotNull(view.CurrentVm, "前提：面板已 Render");
            Assert.AreEqual(2, view.CurrentVm.Month, "前提：显示第 2 月报告");

            Assert.Greater(view.CurrentVm.RawLines.Count, 60,
                $"前提：编排后 Raw 行数远超一屏（实测 {view.CurrentVm.RawLines.Count} 行）");

            view.RawToggle.isOn = true;   // 真实控件路径：开 Raw（付费闸门：4 → 3）
            yield return null;
            Assert.IsTrue(view.IsRawDisplayed, "toggle 开启后必须切到 Raw 档");

            Assert.IsNotNull(view.BodyScroll, "运行时装配：正文必须包 ScrollRect");
            Assert.IsTrue(view.BodyScroll.vertical, "垂直滚动必须开启");
            Assert.IsNotNull(view.ScrollContent, "运行时装配：滚动 Content 必须存在");
            Assert.IsNotNull(view.ScrollViewport, "运行时装配：滚动 Viewport 必须存在");
            float contentHeight = view.ScrollContent.rect.height;
            float viewportHeight = view.ScrollViewport.rect.height;
            float row0Height = view.ScrollContent.childCount > 0
                ? ((RectTransform)view.ScrollContent.GetChild(0)).rect.height
                : 0f;
            Debug.Log($"[ColonyVerticalSlice] C5 scroll metrics: content={contentHeight:0.0} viewport={viewportHeight:0.0} " +
                $"rows={view.ScrollContent.childCount} row0H={row0Height:0.0} RawLines={view.CurrentVm.RawLines.Count}");
            Assert.Greater(contentHeight, viewportHeight,
                $"Raw 内容超屏必须可滚：content 高 {contentHeight:0} 必须 > viewport 高 {viewportHeight:0}" +
                $"｜RawLines={view.CurrentVm.RawLines.Count} Displayed={view.DisplayedLineCount}" +
                $" childCount={view.ScrollContent.childCount} row0Height={row0Height:0.0}");
        }

        /// <summary>
        /// C6（TASK-A-COL-07）：点异常项 → 过滤视图行数 &lt; 全量 Raw 行数且高亮存在。
        /// 编排：残垣城双镇压（组织度 0.65 越阈值 A/B）→ 第 7 天周结确定性涌现组织者
        /// （Covert Uprising 事件落在月 1 窗口 [1,30]）；真实 UGUI 按钮路径点击（ClickAnomalyRow）。
        /// </summary>
        [UnityTest]
        public IEnumerator C6_AnomalyClick_FiltersRawByKindWithHighlights()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;

            Settlement repressed = host.World.SettlementOf("残垣城");
            Assert.IsNotNull(repressed, "前提：残垣城聚落存在（places.csv 人口第二）");
            repressed.Apply(Decision.Harsh);   // 组织度 +0.30 ×2 → 0.65 > 阈值 A(0.35)/B(0.55)
            repressed.Apply(Decision.Harsh);
            host.DebugFastForward(31);         // 月 1 结算：第 7 天组织者涌现（Uprising Covert）在窗口内

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");
            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view));
            Assert.IsNotNull(view.CurrentVm);
            Assert.AreEqual(1, view.CurrentVm.Month, "前提：显示第 1 月报告");

            int fullRawCount = view.CurrentVm.RawLines.Count;
            Assert.Greater(fullRawCount, 0, "前提：全量 Raw 行非空");
            int orgIndex = -1;
            for (int i = 0; i < view.CurrentVm.AnomalyLines.Count; i++)
            {
                if (view.CurrentVm.AnomalyLines[i].Contains("越过阈值"))
                {
                    orgIndex = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(orgIndex, 0,
                "前提：组织度越阈异常在高亮清单中：" + string.Join("｜", view.CurrentVm.AnomalyLines));

            view.ClickAnomalyRow(orgIndex);   // 真实 UGUI 按钮路径（onClick.Invoke；付费闸门 3 → 2）
            yield return null;

            Assert.IsTrue(view.IsRawDisplayed, "点异常项必须切到 Raw 视图");
            Assert.AreEqual(EventKind.Uprising, view.CurrentVm.RawKindFilter,
                "组织度越阈异常必须按 Uprising 过滤");
            Debug.Log($"[ColonyVerticalSlice] C6 filter metrics: filteredRows={view.DisplayedLineCount} " +
                $"fullRaw={fullRawCount} hits={view.CurrentVm.RawHitFlags.Count(h => h)} " +
                $"filter={view.CurrentVm.RawKindFilter} intel={host.World.Governor.IntelPoints}");
            Assert.Less(view.DisplayedLineCount, fullRawCount,
                $"过滤视图行数（{view.DisplayedLineCount}）必须 < 全量 Raw 行数（{fullRawCount}）");
            Assert.Greater(view.CurrentVm.RawHitFlags.Count(h => h), 0, "命中行高亮必须存在");
        }

        /// <summary>
        /// C7（TASK-A-COL-07）：IntelPoints=0 时按钮禁用态可见——toggle interactable=false、
        /// 提示行「情报渠道不足（每月 +1）」显示、顶部情报点常驻显示 0。
        /// </summary>
        [UnityTest]
        public IEnumerator C7_ZeroIntel_RawToggleDisabledWithHint()
        {
            yield return LoadFreshSlice();
            ColonyWorldHost host = ColonyWorldHost.Instance;
            host.DebugFastForward(31);   // 月结维持费 +1：2 → 3

            var governor = host.World.Governor;
            Assert.AreEqual(3, governor.IntelPoints, "前提：初始 2 + 月度维持费 1");
            Assert.IsTrue(governor.SpendIntelPoint());
            Assert.IsTrue(governor.SpendIntelPoint());
            Assert.IsTrue(governor.SpendIntelPoint());
            Assert.AreEqual(0, governor.IntelPoints, "前提：情报点已耗尽");
            Assert.IsFalse(governor.SpendIntelPoint(), "前提：Domain 闸门在 0 点时拒绝");

            host.OpenReportPanel();
            yield return WaitFor(() =>
                UIManager.Instance != null && UIManager.Instance.IsOnStack<ReportPanelView>(),
                "报告面板入栈");
            Assert.IsTrue(UIManager.Instance.TryGetView(out ReportPanelView view));
            Assert.IsNotNull(view.CurrentVm);

            Assert.IsFalse(view.CurrentVm.RawAffordable, "VM 禁用态：余额 0");
            Assert.IsNotNull(view.RawToggle, "禁用态载体：查 Raw toggle 必须存在");
            Debug.Log($"[ColonyVerticalSlice] C7 disabled metrics: intel={host.World.Governor.IntelPoints} " +
                $"interactable={view.RawToggle.interactable} hintVisible={view.IsRawHintVisible} " +
                $"label={view.IntelPointsLabel}");
            Assert.IsFalse(view.RawToggle.interactable, "情报点 0：查 Raw 按钮必须置灰（interactable=false）");
            Assert.IsTrue(view.IsRawHintVisible, "提示行「情报渠道不足（每月 +1）」必须可见");
            StringAssert.Contains("情报点 0", view.IntelPointsLabel, "顶部情报点常驻必须显示 0");
            Assert.IsFalse(view.IsRawDisplayed, "禁用态下不得已在 Raw 档");
        }

        // ---- fixture 纪律与工具 -----------------------------------------------------

        /// <summary>
        /// 取证隔离入口：销毁前序残留宿主 → 单模式加载 SCN_Game → 等待挂载。
        /// 世界从 Day=1 干净开始（宿主 DontDestroyOnLoad，跨场景存活，必须显式清场）。
        /// </summary>
        private IEnumerator LoadFreshSlice()
        {
            if (ColonyWorldHost.Instance != null)
            {
                UnityEngine.Object.Destroy(ColonyWorldHost.Instance.gameObject);
                yield return null;
                Assert.IsNull(ColonyWorldHost.Instance, "环境不干净：前序宿主未销毁（fixture 泄漏）");
            }

            yield return SceneManager.LoadSceneAsync(ColonyWorldHost.GameSceneName);
            Assert.AreEqual(ColonyWorldHost.GameSceneName, SceneManager.GetActiveScene().name,
                "SCN_Game 必须成为活动场景");
            yield return null;
            yield return null;

            Assert.IsNotNull(ColonyWorldHost.Instance, "SCN_Game 加载后必须自动挂载 ColonyWorldHost（运行时装配）");
            yield return WaitFor(() => ColonyWorldHost.Instance.IsWorldReady, "数据驱动建局完成");
        }

        /// <summary>有界等待（10s）：条件不满足超时即红，绝不挂死整个 PlayMode 轮次。</summary>
        private static IEnumerator WaitFor(Func<bool> done, string what)
        {
            float waited = 0f;
            while (!done() && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            Assert.IsTrue(done(), $"超时等待：{what}");
        }
    }
}
