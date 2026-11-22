using System;
using System.Collections.Generic;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 关系网事件引擎（TASK-A-COL-09）：colonyRelations.csv 40 条进每日结算。
    /// 触发语义（简报原文逐条落地）：
    ///   · GameWorld.DailyTick 每日按 visibility 条件触发——Covert 每日最多 1 条（Id 序第一个
    ///     未触发者）；Personal 需玩家在场（Governor.PresentRegion）或该地区有线人
    ///     （Governor.Informants）；Public 直接入日志。三档都按 Id 升序扫描（确定性，
    ///     不消耗世界 Rng 流——与 ColonyDailyBehavior 同款纪律，开关内容不扰动既有随机序列）。
    ///   · 匹配双方关系边：按姓名在具名层找 id，无边时临时补一条 Colleague 边。
    ///   · 状态变化写入：grievance/loyalty/org，方向按事件语义字段（d_a_*/d_b_*），钳制 0..1。
    ///   · EventLog（Covert 为主，visibility 原样落档）；aftermath 非「无」进 Delayed 队列
    ///     （默认 30 天），visibility 跟随事件本体。
    ///   · 已触发事件不重复：Fired 集合记录（进存档，Restore 不重放）。
    /// </summary>
    public sealed class ColonyRelationEngine
    {
        /// <summary>aftermath 延迟天数（简报默认 30 天）。</summary>
        public const int AftermathDelayDays = 30;

        /// <summary>事件文本的日志前缀（Truth/Raw 里识别内容表驱动的关系网行）。</summary>
        public const string LogMarker = "[关系]";

        private List<ColonyRelationEventInput> _events;

        /// <summary>已触发事件 Id 集合（进度，进存档；Restore 后不重放）。</summary>
        public readonly HashSet<string> Fired = new();

        /// <summary>当日触发的事件 Id（每日结算开头清零；测试断言用）。</summary>
        public readonly List<string> FiredThisDay = new();

        private int _covertFiredToday;

        public ColonyRelationEngine(List<ColonyRelationEventInput> events)
        {
            _events = events ?? new List<ColonyRelationEventInput>();
        }

        /// <summary>Restore 后内容回填（进度集合不动——已触发不重放）。</summary>
        public void Attach(List<ColonyRelationEventInput> events)
            => _events = events ?? new List<ColonyRelationEventInput>();

        /// <summary>内容条数（E1 断言用）。</summary>
        public int EventCount => _events.Count;

        /// <summary>每日结算：visibility 闸门 → 触发 → 边匹配 → 状态写入 → 日志/延迟后果。</summary>
        public void SettleDaily(GameWorld w)
        {
            FiredThisDay.Clear();
            _covertFiredToday = 0;
            if (_events.Count == 0) return;

            var byName = ColonyContentLookup.NameIndex(w);
            foreach (var e in _events)
            {
                if (Fired.Contains(e.Id)) continue;
                if (!byName.TryGetValue(ColonyContentLookup.NameOf(e.NpcA), out var a)
                    || !byName.TryGetValue(ColonyContentLookup.NameOf(e.NpcB), out var b))
                {
                    continue;   // 姓名未命中具名层：不触发（内容机检保证命中，此处防御性跳过）
                }

                // Covert 每日最多 1 条（Id 序第一个未触发者）；先于其余闸门判定
                if (e.Visibility == "Covert" && _covertFiredToday >= 1) continue;
                if (!PassGate(w, a, e.Visibility)) continue;

                Fired.Add(e.Id);
                FiredThisDay.Add(e.Id);
                if (e.Visibility == "Covert") _covertFiredToday++;

                Apply(w, a, b, e);
            }
        }

        /// <summary>visibility 触发闸门（Covert 限额已在 SettleDaily 计数；Personal 在场或线人；Public 直接过）。</summary>
        private static bool PassGate(GameWorld w, NamedNpc a, string visibility)
        {
            switch (visibility)
            {
                case "Public":
                case "Covert":
                    return true;
                case "Personal":
                    string region = w.RegionOf(a);
                    return w.Governor.PresentRegion == region || w.Governor.Informants.Contains(region);
                default:
                    return false;
            }
        }

        private void Apply(GameWorld w, NamedNpc a, NamedNpc b, ColonyRelationEventInput e)
        {
            // 匹配双方关系边；无边临时补一条 Colleague（简报原文）
            EnsureEdge(w, a.Id, b.Id);

            // 状态变化写入（方向按事件语义字段；0..1 钳制是世界状态律同款）
            a.Grievance = Math.Clamp(a.Grievance + e.DA_Grievance, 0f, 1f);
            a.Loyalty = Math.Clamp(a.Loyalty + e.DA_Loyalty, 0f, 1f);
            a.Organization = Math.Clamp(a.Organization + e.DA_Org, 0f, 1f);
            b.Grievance = Math.Clamp(b.Grievance + e.DB_Grievance, 0f, 1f);
            b.Loyalty = Math.Clamp(b.Loyalty + e.DB_Loyalty, 0f, 1f);
            b.Organization = Math.Clamp(b.Organization + e.DB_Org, 0f, 1f);

            var vis = ColonyContentLookup.ParseVis(e.Visibility);
            string where = w.RegionOf(a);
            w.Log.Add(w.Day, EventKind.Relation, where, $"{LogMarker} {e.Event}", vis);

            // aftermath 非「无」进延迟后果队列（默认 30 天；visibility 跟随事件本体）
            if (!string.IsNullOrEmpty(e.Aftermath) && e.Aftermath != "无")
            {
                w.Delayed(w.Day + AftermathDelayDays, EventKind.Relation, where, $"{LogMarker} {e.Aftermath}", vis);
            }
        }

        /// <summary>双方已有任一条边即视为已匹配（AddFromCard 允许同对人多条边）；无边补 Colleague。</summary>
        private static void EnsureEdge(GameWorld w, int idA, int idB)
        {
            foreach (var edge in w.Relations.EdgesOf(idA))
            {
                if ((edge.A == idA && edge.B == idB) || (edge.A == idB && edge.B == idA)) return;
            }

            w.Relations.Add(new RelationshipEdge
            {
                A = idA,
                B = idB,
                Affinity = 0f,
                PublicAffinity = 0f,
                Type = RelationType.Colleague,
                Trust = 0.5f,
                Debt = 0f,
            });
        }
    }
}
