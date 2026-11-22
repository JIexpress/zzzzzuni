using System;
using System.Collections.Generic;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Player
{
    /// <summary>
    /// 全局声望（声望认知 §1）：四个加权项。
    /// 信誉违约的权重是守约的两倍——人记仇比记恩牢；信誉有半衰期，每 30 天向 0.5 回归。
    /// </summary>
    public class PrestigeSystem
    {
        public float Production;      // 产值：你治下创造了多少（0..1）
        public float TradeValue;      // 贸易值（0..1）
        public float MilitaryDeed;    // 军事成就（0..1）

        public int PromisesKept, PromisesBroken;

        public float Credibility { get; private set; } = 0.5f;

        /// <summary>§1.1：(kept - broken×2) / max(1, total)。</summary>
        public void SettleCredibility()
        {
            int total = PromisesKept + PromisesBroken;
            float raw = (PromisesKept - PromisesBroken * 2.0f) / Math.Max(1, total);
            // 与现值缓慢融合，避免单次结算跳变
            Credibility = Math.Clamp(Credibility * 0.7f + (raw * 0.5f + 0.5f) * 0.3f, 0f, 1f);
        }

        /// <summary>每 30 天向 0.5 回归一点（§1.1）。</summary>
        public void MonthlyDecay() => Credibility = Math.Clamp(Credibility + (0.5f - Credibility) * 0.05f, 0f, 1f);

        /// <summary>§1 原式。</summary>
        public float GlobalPrestige => Production * 0.30f + TradeValue * 0.25f + Credibility * 0.25f + MilitaryDeed * 0.20f;

        /// <summary>§1.2 不显示数字，显示称谓。</summary>
        public string Title()
        {
            float p = GlobalPrestige;
            return p < 0.2f ? "「那个新来的」" :
                   p < 0.45f ? "「某某号上的」" :
                   p < 0.7f ? "「管事的」" :
                   p < 0.9f ? "「那位」" : "「传说里的那个人」";
        }
    }

    /// <summary>
    /// 管理者（玩家）。四层声望的漏斗（声望 §0）：
    /// 全局（这里）→ 地区（RegionState.Reputation）→ 事迹（LocalDeed 列表）→ 个人看法（每个 NPC 的 PlayerImage）。
    /// </summary>
    public class Governor
    {
        public PrestigeSystem Prestige = new();

        // ── 行为印记：喂给面具动力学（NPC规格 §3 决定因素）
        public int RepressionCount;                    // 恐怖统治 → MaskStrength ↑
        public readonly Dictionary<string, uint> LastVisit = new();   // 亲自下基层 → 面具被撕开
        public readonly HashSet<string> Informants = new();           // 线人覆盖的地区
        public string PresentRegion = "";              // 玩家当前在哪块地

        // ── 情报（真实感⑤：信息比资源贵——要买、会过期、有假货）
        public readonly List<IntelReport> Intel = new();

        public void Visit(string region, uint day) => LastVisit[region] = day;

        public IntelReport BuyIntel(uint day, string claim, int validDays, bool forged)
        {
            var r = new IntelReport { Claim = claim, BoughtDay = day, ValidDays = validDays, Forged = forged };
            Intel.Add(r);
            return r;
        }

        /// <summary>
        /// 观察动作（异常行为 §4.1/§8 MVP 第 5 步）：守一会儿。
        /// 能发现伪装的抗议证据（间接的、无法指控的），也可能一无所获。
        /// </summary>
        public bool Observe(GameWorld w, NamedNpc target)
        {
            if (target.Protest is { IsCovert: true } && w.Rng.Chance(0.45f))
            {
                target.Protest.DiscoveredByPlayer = true;
                w.Log.Add(w.Day, EventKind.Observation, w.RegionOf(target),
                    $"你在{target.Name}那里守到半夜：他嘴上说「{PickExcuse(w)}」，可半夜有人来提货——给的是平常价。怀疑成立，但你无法指控。",
                    EventVisibility.Personal);
                return true;
            }
            w.Log.Add(w.Day, EventKind.Observation, w.RegionOf(target),
                $"你观察了{target.Name}一阵子，没看出什么。", EventVisibility.Personal);
            return false;
        }

        private static string PickExcuse(GameWorld w) => w.Rng.Pick(new[] { "最近供应紧张", "身体不太舒服", "手头设备有点问题" });
    }

    /// <summary>一条情报：会过期，可能是假的（真实感路线 ⑤）。</summary>
    public class IntelReport
    {
        public string Claim;
        public uint BoughtDay;
        public int ValidDays;
        public bool Forged;

        public bool Expired(uint day) => day > BoughtDay + ValidDays;

        public override string ToString()
            => (Forged ? "[假] " : "") + Claim + (Expired(BoughtDay + (uint)ValidDays + 1) ? $"（{BoughtDay} 买到，已过期）" : $"（{BoughtDay} 买到，还有 {Math.Max(0, (int)BoughtDay + ValidDays - (int)BoughtDay)} 天）");
    }
}
