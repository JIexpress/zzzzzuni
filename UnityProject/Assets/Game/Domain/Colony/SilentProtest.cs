using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>四种无声抗议形式（异常行为 §3.1）。共同点：都能说出一套合理的解释。</summary>
    public enum ProtestForm : byte
    {
        RefuseDraft,       // 拒绝征召：家庭原因、身体不适 → 玩家看到"招不到人"
        InflatePrice,      // 涨价：成本上涨、供应紧张 → 玩家看到"东西变贵"
        ForgeCertificate,  // 伪造证明：技术问题、合规问题 → 玩家看到"任务卡在审查"
        ReduceOutput,      // 降低产出：能力不足 → 玩家看到"效率变低"
    }

    /// <summary>§3.2 数据结构。</summary>
    public class SilentProtest
    {
        public float Level;                        // 0..1 抗议强度
        public HashSet<ProtestForm> Forms = new();
        public bool IsCovert;                      // 是否伪装（假理由）
        public bool DiscoveredByPlayer;            // 玩家是否已经识破
        public float Duration;                     // 持续了多久

        public string FormCn() => string.Join("+", FormsCn());
        private IEnumerable<string> FormsCn()
        {
            foreach (var f in Forms)
                yield return f switch
                {
                    ProtestForm.RefuseDraft => "拒绝征召",
                    ProtestForm.InflatePrice => "涨价",
                    ProtestForm.ForgeCertificate => "伪造证明",
                    ProtestForm.ReduceOutput => "降低产出",
                    _ => "?",
                };
        }
    }

    /// <summary>谁会抗议、怎么抗议——由人格决定（§3.3 HEXACO 路由）。</summary>
    public static class ProtestRouter
    {
        public static SilentProtest ChooseProtest(NamedNpc n, float grievance)
        {
            var p = new SilentProtest { Level = Math.Clamp(grievance * 1.0f, 0f, 1f) };

            // 高宜人性 + 低神经质：不会抗议，会正面说来找你谈
            if (n.Agreeableness > 0.7f && n.Neuroticism < 0.5f)
                return null;

            // 低诚实-谦逊 + 高尽责性：用规则对付你（伪造证明）
            if (n.HonestyHumility < 0.3f && n.Conscientiousness > 0.6f)
                p.Forms.Add(ProtestForm.ForgeCertificate);

            // 高神经质：消极怠工
            if (n.Neuroticism > 0.6f)
                p.Forms.Add(ProtestForm.ReduceOutput);

            // 低外向性 + 高地位：拒绝（不吵不闹，就是不来）
            if (n.Extraversion < 0.4f && n.Status > 0.5f)
                p.Forms.Add(ProtestForm.RefuseDraft);

            // 商人：涨价（最直接的经济手段）
            if (n.IsMerchant)
                p.Forms.Add(ProtestForm.InflatePrice);

            if (p.Forms.Count == 0) return null;
            p.IsCovert = true;   // 默认伪装——每一种都能给出合理解释
            return p;
        }
    }

    /// <summary>§3.4 四种形式的具体实现 + §3.6 传染。</summary>
    public static class ProtestEffects
    {
        /// <summary>② 涨价（§3.4）：名声差 → 涨；抗议强 → 涨。幅度合理到说不出什么（15%~60%）。</summary>
        public static (float multiplier, string displayReason) Price(NamedNpc n, float localReputation)
        {
            if (n.Protest == null || !n.Protest.Forms.Contains(ProtestForm.InflatePrice))
                return (1.0f, null);

            float level = n.Protest.Level;
            float m = 1.0f
                + localReputation * -0.4f       // 名声差 → 涨
                + level * 0.6f;                 // 抗议强度
            m = Math.Clamp(m, 1.0f, 1.6f);      // 涨一倍玩家立刻知道在针对他；两成他会怀疑但没法指控

            // 包装成正常理由：抗议强 → 假理由；否则真涨价（没伪装）
            string reason = level > 0.3f ? "最近供应紧张" : null;
            return (m, reason);
        }

        /// <summary>④ 降低产出（§3.4）：全是"能力问题"，不是"态度问题"。</summary>
        public static (float qty, float quality, float timeScale) ReduceOutput(NamedNpc n)
        {
            if (n.Protest == null || !n.Protest.Forms.Contains(ProtestForm.ReduceOutput))
                return (1f, 1f, 1f);
            float l = n.Protest.Level;
            return (1f - l * 0.5f, 1f - l * 0.35f, 1f + l * 0.8f);
        }

        /// <summary>③ 伪造证明：任务卡住，玩家看到的是"流程"，不是"有人在拖"（§3.4）。</summary>
        public static int StallDays(NamedNpc n, out string pretext)
        {
            pretext = null;
            if (n.Protest == null || !n.Protest.Forms.Contains(ProtestForm.ForgeCertificate)) return 0;
            pretext = "不是我不干，是安全认证没过。你要我违规操作吗？";
            return 3;   // "还在审"
        }

        /// <summary>① 拒绝征召：表面上给一个体面理由（§3.4）。</summary>
        public static bool RefusesDraft(NamedNpc n, Rng rng, out string displayReason)
        {
            displayReason = null;
            if (n.Protest == null || !n.Protest.Forms.Contains(ProtestForm.RefuseDraft)) return false;
            var pretexts = new[] { "母亲病重", "孩子还小", "身体有旧伤" };
            displayReason = rng.Pick(pretexts);
            return true;
        }

        /// <summary>§3.6 传染：每 6 小时结算一次，沿关系边扩散——亲近的人会被影响，强度打折。</summary>
        public static void ContagionStep(List<(NamedNpc a, NamedNpc b, float affinity)> edges)
        {
            foreach (var (a, b, affinity) in edges)
            {
                if (a.Protest != null && a.Protest.Level > 0.4f && affinity > 0.5f)
                {
                    float contagion = a.Protest.Level * 0.15f * affinity;
                    b.Grievance = Math.Clamp(b.Grievance + contagion, 0f, 1f);
                }
            }
        }
    }
}
