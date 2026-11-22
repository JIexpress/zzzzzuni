// ColonyActionScorer.cs —— TASK-A-COL-06：效用评分公式（公开可测的纯函数）。
// 「效用 AI 的『为什么选这个』要能从分数读出来」（理论参考 §6③）：每个候选的分数完全由
// 人格/怨气/组织度/地位 + 邻居态势决定，权重全部是具名常量——改任何一个数，哪个 NPC 在
// 哪种状态下会改选什么，可以手算复现。公式对玩家隐藏（Covert），对开发者可解释（本文件）。
//
// 手算样例（T1 的两个变体）：
//   高宜人性(0.9)低神经质(0.3)：ProtestRouter 返回 null → ProtestIntensity=0 → Protest 分=0（人格否决）。
//   商人(IsMerchant)怨气 0.8：路由必含涨价 → 0.6×0.8 + 0.2×(1-宜人性) + 0.2×神经质 + 0.15（涨价加成）
//     = 宜人性 0.2/神经质 0.6 时 0.91 —— 远超 Work 基线（0.58+0.1×尽责−0.25×怨气=0.45）。
// 评分必须纯：不读时钟、不消耗随机流、不改状态（与 Game.Domain.AI.Utility 的 UtilityAction 同纪律）。
// 权重经离线校准（真实 100 卡人格分布推 30 天，Rng 流与 Unity 同源）：Work 占比 0.432 /
// Protest 0.338 / Gossip 0.230，全局怨气均值稳定在 ~0.47（ProtestVent 泄压对冲
// PropagateDaily 情绪通道的正反馈——没有泄压阀，自主抗议几天内 runaway 成全员抗议）。

using System;

namespace Game.Domain.Colony.Autonomy
{
    /// <summary>五个行为候选的效用评分（纯函数，权重常量全部公开）。</summary>
    public static class ColonyActionScorer
    {
        // ── Work：默认基线。尽责的人多干一点，怨气大的人怠一点 ──
        public const float WorkBaseline = 0.58f;
        public const float WorkConscientiousnessWeight = 0.10f;
        public const float WorkGrievancePenalty = 0.25f;

        // ── Protest：怨气×人格（强度复用 ProtestRouter，宜人性/神经质调制，商人涨价加成）──
        public const float ProtestIntensityWeight = 0.60f;
        public const float ProtestDefectionWeight = 0.20f;    // (1 − 宜人性)：越不亲和越敢抗议
        public const float ProtestNeuroticismWeight = 0.20f;  // 神经质：情绪放大器
        public const float ProtestMerchantBonus = 0.15f;      // 涨价是商人最直接的经济手段（§3.3）

        // ── Recruit：仅组织者。自身组织度 × 可招募对象密度（怨气>0.4 的邻居占比）──
        public const float RecruitOrganizationWeight = 0.50f;
        public const float RecruitAggrievedWeight = 0.50f;
        /// <summary>招募对象的怨气门槛（简报钉死：向怨气&gt;0.4 的关系边邻居招募）。</summary>
        public const float RecruitGrievanceThreshold = 0.4f;

        // ── Gossip：传闻欲（GossipRate=0.3+0.7×外向性）× 有受众；没有邻居的传闻没有效用 ──
        public const float GossipRateWeight = 0.70f;

        // ── Rest：低存在感兜底（疲劳越高越想歇——当前模拟无疲劳写入源，恒为基线）──
        public const float RestBaseline = 0.25f;
        public const float RestFatigueWeight = 0.50f;

        /// <summary>一个候选行为的效用分（0..1 钳制）。</summary>
        public static float Score(ColonyAction action, ColonyNpcContext c) => action switch
        {
            ColonyAction.Work => Math.Clamp(
                WorkBaseline + WorkConscientiousnessWeight * c.Conscientiousness - WorkGrievancePenalty * c.Grievance,
                0f, 1f),

            ColonyAction.Protest => c.ProtestIntensity <= 0f
                ? 0f
                : Math.Clamp(
                    ProtestIntensityWeight * c.ProtestIntensity
                    + ProtestDefectionWeight * (1f - c.Agreeableness)
                    + ProtestNeuroticismWeight * c.Neuroticism
                    + (c.ProtestInflatePrice ? ProtestMerchantBonus : 0f),
                    0f, 1f),

            ColonyAction.Recruit => c.IsOrganizer
                ? Math.Clamp(
                    RecruitOrganizationWeight * c.Organization + RecruitAggrievedWeight * c.AggrievedNeighborShare,
                    0f, 1f)
                : 0f,

            ColonyAction.Gossip => (c.HasNeighbors ? 1f : 0f) * Math.Clamp(GossipRateWeight * c.GossipRate, 0f, 1f),

            ColonyAction.Rest => Math.Clamp(RestBaseline + RestFatigueWeight * c.Fatigue, 0f, 1f),

            _ => 0f,
        };
    }
}
