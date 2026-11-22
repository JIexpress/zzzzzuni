// ColonyAutonomyTests.cs —— TASK-A-COL-06：NPC 自主行为（效用 AI/任务板接组织度涌现）T1-T5。
// T1 人格改评分第一 · T2 30 天行为分布非退化 · T3 组织者招募传导 · T4 涌现加速对照 · T5 性能上界。
// 建局一律走 A-COL-02 的数据驱动路径（五张 GLM 表资产 → ColonyDataLoader.BuildWorld，同 M/S 系列口径）；
// 断言全部可证伪：评分手算可复现（ColonyActionScorer 常量），种子固定 → 分布确定性可复现。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Autonomy;
using Game.Domain.Colony.Sim;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace Game.Domain.Tests
{
    /// <summary>NPC 自主行为（TASK-A-COL-06）：人格可解释的评分、非退化分布、招募传导、涌现加速、性能上界。</summary>
    public sealed class ColonyAutonomyTests
    {
        // 与 V1-V7/D1-D4/M1-M5/S1-S5 同种子：随机流确定性可复现（自主行为本身不消耗 Rng 流）。
        private const ulong Seed = 20261122UL;

        private static ConfigService _config;
        private static ColonyWorldData _data;

        [OneTimeSetUp]
        public void LoadRealTables()
        {
            _config = new ConfigService();
            LoadTable<NpcsConfigRow>(_config, "npcs");
            LoadTable<PlacesConfigRow>(_config, "places");
            LoadTable<PoliciesConfigRow>(_config, "policies");
            LoadTable<QuestsConfigRow>(_config, "quests");
            LoadTable<DialogueConfigRow>(_config, "dialogue");
            _data = ColonyConfigAdapter.ToWorldData(_config);
        }

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（照抄 ColonyDataDrivenTests 同款）。</summary>
        private static void LoadTable<TRow>(ConfigService config, string stem) where TRow : ConfigRow
        {
            var csvPath = $"{CsvImporter.CsvRoot}/{stem}.csv";
            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            Assert.IsNotNull(table, $"殖民地表资产缺失：{assetPath}");
            config.Load(table);
        }

        /// <summary>每个用例独立建局（数据驱动，种子固定 → 断言确定性可复现）。</summary>
        private static GameWorld BuildWorld() => ColonyDataLoader.BuildWorld(Seed, _data);

        /// <summary>挑一个无特殊身份的具名 NPC（非商人、非组织者）做人格实验素材。</summary>
        private static NamedNpc PickPlainNpc(GameWorld w)
        {
            NamedNpc n = w.Named.FirstOrDefault(x => !x.IsMerchant && x.Role != ColonyDailyBehavior.OrganizerRole);
            Assert.IsNotNull(n, "前提：数据驱动建局必须有非商人非组织者的具名 NPC");
            return n;
        }

        // ────────────────────────────── T1-T5 ──────────────────────────────

        /// <summary>
        /// T1：同一 NPC 改人格参数 → 评分第一名变化，且「为什么」从分数读得出来：
        /// 高宜人性(0.9)低神经质(0.3) → ProtestRouter 返回 null → Protest 候选分=0（人格否决，不选抗议）；
        /// 同一人改成 IsMerchant+低宜人性+高怨气 → 路由必含涨价 → Protest 候选分登顶；
        /// 涨价加成（ProtestMerchantBonus）可从两个 context 的分差里逐字读出。
        /// </summary>
        [Test]
        public void T1_ScoringPersonality()
        {
            var w = BuildWorld();
            NamedNpc npc = PickPlainNpc(w);
            npc.Grievance = 0.8f;

            // ── 变体 A：高宜人性 + 低神经质 → 人格否决抗议候选 ──
            npc.Agreeableness = 0.9f;
            npc.Neuroticism = 0.3f;
            npc.HonestyHumility = 0.6f;
            npc.Conscientiousness = 0.7f;
            npc.Extraversion = 0.5f;
            npc.Status = 0.5f;
            ColonyNpcContext ctxA = ColonyDailyBehavior.BuildContext(w, npc);
            ColonyTaskBoard boardA = ColonyTaskBoard.For(ctxA);

            Assert.AreEqual(0f, ctxA.ProtestIntensity, 1e-5f,
                "高宜人性(0.9>0.7)低神经质(0.3<0.5)：ProtestRouter 必须返回 null（§3.3 会正面来找你谈）");
            Assert.AreEqual(0f, boardA.ScoreOf(ColonyAction.Protest), 1e-5f,
                "人格否决后 Protest 候选分必须是 0——效用 AI 的『不选抗议』能从分数读出来");
            ColonyAction topA = boardA.Select();
            Assert.AreNotEqual(ColonyAction.Protest, topA, "高宜人性低神经质者不得选抗议");
            Assert.Greater(boardA.ScoreOf(topA), 0f, "第一名必须是正分候选（防全零退化）");

            // ── 变体 B：同一人改成商人 + 低宜人性 + 高怨气 → 涨价候选登顶 ──
            npc.IsMerchant = true;
            npc.Agreeableness = 0.2f;
            npc.Neuroticism = 0.6f;
            ColonyNpcContext ctxB = ColonyDailyBehavior.BuildContext(w, npc);
            ColonyTaskBoard boardB = ColonyTaskBoard.For(ctxB);

            Assert.IsTrue(ctxB.ProtestInflatePrice,
                "IsMerchant + 高怨气：路由结果必须含涨价形式（§3.3 商人最直接的经济手段）");
            Assert.AreEqual(0.91f, boardB.ScoreOf(ColonyAction.Protest), 1e-3f,
                "手算：0.6×0.8 + 0.2×0.8 + 0.2×0.6 + 0.15 = 0.91（公式公开可测）");
            Assert.AreEqual(ColonyAction.Protest, boardB.Select(), "IsMerchant+高怨气 → 涨价候选必须登顶");
            Assert.AreNotEqual(topA, boardB.Select(), "同一 NPC 改人格参数 → 评分第一名必须变化");

            // ── 涨价加成单因子隔离：唯一差异 = ProtestInflatePrice → 分差恰为 ProtestMerchantBonus ──
            var withInflate = new ColonyNpcContext
            {
                Grievance = 0.8f, Agreeableness = 0.2f, Neuroticism = 0.6f,
                ProtestIntensity = 0.8f, ProtestInflatePrice = true,
            };
            var withoutInflate = new ColonyNpcContext
            {
                Grievance = 0.8f, Agreeableness = 0.2f, Neuroticism = 0.6f,
                ProtestIntensity = 0.8f, ProtestInflatePrice = false,
            };
            Assert.AreEqual(ColonyActionScorer.ProtestMerchantBonus,
                ColonyActionScorer.Score(ColonyAction.Protest, withInflate)
                - ColonyActionScorer.Score(ColonyAction.Protest, withoutInflate), 1e-5f,
                "InflatePrice 候选分升必须恰为商人加成常量（简报 T1 的『候选分升』逐字可读）");
        }

        /// <summary>
        /// T2：100 具名 NPC 推 30 天（种子固定）→ 行为分布非退化：≥3 种 Action 出现；
        /// Work 占比落在 40-90%（既防全员抗议、也防自主行为形同虚设）；无组织者时 Recruit 恒为 0
        /// （「仅 Organizer」的门禁）；每日计数 = 当日具名数。
        /// </summary>
        [Test]
        public void T2_DailyDistribution()
        {
            var w = BuildWorld();
            Assert.AreEqual(100, w.Named.Count, "前提：数据驱动建局 100 具名 NPC");
            var counts = new Dictionary<ColonyAction, int>();
            int total = 0;

            for (int day = 0; day < 30; day++)
            {
                w.AdvanceDays(1);
                Assert.AreEqual(w.Named.Count, w.AutonomousActionsToday,
                    $"第 {day + 1} 天：自主行为计数必须等于当日具名数（一人一选）");
                foreach (KeyValuePair<int, ColonyAction> kv in w.AutonomousChoicesToday)
                {
                    counts[kv.Value] = counts.TryGetValue(kv.Value, out int c) ? c + 1 : 1;
                    total++;
                }
            }

            Assert.GreaterOrEqual(counts.Count, 3,
                $"行为分布必须非退化（≥3 种 Action），实际：{Describe(counts)}");
            float workShare = counts.TryGetValue(ColonyAction.Work, out int work) ? work / (float)total : 0f;
            Assert.GreaterOrEqual(workShare, 0.40f,
                $"Work 占比必须 ≥40%（防全员抗议），实际 {workShare:0.00}：{Describe(counts)}");
            Assert.LessOrEqual(workShare, 0.90f,
                $"Work 占比必须 ≤90%（防自主行为形同虚设），实际 {workShare:0.00}：{Describe(counts)}");
            Assert.AreEqual(0, counts.TryGetValue(ColonyAction.Recruit, out int recruit) ? recruit : 0,
                "本局无组织者：Recruit 必须恒为 0（仅 Organizer 门禁）");
            Assert.Greater(total, 0, "30 天 × 100 人必须产生决策记录");
        }

        /// <summary>
        /// T3：组织者存在 + 邻居怨气&gt;0.4 → Recruit 被选中时邻居组织度上涨且 Log 有 Covert 事件；
        /// 每招募一人向聚落群体组织度回流（涌现加速的机制来源）；选择入 AutonomousChoicesToday。
        /// </summary>
        [Test]
        public void T3_OrganizerRecruits()
        {
            var w = BuildWorld();
            Settlement s = w.Settlements[0];
            NamedNpc organizer = PickPlainNpc(w);

            // 摆出组织者：高组织度、低怨气（人格门掉 Protest）、有具名聚落归属。
            organizer.Role = ColonyDailyBehavior.OrganizerRole;
            organizer.Organization = 0.8f;
            organizer.Grievance = 0.1f;
            organizer.Agreeableness = 0.5f;
            organizer.Neuroticism = 0.4f;
            organizer.HonestyHumility = 0.6f;
            organizer.Conscientiousness = 0.7f;
            organizer.Extraversion = 0.5f;
            organizer.Status = 0.5f;
            s.NamedNpcs.Add(organizer.Id);

            // 全部具名邻居推过招募怨气门槛（简报语义：向怨气>0.4 的关系边邻居招募）。
            List<NamedNpc> targets = w.Relations.EdgesOf(organizer.Id)
                .Select(e => e.A == organizer.Id ? e.B : e.A)
                .Distinct()
                .Where(id => w.NamedById.ContainsKey(id))
                .Select(id => w.NamedById[id])
                .ToList();
            Assert.GreaterOrEqual(targets.Count, 3, "前提：组织者必须有 ≥3 个具名邻居");
            float[] orgBefore = targets.Select(t => t.Organization).ToArray();
            foreach (NamedNpc t in targets) t.Grievance = 0.6f;
            float settlementOrgBefore = s.Organization;

            ColonyAction? chosen = ColonyDailyBehavior.SettleFor(w, organizer);

            Assert.AreEqual(ColonyAction.Recruit, chosen,
                "组织者（组织度 0.8、邻居全怨气>0.4）必须自己选中招募：0.5×0.8+0.5×1.0=0.9 > Work 基线");
            for (int i = 0; i < targets.Count; i++)
            {
                Assert.AreEqual(orgBefore[i] + ColonyDailyBehavior.RecruitOrgGain, targets[i].Organization, 1e-5f,
                    $"被招募邻居 {targets[i].Name} 的组织度必须上涨");
            }
            Assert.AreEqual(settlementOrgBefore + targets.Count * ColonyDailyBehavior.RecruitSettlementGain,
                s.Organization, 1e-5f, "招募产出必须向聚落群体组织度回流（涌现加速的机制来源）");

            GameEvent covert = w.Log.All.FirstOrDefault(e =>
                e.Vis == EventVisibility.Covert && e.Kind == EventKind.Uprising && e.Text.Contains(organizer.Name));
            Assert.IsNotNull(covert, "招募必须写一条 Covert 事件（地下串联玩家看不见——视野纪律）");
            Assert.AreEqual(1, w.AutonomousActionsToday, "自主行为计数必须 +1");
            Assert.AreEqual(ColonyAction.Recruit, w.AutonomousChoicesToday[organizer.Id],
                "选择必须记录进 AutonomousChoicesToday（可解释性入口）");
        }

        /// <summary>
        /// T4：涌现加速——同编排（同种子同 fixture）对照组（AutonomyEnabled=false）vs 实验组（默认开）
        /// 推 30 天：实验组聚落组织度增量必须严格大于对照组（自主行为在推动，不只是参数在动）。
        /// </summary>
        [Test]
        public void T4_EmergenceAccelerates()
        {
            GameWorld ctl = BuildWorld();
            GameWorld exp = BuildWorld();
            FixtureOrganizerWithAggrievedNeighbors(ctl);
            FixtureOrganizerWithAggrievedNeighbors(exp);
            ctl.AutonomyEnabled = false;   // 对照组：纯参数路径（NPC 被动响应）
            float ctlBefore = ctl.Settlements[0].Organization;
            float expBefore = exp.Settlements[0].Organization;

            ctl.AdvanceDays(30);
            exp.AdvanceDays(30);

            float ctlDelta = ctl.Settlements[0].Organization - ctlBefore;
            float expDelta = exp.Settlements[0].Organization - expBefore;
            Assert.AreEqual(0f, ctlDelta, 1e-4f,
                "前提自证：对照组（无自主行为）聚落组织度 30 天内不得自行增长（撕裂方差未越加速线）");
            Assert.Greater(expDelta, ctlDelta + 0.01f,
                $"涌现加速：实验组聚落组织度增量（{expDelta:0.000}）必须严格大于对照组（{ctlDelta:0.000}）——" +
                "同等编排下自主行为让组织度长得更快");
            Assert.Greater(exp.AutonomousActionsToday, 0, "实验组当日自主行为计数必须非零");
            Assert.AreEqual(exp.Named.Count, exp.AutonomousActionsToday,
                "实验组当日自主行为计数必须等于当日具名数（一人一选——计数按 NPC 不按 id）");
            Assert.GreaterOrEqual(exp.AutonomousChoicesToday.Count, 100,
                "实验组当日选择记录必须覆盖全部建局具名 NPC（周结涌现的组织者与既有者可能同 id" +
                "（TrySpawnOrganizer id=90_000+population 的既有行为），按 id 记录会合并，故只按下限断言）");
            Assert.AreEqual(0, ctl.AutonomousActionsToday, "对照组计数必须为 0（开关关闭）");
            Assert.AreEqual(0, ctl.AutonomousChoicesToday.Count, "对照组不得有选择记录");
        }

        /// <summary>
        /// T5：100 NPC × 30 天自主结算耗时 &lt; 50ms（Stopwatch 断言——自主行为不能把每日 tick 拖爆）。
        /// 口径 = ColonyDailyBehavior.SettleFor 的纯自主结算路径（不含每日 tick 的其余系统）。
        /// </summary>
        [Test]
        public void T5_PerfBound()
        {
            var w = BuildWorld();
            // 预热一轮（首跑含 JIT/委托绑定，不进计时）。
            foreach (NamedNpc n in w.Named) ColonyDailyBehavior.SettleFor(w, n);

            var sw = Stopwatch.StartNew();
            for (int day = 0; day < 30; day++)
            {
                foreach (NamedNpc n in w.Named)
                {
                    ColonyDailyBehavior.SettleFor(w, n);
                }
            }
            sw.Stop();

            Assert.Less(sw.Elapsed.TotalMilliseconds, 50.0,
                $"100 NPC × 30 天自主结算必须 < 50ms，实际 {sw.Elapsed.TotalMilliseconds:0.000}ms");
        }

        // ────────────────────────────── fixture ──────────────────────────────

        /// <summary>
        /// T4 共用编排：把一个普通具名 NPC 摆成组织者（高组织度/低怨气/人格门掉 Protest），
        /// 归入聚落[0] 名单，并把 3 个具名邻居的怨气推过招募门槛。两个世界同函数 → 同编排。
        /// </summary>
        private static void FixtureOrganizerWithAggrievedNeighbors(GameWorld w)
        {
            NamedNpc organizer = PickPlainNpc(w);
            organizer.Role = ColonyDailyBehavior.OrganizerRole;
            organizer.Organization = 0.8f;
            organizer.Grievance = 0.1f;
            organizer.Agreeableness = 0.5f;
            organizer.Neuroticism = 0.4f;
            organizer.HonestyHumility = 0.6f;
            organizer.Conscientiousness = 0.7f;
            organizer.Extraversion = 0.5f;
            organizer.Status = 0.5f;
            w.Settlements[0].NamedNpcs.Add(organizer.Id);

            List<NamedNpc> targets = w.Relations.EdgesOf(organizer.Id)
                .Select(e => e.A == organizer.Id ? e.B : e.A)
                .Distinct()
                .Where(id => w.NamedById.ContainsKey(id))
                .Select(id => w.NamedById[id])
                .ToList();
            Assert.GreaterOrEqual(targets.Count, 3, "前提：组织者必须有 ≥3 个具名邻居（两世界同构）");
            foreach (NamedNpc t in targets) t.Grievance = 0.6f;
        }

        private static string Describe(Dictionary<ColonyAction, int> counts)
        {
            return string.Join("、", counts.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
