using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 人生节点引擎（TASK-A-COL-09）：colonyLife.csv 190 条进每日结算。
    /// 触发语义（简报原文）：查该 NPC 的 trigger 天数区间命中（「第 X-Y 天」为硬门槛；
    /// 「且其 grievance&gt;0.5 时优先」是内容侧的优先级语气，不做硬门槛——登记）。
    /// 命中后：
    ///   · 事件文本入 Log（Personal，简报原文）；
    ///   · rewrite 字段解析「从『X』改为『Y』」→ 真实改写 npc.Want/Conflict/Secret，
    ///     真实旧值留痕进事件文本（rewrite_text 里的『X』是内容侧转述，与卡字段逐字不同
    ///     占 145/145——check_t9 只验格式，故留痕用真实旧值，登记）；
    ///   · settlement_effect 解析写入对应 Settlement：当事人所在聚落（RegionOf→Settlement）。
    ///     段式与映射（登记）：「平均怨气/平均忠诚/平均组织度 ±d」→ 聚落 AvgGrievance/
    ///     AvgLoyalty/Organization ±d（0..1 钳制）；「N 人怨气/忠诚/组织度 ±d」→ 落到当事人
    ///     一人（人数是叙事强度标注）；「产能 ±N%」→ 该聚落市场节点 BasePrice×(1∓N/100)
    ///     （产能升=收购价降的行情修正；无市场节点的聚落留痕不入数字）；「库存 ±N%」→
    ///     Stock×(1+N/100)；「持续 N 天」随文本留痕（一次性入账不复原——复原型时效效果需要
    ///     引擎级定时器，登记待裁决）。
    /// 已触发生命节点不重复：Fired 集合进存档，Restore 不重放。不消耗世界 Rng 流。
    /// </summary>
    public sealed class ColonyLifeEngine
    {
        /// <summary>事件文本的日志前缀（Truth/Raw 里识别内容表驱动的人生节点行）。</summary>
        public const string LogMarker = "[人生]";

        /// <summary>trigger 天数区间（「第 X-Y 天」；check_t9 契约：190/190 命中）。</summary>
        private static readonly Regex DayRangeRx = new(@"第\s*(\d+)\s*-\s*(\d+)\s*天", RegexOptions.Compiled);

        /// <summary>settlement_effect 的平均参数段（→ 聚落聚合值）。</summary>
        private static readonly Regex AvgRx = new(@"平均(怨气|忠诚|组织度)\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        /// <summary>settlement_effect 的人数段（「N 人怨气 +0.05」→ 当事人一人落地）。</summary>
        private static readonly Regex PeopleRx = new(@"\d+\s*人(怨气|忠诚|组织度)\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        /// <summary>settlement_effect 的产能/库存百分比段（→ 市场节点）。</summary>
        private static readonly Regex PercentRx = new(@"(产能|库存)\s*([+-]?\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

        /// <summary>settlement_effect 的裸参数段（「矿区组织度 -0.05」→ 聚落聚合值兜底）。</summary>
        private static readonly Regex PlainRx = new(@"(怨气|忠诚|组织度)\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        private List<ColonyLifeEventInput> _events;
        private readonly List<(ColonyLifeEventInput e, uint from, uint to)> _schedule = new();

        /// <summary>已触发事件 Id 集合（进度，进存档；Restore 后不重放）。</summary>
        public readonly HashSet<string> Fired = new();

        /// <summary>当日触发的事件 Id（每日结算开头清零；测试断言用）。</summary>
        public readonly List<string> FiredThisDay = new();

        public ColonyLifeEngine(List<ColonyLifeEventInput> events)
        {
            _events = events ?? new List<ColonyLifeEventInput>();
            RebuildSchedule();
        }

        /// <summary>Restore 后内容回填（进度集合不动；触发区间重解析）。</summary>
        public void Attach(List<ColonyLifeEventInput> events)
        {
            _events = events ?? new List<ColonyLifeEventInput>();
            RebuildSchedule();
        }

        /// <summary>内容条数（E1 断言用）。</summary>
        public int EventCount => _schedule.Count;

        private void RebuildSchedule()
        {
            _schedule.Clear();
            foreach (var e in _events)
            {
                if (TryParseTrigger(e.Trigger, out uint from, out uint to))
                {
                    _schedule.Add((e, from, to));
                }
            }
        }

        /// <summary>每日结算：trigger 天数区间命中的未触发节点逐条落地。</summary>
        public void SettleDaily(GameWorld w)
        {
            FiredThisDay.Clear();
            if (_schedule.Count == 0) return;

            var byName = ColonyContentLookup.NameIndex(w);
            foreach (var (e, from, to) in _schedule)
            {
                if (Fired.Contains(e.Id)) continue;
                if (w.Day < from || w.Day > to) continue;
                if (!byName.TryGetValue(e.Name, out var npc)) continue;

                Fired.Add(e.Id);
                FiredThisDay.Add(e.Id);
                Apply(w, npc, e);
            }
        }

        private static void Apply(GameWorld w, NamedNpc npc, ColonyLifeEventInput e)
        {
            string where = w.RegionOf(npc);
            w.Log.Add(w.Day, EventKind.Life, where, $"{LogMarker} {BuildText(npc, e)}", EventVisibility.Personal);
            ApplySettlementEffect(w, npc, e.SettlementEffect);
        }

        /// <summary>
        /// 事件文本：正文 + rewrite 改写（旧值留痕进文本）。rewrite_field=「无」只入正文；
        /// rewrite_text 解析失败时不动档案字段（文本照入——内容侧格式由机检保证）。
        /// </summary>
        internal static string BuildText(NamedNpc npc, ColonyLifeEventInput e)
        {
            string field = e.RewriteField switch
            {
                "want" => "want",
                "conflict" => "conflict",
                "secret" => "secret",
                _ => null,
            };
            if (field == null) return e.Text;

            string newValue = RewriteNewValue(e.RewriteText);
            if (newValue == null) return e.Text;

            string oldValue = field switch
            {
                "want" => npc.Want,
                "conflict" => npc.Conflict,
                "secret" => npc.Secret,
                _ => "",
            } ?? "";

            switch (field)
            {
                case "want": npc.Want = newValue; break;
                case "conflict": npc.Conflict = newValue; break;
                case "secret": npc.Secret = newValue; break;
            }

            return $"{e.Text}（{e.Name}的{field}从「{oldValue}」改为「{newValue}」——{e.Id}）";
        }

        /// <summary>「从『X』改为『Y』」→ Y；解析失败返回 null（不动档案）。</summary>
        public static string RewriteNewValue(string rewriteText)
        {
            const string sep = "』改为『";
            int at = rewriteText?.IndexOf(sep, StringComparison.Ordinal) ?? -1;
            if (at < 0) return null;
            int start = at + sep.Length;
            int end = rewriteText.IndexOf('』', start);
            return end < 0 ? null : rewriteText[start..end];
        }

        /// <summary>
        /// trigger「第 X-Y 天[，且其 …]」→ 区间（check_t9 契约：区间起点 ≥30、X&lt;Y）。
        /// 附加条件（grievance&gt;0.5 时优先）是内容侧优先级语气，不做硬门槛（登记）。
        /// </summary>
        public static bool TryParseTrigger(string trigger, out uint from, out uint to)
        {
            Match m = DayRangeRx.Match(trigger ?? "");
            if (!m.Success)
            {
                from = to = 0;
                return false;
            }

            from = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            to = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return from <= to;
        }

        /// <summary>settlement_effect 逐段解析落地（映射规则见类注释，登记待裁决）。</summary>
        public static void ApplySettlementEffect(GameWorld w, NamedNpc npc, string effect)
        {
            if (string.IsNullOrEmpty(effect) || effect == "无") return;
            Settlement settlement = w.SettlementOf(w.RegionOf(npc));
            MarketNode market = FindNode(w, w.RegionOf(npc));

            foreach (var raw in effect.Split('；'))
            {
                string seg = raw.Trim();
                if (seg.Length == 0) continue;

                // ① 产能/库存百分比 → 市场节点（产能升=收购价降的行情修正；无节点留痕不入数字）
                Match pct = PercentRx.Match(seg);
                if (pct.Success && market != null)
                {
                    float v = float.Parse(pct.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (pct.Groups[1].Value == "产能")
                    {
                        market.BasePrice = Math.Max(0f, market.BasePrice * (1f - v / 100f));
                    }
                    else
                    {
                        market.Stock = Math.Max(0f, market.Stock * (1f + v / 100f));
                    }
                    continue;
                }

                // ② 「N 人怨气/忠诚/组织度」→ 当事人一人落地（人数是叙事强度标注，登记）
                Match people = PeopleRx.Match(seg);
                if (people.Success)
                {
                    float v = float.Parse(people.Groups[2].Value, CultureInfo.InvariantCulture);
                    ApplyPersonal(npc, people.Groups[1].Value, v);
                    continue;
                }

                // ③ 「平均怨气/忠诚/组织度」→ 聚落聚合值
                Match avg = AvgRx.Match(seg);
                if (avg.Success && settlement != null)
                {
                    float v = float.Parse(avg.Groups[2].Value, CultureInfo.InvariantCulture);
                    ApplySettlement(settlement, avg.Groups[1].Value, v);
                    continue;
                }

                // ④ 裸「怨气/忠诚/组织度」（如「矿区组织度 -0.05」）→ 聚落聚合值兜底
                Match plain = PlainRx.Match(seg);
                if (plain.Success && settlement != null)
                {
                    float v = float.Parse(plain.Groups[2].Value, CultureInfo.InvariantCulture);
                    ApplySettlement(settlement, plain.Groups[1].Value, v);
                }
            }
        }

        private static void ApplyPersonal(NamedNpc npc, string kind, float v)
        {
            switch (kind)
            {
                case "怨气": npc.Grievance = Math.Clamp(npc.Grievance + v, 0f, 1f); break;
                case "忠诚": npc.Loyalty = Math.Clamp(npc.Loyalty + v, 0f, 1f); break;
                case "组织度": npc.Organization = Math.Clamp(npc.Organization + v, 0f, 1f); break;
            }
        }

        private static void ApplySettlement(Settlement s, string kind, float v)
        {
            switch (kind)
            {
                case "怨气": s.AvgGrievance = Math.Clamp(s.AvgGrievance + v, 0f, 1f); break;
                case "忠诚": s.AvgLoyalty = Math.Clamp(s.AvgLoyalty + v, 0f, 1f); break;
                case "组织度": s.Organization = Math.Clamp(s.Organization + v, 0f, 1f); break;
            }
        }

        private static MarketNode FindNode(GameWorld w, string name)
        {
            foreach (var n in w.Market.Nodes)
            {
                if (n.Name == name) return n;
            }
            return null;
        }
    }
}
