// ReportPresenter.cs —— TASK-A-COL-05：月度报告 MVP 的 P 层（纯 C#，不依赖 UnityEngine）。
// 职责：Report（Domain，Game.Domain.Colony.Player.Report）→ 面板 VM（ReportPanelVm）→ 视图渲染；
// 视图交互（查原始记录 toggle / 关闭按钮）经事件上抛到这里，业务决定权在 P 层。
// 可 EditMode 测试：view 传裸 ReportPanelView 实例（不调 OnInit 就不建 GameObject），
// Attach → Render 全链路不碰 Unity 对象。

using System;
using System.Collections.Generic;
using Game.Domain.Colony.Player;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.Colony
{
    /// <summary>
    /// Push args of <see cref="ReportPanelView"/>（照 MainMenuArgs 先例：P 层纯 C# 够不到
    /// 流程持有者，打开者（ColonyWorldHost）把当月报告与关闭意图注入进来）。
    /// </summary>
    public sealed class ReportPanelArgs
    {
        /// <summary>当前月份（0 = 尚未结算）。</summary>
        public int Month { get; private set; }

        /// <summary>当月 Summary 档 Report（AI 执政官口径；null = 未结算空状态）。</summary>
        public Report Summary { get; private set; }

        /// <summary>当月 Raw 档 Report（诚实口径 Truth 全量；null = 未结算空状态）。</summary>
        public Report Raw { get; private set; }

        /// <summary>关闭面板的意图（由打开者注入——通常是 UIManager.Pop）。</summary>
        public Action Close { get; private set; }

        /// <summary>宿主入口：从月结事件载荷构造（Close 由宿主注入）。</summary>
        public static ReportPanelArgs Create(ColonyReportReady latest, Action close)
        {
            return new ReportPanelArgs
            {
                Month = latest?.Month ?? 0,
                Summary = latest?.Summary,
                Raw = latest?.Raw,
                Close = close,
            };
        }

        /// <summary>测试/直构入口：显式给齐月份与两档报告。</summary>
        public static ReportPanelArgs Create(int month, Report summary, Report raw, Action close)
        {
            return new ReportPanelArgs
            {
                Month = month,
                Summary = summary,
                Raw = raw,
                Close = close,
            };
        }
    }

    /// <summary>
    /// 月度报告 Presenter（MVP 的 P 层）：把两档 Report 编译成 <see cref="ReportPanelVm"/>
    /// 交给视图；订阅 <see cref="ColonyReportReady"/>（订阅发生在 Attach 事件作用域内，
    /// Detach 由 <see cref="UIPresenter"/> 作用域批量退订）实现"面板开着时新月份结算就地刷新"。
    /// </summary>
    public sealed class ReportPresenter : UIPresenter<ReportPanelView>
    {
        private readonly ReportPanelView _view;
        private ReportPanelArgs _args;
        private bool _showRaw;

        /// <summary>Creates the presenter for the injected view（宿主经 UIBinding 注册此工厂）。</summary>
        public ReportPresenter(ReportPanelView view)
            : base(view)
        {
            _view = view;
        }

        /// <inheritdoc />
        protected override void OnAttach(object args)
        {
            _args = args as ReportPanelArgs;
            _showRaw = false; // 默认 Summary 档——视野纪律：主动检查才见 Raw

            _view.RawToggleChanged += OnRawToggle;
            _view.CloseRequested += OnClose;
            EventBus.Subscribe<ColonyReportReady>(OnReportReady);

            Render();
        }

        /// <inheritdoc />
        protected override void OnDetach()
        {
            _view.RawToggleChanged -= OnRawToggle;
            _view.CloseRequested -= OnClose;
            _args = null;
        }

        /// <summary>
        /// P 层显式开关 Raw 档（测试与宿主均可调；同步视图 toggle 状态但不回流触发事件）。
        /// </summary>
        public void SetRawVisible(bool showRaw)
        {
            _showRaw = showRaw;
            _view.SetRawToggleState(showRaw);
            Render();
        }

        private void OnRawToggle(bool showRaw)
        {
            _showRaw = showRaw;
            Render();
        }

        private void OnClose()
        {
            _args?.Close?.Invoke();
        }

        private void OnReportReady(ColonyReportReady evt)
        {
            // 面板开着时新月份结算：就地换到最新报告并回到默认 Summary 视野
            // （Raw 是结算时刻快照，旧 Raw 不再代表"当月"，必须随报告一起换）。
            _args = ReportPanelArgs.Create(evt, _args?.Close);
            _showRaw = false;
            _view.SetRawToggleState(false);
            Render();
        }

        private void Render()
        {
            int month = _args?.Month ?? 0;
            _view.Render(BuildVm(_args?.Summary, _args?.Raw, month, _showRaw));
        }

        /// <summary>
        /// Report → VM（简报点名的 P 层纯逻辑）。视野纪律由输入 Report 的构成保证：
        /// Summary 档的 Lines/Anomalies 天然不含 Covert 来源行（A-COL-04 M2），
        /// Raw 档天然含（Truth 全量）——本方法不重复过滤，只做展示编排。
        /// </summary>
        public static ReportPanelVm BuildVm(Report summary, Report raw, int month, bool showRaw)
        {
            bool hasReport = summary != null && raw != null;
            var empty = new string[0];

            var anomalyLines = new List<string>();
            if (summary != null)
            {
                foreach (string anomaly in summary.Anomalies)
                {
                    anomalyLines.Add("▲ " + anomaly); // 高亮行完整清单（不受 Summary 页数预算截断）
                }
            }

            string title = hasReport ? $"月度报告 · 第 {month} 月" : "月度报告 · 尚未结算";
            return new ReportPanelVm(
                month,
                title,
                summary?.Lines ?? empty,
                anomalyLines,
                raw?.Lines ?? empty,
                showRaw && hasReport,
                hasReport);
        }
    }
}
