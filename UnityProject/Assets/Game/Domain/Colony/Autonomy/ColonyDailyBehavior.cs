// ColonyDailyBehavior.cs —— TASK-A-COL-06：具名 NPC 的每日自主行为结算入口（GameWorld.DailyTick 挂点）。
// 「NPC 不是按预设跑，而是自己找事」：每个具名 NPC 每天用 ColonyTaskBoard 按效用评分自选一个行为，
// 选中行为的产出写回模拟状态（怨气/组织度/PlayerImage）+ EventLog——Covert 为主：NPC 自己找事，
// 玩家大多看不见（视野纪律 §4，符合规格）。
//
// 确定性纪律：本类不消耗世界 Rng 流（评分纯函数、邻居/传闻边选择全确定性）——开关自主行为
// 不扰动市场漂移等既有随机序列，T4 对照实验的增量差才能归因于自主行为本身。
// 复用纪律：Protest 复用 ProtestRouter（抗议状态仍由已有机制消费：传染/涨价/观察取证），
// Gossip 复用 PlayerImage.Receive 与关系边 Trust 通道——本类只是把它们行为化成 NPC 的主动选择，
// Enemy / Game.Domain.AI 现有文件与消费方零改动（简报白名单）。

using System;
using System.Collections.Generic;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Autonomy
{
    /// <summary>每日自主行为结算：上下文构建 → 任务板分派 → 行为产出。</summary>
    public static class ColonyDailyBehavior
    {
        /// <summary>组织者角色标记（Settlement.TrySpawnOrganizer 同款 Role 字段值）。</summary>
        public const string OrganizerRole = "Organizer";

        /// <summary>自主行为日志的文本标记（MonthlyReport.Raw 按 Kind 折叠自主行的识别键）。</summary>
        public const string AutonomyLogMarker = "[自主]";

        // ── 行为产出旋钮（简报：行为写回模拟状态；数值为种子值，实测调整）──
        /// <summary>Recruit：每个被招募邻居的组织度增量。</summary>
        public const float RecruitOrgGain = 0.02f;
        /// <summary>Recruit：每招募一人回流聚落群体组织度的增量（涌现加速的机制来源，T4）。</summary>
        public const float RecruitSettlementGain = 0.001f;
        /// <summary>
        /// Protest：抗议排解怨气的幅度（倾诉即泄压——行为写回怨气的负通道）。
        /// 这是自主行为层的稳定器：没有它，自主抗议经 PropagateDaily 情绪通道正反馈，
        /// 全局怨气几天内 runaway 到 0.9、全员抗议（T2 的 Work 占比守不住）。
        /// </summary>
        public const float ProtestVent = 0.4f;

        /// <summary>
        /// 单个具名 NPC 的每日自主结算：评分选行为 → 执行产出 → 计数入
        /// <see cref="GameWorld.AutonomousActionsToday"/> / <see cref="GameWorld.AutonomousChoicesToday"/>。
        /// <see cref="GameWorld.AutonomyEnabled"/> 关闭时直接返回 null（不评分、不写状态、不计数）。
        /// 返回选中的行为（可解释性入口：配合 ColonyDailyBehavior.BuildContext + ColonyTaskBoard.Ranked
        /// 能完整复盘「他今天为什么找这件事」）。
        /// </summary>
        public static ColonyAction? SettleFor(GameWorld w, NamedNpc n)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (n == null) throw new ArgumentNullException(nameof(n));
            if (!w.AutonomyEnabled) return null;

            ColonyNpcContext ctx = BuildContext(w, n);
            ColonyTaskBoard board = ColonyTaskBoard.For(ctx);
            ColonyAction choice = board.Select();

            Apply(w, n, choice, ctx);

            w.AutonomousActionsToday++;
            w.AutonomousChoicesToday[n.Id] = choice;
            return choice;
        }

        /// <summary>
        /// 构建评分输入：沿关系图统计具名邻居与怨气邻居（只认 <see cref="GameWorld.NamedById"/> 里的
        /// 具名邻居——背景层没有图）。邻居按 id 去重：关系网建网允许同一对人出现多条边
        /// （AddFromCard 随机抽邻居可重复），不去重会把重复边当成多个邻居。
        /// 公开：开发者可解释性的第一步（看 context 再看 Ranked）。
        /// </summary>
        public static ColonyNpcContext BuildContext(GameWorld w, NamedNpc n)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (n == null) throw new ArgumentNullException(nameof(n));

            int total = 0, aggrieved = 0;
            var seen = new HashSet<int>();
            IReadOnlyList<RelationshipEdge> edges = w.Relations.EdgesOf(n.Id);
            for (int i = 0; i < edges.Count; i++)
            {
                RelationshipEdge e = edges[i];
                int otherId = e.A == n.Id ? e.B : e.A;
                if (!seen.Add(otherId) || !w.NamedById.TryGetValue(otherId, out NamedNpc other))
                {
                    continue;
                }

                total++;
                if (other.Grievance > ColonyActionScorer.RecruitGrievanceThreshold)
                {
                    aggrieved++;
                }
            }

            return ColonyNpcContext.From(n, total, aggrieved);
        }

        // ── 行为产出（写状态 + Covert 事件）─────────────────────────────

        private static void Apply(GameWorld w, NamedNpc n, ColonyAction action, ColonyNpcContext ctx)
        {
            switch (action)
            {
                case ColonyAction.Protest:
                    ApplyProtest(w, n);
                    break;
                case ColonyAction.Recruit:
                    ApplyRecruit(w, n);
                    break;
                case ColonyAction.Gossip:
                    ApplyGossip(w, n);
                    break;

                // Work：产出/配合——Cooperation 已有（BeliefContract），行为化留接口（简报原文）：
                // 选中即计数，不写状态不写事件（例行公事玩家看不见，也不该看见）。
                // Rest：低存在感兜底，同样只计数。
                case ColonyAction.Work:
                case ColonyAction.Rest:
                    break;
            }
        }

        /// <summary>
        /// Protest：复用 ProtestRouter 的结果作为抗议状态——传染/涨价/观察取证等已有机制照常消费。
        /// 同时排解怨气（ProtestVent）：说出来怪话，气顺了一点——自主行为层的泄压阀，
        /// 把「抗议→传染→更大怨气」的正反馈钳成波浪式起伏（T2 行为分布非退化的机制保障）。
        /// </summary>
        private static void ApplyProtest(GameWorld w, NamedNpc n)
        {
            SilentProtest protest = ProtestRouter.ChooseProtest(n, n.Grievance);
            if (protest == null)
            {
                return;   // 评分门槛已保证非空；人格在当日中途被改写时兜底为无产出
            }

            n.Protest = protest;
            n.Grievance = Math.Max(0f, n.Grievance - ProtestVent);
            w.Log.Add(w.Day, EventKind.Protest, w.RegionOf(n),
                $"{AutonomyLogMarker} {n.Name} 在私下酝酿不满（强度 {protest.Level:0.00}，形式：{protest.FormCn()}）——说出来的都是合理解释。",
                EventVisibility.Covert);
        }

        /// <summary>
        /// Recruit：组织者沿关系边向怨气&gt;0.4 的具名邻居招募——组织度传导行为化。
        /// 邻居按 id 去重（同对人可能有多条边），每个邻居至多 +RecruitOrgGain 一次；
        /// 每招募一人向聚落群体组织度小幅回流（地下网络长大的涌现加速，T4）。
        /// </summary>
        private static void ApplyRecruit(GameWorld w, NamedNpc n)
        {
            int recruited = 0;
            var seen = new HashSet<int>();
            IReadOnlyList<RelationshipEdge> edges = w.Relations.EdgesOf(n.Id);
            for (int i = 0; i < edges.Count; i++)
            {
                RelationshipEdge e = edges[i];
                int otherId = e.A == n.Id ? e.B : e.A;
                if (!seen.Add(otherId) || !w.NamedById.TryGetValue(otherId, out NamedNpc other))
                {
                    continue;
                }

                if (other.Grievance > ColonyActionScorer.RecruitGrievanceThreshold)
                {
                    other.Organization = Math.Clamp(other.Organization + RecruitOrgGain, 0f, 1f);
                    recruited++;
                }
            }

            if (recruited == 0)
            {
                return;
            }

            Settlement s = SettlementOfNpc(w, n);
            if (s != null)
            {
                s.Organization = Math.Min(1f, s.Organization + recruited * RecruitSettlementGain);
            }

            w.Log.Add(w.Day, EventKind.Uprising, s?.Name ?? w.RegionOf(n),
                $"{AutonomyLogMarker} {n.Name} 在地下串联了 {recruited} 个不满者——组织度在人和网两头同时生长。",
                EventVisibility.Covert);
        }

        /// <summary>
        /// Gossip：沿信任度最高的关系边（平局取第一条，确定性；邻居按 id 去重）向具名邻居传播
        /// PlayerImage 传闻——已有信息传导机制的行为化入口（失真 ← 1 − Trust，与
        /// RelationshipGraph.PropagateDaily 同式）。
        /// </summary>
        private static void ApplyGossip(GameWorld w, NamedNpc n)
        {
            RelationshipEdge best = null;
            var seen = new HashSet<int>();
            IReadOnlyList<RelationshipEdge> edges = w.Relations.EdgesOf(n.Id);
            for (int i = 0; i < edges.Count; i++)
            {
                RelationshipEdge e = edges[i];
                int otherId = e.A == n.Id ? e.B : e.A;
                if (!seen.Add(otherId) || !w.NamedById.ContainsKey(otherId))
                {
                    continue;
                }

                if (best == null || e.Trust > best.Trust)
                {
                    best = e;
                }
            }

            if (best == null)
            {
                return;
            }

            int listenerId = best.A == n.Id ? best.B : best.A;
            if (!w.NamedById.TryGetValue(listenerId, out NamedNpc listener) || listener.Image == null)
            {
                return;
            }

            bool good = n.Loyalty > 0.6f;
            string claim = good ? "听说管理者救人的好事" : "听说管理者干了坏事";
            listener.Image.Receive(claim,
                good ? PlayerImage.W_CONFIDANT : PlayerImage.W_RUMOR,
                1f - best.Trust, "传闻", best.Trust, w.Day);

            w.Log.Add(w.Day, EventKind.Reputation, w.RegionOf(n),
                $"{AutonomyLogMarker} {n.Name} 跟{listener.Name}私下聊起了管理者的传闻。",
                EventVisibility.Covert);
        }

        /// <summary>NPC 所属聚落（按聚落具名名单反查；未入任何聚落名单的具名 NPC 返回 null）。</summary>
        private static Settlement SettlementOfNpc(GameWorld w, NamedNpc n)
        {
            for (int i = 0; i < w.Settlements.Count; i++)
            {
                Settlement s = w.Settlements[i];
                if (s.NamedNpcs.Contains(n.Id))
                {
                    return s;
                }
            }

            return null;
        }
    }
}
