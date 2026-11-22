// ColonyNpcContext.cs —— TASK-A-COL-06：效用评分的输入快照（人格/怨气/组织度/地位 + 邻居态势）。
// 纯数据、零随机：同一个 NPC 同一天的状态 → 同一个 context → 同一个评分（可解释性的根基——
// 「为什么选这个」能从分数读出来，理论参考 §6③：公式对玩家隐藏、对开发者可解释）。
// Protest 字段来自 ProtestRouter.ChooseProtest 的结果（简报：复用抗议路由作为候选强度），
// 该函数是纯函数（无 RNG），所以整条评分链保持确定性——ColonyDailyBehavior 不消耗世界 Rng 流，
// 开关自主行为不会扰动市场漂移等既有随机序列（T4 对照组的因果纯度依赖这一点）。

using System;

namespace Game.Domain.Colony.Autonomy
{
    /// <summary>
    /// 一个具名 NPC 当日的效用评分输入（不可变快照——按约定只经对象初始化器构建、构建后不改写；
    /// 属性用 set 而非 init：Unity 2022 运行时缺 IsExternalInit）。
    /// </summary>
    public struct ColonyNpcContext
    {
        // ── 真实状态（玩家永远看不到，NamedNpc §1.1）──
        public float Grievance { get; set; }          // 怨气 0..1
        public float Organization { get; set; }       // 组织度 0..1
        public float Status { get; set; }             // 社会地位 0..1
        public float Fatigue { get; set; }            // 疲劳 0..1（Rest 的体力输入）

        // ── 人格（HEXACO 简化五轴，抗议路由同源）──
        public float Agreeableness { get; set; }
        public float Neuroticism { get; set; }
        public float HonestyHumility { get; set; }
        public float Conscientiousness { get; set; }
        public float Extraversion { get; set; }

        // ── 身份标记 ──
        public bool IsMerchant { get; set; }          // 商人：抗议手段含涨价（§3.3）
        public bool IsOrganizer { get; set; }         // 组织者：Recruit 的唯一资格

        // ── 关系边邻居态势（Recruit / Gossip 的受众）──
        /// <summary>有具名关系边邻居（没有受众的传闻没有效用）。</summary>
        public bool HasNeighbors { get; set; }
        /// <summary>怨气 &gt; 0.4 的邻居占具名邻居的比例（Recruit 强度，简报钉死门槛 0.4）。</summary>
        public float AggrievedNeighborShare { get; set; }

        // ── 抗议候选强度（简报：复用 ProtestRouter 结果）──
        /// <summary>ProtestRouter 结果的强度（路由返回 null → 0，即人格否决了抗议候选）。</summary>
        public float ProtestIntensity { get; set; }
        /// <summary>路由结果含涨价形式（商人的直接经济手段——候选分加成，T1 可读）。</summary>
        public bool ProtestInflatePrice { get; set; }

        /// <summary>传闻欲（NamedNpc.GossipRate 同式：0.3 + 0.7×外向性）。</summary>
        public float GossipRate => 0.3f + 0.7f * Extraversion;

        /// <summary>
        /// 从具名 NPC 当前状态 + 邻居统计构建快照。<paramref name="neighborTotal"/> /
        /// <paramref name="aggrievedNeighbors"/> 由调用方沿关系图统计（只认具名邻居）。
        /// </summary>
        public static ColonyNpcContext From(NamedNpc n, int neighborTotal, int aggrievedNeighbors)
        {
            if (n == null) throw new ArgumentNullException(nameof(n));

            SilentProtest routed = ProtestRouter.ChooseProtest(n, n.Grievance);
            return new ColonyNpcContext
            {
                Grievance = Math.Clamp(n.Grievance, 0f, 1f),
                Organization = Math.Clamp(n.Organization, 0f, 1f),
                Status = Math.Clamp(n.Status, 0f, 1f),
                Fatigue = Math.Clamp(n.Fatigue, 0f, 1f),
                Agreeableness = n.Agreeableness,
                Neuroticism = n.Neuroticism,
                HonestyHumility = n.HonestyHumility,
                Conscientiousness = n.Conscientiousness,
                Extraversion = n.Extraversion,
                IsMerchant = n.IsMerchant,
                IsOrganizer = n.Role == ColonyDailyBehavior.OrganizerRole,
                HasNeighbors = neighborTotal > 0,
                AggrievedNeighborShare = neighborTotal == 0
                    ? 0f
                    : Math.Clamp((float)aggrievedNeighbors / neighborTotal, 0f, 1f),
                ProtestIntensity = routed?.Level ?? 0f,
                ProtestInflatePrice = routed != null && routed.Forms.Contains(ProtestForm.InflatePrice),
            };
        }
    }
}
