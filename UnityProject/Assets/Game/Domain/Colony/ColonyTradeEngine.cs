using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 贸易事件引擎（TASK-A-COL-09）：colonyTrades.csv 20 条进每日结算。
    /// 触发语义（登记待裁决——源 CSV 无 trigger 列，简报「按 trigger 天数区间/条件触发」在
    /// 数据侧缺失）：按确定性调度落地，事件 T{i}（EventId 1..20）的触发日 = EventId×
    /// <see cref="TriggerDayStep"/>（T01@5 … T20@100），「每事件首个满足日触发一次」=
    /// Day ≥ 触发日的第一个结算日触发（游戏日逐 1 推进时即恰好在触发日当天）。
    /// 消费逻辑（简报原文）：解析 impact 串影响 MarketState 对应节点（价格/库存百分比）
    /// + EventLog（visibility 原样）。影响串段式「地名：货物 +N%」（RFC4180 引号承载，
    /// 百分比与 check_t7 同式：10%~80%（价）/10%~50%（库存）、5% 倍数）。
    /// 建局只给人口前二地区建市场节点（双河城/残垣城）——impact 地名命中节点才落数字，
    /// 其余地名（青石城等）仅随事件文本留痕（登记）。d_* 六个当事人数值列按简报消费口径
    /// 只限关系网引擎（贸易引擎不动 NPC 状态——登记待裁决）。
    /// 确定性纪律：不消耗世界 Rng 流；已触发集合 Fired 进存档，Restore 不重放。
    /// </summary>
    public sealed class ColonyTradeEngine
    {
        /// <summary>触发日步长（CSV 无 trigger 列的确定性调度；登记待裁决）。</summary>
        public const int TriggerDayStep = 5;

        /// <summary>事件文本的日志前缀（Truth/Raw 里识别内容表驱动的贸易行）。</summary>
        public const string LogMarker = "[贸易]";

        /// <summary>影响段百分比（check_t7 同式：[±]N 或 [±]N.N 后跟 %）。</summary>
        private static readonly Regex PercentRx = new(@"([+-]?\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

        private List<ColonyTradeEventInput> _events;

        /// <summary>已触发事件 Id 集合（进度，进存档；Restore 后不重放）。</summary>
        public readonly HashSet<string> Fired = new();

        /// <summary>当日触发的事件 Id（每日结算开头清零；测试断言用）。</summary>
        public readonly List<string> FiredThisDay = new();

        public ColonyTradeEngine(List<ColonyTradeEventInput> events)
        {
            _events = events ?? new List<ColonyTradeEventInput>();
        }

        /// <summary>Restore 后内容回填（进度集合不动——已触发不重放）。</summary>
        public void Attach(List<ColonyTradeEventInput> events)
            => _events = events ?? new List<ColonyTradeEventInput>();

        /// <summary>内容条数（E1 断言用）。</summary>
        public int EventCount => _events.Count;

        /// <summary>事件触发日（T{i} → i×5；登记的确定性调度）。</summary>
        public static uint TriggerDayOf(ColonyTradeEventInput e)
            => (uint)Math.Max(1, e.EventId) * TriggerDayStep;

        /// <summary>每日结算：到首个满足日的事件逐条触发（一次）。</summary>
        public void SettleDaily(GameWorld w)
        {
            FiredThisDay.Clear();
            if (_events.Count == 0) return;

            foreach (var e in _events)
            {
                if (Fired.Contains(e.Id)) continue;
                if (w.Day < TriggerDayOf(e)) continue;

                Fired.Add(e.Id);
                FiredThisDay.Add(e.Id);
                Apply(w, e);
            }
        }

        private void Apply(GameWorld w, ColonyTradeEventInput e)
        {
            ApplyImpacts(w, e.PriceImpact, isPrice: true);
            ApplyImpacts(w, e.StockImpact, isPrice: false);

            string where = FirstPlace(e) ?? w.Regions[0].Name;
            w.Log.Add(w.Day, EventKind.Market, where, $"{LogMarker} {e.Event}",
                ColonyContentLookup.ParseVis(e.Visibility));
        }

        /// <summary>
        /// impact 串（「地名：货物 +N%；…」）逐段解析并落到市场节点：
        /// 价格段 → BasePrice×(1+pct/100)；库存段 → Stock×(1+pct/100)；钳 ≥0。
        /// 地名无市场节点时跳过（效果不入数字，事件文本已留痕——登记）。
        /// </summary>
        private static void ApplyImpacts(GameWorld w, string impacts, bool isPrice)
        {
            if (string.IsNullOrEmpty(impacts) || impacts == "无") return;
            foreach (var raw in impacts.Split('；'))
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;
                int colon = seg.IndexOf('：');
                if (colon <= 0) continue;

                string place = seg[..colon].Trim();
                var m = PercentRx.Match(seg[(colon + 1)..]);
                if (!m.Success) continue;
                float pct = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (pct == 0f) continue;

                var node = FindNode(w, place);
                if (node == null) continue;
                if (isPrice) node.BasePrice = Math.Max(0f, node.BasePrice * (1f + pct / 100f));
                else node.Stock = Math.Max(0f, node.Stock * (1f + pct / 100f));
            }
        }

        private static MarketNode FindNode(GameWorld w, string place)
        {
            foreach (var n in w.Market.Nodes)
            {
                if (n.Name == place) return n;
            }
            return null;
        }

        /// <summary>日志 where 取 places 串首个地名（空则建局主城兜底）。</summary>
        private static string FirstPlace(ColonyTradeEventInput e)
        {
            string places = e.Places ?? "";
            int sep = places.IndexOf('、');
            return sep > 0 ? places[..sep] : (places.Length > 0 ? places : null);
        }
    }
}
