using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>玩家决策的两种姿态（MVP 7：温和 / 强硬）。</summary>
    public enum Decision : byte { Gentle, Harsh }

    /// <summary>
    /// 聚落：统计层（§1.2，几千组，每周更新），只跑统计方程。
    /// 性能原则（§2）：玩家看不见的，一律不模拟——背景层十几万人只是这里的 Population 数字。
    /// </summary>
    public class Settlement
    {
        public string Name;
        public int Population;

        public float AvgLoyalty;
        public float AvgGrievance;
        public float Organization;      // 群体组织度 ← 反抗的温床
        public float LeanMean;          // 派系倾向均值（-100 保守 .. +100 激进）
        public float LeanVariance;      // 分布宽度（越大越容易撕裂）

        public float FoodSupply, Housing, Security;

        /// <summary>镇压造成的不可逆怨气地板（"仇恨不可逆"）：AvgGrievance 可以随时间降，但这部分降不下去。</summary>
        public float GrievanceFloor;

        public List<int> NamedNpcs = new();   // 该聚落已具名化的人（§1.2）

        // 涌现链阈值（§10 / §6.2）
        public const float THRESHOLD_A = 0.35f;   // 第一个组织者
        public const float THRESHOLD_B = 0.55f;   // 小规模骚乱
        public const float THRESHOLD_C = 0.75f;   // 大游行
        public const float TEAR_VARIANCE = 0.60f; // 超过则组织度加速

        public Settlement(string name, int population, float loyalty, float grievance)
        {
            Name = name;
            Population = population;
            AvgLoyalty = loyalty;
            AvgGrievance = grievance;
            Organization = 0.05f;
            LeanMean = 0f;
            LeanVariance = 0.20f;
            FoodSupply = 0.6f; Housing = 0.6f; Security = 0.6f;
        }

        /// <summary>温和/强硬决策 → 聚落参数变化 + 派系撕裂（MVP 7）。参数为初值，需实测调整。</summary>
        public void Apply(Decision d)
        {
            if (d == Decision.Gentle)
            {
                AvgGrievance = Math.Max(GrievanceFloor, AvgGrievance - 0.15f);   // 降到地板为止
                Organization = Math.Max(0f, Organization - 0.05f);
                LeanMean += 5f;                  // 温和让人趋保守
                LeanVariance = Math.Max(0.05f, LeanVariance - 0.03f);
                FoodSupply = Math.Min(1f, FoodSupply + 0.05f);
            }
            else
            {
                // 镇压：组织度 +0.3，仇恨不可逆（异常行为 §3.6 的原则）
                AvgGrievance = Math.Min(1f, AvgGrievance + 0.25f);
                GrievanceFloor += 0.10f;
                Organization = Math.Min(1f, Organization + 0.30f);
                LeanMean -= 10f;                 // 强硬让人趋激进
                LeanVariance += 0.08f;           // 撕裂加大
                Security = Math.Min(1f, Security + 0.10f);
                FoodSupply = Math.Max(0f, FoodSupply - 0.08f);
            }
        }

        /// <summary>每周结算：撕裂方差超过阈值时组织度加速（§10），怨气向不可逆地板收敛。</summary>
        public void WeeklyTick(Rng rng)
        {
            if (LeanVariance > TEAR_VARIANCE)
                Organization = Math.Min(1f, Organization + 0.02f + LeanVariance * 0.01f);   // 加速
            // 怨气自然回落，但永远降不到地板以下
            AvgGrievance = Math.Max(GrievanceFloor, AvgGrievance - 0.01f);
            if (rng.Chance(0.2f)) LeanVariance = Math.Max(0.05f, LeanVariance - 0.005f);
        }

        /// <summary>§6.3 具名化：反抗领袖不是设计出来的，是从参数里长出来的。</summary>
        /// <returns>新组织者；未达阈值返回 null。</returns>
        public NamedNpc TrySpawnOrganizer(int id, Rng rng)
        {
            if (Organization <= THRESHOLD_A || NamedNpcs.Count >= 8) return null;
            var n = new NamedNpc
            {
                Id = id,
                Name = rng.Pick(NpcDirectory.Origins) == "船民" ? "老舵" : "无名者" + id,
                Job = "组织者",
                Role = "Organizer",
                Loyalty = 0.2f,
                Grievance = AvgGrievance,
                Organization = Organization,        // §6.3：继承聚落组织度
                FactionLean = (sbyte)Math.Clamp(LeanMean + 30, -100, 100),
                MaskStrength = 0.8f,                // 地下工作者面具极强
                Contract = new BeliefContract
                {
                    CoreBelief = "团结才能活下去",
                    Wonts = new List<string> { "绝不出卖同伴", "绝不在镇压面前散伙" },
                    Tolerance = 2,
                    LeaveStyle = LeaveStyle.TurnYou,
                },
            };
            NamedNpcs.Add(id);
            return n;
        }

        /// <summary>§6.2 涌现链的当前状态（全是参数，没有脚本）。</summary>
        public string EmergentState()
        {
            if (Organization > THRESHOLD_C) return "超过阈值 C：大游行正在发生（同时改变大量 NPC 的倾向）。";
            if (Organization > THRESHOLD_B) return "超过阈值 B：小规模骚乱。镇压得越狠，下一环来得越快。";
            if (Organization > THRESHOLD_A) return "超过阈值 A：出现了第一个组织者，开始招募。";
            if (LeanVariance > TEAR_VARIANCE) return "派系撕裂：聚落内部两极化，组织度在加速。";
            return "表面平静。";
        }

        /// <summary>§6.1 阈值模型：每个人心里有个「需要多少人上街我才敢上」的阈值。</summary>
        public float IndividualThreshold(float org) => 0.60f - org * 0.50f;

        public string Stats() =>
            $"{Name}（{Population} 人）：忠诚 {AvgLoyalty:0.00} / 怨气 {AvgGrievance:0.00}（不可逆地板 {GrievanceFloor:0.00}）" +
            $" / 组织度 {Organization:0.00} / 倾向均值 {LeanMean:+0;-0} / 撕裂方差 {LeanVariance:0.00} —— {EmergentState()}";
    }
}
