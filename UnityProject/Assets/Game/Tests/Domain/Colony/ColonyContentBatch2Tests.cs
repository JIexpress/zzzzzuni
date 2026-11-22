// ColonyContentBatch2Tests.cs —— TASK-A-COL-09：第二批内容三表（关系网 40/贸易 20/人生节点 190）
// 进引擎的 E1-E6 数据驱动测试。走 ColonyConfigAdapter 扩展（Game.View 侧生产适配器：
// LoadWorldData 五表 + LoadContentBundle 三表）→ ColonyDataLoader.BuildWorld（ColonyWorldData.Content
// 注入 GameWorld.Content 挂点）。与 D1-D4（五表管线）、V1-V7（内存 fixture 逻辑）互补：
// E 系证明第二批内容数据进引擎且消费逻辑按简报语义落地。
//
// 触发语义裁决（登记详见任务日志 §4）：
//   · 贸易事件 CSV 无 trigger 列 → 触发日 = EventId×5（T01@5 … T20@100），首个满足日触发一次；
//   · 人生节点 trigger「第 X-Y 天」为硬门槛（190/190 命中），「且其 … 时优先」是叙事语气不设门槛；
//   · 关系网按 visibility 闸门：Covert 每日 1 条（Id 序）、Personal 需在场/线人、Public 直接入日志。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Sim;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Data.Config;
using ViewCfg = Game.View.Colony.ColonyConfigAdapter;

namespace Game.Domain.Tests
{
    /// <summary>第二批内容三表进引擎（TASK-A-COL-09 E1-E6）：种子固定 → 断言确定性可复现。</summary>
    public sealed class ColonyContentBatch2Tests
    {
        // 与 V1-V7/D1-D4/S1-S5 同种子：建局随机流（人格启发式/MF/关系网）确定性可复现。
        private const ulong Seed = 20261122UL;

        private static ConfigService _config;
        private static ColonyWorldData _data;

        [OneTimeSetUp]
        public void LoadRealTables()
        {
            _config = new ConfigService();
            // 生产适配器（Game.View.Colony.ColonyConfigAdapter）：五表 → 建局输入；三表 → 内容包。
            _data = ViewCfg.LoadWorldData(_config);
            _data.Content = ViewCfg.LoadContentBundle(_config);
        }

        [TearDown]
        public void TearDown()
        {
            // EventBus 是进程级静态：清掉可能残留的排队事件（A-COL-01/02 fixture 纪律）。
            EventBus.FlushQueued();
        }

        /// <summary>含三引擎挂点的数据驱动建局（每个用例独立世界，不共享实例）。</summary>
        private static GameWorld BuildContentWorld() => ColonyDataLoader.BuildWorld(Seed, _data);

        /// <summary>把某天引擎触发的全部关系事件按 NPC 聚合出 d_* 增量（断言与 CSV 数值一致）。</summary>
        private static (float g, float l, float o) ExpectedDeltas(
            NamedNpc npc, List<string> firedIds, Dictionary<string, ColonyRelationEventInput> byId)
        {
            float g = 0, l = 0, o = 0;
            foreach (var id in firedIds)
            {
                var e = byId[id];
                if (ColonyContentLookup.NameOf(e.NpcA) == npc.Name)
                {
                    g += e.DA_Grievance; l += e.DA_Loyalty; o += e.DA_Org;
                }
                if (ColonyContentLookup.NameOf(e.NpcB) == npc.Name)
                {
                    g += e.DB_Grievance; l += e.DB_Loyalty; o += e.DB_Org;
                }
            }
            return (g, l, o);
        }

        private static void AssertClampedDelta(NamedNpc npc, (float g, float l, float o) d, (float g, float l, float o) before)
        {
            Assert.AreEqual(Math.Clamp(before.g + d.g, 0f, 1f), npc.Grievance, 1e-5f,
                $"{npc.Name} 的怨气增量必须与 CSV d_* 一致（0..1 钳制后）");
            Assert.AreEqual(Math.Clamp(before.l + d.l, 0f, 1f), npc.Loyalty, 1e-5f,
                $"{npc.Name} 的忠诚增量必须与 CSV d_* 一致（0..1 钳制后）");
            Assert.AreEqual(Math.Clamp(before.o + d.o, 0f, 1f), npc.Organization, 1e-5f,
                $"{npc.Name} 的组织度增量必须与 CSV d_* 一致（0..1 钳制后）");
        }

        // ────────────────────────────── E1 ──────────────────────────────

        /// <summary>E1：三表 40/20/190 行；抽查 3 行字段与源 CSV 逐字一致（防转码损坏）。</summary>
        [Test]
        public void E1_ThreeTablesLoad()
        {
            Assert.AreEqual(40, _config.All<ColonyRelationsConfigRow>().Count, "colonyRelations 必须整表 40 行");
            Assert.AreEqual(20, _config.All<ColonyTradesConfigRow>().Count, "colonyTrades 必须整表 20 行");
            Assert.AreEqual(190, _config.All<ColonyLifeConfigRow>().Count, "colonyLife 必须整表 190 行");

            // 抽查 1：关系网首行 R01（源 CSV 第 1 行逐字）。
            var r01 = _config.Get<ColonyRelationsConfigRow>("R01");
            Assert.AreEqual(1, r01.EventId, "R01.EventId 与源 event_id 一致");
            Assert.AreEqual("郑大镐（矿工）", r01.NpcA, "R01.NpcA 与源 CSV 逐字一致");
            Assert.AreEqual("俞半山（矿工）", r01.NpcB, "R01.NpcB 与源 CSV 逐字一致");
            Assert.AreEqual("战友", r01.PairType, "R01.PairType 与源 CSV 逐字一致");
            Assert.AreEqual("恩怨", r01.Topic, "R01.Topic 与源 CSV 逐字一致");
            Assert.AreEqual("Covert", r01.Visibility, "R01.Visibility 与源 CSV 逐字一致");
            Assert.AreEqual(0.05f, r01.DAGrievance, 1e-6f, "R01.d_a_grievance 与源一致");
            Assert.AreEqual(-0.05f, r01.DALoyalty, 1e-6f, "R01.d_a_loyalty 与源一致");
            Assert.AreEqual(-0.05f, r01.DAOrg, 1e-6f, "R01.d_a_org 与源一致");
            Assert.AreEqual(0.15f, r01.DBGrievance, 1e-6f, "R01.d_b_grievance 与源一致");
            Assert.AreEqual(-0.1f, r01.DBLoyalty, 1e-6f, "R01.d_b_loyalty 与源一致");
            Assert.AreEqual(0.1f, r01.DBOrg, 1e-6f, "R01.d_b_org 与源一致");
            Assert.IsTrue(r01.Aftermath.Length > 0 && r01.Aftermath != "无", "R01（Covert）aftermath 非「无」");

            // 抽查 2：贸易首行 T01。
            var t01 = _config.Get<ColonyTradesConfigRow>("T01");
            Assert.AreEqual(1, t01.EventId, "T01.EventId 与源 event_id 一致");
            Assert.AreEqual("计百川（会计）", t01.NpcA, "T01.NpcA 与源 CSV 逐字一致");
            Assert.AreEqual("屠永年（冶炼工）", t01.NpcB, "T01.NpcB 与源 CSV 逐字一致");
            Assert.AreEqual("铁砧营、铁砧城", t01.Places, "T01.Places 与源 CSV 逐字一致");
            Assert.AreEqual("库存漂移", t01.EventKind, "T01.EventKind 与源 CSV 逐字一致");
            Assert.AreEqual("铁砧城：合金锭 +15%", t01.PriceImpact, "T01.price_impact 与源 CSV 逐字一致");
            Assert.AreEqual("铁砧营：合金锭库存 -10%", t01.StockImpact, "T01.stock_impact 与源 CSV 逐字一致");
            Assert.AreEqual("Covert", t01.Visibility, "T01.Visibility 与源 CSV 逐字一致");

            // 抽查 3：人生节点首行 L001 与末行 L190。
            var l001 = _config.Get<ColonyLifeConfigRow>("L001");
            Assert.AreEqual("郑大镐", l001.Name, "L001.Name 与源 CSV 逐字一致");
            Assert.AreEqual("郑大镐（矿工）", l001.Npc, "L001.Npc 与源 CSV 逐字一致");
            Assert.AreEqual("旧伤复发", l001.EventType, "L001.EventType 与源 CSV 逐字一致");
            Assert.AreEqual("第 600-750 天，且其 grievance>0.5 时优先", l001.Trigger, "L001.Trigger 与源 CSV 逐字一致");
            Assert.AreEqual("want", l001.RewriteField, "L001.rewrite_field 与源 CSV 逐字一致");
            Assert.LessOrEqual(l001.Text.Length, 40, "L001.text ≤40 字（check_t9 契约）");
            var l190 = _config.Get<ColonyLifeConfigRow>("L190");
            Assert.AreEqual("东郭息", l190.Name, "L190.Name 与源 CSV 逐字一致");
            Assert.AreEqual("恩人归来", l190.EventType, "L190.EventType 与源 CSV 逐字一致");

            // 内容包投影行数一致（三引擎注入路径同源）。
            Assert.AreEqual(40, _data.Content.Relations.Count, "投影：关系网 40 条");
            Assert.AreEqual(20, _data.Content.Trades.Count, "投影：贸易 20 条");
            Assert.AreEqual(190, _data.Content.Life.Count, "投影：人生节点 190 条");
        }

        // ────────────────────────────── E2 ──────────────────────────────

        /// <summary>
        /// E2：推进天数 → 关系事件按 visibility 闸门触发（Covert 每日 1 条 / Personal 需在场或线人 /
        /// Public 直接入日志）；触发事件双方状态变化与 CSV 数值一致；Covert 进 Truth 不进 PlayerFeed；
        /// aftermath 进延迟队列（30 天后回账）。
        /// </summary>
        [Test]
        public void E2_RelationEngineFires()
        {
            var w = BuildContentWorld();
            Assert.IsNotNull(w.Content, "数据驱动建局必须注入内容引擎挂点");
            Assert.IsTrue(w.AutonomyEnabled, "AutonomyEnabled 默认开——既有开关语义不变（对照断言）");

            var byId = _data.Content.Relations.ToDictionary(e => e.Id);
            var r01 = byId["R01"];
            var a = w.Named.First(n => n.Name == ColonyContentLookup.NameOf(r01.NpcA));
            var b = w.Named.First(n => n.Name == ColonyContentLookup.NameOf(r01.NpcB));
            var beforeA = (a.Grievance, a.Loyalty, a.Organization);
            var beforeB = (b.Grievance, b.Loyalty, b.Organization);

            // 第 1 天：Public 直接过 + Covert 首条（R01）；Personal 全部被在场/线人闸门拦住。
            w.Content.SettleDaily(w);
            Assert.AreEqual(7, w.Content.Relations.FiredThisDay.Count,
                "第 1 天必须恰好触发 6 条 Public + 第一个 Covert（R01）");
            Assert.Contains("R01", w.Content.Relations.FiredThisDay, "第一个 Covert 必须是 Id 序最前的 R01");
            CollectionAssert.DoesNotContain(w.Content.Relations.FiredThisDay, "R02", "Personal 无在场/线人不得触发");

            // 双方状态变化与 CSV 数值一致（聚合当天全部已触发事件，防同日多事件混淆）。
            AssertClampedDelta(a, ExpectedDeltas(a, w.Content.Relations.FiredThisDay, byId), beforeA);
            AssertClampedDelta(b, ExpectedDeltas(b, w.Content.Relations.FiredThisDay, byId), beforeB);

            // Covert 进 Truth 不进 PlayerFeed（§4 视野纪律）。
            string covertText = $"{ColonyRelationEngine.LogMarker} {r01.Event}";
            Assert.IsTrue(w.Log.All.Any(e => e.Text == covertText && e.Vis == EventVisibility.Covert),
                "R01（Covert）必须以 Covert 可见性进 Truth 日志");
            var feed = w.Log.PlayerFeed(1, w.Regions[0].Name, new HashSet<string>());
            Assert.IsFalse(feed.Any(e => e.Text == covertText), "Covert 事件永不进 PlayerFeed");

            // 第 2 天：Covert 限额复位（R03 触发）；玩家在场 → R02 触发；线人覆盖 → R05 触发。
            // （在 AdvanceDays 之前做：AdvanceDays 会按日把剩余 Covert 逐日耗尽，污染第 2 天断言。）
            var r02 = byId["R02"];
            var r05 = byId["R05"];
            var present = w.Named.First(n => n.Name == ColonyContentLookup.NameOf(r02.NpcA));
            var informant = w.Named.First(n => n.Name == ColonyContentLookup.NameOf(r05.NpcA));
            w.Governor.PresentRegion = w.RegionOf(present);
            w.Governor.Informants.Add(w.RegionOf(informant));
            w.Content.SettleDaily(w);
            Assert.Contains("R03", w.Content.Relations.FiredThisDay, "Covert 每日 1 条限额按日复位（第 2 天轮到 R03）");
            Assert.Contains("R02", w.Content.Relations.FiredThisDay, "玩家在场（PresentRegion）→ Personal 触发");
            Assert.Contains("R05", w.Content.Relations.FiredThisDay, "线人覆盖（Informants）→ Personal 触发");
            Assert.AreEqual(1, w.Content.Relations.FiredThisDay.Count(id => byId[id].Visibility == "Covert"),
                "Covert 每日最多 1 条（第 2 天仍只触发 1 条 Covert）");
            Assert.IsTrue(w.Log.All.Any(e => e.Text.Contains(r02.Event) && e.Vis == EventVisibility.Personal),
                "Personal 事件以 Personal 可见性入日志");

            // aftermath 非「无」进延迟队列（默认 30 天）→ 30 天后回账进 Truth。
            Assert.Greater(r01.Aftermath.Length, 0, "前提：R01 aftermath 非空");
            w.AdvanceDays(ColonyRelationEngine.AftermathDelayDays + 1);
            Assert.IsTrue(w.Log.All.Any(e => e.Text.Contains(r01.Aftermath)),
                "R01 的 aftermath 必须在 fire 日 +30 后经延迟队列回账进 Truth");
        }

        // ────────────────────────────── E3 ──────────────────────────────

        /// <summary>
        /// E3：贸易事件触发 → MarketState 对应节点价格/库存按 impact 变化（5% 倍数精确断言）。
        /// 锚点 T03（触发日 15）：残垣城 泵阀 +10%（价）/-20%（库存）——残垣城是建局市场节点二。
        /// </summary>
        [Test]
        public void E3_TradeEngineDrifts()
        {
            var w = BuildContentWorld();
            MarketNode yuanheng = w.Market.Nodes.First(n => n.Name == "残垣城");
            MarketNode shuanghe = w.Market.Nodes.First(n => n.Name == "双河城");
            float priceBefore = yuanheng.BasePrice;
            float stockBefore = yuanheng.Stock;
            float shuanghePriceBefore = shuanghe.BasePrice;
            float shuangheStockBefore = shuanghe.Stock;

            w.Day = 15;   // T01@5/T02@10/T03@15 的首个满足日（前两者不涉及残垣城——锚点干净）
            w.Content.SettleDaily(w);
            CollectionAssert.AreEquivalent(new[] { "T01", "T02", "T03" }, w.Content.Trades.FiredThisDay,
                "Day=15 必须触发 T01/T02/T03（触发日 = EventId×5）");
            CollectionAssert.DoesNotContain(w.Content.Trades.FiredThisDay, "T04", "T04（触发日 20）不得提前触发");

            Assert.AreEqual(priceBefore * 1.10f, yuanheng.BasePrice, 1e-3f,
                "T03 价格影响「残垣城：泵阀 +10%」必须按 5% 倍数精确落地（BasePrice ×1.10）");
            Assert.AreEqual(stockBefore * 0.80f, yuanheng.Stock, 1e-2f,
                "T03 库存影响「残垣城：泵阀好件库存 -20%」必须精确落地（Stock ×0.80）");
            Assert.AreEqual(shuanghePriceBefore, shuanghe.BasePrice, 1e-5f,
                "T01-T03 不涉及双河城，其行情不得漂移");
            Assert.AreEqual(shuangheStockBefore, shuanghe.Stock, 1e-3f, "T01-T03 不涉及双河城库存");

            // 已触发不重复：同日再结算，市场不再漂移。
            w.Content.SettleDaily(w);
            Assert.AreEqual(priceBefore * 1.10f, yuanheng.BasePrice, 1e-3f, "已触发贸易事件不得二次生效");
            Assert.AreEqual(0, w.Content.Trades.FiredThisDay.Count, "重复结算不产生新触发");

            // 事件留痕：Truth 日志含 T03 文本，visibility 原样（T03=Personal）。
            var t03 = _data.Content.Trades.First(e => e.Id == "T03");
            Assert.IsTrue(w.Log.All.Any(e => e.Text.Contains(t03.Event) && e.Kind == EventKind.Market
                && e.Vis == EventVisibility.Personal), "T03 必须以 Market/Personal 入 Truth 日志");
        }

        // ────────────────────────────── E4 ──────────────────────────────

        /// <summary>
        /// E4：人生节点命中 → rewrite 真实改写 want/conflict/secret 且真实旧值出现在事件文本；
        /// settlement_effect 落到对应聚落（平均参数段 → 聚落聚合值）。
        /// 锚点：L013 桑小满（第 60-150 天，全表唯一活跃日）改写 secret；L008 雷千钧（第 981 天
        /// 全表唯一活跃日）「炉区平均忠诚 +0.05」落到其聚落。
        /// </summary>
        [Test]
        public void E4_LifeRewrite()
        {
            var w = BuildContentWorld();
            var l013 = _data.Content.Life.First(e => e.Id == "L013");
            var sang = w.Named.First(n => n.Name == l013.Name);
            string oldSecret = sang.Secret;
            Assert.AreEqual(0, w.Content.Life.Fired.Count, "前提：第 60 天前人生节点零触发");

            w.Day = 60;
            w.Content.SettleDaily(w);
            CollectionAssert.Contains(w.Content.Life.FiredThisDay, "L013", "第 60 天必须命中 L013（全表唯一活跃日）");
            Assert.AreEqual(1, w.Content.Life.FiredThisDay.Count, "L013 日是全表唯一活跃日（确定性锚点）");

            // rewrite 真实改写 secret + 真实旧值留痕进事件文本。
            string newSecret = ColonyLifeEngine.RewriteNewValue(l013.RewriteText);
            Assert.IsNotNull(newSecret, "rewrite_text 必须可解析出「改为」的新值");
            Assert.AreEqual(newSecret, sang.Secret, "L013 命中后桑小满的 secret 必须被真实改写");
            Assert.AreNotEqual(oldSecret, sang.Secret, "改写必须发生（旧 secret ≠ 新 secret）");
            string logLine = w.Log.All.FirstOrDefault(e => e.Text.Contains(l013.Text))?.Text;
            Assert.IsNotNull(logLine, "L013 事件文本必须入 Truth 日志");
            StringAssert.Contains(oldSecret, logLine, "真实旧值必须留痕进事件文本（简报原文）");
            StringAssert.Contains("L013", logLine, "事件文本必须可追溯到人生节点 Id");
            Assert.AreEqual(EventVisibility.Personal,
                w.Log.All.First(e => e.Text.Contains(l013.Text)).Vis, "人生节点入 Log（Personal，简报原文）");

            // settlement_effect：L008「炉区平均忠诚 +0.05」落到雷千钧所在聚落（地球出身 → 青石城）。
            var l008 = _data.Content.Life.First(e => e.Id == "L008");
            var lei = w.Named.First(n => n.Name == l008.Name);
            Settlement leiSettlement = w.SettlementOf(w.RegionOf(lei));
            Assert.AreEqual("青石城", leiSettlement.Name, "前提：雷千钧（地球出身）按 DefaultRegionOf 落青石城");
            float loyaltyBefore = leiSettlement.AvgLoyalty;

            w.Day = 981;
            w.Content.SettleDaily(w);
            CollectionAssert.Contains(w.Content.Life.FiredThisDay, "L008", "第 981 天必须命中 L008（全表唯一活跃日）");
            Assert.AreEqual(loyaltyBefore + 0.05f, leiSettlement.AvgLoyalty, 1e-5f,
                "L008 settlement_effect「平均忠诚 +0.05」必须落到对应聚落");
            Assert.AreEqual(1, w.Content.Life.FiredThisDay.Count, "第 981 天同样是全表唯一活跃日");
        }

        // ────────────────────────────── E5 ──────────────────────────────

        /// <summary>
        /// E5：存档（Capture→codec 往返→Restore）→ 续推 → 已触发内容不重复触发；
        /// 新内容照常触发（引擎存活），延迟后果跨档回账。
        /// </summary>
        [Test]
        public void E5_NoReplayAfterRestore()
        {
            var w = BuildContentWorld();
            w.AdvanceDays(25);   // 走真实 GameWorld.DailyTick 挂点：关系 25 条（6 Public + 19 Covert）、贸易 T01-T05

            Assert.GreaterOrEqual(w.Content.Relations.Fired.Count, 25, "前提：25 天内关系事件已批量触发");
            Assert.GreaterOrEqual(w.Content.Trades.Fired.Count, 5, "前提：T01-T05 已触发（触发日 ≤25）");
            Assert.AreEqual(0, w.Content.Life.Fired.Count, "前提：人生节点最早 60 天，尚无触发");
            string r01Text = $"{ColonyRelationEngine.LogMarker} {_data.Content.Relations.First(e => e.Id == "R01").Event}";
            string t01Text = $"{ColonyTradeEngine.LogMarker} {_data.Content.Trades.First(e => e.Id == "T01").Event}";
            Assert.IsTrue(w.Log.All.Any(e => e.Text == r01Text), "前提：R01 在读档前已入 Truth");

            // Capture → codec JSON 往返 → Restore（S5 同款 payload 通道语义）。
            ColonySaveData captured = ColonySaveCapture.Capture(w);
            ColonySaveData roundTrip = ColonySaveCodec.FromJToken(ColonySaveCodec.ToJToken(captured));
            Assert.AreEqual(captured.FiredRelations.Count, roundTrip.FiredRelations.Count, "codec 往返：关系进度不丢");
            Assert.AreEqual(captured.FiredTrades.Count, roundTrip.FiredTrades.Count, "codec 往返：贸易进度不丢");
            GameWorld restored = ColonySaveRestore.Restore(roundTrip);

            // 读档世界：进度集合逐集合一致（Restore 不重放的前提）。
            Assert.IsNotNull(restored.Content, "有进度的读档必须重建内容引擎聚合");
            CollectionAssert.AreEquivalent(w.Content.Relations.Fired, restored.Content.Relations.Fired,
                "读档：关系已触发集合必须与存档一致");
            CollectionAssert.AreEquivalent(w.Content.Trades.Fired, restored.Content.Trades.Fired,
                "读档：贸易已触发集合必须与存档一致");
            Assert.AreEqual(0, restored.Content.Life.Fired.Count, "读档：人生节点进度为空");

            // 内容回填（内容是建局输入不入档）后续推 20 天。
            restored.Content.Attach(_data.Content);
            restored.AdvanceDays(20);

            // 已触发内容不重放：读档日志从零重建，重放会让 R01/T01 文本再次出现——必须为零。
            Assert.IsFalse(restored.Log.All.Any(e => e.Text == r01Text), "已触发关系事件在读档后不得重放");
            Assert.IsFalse(restored.Log.All.Any(e => e.Text == t01Text), "已触发贸易事件在读档后不得重放");
            CollectionAssert.AreEquivalent(w.Content.Relations.Fired, restored.Content.Relations.Fired,
                "续推 20 天：关系已触发集合不得增长（Covert 已耗尽 / Personal 无在场 / Public 已全部入日志）");
            Assert.GreaterOrEqual(restored.Content.Trades.Fired.Count, 9,
                "续推 20 天（Day 26→45）：T06-T09 必须照常触发——引擎在读档世界存活");

            // 跨档延迟后果：R01 aftermath（dueDay 31）在读档后照常回账（延迟队列随档）。
            string r01Aftermath = _data.Content.Relations.First(e => e.Id == "R01").Aftermath;
            Assert.IsTrue(restored.Log.All.Any(e => e.Text.Contains(r01Aftermath)),
                "读档前入队的延迟后果必须在读档后照常回账");
        }

        // ────────────────────────────── E6 ──────────────────────────────

        /// <summary>E6：含三引擎的 DailyTick×30 天 &lt; 80ms（自主行为 50ms 红线之上留余量）。</summary>
        [Test]
        public void E6_PerfBound()
        {
            var w = BuildContentWorld();
            var sw = Stopwatch.StartNew();
            w.AdvanceDays(30);
            sw.Stop();

            Assert.Less(sw.ElapsedMilliseconds, 80L,
                $"含三引擎的 DailyTick×30 天必须 <80ms（实测 {sw.ElapsedMilliseconds}ms）");
            Assert.GreaterOrEqual(w.Content.Relations.Fired.Count, 6,
                "被测窗口内内容引擎必须真的在跑（Public 6 条 + Covert 19 条）");
            Assert.GreaterOrEqual(w.Content.Trades.Fired.Count, 6, "贸易事件 T01-T06（触发日 ≤30）必须在窗口内触发");
        }
    }
}
