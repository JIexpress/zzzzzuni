// ReportPresenter.cs —— TASK-A-COL-05：月度报告 MVP 的 P 层（纯 C#，不依赖 UnityEngine）。
// 职责：Report（Domain，Game.Domain.Colony.Player.Report）→ 面板 VM（ReportPanelVm）→ 视图渲染；
// 视图交互（查原始记录 toggle / 关闭按钮）经事件上抛到这里，业务决定权在 P 层。
// 可 EditMode 测试：view 传裸 ReportPanelView 实例（不调 OnInit 就不建 GameObject），
// Attach → Render 全链路不碰 Unity 对象。
//
// TASK-A-COL-07 迭代（情报点买 Raw + 异常项跳转）新增三条用户路径语义：
//   · 开 Raw 是付费动作：toggle 开启或点异常项从 Summary 跳 Raw，都先经
//     ReportPanelArgs.SpendIntel 扣 1 情报点；余额不足 → 拒绝（弹回 Summary + 禁用态 +
//     提示行，VM.RawAffordable=false）。「Raw 每次打开消耗 1 点」——已在 Raw 态内点其他
//     异常项只是切过滤，不重复扣。
//   · 异常项是入口：点 Summary 里的异常项 → 切 Raw + 按该类异常最相关的日志 Kind 过滤 +
//     命中行高亮（映射表见 <see cref="KindOfAnomaly"/>，行解析见 <see cref="TryParseRawLineKind"/>）。
//   · SetRawVisible 仍是程序直改通道（P 层显式开关，生产 UI 不调用），不走情报点——
//     A-COL-05 P3/P4 契约保持。

using System;
using System.Collections.Generic;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Sim;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.Colony
{
    /// <summary>
    /// Push args of <see cref="ReportPanelView"/>（照 MainMenuArgs 先例：P 层纯 C# 够不到
    /// 流程持有者，打开者（ColonyWorldHost）把当月报告、关闭意图与情报点钱包注入进来）。
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

        /// <summary>
        /// 情报点余额查询（TASK-A-COL-07；null = 未接入情报点——A-COL-05 兼容口径，Raw 免费）。
        /// 由打开者注入（生产：ColonyWorldHost 注入 Governor.IntelPoints 的活查询）。
        /// </summary>
        public Func<int> IntelBalance { get; private set; }

        /// <summary>
        /// 扣 1 情报点的意图（不足返回 false 不扣；null = 未接入）。生产注入 Governor.SpendIntelPoint。
        /// </summary>
        public Func<bool> SpendIntel { get; private set; }

        /// <summary>宿主入口：从月结事件载荷构造（Close 由宿主注入；情报点钱包可选注入）。</summary>
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

        /// <summary>宿主入口（TASK-A-COL-07）：携带情报点钱包（活查询 + 扣点意图）。</summary>
        public static ReportPanelArgs Create(ColonyReportReady latest, Action close,
            Func<int> intelBalance, Func<bool> spendIntel)
        {
            return new ReportPanelArgs
            {
                Month = latest?.Month ?? 0,
                Summary = latest?.Summary,
                Raw = latest?.Raw,
                Close = close,
                IntelBalance = intelBalance,
                SpendIntel = spendIntel,
            };
        }

        /// <summary>测试/直构入口：显式给齐月份与两档报告（情报点钱包可选——缺省 = 未接入）。</summary>
        public static ReportPanelArgs Create(int month, Report summary, Report raw, Action close,
            Func<int> intelBalance = null, Func<bool> spendIntel = null)
        {
            return new ReportPanelArgs
            {
                Month = month,
                Summary = summary,
                Raw = raw,
                Close = close,
                IntelBalance = intelBalance,
                SpendIntel = spendIntel,
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
        private EventKind? _rawKindFilter;   // 异常项跳转的过滤 Kind（null = 全量）

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
            _showRaw = false;        // 默认 Summary 档——视野纪律：主动检查才见 Raw
            _rawKindFilter = null;

            _view.RawToggleChanged += OnRawToggle;
            _view.AnomalyClicked += OnAnomalyClicked;
            _view.CloseRequested += OnClose;
            EventBus.Subscribe<ColonyReportReady>(OnReportReady);

            Render();
        }

        /// <inheritdoc />
        protected override void OnDetach()
        {
            _view.RawToggleChanged -= OnRawToggle;
            _view.AnomalyClicked -= OnAnomalyClicked;
            _view.CloseRequested -= OnClose;
            _args = null;
        }

        /// <summary>
        /// P 层显式开关 Raw 档（测试与宿主均可调；同步视图 toggle 状态但不回流触发事件）。
        /// 程序直改通道不走情报点（生产 UI 只经 toggle/异常项两条付费路径）。
        /// </summary>
        public void SetRawVisible(bool showRaw)
        {
            _showRaw = showRaw;
            _rawKindFilter = null;   // 直改是全量口径
            _view.SetRawToggleState(showRaw);
            Render();
        }

        private void OnRawToggle(bool showRaw)
        {
            if (!showRaw)
            {
                _showRaw = false;
                _rawKindFilter = null;   // 合上 Raw 即清过滤（下次打开重新选）
                Render();
                return;
            }

            if (_showRaw)
            {
                return;   // 已在 Raw：重复开启不重复扣点
            }

            if (!TrySpendIntelForRaw())
            {
                RefuseRaw();
                return;
            }

            _showRaw = true;
            Render();
        }

        /// <summary>
        /// 异常项点击（视图按钮 → 此处）：切到 Raw + 按该异常最相关的 Kind 过滤。
        /// Summary → Raw 的过渡同 toggle 一样消耗 1 情报点（不足拒绝）；已在 Raw 态内
        /// 只是切过滤，不重复扣（「每次打开消耗 1 点」）。
        /// </summary>
        private void OnAnomalyClicked(int anomalyIndex)
        {
            ReportPanelVm vm = _view.CurrentVm;
            string anomaly = vm != null && anomalyIndex >= 0 && anomalyIndex < vm.AnomalyLines.Count
                ? vm.AnomalyLines[anomalyIndex]
                : null;
            if (anomaly == null)
            {
                return;   // 越界/空状态：无害忽略
            }

            if (!_showRaw && !TrySpendIntelForRaw())
            {
                RefuseRaw();
                return;
            }

            _showRaw = true;
            _rawKindFilter = KindOfAnomaly(anomaly);
            Render();
        }

        /// <summary>开 Raw 前的扣点闸门：未接入情报点 = 免费通道（兼容旧调用方）；接入则扣 1 点。</summary>
        private bool TrySpendIntelForRaw() => _args?.SpendIntel == null || _args.SpendIntel();

        /// <summary>拒绝开 Raw：弹回 Summary 档；Render 后 VM 携带禁用态（按钮置灰 + 提示行）。</summary>
        private void RefuseRaw()
        {
            _showRaw = false;
            _view.SetRawToggleState(false);
            Render();
        }

        private void OnClose()
        {
            _args?.Close?.Invoke();
        }

        private void OnReportReady(ColonyReportReady evt)
        {
            // 面板开着时新月份结算：就地换到最新报告并回到默认 Summary 视野
            // （Raw 是结算时刻快照，旧 Raw 不再代表"当月"，必须随报告一起换）；
            // 情报点钱包是 Governor 的活引用，随 args 原样保留。
            _args = ReportPanelArgs.Create(evt, _args?.Close, _args?.IntelBalance, _args?.SpendIntel);
            _showRaw = false;
            _rawKindFilter = null;
            _view.SetRawToggleState(false);
            Render();
        }

        private void Render()
        {
            int month = _args?.Month ?? 0;
            int? intelPoints = _args?.IntelBalance?.Invoke();
            _view.Render(BuildVm(_args?.Summary, _args?.Raw, month, _showRaw, intelPoints, _rawKindFilter));
        }

        /// <summary>
        /// Report → VM（简报点名的 P 层纯逻辑）。视野纪律由输入 Report 的构成保证：
        /// Summary 档的 Lines/Anomalies 天然不含 Covert 来源行（A-COL-04 M2），
        /// Raw 档天然含（Truth 全量）——本方法不重复过滤，只做展示编排。
        /// 4 参重载是 A-COL-05 契约（未接入情报点、无过滤）。
        /// </summary>
        public static ReportPanelVm BuildVm(Report summary, Report raw, int month, bool showRaw)
            => BuildVm(summary, raw, month, showRaw, null, null);

        /// <summary>
        /// Report → VM（TASK-A-COL-07 全量版）：情报点余额（null = 未接入）+ Raw 过滤态。
        /// 过滤态显示行 = Raw 头行 + 命中 <paramref name="rawKindFilter"/> 的行（命中行带高亮标记）。
        /// </summary>
        public static ReportPanelVm BuildVm(Report summary, Report raw, int month, bool showRaw,
            int? intelPoints, EventKind? rawKindFilter)
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

            IReadOnlyList<string> rawLines = raw?.Lines ?? empty;
            var display = new List<string>();
            var hits = new List<bool>();
            if (rawKindFilter == null)
            {
                display.AddRange(rawLines);                      // 未过滤 = 全量（C4 契约）
                hits.AddRange(new bool[rawLines.Count]);
            }
            else
            {
                for (int i = 0; i < rawLines.Count; i++)
                {
                    string line = rawLines[i];
                    if (i == 0 && line.StartsWith("【", StringComparison.Ordinal))
                    {
                        display.Add(line);                       // Raw 头行保留（上下文）
                        hits.Add(false);
                        continue;
                    }

                    if (TryParseRawLineKind(line, out EventKind kind) && kind == rawKindFilter.Value)
                    {
                        display.Add(line);                       // 命中行（视图高亮色）
                        hits.Add(true);
                    }
                }
            }

            string title = hasReport ? $"月度报告 · 第 {month} 月" : "月度报告 · 尚未结算";
            return new ReportPanelVm(
                month,
                title,
                summary?.Lines ?? empty,
                anomalyLines,
                rawLines,
                showRaw && hasReport,
                hasReport,
                intelPoints,
                rawKindFilter,
                display,
                hits);
        }

        /// <summary>
        /// 异常项 → Raw 过滤 Kind（TASK-A-COL-07 跳转映射）。异常项是状态/事件混合口径
        /// （MonthlyReport.DetectAnomalies），映射取「该类异常最相关的日志 Kind」：
        /// 组织度越阈 ↔ Uprising（组织者涌现是因，Covert 也进 Raw）；怨气地板 ↔ Protest；
        /// 具名离开 ↔ Departure；支线回找 ↔ Quest；无法映射的异常不过滤（全量 Raw）。
        /// </summary>
        internal static EventKind? KindOfAnomaly(string anomalyLine)
        {
            if (anomalyLine == null)
            {
                return null;
            }

            if (anomalyLine.Contains("支线回找")) return EventKind.Quest;
            if (anomalyLine.Contains("离开")) return EventKind.Departure;
            if (anomalyLine.Contains("越过阈值") || anomalyLine.Contains("组织度")) return EventKind.Uprising;
            if (anomalyLine.Contains("怨气地板")) return EventKind.Protest;
            return null;
        }

        /// <summary>
        /// 解析 Raw 行的事件 Kind（MonthlyReport.BuildRaw 的行格式契约）：
        /// 常规行 = "[Day][Vis][Kind][Where] Text"（第 3 段）；
        /// 自主折叠行 = "[自主折叠][Kind] …"（第 2 段，A-COL-06 折叠口径）。
        /// 解析失败（头行 / 异常项节行 / 未知格式）返回 false——过滤态下不显示。
        /// </summary>
        internal static bool TryParseRawLineKind(string line, out EventKind kind)
        {
            kind = default;
            if (string.IsNullOrEmpty(line) || line[0] != '[')
            {
                return false;
            }

            var segments = new List<string>(4);
            int cursor = 0;
            while (cursor < line.Length && line[cursor] == '[')
            {
                int close = line.IndexOf(']', cursor + 1);
                if (close < 0)
                {
                    return false;
                }

                segments.Add(line.Substring(cursor + 1, close - cursor - 1));
                cursor = close + 1;
            }

            string kindText = null;
            if (segments.Count >= 3 && segments[0] != "自主折叠")
            {
                kindText = segments[2];
            }
            else if (segments.Count >= 2 && segments[0] == "自主折叠")
            {
                kindText = segments[1];
            }

            return kindText != null && Enum.TryParse(kindText, ignoreCase: false, out kind);
        }
    }
}
