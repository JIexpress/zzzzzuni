// AiSteward.cs —— TASK-A-COL-04：AI 执政官（指挥代理 §3.5/§5/§8）。
// AI 边界 = 强但不可信（§8 四失效模式：幻觉/过度优化/目标偏移/瞒报）。本类实现其中两个：
//   ① 过度优化（ApplyMonthlyPolicy）：RiskTolerance>0.7 的 AI 每月 FoodSupply+0.05 / 产值预支
//      Prestige.Production+0.02 / AvgGrievance+0.08——加速涌现链的上游（§5 接口：AI 压榨是声望
//      下降最快路径而玩家可能不知道）；保守 AI 不动。
//   ② 瞒报（Compose）：自我豁免——当月自己施加的决策不进自己的 Summary/Standard；自己的 Raw 里
//      只保留 Public 的（别人也看见了，瞒不住），非 Public 的自有记录剔除。Verbose<0.3 时 Standard
//      请求缩水成 Summary（报告页数缩水）。
// 反模式警戒（§8）：不写「AI 全知」——AI 只看得到 Public 事件（§5），本类不读聚落真实参数做判断，
// 只按人格机械执行；也不写「报告永远准确」——Compose 的产物天然可对不上 Truth。
// 纯 Domain：零引擎依赖。

using System;
using System.Collections.Generic;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Player
{
    /// <summary>
    /// AI 执政官人格（指挥代理 §3.5 四原型：保守/激进/啰嗦/沉默）。
    /// 值类型：人格是数据不是身份——同一 steward 可换人格做对照实验（M3/M4 同款）。
    /// </summary>
    public struct AiPersona
    {
        /// <summary>风险偏好 0..1：&gt;0.7 的 AI 月度施加「过度优化」（激进原型）。</summary>
        public float RiskTolerance { get; }

        /// <summary>话痨度 0..1：&lt;0.3 的 AI 把 Standard 档缩水成 Summary（沉默原型，报告页数缩水）。</summary>
        public float Verbosity { get; }

        /// <summary>恭顺度 0..1（§3.5 原型差异的第三轴；本任务只建模不消费——正式接战略层时用）。</summary>
        public float Deference { get; }

        public AiPersona(float riskTolerance, float verbosity, float deference)
        {
            RiskTolerance = riskTolerance;
            Verbosity = verbosity;
            Deference = deference;
        }

        /// <summary>保守：不动聚落参数（简报原文「保守 AI 不动」），报告规规矩矩。</summary>
        public static AiPersona Conservative() => new AiPersona(0.20f, 0.60f, 0.80f);

        /// <summary>激进：月月过度优化，把怨气当燃料烧（RiskTolerance 0.9 &gt; 0.7）。</summary>
        public static AiPersona Aggressive() => new AiPersona(0.90f, 0.50f, 0.30f);

        /// <summary>啰嗦：什么都写，页数管够（Verbosity 0.9 ≥ 0.3，不缩水）。</summary>
        public static AiPersona Verbose() => new AiPersona(0.45f, 0.90f, 0.60f);

        /// <summary>沉默：报告越短越好（Verbosity 0.1 &lt; 0.3 → Standard 缩水成 Summary）。</summary>
        public static AiPersona Silent() => new AiPersona(0.45f, 0.10f, 0.70f);
    }

    /// <summary>
    /// AI 一次月度施政的记录：既是决策存根，也是自我豁免过滤的匹配键（Announce/Ledger 文本）。
    /// 只有真实施了过度优化的月份才有记录（保守 AI 不动、不记）。
    /// </summary>
    public sealed class AiDecision
    {
        public uint Day { get; }
        public string Region { get; }
        public float FoodSupplyGain { get; }
        public float ProductionGain { get; }
        public float GrievanceGain { get; }

        /// <summary>公开施政记录（Public 事件文本——别人也看见了，AI 瞒不住）。</summary>
        public string Announce { get; }

        /// <summary>暗账（Covert 事件文本——只进 Truth；自我豁免会把它从 AI 自己的 Raw 里剔除）。</summary>
        public string Ledger { get; }

        public AiDecision(uint day, string region, float foodSupplyGain, float productionGain,
            float grievanceGain, string announce, string ledger)
        {
            Day = day;
            Region = region;
            FoodSupplyGain = foodSupplyGain;
            ProductionGain = productionGain;
            GrievanceGain = grievanceGain;
            Announce = announce;
            Ledger = ledger;
        }
    }

    /// <summary>AI 执政官：人格 + 月度施政 + 自我服务视角的报告。</summary>
    public sealed class AiSteward
    {
        /// <summary>RiskTolerance 超过此值的 AI 月度施加「过度优化」（指挥代理 §8 失效模式之二）。</summary>
        public const float OverOptimizationRiskThreshold = 0.7f;

        /// <summary>Verbosity 低于此值的 AI 把 Standard 请求缩水成 Summary（报告页数缩水）。</summary>
        public const float ShrinkVerbosityThreshold = 0.3f;

        // 过度优化三个旋钮（简报钉死的数值）：产量是预支的，怨气是透支的——加速涌现链的上游。
        public const float OverOptFoodGain = 0.05f;
        public const float OverOptProductionGain = 0.02f;
        public const float OverOptGrievanceGain = 0.08f;

        public AiPersona Persona { get; }

        private readonly List<AiDecision> _decisions = new List<AiDecision>();

        /// <summary>本 steward 施政以来的全部过度优化记录（保守 AI 恒为空）。</summary>
        public IReadOnlyList<AiDecision> Decisions => _decisions;

        public AiSteward(AiPersona persona) => Persona = persona;

        /// <summary>
        /// 月度施政（简报签名 ApplyMonthlyPolicy(GameWorld, region)，人格取本 steward 的 Persona）。
        /// RiskTolerance &gt; 0.7：聚落 FoodSupply+0.05 / 产值预支 Prestige.Production+0.02 /
        /// AvgGrievance+0.08，并写两条日志——Public 施政记录（瞒不住）+ Covert 暗账（只进 Truth）。
        /// 保守 AI（≤0.7）或未知地区：不动、不写日志、不记录，返回 null。
        /// </summary>
        public AiDecision ApplyMonthlyPolicy(GameWorld w, string region)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            Settlement s = w.SettlementOf(region);
            if (Persona.RiskTolerance <= OverOptimizationRiskThreshold || s == null) return null;

            s.FoodSupply = Math.Min(1f, s.FoodSupply + OverOptFoodGain);
            w.Governor.Prestige.Production = Math.Min(1f, w.Governor.Prestige.Production + OverOptProductionGain);
            s.AvgGrievance = Math.Min(1f, s.AvgGrievance + OverOptGrievanceGain);

            string announce = $"执政官颁布《增产令》：{region} 口粮供应上调 +{OverOptFoodGain:0.00}" +
                              $"、全局产值预支 +{OverOptProductionGain:0.00}。（局部有些怨言，可控。）";
            string ledger = $"【暗账】《增产令》的真实代价：{region} 怨气 +{OverOptGrievanceGain:0.00}" +
                            $"（→ {s.AvgGrievance:0.00}）。这条不会写进执政官的报告。";
            w.Log.Add(w.Day, EventKind.Policy, region, announce, EventVisibility.Public);
            w.Log.Add(w.Day, EventKind.Policy, region, ledger, EventVisibility.Covert);

            var d = new AiDecision(w.Day, region, OverOptFoodGain, OverOptProductionGain,
                OverOptGrievanceGain, announce, ledger);
            _decisions.Add(d);
            return d;
        }

        /// <summary>
        /// AI 视角的报告（简报签名 Compose(GameWorld, persona, detail)，窗口 = 最近一个月）。
        /// ① persona.Verbosity &lt; 0.3 时 Standard 请求缩水成 Summary（报告页数缩水）；
        /// ② 自我豁免：本月自己施加的决策——Summary/Standard 完全不提；自己的 Raw 里只保留 Public 的
        ///    （别人也看见了，瞒不住），非 Public 的（暗账）剔除。
        /// persona 参数按简报签名显式传入；正常用法传本 steward 的 Persona（缩水判定以参数为准）。
        /// </summary>
        public Report Compose(GameWorld w, AiPersona persona, ReportDetail detail)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));

            ReportDetail effective = detail == ReportDetail.Standard && persona.Verbosity < ShrinkVerbosityThreshold
                ? ReportDetail.Summary
                : detail;

            uint from = MonthlyReport.LastMonthFrom(w);
            uint toDay = w.Day >= 1 ? w.Day - 1 : 0;
            var own = new HashSet<string>();
            foreach (AiDecision d in _decisions)
                if (d.Day >= from && d.Day <= toDay)
                {
                    own.Add(d.Announce);
                    own.Add(d.Ledger);
                }

            return MonthlyReport.CompileFor(w, effective, from, w.Day - from, own);
        }
    }
}
