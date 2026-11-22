using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Policy
{
    /// <summary>一条可颁布的政策（数据来自 GLM 政策表：40 条，含 5 挖坑 + 5 暗药）。</summary>
    public class PolicyDef
    {
        public int Id;
        public string Name, Desc, Benefit, Cost, WhoUnhappy, Difficulty;
        public bool IsTrap;         // 看起来好实际上很糟
        public bool IsDarkMedicine; // 看起来很糟实际上有奇效
    }

    /// <summary>
    /// 政策系统：颁布 → 执行度结算 → 文化加权的怨气 → 「谁会不满」落到具体人群 → 延迟后果。
    /// 不做「提高 10% 满意度」的纯数值——每条政策写的是具体发生了什么。
    /// </summary>
    public static class PolicySystem
    {
        /// <summary>
        /// 颁布一条政策。
        /// execution：执行质量 0..1（声望认知 §2.1：好政策+执行糟糕 = 「说得好听，做不到」，失望比愤怒难修复）。
        /// </summary>
        public static void Enact(GameWorld w, PolicyDef p, string regionName, float execution)
        {
            var region = w.Regions.FirstOrDefault(r => r.Name == regionName);
            if (region == null) { w.Log.Add(w.Day, EventKind.Policy, regionName, $"找不到地区 {regionName}", EventVisibility.Public); return; }

            // 声望认知 §2.1：执行与有利分开算。挖坑政策短期“有利”很高——这正是坑的伪装。
            float apparentBenefit = p.IsTrap ? 0.6f
                                  : p.IsDarkMedicine ? -0.4f
                                  : BenefitScore(p.Benefit);
            region.PolicyExecution = execution;
            region.PolicyBenefit = apparentBenefit;

            w.Log.Add(w.Day, EventKind.Policy, regionName,
                $"颁布《{p.Name}》：{p.Desc}（执行度 {execution:0.00}）—— {region.PolicyVerdict()}",
                EventVisibility.Public);

            // 「谁会不满」→ 具体人群：关键词 → 职业组映射（数据侧写“中层管理者”，框架翻译成具体的人）
            var unhappy = w.Named.Where(n => MatchGroup(p.WhoUnhappy, n) && w.RegionOf(n) == regionName).ToList();
            var ev = new OffenseEvent
            {
                Summary = $"{p.Name}：{p.Cost}",
                ContractBreak = p.IsDarkMedicine ? 0.5f : 0.45f,
                PublicShame = 0.1f,
                Care = 0.4f, Fairness = 0.3f, GroupLoyalty = 0.2f, Liberty = 0.35f,
            };
            float totalOffense = 0f;
            foreach (var n in unhappy)
            {
                float o = ev.ComputeOffense(n);          // 文化加权（同一决策三种反应）
                n.Grievance = Math.Clamp(n.Grievance + o * 0.35f, 0f, 1f);
                totalOffense += o;
            }
            var settlement = w.Settlements.FirstOrDefault(s => s.Name == regionName);
            if (settlement != null)
                settlement.AvgGrievance = Math.Clamp(settlement.AvgGrievance + Math.Min(0.3f, totalOffense * 0.03f), 0f, 1f);

            w.Log.Add(w.Day, EventKind.Policy, regionName,
                $"不满人群：{p.WhoUnhappy}（实际波及 {unhappy.Count} 个具名 NPC；最大罪名标签：{(unhappy.Count > 0 ? ev.Explain(unhappy[0]) : "无")}）",
                EventVisibility.Covert);

            // 延迟后果（真实感三支柱之一）：挖坑 30 天后爆雷；暗药 30 天后见效
            if (p.IsTrap)
                w.Delayed(w.Day + 30, EventKind.Policy, regionName,
                    $"《{p.Name}》颁行一个月：当初排队颂德的人还在——但{p.Cost}正在变成现实。", EventVisibility.Public);
            if (p.IsDarkMedicine)
                w.Delayed(w.Day + 30, EventKind.Policy, regionName,
                    $"《{p.Name}》骂名照旧，但{p.Benefit}——骂你的人也得承认这点。", EventVisibility.Public);
        }

        private static float BenefitScore(string benefit) => benefit.Length > 6 ? 0.5f : 0.3f;

        /// <summary>「谁会不满」文本 → 具体职业组。框架侧的关键词映射，内容侧换表即可。</summary>
        private static bool MatchGroup(string who, NamedNpc n)
        {
            if (string.IsNullOrEmpty(who)) return false;
            foreach (var (kw, jobs) in Groups)
                if (who.Contains(kw) && jobs.Contains(n.Job)) return true;
            // 兜底：不满文本里直接出现了这个 NPC 的职业名
            return who.Contains(n.Job);
        }

        private static readonly (string kw, HashSet<string> jobs)[] Groups =
        {
            ("商人", new HashSet<string> { "走私贩", "废料回收商", "赌场老板", "会计" }),
            ("船主", new HashSet<string> { "货运飞行员", "轨道拖船驾驶员", "船坞装配工" }),
            ("矿",  new HashSet<string> { "矿工", "冶炼工", "船坞装配工" }),
            ("兵",  new HashSet<string> { "保安", "赏金猎人" }),
            ("教",  new HashSet<string> { "神父" }),
            ("医生", new HashSet<string> { "医生", "护士", "义体技师" }),
            ("工",  new HashSet<string> { "矿工", "冶炼工", "船坞装配工", "废料回收商", "义体技师" }),
        };
    }
}
