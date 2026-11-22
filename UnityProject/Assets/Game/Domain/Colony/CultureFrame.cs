using System;
using System.Linq;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 文化框架（规格第三部分 §21）：八个浮点数。
    /// 核心主张：文化不改参数，改「参数 → 反应」的映射函数。
    /// 移民二代 = 两套系数按比例混合（§21 允许插值）。
    /// </summary>
    public struct CulturalFrame
    {
        public float OffenseToPublicShame;    // 当众丢面子的敏感度
        public float OffenseToContractBreak;  // 违约的敏感度
        public float OffenseToDoctrine;       // 触犯信仰的敏感度
        public float Expressiveness;          // 不满外显的倾向
        public float Patience;                // 忍耐阈值
        public float Recovery;                // 恢复速度
        public float Collectivism;            // 连带责任范围
        public float FormalityToStrangers;    // 对陌生人的正式度

        /// <summary>忠诚指向（§20 第四条：决定「违背什么才真正伤到他」）。</summary>
        public enum LoyaltyTarget : byte { Person, Contract, Doctrine }

        public LoyaltyTarget Target;

        public static CulturalFrame Rural() => new()      // 乡土 / 人情：忍很久，撕破脸不可逆，忠于人与关系
        {
            OffenseToPublicShame = 0.85f, OffenseToContractBreak = 0.30f, OffenseToDoctrine = 0.10f,
            Expressiveness = 0.15f, Patience = 0.85f, Recovery = 0.10f,
            Collectivism = 0.80f, FormalityToStrangers = 0.70f, Target = LoyaltyTarget.Person,
        };

        public static CulturalFrame Atomized() => new()   // 原子化 / 直接：阈值低恢复快，忠于契约与职位
        {
            OffenseToPublicShame = 0.20f, OffenseToContractBreak = 0.75f, OffenseToDoctrine = 0.05f,
            Expressiveness = 0.75f, Patience = 0.35f, Recovery = 0.70f,
            Collectivism = 0.15f, FormalityToStrangers = 0.30f, Target = LoyaltyTarget.Contract,
        };

        public static CulturalFrame Doctrine() => new()   // 教义型：阈值极低，忠于教义
        {
            OffenseToPublicShame = 0.40f, OffenseToContractBreak = 0.20f, OffenseToDoctrine = 0.90f,
            Expressiveness = 0.35f, Patience = 0.50f, Recovery = 0.05f,
            Collectivism = 0.60f, FormalityToStrangers = 0.60f, Target = LoyaltyTarget.Doctrine,
        };

        /// <summary>移民二代：两套系数按比例插值（§21）。</summary>
        public static CulturalFrame Mix(CulturalFrame a, CulturalFrame b, float t) => new()
        {
            OffenseToPublicShame = a.OffenseToPublicShame + (b.OffenseToPublicShame - a.OffenseToPublicShame) * t,
            OffenseToContractBreak = a.OffenseToContractBreak + (b.OffenseToContractBreak - a.OffenseToContractBreak) * t,
            OffenseToDoctrine = a.OffenseToDoctrine + (b.OffenseToDoctrine - a.OffenseToDoctrine) * t,
            Expressiveness = a.Expressiveness + (b.Expressiveness - a.Expressiveness) * t,
            Patience = a.Patience + (b.Patience - a.Patience) * t,
            Recovery = a.Recovery + (b.Recovery - a.Recovery) * t,
            Collectivism = a.Collectivism + (b.Collectivism - a.Collectivism) * t,
            FormalityToStrangers = a.FormalityToStrangers + (b.FormalityToStrangers - a.FormalityToStrangers) * t,
            Target = t < 0.5f ? a.Target : b.Target,
        };

        /// <summary>§24 集体主义 = 连带责任：一个人犯错，多少人受罚。</summary>
        public int PunishedCount() => 1 + (int)(Collectivism * 15f);
    }

    /// <summary>
    /// 冒犯事件：文化三分量（§22 ComputeOffense）+ MFT 六分量（理论参考 §3.2）。
    /// 同一决策，文化系数驱动出三种结果（§22 示例：迁移决策 乡土+0.40 / 原子+0.05 / 教义+0.55）。
    /// </summary>
    public class OffenseEvent
    {
        public float PublicShame;     // 当众羞辱分量
        public float ContractBreak;   // 违约/克扣分量
        public float DoctrineHit;     // 触犯教义分量
        public string Summary;        // 发生了什么（写进事件日志）

        // MFT 六分量（理论参考 §3.2）：GroupLoyalty 对应 MFT 的 Loyalty 维
        public float Care, Fairness, GroupLoyalty, Authority, Sanctity, Liberty;

        /// <summary>§22 原式：忠诚指向校正 —— 命中他真正在意的东西 → ×3。</summary>
        public float ComputeOffense(NamedNpc n)
        {
            var c = n.Culture;
            float offense = 0f;
            offense += PublicShame * c.OffenseToPublicShame;
            offense += ContractBreak * c.OffenseToContractBreak;
            offense += DoctrineHit * c.OffenseToDoctrine;
            if (Touches(n.Culture.Target)) offense *= 3.0f;
            return offense;
        }

        /// <summary>理论参考 §3.2：moralOffense 六维加权（给「他为什么生气」一个可解释标签）。</summary>
        public float MoralOffense(NamedNpc n) =>
            Care * n.MF[0] + Fairness * n.MF[1] + GroupLoyalty * n.MF[2]
            + Authority * n.MF[3] + Sanctity * n.MF[4] + Liberty * n.MF[5];

        /// <summary>他生气的「罪名」：权重最高的那个分量（公式对玩家隐藏，§6③）。</summary>
        public string Explain(NamedNpc n)
        {
            var c = n.Culture;
            var items = new (string name, float v)[]
            {
                ("当众受辱", PublicShame * c.OffenseToPublicShame),
                ("契约被破", ContractBreak * c.OffenseToContractBreak),
                ("教义被犯", DoctrineHit * c.OffenseToDoctrine),
                ("同伴受害", Care * n.MF[0]),
                ("不公平",   Fairness * n.MF[1]),
                ("群体被伤", GroupLoyalty * n.MF[2]),
                ("权威受冒", Authority * n.MF[3]),
                ("禁忌被碰", Sanctity * n.MF[4]),
                ("被剥夺",   Liberty * n.MF[5]),
            };
            return items.OrderByDescending(x => x.v).First().name;
        }

        private bool Touches(CulturalFrame.LoyaltyTarget t) => t switch
        {
            CulturalFrame.LoyaltyTarget.Person => PublicShame > 0.3f,
            CulturalFrame.LoyaltyTarget.Contract => ContractBreak > 0.3f,
            CulturalFrame.LoyaltyTarget.Doctrine => DoctrineHit > 0.2f,
            _ => false,
        };
    }
}
