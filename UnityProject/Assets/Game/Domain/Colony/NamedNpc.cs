using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>认知五档（《声望认知与人口》§3）：认识与不认识的差别。</summary>
    public enum Recognition : byte { Stranger, HeardOf, Seen, Known, Devoted }

    /// <summary>
    /// 具名层个体（§1.1，几百个，每天更新）。
    /// 字段分三块：真实状态（玩家永远看不到）/ 面具（决定公开表现）/ 玩家认知（UI 只读这个）。
    /// </summary>
    public class NamedNpc
    {
        public int Id;
        public string Name;
        public string Job;
        public string Origin = "";        // 地球 / 殖民地第二代 / 船民 / 难民（决定地区分配与文化插值）
        public string Role = "";          // §6.3：具名化出来的组织者会带 Role="Organizer"

        // ---- 真实状态：玩家永远看不到（§1.1）----
        public float Loyalty;             // 真实忠诚 0..1
        public float Grievance;           // 怨气 0..1
        public float Organization;        // 组织度 0..1（参与地下网络的程度）
        public sbyte FactionLean;         // 派系倾向 -100(保守)..+100(激进)

        // ---- 面具：决定公开怎么表现 ----
        public float MaskStrength;        // 0..1，由恐惧 + 利益依赖决定（基准 0.30，暴政上限 0.95）

        // ---- 玩家认知：UI 只读这个 ----
        public float KnownLoyalty;        // 上次观测到的值（可能已被面具污染）
        public uint LastObserved;         // 上次观测的日期

        // ---- 信念契约 ----
        public BeliefContract Contract;

        // ---- 认知档位（声望认知 §3.5）----
        public float RecognitionScore;
        public Recognition Recognition => RecognitionRules.Of(RecognitionScore);

        // ---- 无声抗议（异常行为 §3）----
        public SilentProtest Protest;

        // ---- 人格（HEXACO 简化五轴，用于抗议路由；种子值，实测调整）----
        public float Agreeableness, Neuroticism, HonestyHumility, Conscientiousness, Extraversion;
        public float Status;              // 0..1 社会地位
        public bool IsMerchant;           // 商人：抗议手段是涨价（§3.3）

        // ---- 骨架（MVP 1：骨架来自 GLM 人设卡）----
        public string Want;               // 他真正想要的一件事
        public string Conflict;           // 同源矛盾
        public string Secret;             // 不愿让人知道的事 → 离开时变成带走的情报
        public string Speech;             // 说话特征
        public string RelationToPower;    // 对管理者的态度

        // ---- 文化、形象与状态（NPC规格第三部分 §20-25 / 第二部分 §13-15 / 理论参考 §2-3）----
        public CulturalFrame Culture = CulturalFrame.Atomized();
        public PlayerImage Image = new();          // 他眼中你是什么人——不读全局声望
        public float[] MF = new float[6];          // MFT 六维道德权重 Care/Fairness/Loyalty/Authority/Sanctity/Liberty
        public float Fatigue, Fear;                // 语气合成 strain 的输入（§13）
        public string Quirk = "";                  // 口癖：只在 10%~20% 概率注入，不是每句

        // 理论参考 §2.1/§2.2 的直接映射
        public float WorkQuality => 0.6f + 0.4f * Conscientiousness;
        public float GossipRate => 0.3f + 0.7f * Extraversion;
        public float BetrayalRisk => (1 - HonestyHumility) * Grievance * (1 - Loyalty);

        /// <summary>
        /// 玩家的一次观测。UI 只显示 KnownLoyalty（MVP 8）。
        /// 面具越强，看到的越向"普通人"（0.5）收缩；再叠加线人误差 ±15%（§10）。
        /// </summary>
        public void Observe(uint day, Rng rng)
        {
            float shown = MaskStrength > 0f
                ? 0.5f + (Loyalty - 0.5f) * (1f - MaskStrength * 0.6f)
                : Loyalty;
            shown += rng.Range(-0.15f, 0.15f);                 // 线人误差（§10）
            KnownLoyalty = Math.Clamp(shown, 0f, 1f);
            LastObserved = day;
        }

        /// <summary>每天结算：信念契约推进 + 面具裂缝（§10：撑不住而漏出真话的人 1~2%）。</summary>
        public bool DailyTick(uint day, Rng rng, List<string> eventLog)
        {
            bool changed = Contract.DailyTick();
            if (changed)
            {
                if (Contract.Stage == Stage.Silent)
                    eventLog.Add($"[{day}天] {Name} 不再提意见了。表面一切正常——沉默期开始，配合度将每天 -0.01。");
                if (Contract.Stage == Stage.Left)
                {
                    Contract.IntelCarried = Secret;            // 走的人带走一条情报（MVP 5）
                    eventLog.Add($"[{day}天] {Name} {Contract.LeaveLine()} 带走的情报：{Contract.IntelCarried}");
                }
            }
            if (Contract.Stage == Stage.Left) return changed;

            if (Contract.Stage == Stage.Doubt && rng.Chance(0.015f))
                eventLog.Add($"[{day}天] {Name} 当面提了不同意见（阶段一：质疑）。");

            // 面具裂缝：每天 1.5% 概率漏出真话
            if (rng.Chance(0.015f))
            {
                MaskStrength = Math.Max(0f, MaskStrength - 0.05f);
                eventLog.Add($"[{day}天] {Name} 面具裂了一道缝（面具强度 → {MaskStrength:0.00}），说了句真话。");
            }
            return changed;
        }

        /// <summary>聚落压力传导：怨气与组织度会被环境推着走（MVP 6/7 的数据来源）。</summary>
        public void AbsorbSettlement(Settlement s, Rng rng)
        {
            Grievance = Math.Clamp(Math.Max(Grievance, s.AvgGrievance + rng.Range(-0.1f, 0.1f) + s.GrievanceFloor * 0.5f), 0f, 1f);
            Loyalty = Math.Clamp(Loyalty - Math.Max(0f, s.GrievanceFloor) * 0.3f, 0f, 1f);   // 仇恨不可逆：忠诚被压下去就回不来
            Organization = Math.Clamp(Math.Max(Organization, s.Organization), 0f, 1f);
        }
    }

    /// <summary>§3.5 认知五档阈值与交互权重（「听说」也能涨，但涨得慢）。</summary>
    public static class RecognitionRules
    {
        // 权重表（§3.5）
        public const float SeeFar = 0.2f;      // 远远看见
        public const float PassBy = 0.5f;      // 擦肩而过
        public const float Talked = 1.0f;      // 说过一句话
        public const float Helped = 3.0f;      // 帮过他
        public const float SavedLife = 8.0f;   // 救过他的命
        public const float HeardTale = 0.4f;   // 他听别人讲你的事
        public const float BigDeed = 2.5f;     // 你在他面前做了件大事

        public static Recognition Of(float score) =>
            score < 0.5f  ? Recognition.Stranger :
            score < 2.0f  ? Recognition.HeardOf :
            score < 5.0f  ? Recognition.Seen :
            score < 12.0f ? Recognition.Known :
                            Recognition.Devoted;

        public static string Cn(this Recognition r) => r switch
        {
            Recognition.Stranger => "陌生", Recognition.HeardOf => "听过", Recognition.Seen => "见过",
            Recognition.Known => "认识", Recognition.Devoted => "崇拜", _ => "?",
        };
    }
}
