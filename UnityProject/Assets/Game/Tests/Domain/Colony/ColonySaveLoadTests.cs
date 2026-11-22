// ColonySaveLoadTests.cs —— TASK-A-COL-03：Colony 存档/读档 S1-S5。
// S1 状态等价（45 天编排后 Capture→Restore 逐字段一致）· S2 确定性档案（§4.4 读档后性格不变）
// · S3 世界继续（读档后事件流非空 / 组织者仍在且不重复具名化 / 轨迹继续）· S4 槽位足迹（只带已实例化
// slot，体积 <1MB）· S5 SaveSystem payload 通道往返。
// 建局一律走 A-COL-02 的 ColonyConfigAdapter 数据驱动路径（五张 GLM 表资产）；SaveSystem 桥在测试侧
// （ColonySaveBridge），Game.Domain.Colony 保持零 ZFrame.Data 依赖。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Domain.Colony;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Core;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;
using ZFrame.Data.Save;

namespace Game.Domain.Tests
{
    public sealed class ColonySaveLoadTests
    {
        // 与 V1-V7/D1-D4 同种子：随机流确定性可复现。
        private const ulong Seed = 20261122UL;

        /// <summary>S3 的阈值 A 舞台：places.csv 人口第二「残垣城」（D2 点名）。</summary>
        private const string RepressedRegion = "残垣城";

        private static ConfigService _config;
        private static ColonyWorldData _data;

        /// <summary>SaveSystem 注入的临时根——绝不碰真实 persistentDataPath（SaveSystemTests 同款纪律）。</summary>
        private string _saveRoot;

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

        [SetUp]
        public void SetUp()
        {
            _saveRoot = Path.Combine(Path.GetTempPath(), "zframe_colony_save_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            // EventBus 是进程级静态：清掉可能残留的排队事件（A-COL-01/02 fixture 纪律）。
            EventBus.FlushQueued();
            if (Directory.Exists(_saveRoot)) Directory.Delete(_saveRoot, recursive: true);
        }

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（路径由产品代码推导，不手写）。</summary>
        private static void LoadTable<TRow>(ConfigService config, string stem) where TRow : ConfigRow
        {
            var csvPath = $"{CsvImporter.CsvRoot}/{stem}.csv";
            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            Assert.IsNotNull(table, $"殖民地表资产缺失：{assetPath}");
            config.Load(table);
        }

        private static GameWorld BuildWorld() => ColonyDataLoader.BuildWorld(Seed, _data);

        // ────────────────────────────── 45 天编排 ──────────────────────────────

        /// <summary>编排产物引用（断言锚点）。</summary>
        private sealed class Orchestration
        {
            public GameWorld World;
            public NamedNpc Merchant;            // 商人抗议（简报点名编排）
            public NamedNpc Violated;            // 违背契约（简报点名编排）
            public Settlement Repressed;         // 镇压×2（简报点名编排）
            public List<(int SlotId, NpcProfile Profile)> Profiled = new();
            public int FreshSlotId;              // 存档前未实例化的槽（S2 新增实例化对照）
        }

        /// <summary>
        /// 数据驱动建局 + 简报点名编排（违背契约 / 镇压×2 / 商人抗议）+ 分两段推进共 45 天，
        /// 并把延迟后果（暗药爆雷 due day 51）留在队列里跨过存档点。
        /// </summary>
        private static Orchestration BuildOrchestrated45()
        {
            var w = BuildWorld();
            var orc = new Orchestration { World = w };

            // ① 商人抗议（V2/V7 同款）：挖坑政策给商人所在地区 → 文化加权怨气 → 强度 0.6 → 涨价抗议入日志
            orc.Merchant = w.Named.First(n => n.IsMerchant);
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), w.RegionOf(orc.Merchant), execution: 0.4f);
            orc.Merchant.Grievance = Math.Max(orc.Merchant.Grievance, 0.6f);
            orc.Merchant.Protest = ProtestRouter.ChooseProtest(orc.Merchant, orc.Merchant.Grievance);
            Assert.IsNotNull(orc.Merchant.Protest, "前提：商人在酝酿伪装抗议（D3 已证此种子确定性成立）");
            (float multiplier, string reason) = ProtestEffects.Price(orc.Merchant, w.Regions[0].Reputation());
            w.Log.Add(w.Day, EventKind.Protest, w.RegionOf(orc.Merchant),
                $"商人 {orc.Merchant.Name} 的货价 ×{multiplier:0.00}（{reason}）", EventVisibility.Public);

            // ② 违背契约（V4 同款）：质疑期开局，45 天推进里滑入沉默期
            orc.Violated = w.Named.First(n => n.Job == "矿工");   // npcs.csv 卡 0 郑大镐（D1 点名）
            orc.Violated.Contract.OnViolation();
            Assert.AreEqual(Stage.Doubt, orc.Violated.Contract.Stage, "前提：违背 Wont 后进质疑期");

            // ③ 镇压×2（V5 同款）：残垣城组织度越过阈值 A——S3 的组织者由此从参数里长出来
            orc.Repressed = w.SettlementOf(RepressedRegion);
            Assert.IsNotNull(orc.Repressed, $"places.csv 建局必须含 {RepressedRegion} 聚落（D2 点名人口第二）");
            orc.Repressed.Apply(Decision.Harsh);
            orc.Repressed.Apply(Decision.Harsh);
            w.Governor.RepressionCount = 2;   // 恐怖统治计入面具动力学（简报 governor.repressionCount）
            // 镇压同时是地区声望输入：有目击者的事迹进实证、无目击者的进传闻池（声望认知 §2.2）
            w.Regions[1].AddDeed(new LocalDeed
            {
                Type = DeedType.Suppress, Magnitude = 1.5f, WitnessCount = 10, IsLegendary = false,
            });
            w.Regions[0].AddDeed(new LocalDeed
            {
                Type = DeedType.Rescue, Magnitude = 0.8f, WitnessCount = 0, IsLegendary = false,
            });

            // ④ 玩家深聊过的背景层（§4.4：存档只带已实例化 slot + 档案覆盖值）
            foreach (int id in new[] { 7, 42, 1337, 98000 })
                orc.Profiled.Add((id, w.Background.EnsureProfile(id)));
            orc.FreshSlotId = 500;   // 全程不实例化——S2 的「读档后第一次接触」对照

            // ⑤ 分两段推进共 45 天；暗药政策在第 21 天颁布，其延迟后果（due day 51）跨过存档点留在队列
            w.AdvanceDays(20);       // day 21
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsDarkMedicine), w.Regions[2].Name, execution: 0.9f);
            w.AdvanceDays(25);       // day 46
            Assert.Greater(w.Day, 45u, "前提：编排共推进 45 天");

            // 前提自证：镇压×2 必须把组织度推过阈值 A 并在第 7 天的周结里涌现出组织者
            Assert.Greater(orc.Repressed.Organization, Settlement.THRESHOLD_A, "前提：残垣城已过阈值 A");
            Assert.IsNotEmpty(orc.Repressed.NamedNpcs, "前提：组织者已在存档前具名化");
            return orc;
        }

        // ────────────────────── 字段级等价（比较基建）──────────────────────

        /// <summary>
        /// 比较前归一：关系边在 DTO 里是集合语义——重建图的 AllEdges 枚举序是字典迭代序，
        /// 与存档顺序可能合法地不同；按 (min(A,B),max(A,B)) 排序后再比，其余字段仍按列表序严格比对。
        /// </summary>
        private static JObject NormalizedForCompare(ColonySaveData d)
        {
            var root = (JObject)ColonySaveCodec.ToJToken(d);
            root["edges"] = new JArray(((JArray)root["edges"])
                .OrderBy(e => Math.Min(((JValue)e["a"]).Value<int>(), ((JValue)e["b"]).Value<int>()))
                .ThenBy(e => Math.Max(((JValue)e["a"]).Value<int>(), ((JValue)e["b"]).Value<int>()))
                .ToList());
            return root;
        }

        /// <summary>两棵 DTO 树的第一个差异点（带路径与两侧值）——失败信息可直接定位字段。</summary>
        private static string FirstDiff(JToken a, JToken b, string path)
        {
            if (a.Type != b.Type) return $"{path}: 类型 {a.Type} ≠ {b.Type}";
            if (a is JObject ao && b is JObject bo)
            {
                foreach (var p in ao.Properties())
                {
                    if (bo[p.Name] == null) return $"{path}.{p.Name}: 读档侧缺字段";
                    string diff = FirstDiff(p.Value, bo[p.Name], $"{path}.{p.Name}");
                    if (diff != null) return diff;
                }
                foreach (var p in bo.Properties())
                    if (ao[p.Name] == null) return $"{path}.{p.Name}: 读档侧多出字段";
                return null;
            }
            if (a is JArray aa && b is JArray ba)
            {
                if (aa.Count != ba.Count) return $"{path}: 元素数 {aa.Count} ≠ {ba.Count}";
                for (int i = 0; i < aa.Count; i++)
                {
                    string diff = FirstDiff(aa[i], ba[i], $"{path}[{i}]");
                    if (diff != null) return diff;
                }
                return null;
            }
            if (a is JValue av && b is JValue bv)
                return JToken.DeepEquals(av, bv) ? null : $"{path}: {av.Value} ≠ {bv.Value}";
            return JToken.DeepEquals(a, b) ? null : $"{path}: 结构不一致";
        }

        private static void AssertFieldLevelEqual(ColonySaveData expected, ColonySaveData actual, string because)
        {
            JObject e = NormalizedForCompare(expected);
            JObject a = NormalizedForCompare(actual);
            string diff = FirstDiff(e, a, "$");
            Assert.IsNull(diff, $"字段级等价失败（{because}）：{diff}");
        }

        private static void AssertProfileEqual(NpcProfile expected, NpcProfile actual, string because)
        {
            Assert.AreEqual(expected.Name, actual.Name, $"{because}: Name");
            Assert.AreEqual(expected.Job, actual.Job, $"{because}: Job");
            Assert.AreEqual(expected.Origin, actual.Origin, $"{because}: Origin");
            Assert.AreEqual(expected.BaseLoyalty, actual.BaseLoyalty, $"{because}: BaseLoyalty");
            Assert.AreEqual(expected.GrievanceBias, actual.GrievanceBias, $"{because}: GrievanceBias");
            Assert.AreEqual(expected.SpeechTrait, actual.SpeechTrait, $"{because}: SpeechTrait");
        }

        // ────────────────────────────── S1-S5 ──────────────────────────────

        /// <summary>
        /// S1 状态等价：建局编排推进 45 天（违背契约/镇压×2/商人抗议）→ Capture → Restore →
        /// 逐字段断言具名 NPC（忠诚/怨气/契约阶段/配合度/面具）、聚落四参数、地区声望输入、governor 信誉，
        /// 最后全量 DTO 字段级等价。
        /// </summary>
        [Test]
        public void S1_StateEquivalence()
        {
            var orc = BuildOrchestrated45();
            var before = ColonySaveCapture.Capture(orc.World);
            var restored = ColonySaveRestore.Restore(before);
            var after = ColonySaveCapture.Capture(restored);

            // ── 具名 NPC 关键字段（简报点名五项）──
            NamedNpc src = orc.Violated;
            NamedNpc dst = restored.NamedById[src.Id];
            Assert.AreEqual(Stage.Silent, src.Contract.Stage, "前提自证：45 天推进后违背契约者已入沉默期");
            Assert.AreEqual(src.Loyalty, dst.Loyalty, "忠诚必须逐字段一致");
            Assert.AreEqual(src.Grievance, dst.Grievance, "怨气必须逐字段一致");
            Assert.AreEqual(src.Contract.Stage, dst.Contract.Stage, "契约阶段必须一致");
            Assert.AreEqual(src.Contract.Cooperation, dst.Contract.Cooperation, "配合度必须一致");
            Assert.AreEqual(src.MaskStrength, dst.MaskStrength, "面具强度必须一致");

            NamedNpc mSrc = orc.Merchant;
            NamedNpc mDst = restored.NamedById[mSrc.Id];
            Assert.AreEqual(mSrc.Grievance, mDst.Grievance, "商人怨气必须一致");
            Assert.IsNotNull(mDst.Protest, "商人的抗议状态必须随档");
            Assert.AreEqual(mSrc.Protest.Level, mDst.Protest.Level, "抗议强度必须一致");
            Assert.IsTrue(mDst.Protest.Forms.SetEquals(mSrc.Protest.Forms), "抗议形式集合必须一致");
            Assert.AreEqual(mSrc.Protest.IsCovert, mDst.Protest.IsCovert, "抗议伪装标记必须一致");

            NamedNpc oSrc = orc.World.NamedById[orc.Repressed.NamedNpcs[0]];
            NamedNpc oDst = restored.NamedById[orc.Repressed.NamedNpcs[0]];
            Assert.AreEqual("Organizer", oDst.Role, "组织者必须随档");
            Assert.AreEqual(oSrc.Organization, oDst.Organization, "组织者的组织度（继承聚落涌现值）必须一致");

            // ── 聚落四参数（忠诚/怨气/组织度/不可逆地板）──
            Assert.AreEqual(orc.Repressed.AvgLoyalty, restored.SettlementOf(RepressedRegion).AvgLoyalty, "聚落忠诚必须一致");
            Assert.AreEqual(orc.Repressed.AvgGrievance, restored.SettlementOf(RepressedRegion).AvgGrievance, "聚落怨气必须一致");
            Assert.AreEqual(orc.Repressed.Organization, restored.SettlementOf(RepressedRegion).Organization, "聚落组织度必须一致");
            Assert.AreEqual(orc.Repressed.GrievanceFloor, restored.SettlementOf(RepressedRegion).GrievanceFloor,
                "不可逆怨气地板必须一致（仇恨不可逆在存档里不能被洗掉）");

            // ── 地区声望输入（简报点名 policyExecution/policyBenefit/deeds/rumorPool）──
            for (int i = 0; i < orc.World.Regions.Length; i++)
            {
                RegionState rSrc = orc.World.Regions[i];
                RegionState rDst = restored.Regions[i];
                Assert.AreEqual(rSrc.PolicyExecution, rDst.PolicyExecution, $"地区[{i}] 政策执行度必须一致");
                Assert.AreEqual(rSrc.PolicyBenefit, rDst.PolicyBenefit, $"地区[{i}] 政策有利度必须一致");
                Assert.AreEqual(rSrc.Reputation(), rDst.Reputation(), $"地区[{i}] 声望值必须一致（deeds/rumors 随档）");
                Assert.AreEqual(before.Regions[i].Deeds.Count, after.Regions[i].Deeds.Count, $"地区[{i}] 实证事迹数必须一致");
                Assert.AreEqual(before.Regions[i].Rumors.Count, after.Regions[i].Rumors.Count, $"地区[{i}] 传闻池条数必须一致");
            }
            Assert.Greater(before.Regions[0].PolicyExecution, 0f, "前提自证：挖坑政策已写进地区声望输入");

            // ── governor 信誉（简报点名 credibility；产值/贸易/军事/承诺一并断言）──
            Assert.AreEqual(orc.World.Governor.Prestige.Credibility, restored.Governor.Prestige.Credibility, "信誉必须一致");
            Assert.AreEqual(orc.World.Governor.Prestige.Production, restored.Governor.Prestige.Production, "产值必须一致");
            Assert.AreEqual(orc.World.Governor.Prestige.TradeValue, restored.Governor.Prestige.TradeValue, "贸易值必须一致");
            Assert.AreEqual(orc.World.Governor.Prestige.PromisesKept, restored.Governor.Prestige.PromisesKept, "守诺计数必须一致");
            Assert.AreEqual(orc.World.Governor.RepressionCount, restored.Governor.RepressionCount, "镇压计数必须一致");
            // ── 情报点（TASK-A-COL-07：存档不丢点数；编排跨第 30 天月结，月度维持费已 +1）──
            Assert.AreEqual(orc.World.Governor.IntelPoints, restored.Governor.IntelPoints, "情报点必须一致（TASK-A-COL-07）");
            Assert.AreEqual(3, restored.Governor.IntelPoints, "前提自证：初始 2 + 第 30 天月结维持费 1 = 3");

            // ── 全量 DTO 字段级等价（覆盖简报清单的每一节）──
            AssertFieldLevelEqual(before, after, "S1 Capture→Restore→Capture");
        }

        /// <summary>
        /// S2 确定性档案（§4.4「读档后他性格变了」禁止发生）：
        /// ① 读档后同一 slot 的档案逐字段一致；② 存档覆盖值 == 原种子规范重生成（种子确定性兜底）；
        /// ③ 存档前未实例化的 slot，读档后首次实例化也落在规范种子路径上（流重放保槽位采样）；
        /// ④ 已实例化标记不丢不增。
        /// </summary>
        [Test]
        public void S2_DeterministicProfile()
        {
            var orc = BuildOrchestrated45();
            (int slotX, NpcProfile profileBefore) = orc.Profiled[1];   // slot 42：存档前已实例化
            int freshSlot = orc.FreshSlotId;

            var before = ColonySaveCapture.Capture(orc.World);
            var restored = ColonySaveRestore.Restore(before);

            // ④ 已实例化标记随档恢复（读档即知谁被深聊过——不是靠触碰时重新生成）
            Assert.AreEqual(before.ProfiledSlots.Count, restored.Background.ProfiledCount,
                "已实例化标记必须随档恢复");
            Assert.AreEqual(orc.World.Background.Count, restored.Background.Count, "背景层规模必须随档");

            // ① 读档后同一 slot：档案逐字段一致
            NpcProfile profileAfter = restored.Background.EnsureProfile(slotX);
            AssertProfileEqual(profileBefore, profileAfter, "读档后 EnsureProfile 同一 slot");

            // ② 种子确定性兜底：存档覆盖值 == 原种子规范重生成（全新建局同 slot 的档案）
            NpcProfile canonical = BuildWorld().Background.EnsureProfile(slotX);
            AssertProfileEqual(profileBefore, canonical, "存档覆盖值对照原种子重生成");

            // ③ 存档前未实例化的 slot：读档后首次实例化 == 规范种子路径
            NpcProfile freshAfter = restored.Background.EnsureProfile(freshSlot);
            NpcProfile freshCanonical = BuildWorld().Background.EnsureProfile(freshSlot);
            AssertProfileEqual(freshCanonical, freshAfter, "读档后首次实例化对照规范种子路径");

            // ④ 新实例化恰好 +1
            Assert.AreEqual(before.ProfiledSlots.Count + 1, restored.Background.ProfiledCount,
                "读档后新实例化恰好增加一个已实例化槽");
        }

        /// <summary>
        /// S3 世界继续：restore 后 AdvanceDays(7) → 事件流非空、桥把新事件发布到 EventBus（游标重置）；
        /// 组织度/怨气沿既有轨迹继续；残垣城已过阈值 A——restore 后组织者仍存在且不重复具名化。
        /// </summary>
        [Test]
        public void S3_WorldContinues()
        {
            var orc = BuildOrchestrated45();
            var before = ColonySaveCapture.Capture(orc.World);
            var restored = ColonySaveRestore.Restore(before);

            // 读档世界从干净日志开始——之后的事件流全部是读档后新事件
            Assert.IsEmpty(restored.Log.All, "读档重建的日志从零开始（历史事件不入档，ColonyEventPump 游标重置）");
            Assert.AreEqual(before.Day, restored.Day, "读档世界的日期与存档一致");

            // 组织者随档：仍在、身份集合与存档前一致（restore 本身不重复具名化）
            int organizerId = orc.Repressed.NamedNpcs[0];
            Assert.IsTrue(restored.NamedById.ContainsKey(organizerId), "restore 后组织者仍存在");
            Assert.AreEqual("Organizer", restored.NamedById[organizerId].Role, "组织者角色随档");
            List<int> idsBefore = orc.World.Named.Where(n => n.Role == "Organizer").Select(n => n.Id).Distinct().ToList();
            List<int> idsAfter = restored.Named.Where(n => n.Role == "Organizer").Select(n => n.Id).Distinct().ToList();
            CollectionAssert.AreEquivalent(idsBefore, idsAfter, "restore 不产生新的组织者身份");

            // 读档后推进 7 天：事件流非空 + 桥发布（新 pump 游标从 0 起算）
            var pump = new ColonyEventPump();
            var received = new List<ColonyEventDto>();
            restored.AdvanceDays(7);
            using (EventBus.Subscribe<ColonyEventDto>(dto => received.Add(dto)))
            {
                pump.Pump(restored);
            }
            Assert.IsNotEmpty(restored.Log.All, "restore 后 AdvanceDays(7) 事件流非空（世界没有停摆）");
            Assert.Greater(received.Count, 0, "桥必须把读档后的新事件发布到 ZFrame EventBus");
            Assert.AreEqual(restored.Log.All.Count, pump.Consumed, "游标重置后与读档后日志条数一致");

            // 组织度沿既有轨迹继续（组织度无自然衰减路径——读档后必须原值保持）
            Settlement s = restored.SettlementOf(RepressedRegion);
            Assert.AreEqual(orc.Repressed.Organization, s.Organization, "组织度沿既有轨迹（无衰减）保持");
            Assert.GreaterOrEqual(s.Organization, Settlement.THRESHOLD_A, "组织度仍在阈值 A 之上（涌现链没被读档打断）");
            // 怨气沿既有轨迹：不可逆地板随档生效，怨气在地板之上继续走
            Assert.AreEqual(orc.Repressed.GrievanceFloor, s.GrievanceFloor, "不可逆地板随档（仇恨不可逆）");
            Assert.GreaterOrEqual(s.AvgGrievance, s.GrievanceFloor, "怨气不得低于不可逆地板");

            // 推进后组织者仍存在且不重复具名化（身份集合不增——涌现只认存档前的那一位）
            List<int> idsAfter7 = restored.Named.Where(n => n.Role == "Organizer").Select(n => n.Id).Distinct().ToList();
            CollectionAssert.AreEquivalent(idsBefore, idsAfter7, "推进 7 天后组织者身份集合不增——不重复具名化");
            Assert.IsTrue(restored.NamedById.ContainsKey(organizerId), "推进后组织者仍存在");
        }

        /// <summary>
        /// S4 槽位足迹：存档只带已实例化 slot——深聊人数 < 背景层总数 10%（§4.2 表：无档案 8B/槽）；
        /// DTO 序列化体积 < 1MB（1000 slot fixture 口径 + 生产口径双验）。
        /// </summary>
        [Test]
        public void S4_SlotFootprint()
        {
            // ① 生产口径：数据驱动建局背景层 100_000 槽，深聊 4 人 → 存档里 profiled slot 远低于 10%
            var orc = BuildOrchestrated45();
            var production = ColonySaveCapture.Capture(orc.World);
            Assert.AreEqual(100_000, orc.World.Background.Count, "数据驱动建局的背景层规模（建局常量）");
            Assert.Less(production.ProfiledSlots.Count, orc.World.Background.Count / 10,
                "存档里的 profiled slot 数必须 < 背景层总数 10%（§4.4：只带已实例化槽）");

            // ② 1000 slot fixture 口径：深聊 80 人（<10%）→ 序列化体积 < 1MB
            GameWorld fixture = BuildFootprintFixture();
            for (int i = 0; i < 80; i++) fixture.Background.EnsureProfile(i);
            var fixtureCapture = ColonySaveCapture.Capture(fixture);
            Assert.Less(fixtureCapture.ProfiledSlots.Count, fixture.Background.Count / 10,
                "fixture 深聊人数必须 < 背景层 10%（§4.2 表：玩家一辈子深聊不过 200 人）");
            int fixtureBytes = Encoding.UTF8.GetByteCount(
                ColonySaveCodec.ToJToken(fixtureCapture).ToString(Formatting.None));
            Assert.Less(fixtureBytes, 1_000_000, "DTO 序列化体积必须 < 1MB（1000 slot fixture 口径）");

            // ③ 生产口径同样 < 1MB——体积不随背景层总数增长（背景层本体不入档）
            int productionBytes = Encoding.UTF8.GetByteCount(
                ColonySaveCodec.ToJToken(production).ToString(Formatting.None));
            Assert.Less(productionBytes, 1_000_000, "生产口径（10 万背景层 + 100 具名 + 内容库）存档体积 < 1MB");
        }

        /// <summary>
        /// S5 SaveSystem 往返：ColonySaveData 经 ZFrame SaveSystem payload 通道落盘（slot_0.json，
        /// 原子写 + meta/modules 结构）→ 全新 SaveSystem 读回 → Restore → Capture 字段级等价。
        /// </summary>
        [Test]
        public void S5_SaveSystemRoundTrip()
        {
            var orc = BuildOrchestrated45();
            var before = ColonySaveCapture.Capture(orc.World);

            // 写出：注入临时根（绝不碰 persistentDataPath），整个 DTO 作为 colony-world 模块的 payload
            var writer = new SaveSystem(_saveRoot) { CurrentDisplayName = "A-COL-03 S5 roundtrip" };
            writer.Register(new ColonySaveModule(before));
            writer.SaveToSlot(0);

            // 落盘证据：slot_0.json 存在，结构 = {version, meta, modules[colony-world][world]}
            string mainPath = writer.SlotPath(0);
            Assert.IsTrue(File.Exists(mainPath), "payload 必须落盘（SaveSystem 原子写）");
            var file = JObject.Parse(File.ReadAllText(mainPath));
            Assert.AreEqual(SaveSystem.CurrentVersion, (int)file["version"], "存档版本头必须由 SaveSystem 写入");
            Assert.IsNotNull(file["meta"], "meta 头必须存在");
            Assert.IsNotNull(file["modules"]?[ColonySaveModule.Key]?[ColonySaveModule.PayloadField],
                "payload 必须挂在 modules.colony-world.world");

            // 读回：全新 SaveSystem + 全新模块（payload 通道 → SaveReader → DTO）
            var reader = new SaveSystem(_saveRoot);
            var module = new ColonySaveModule();
            reader.Register(module);
            SaveData loaded = reader.LoadFromSlot(0);
            Assert.IsNotNull(loaded, "读档成功（主文件候选）");
            Assert.AreEqual(SaveSystem.CurrentVersion, loaded.Version, "读回的版本头一致");
            Assert.IsNotNull(module.Data, "payload 必须经 SaveReader 回填成 DTO");

            // Restore → Capture：字段级等价（整棵 DTO 树）
            var restored = ColonySaveRestore.Restore(module.Data);
            var after = ColonySaveCapture.Capture(restored);
            AssertFieldLevelEqual(before, after, "S5 SaveSystem payload 往返");

            // 抽点复核：状态经「真文件」往返后逐字段一致（不止内存 JSON 树）
            Assert.AreEqual(before.Day, after.Day, "日期一致");
            Assert.AreEqual(before.WorldSeed, after.WorldSeed, "原种子随档");
            Assert.AreEqual(before.Named.Count, after.Named.Count, "具名层规模一致");
            Assert.AreEqual(before.ProfiledSlots.Count, after.ProfiledSlots.Count, "已实例化槽规模一致");
            Assert.AreEqual(before.Governor.Credibility, after.Governor.Credibility, "governor 信誉一致");
            Assert.AreEqual(before.Governor.IntelPoints, after.Governor.IntelPoints, "情报点一致（TASK-A-COL-07 真文件往返）");
        }

        /// <summary>
        /// S4 的 1000 slot 足迹 fixture（A-COL-01 fixture 同款建局骨架，最小内容）。
        /// MF 抽取与主建局同式（卡数×6），保证 Restore 的流重放公式对所有建局路径通用。
        /// </summary>
        private static GameWorld BuildFootprintFixture()
        {
            var w = new GameWorld(Seed);
            var regions = new[]
            {
                new RegionState("足迹甲城", 0.10f),
                new RegionState("足迹乙堡", -0.15f),
                new RegionState("足迹丙港", 0.05f),
            };
            w.BindRegions(regions);
            w.Settlements.Add(new Settlement("足迹甲城", 9000, 0.55f, 0.35f));
            w.Settlements.Add(new Settlement("足迹乙堡", 4200, 0.55f, 0.40f));
            w.Settlements.Add(new Settlement("足迹丙港", 3000, 0.55f, 0.30f));

            var npcs = new List<NamedNpc>();
            for (int i = 0; i < 8; i++)
            {
                npcs.Add(new NamedNpc
                {
                    Id = i,
                    Name = "足迹" + i,
                    Job = "矿工",
                    Origin = i % 2 == 0 ? "地球" : "船民",
                    Loyalty = 0.5f, Grievance = 0.3f, MaskStrength = 0.3f,
                    Contract = new BeliefContract
                    {
                        CoreBelief = "井下的人要互相盯着后背",
                        Wonts = new List<string> { "绝不丢下被困的人", "绝不瞒报塌方" },
                    },
                });
            }
            var rr = new Rng(Seed ^ ColonyDataLoader.ContentRngSalt);
            foreach (var n in npcs)
            {
                n.Culture = ColonyDataLoader.CultureOfOrigin(n.Origin);
                for (int i = 0; i < ColonyDataLoader.MoralFoundationDraws; i++) n.MF[i] = rr.Range(0.2f, 0.9f);
            }
            w.RegisterNamed(npcs);
            w.Background = new NpcDirectory(1000, Seed, regions, rr);
            w.PolicyLibrary.Add(new PolicyDef { Id = 1, Name = "足迹政策", Desc = "足迹 fixture", Benefit = "b", Cost = "c" });
            return w;
        }
    }
}
