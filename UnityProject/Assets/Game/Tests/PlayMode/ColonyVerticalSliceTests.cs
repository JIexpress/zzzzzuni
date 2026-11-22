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
