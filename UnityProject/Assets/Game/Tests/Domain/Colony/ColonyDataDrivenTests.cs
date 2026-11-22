// ColonyDataDrivenTests.cs —— TASK-A-COL-02：GLM 五数据源走 ConfigService CSV 管线的数据驱动建局测试。
// 与 A-COL-01 的 ColonyVerticalLoopTests（V1-V7，内存 fixture）互补：
//   V1-V7 证明模拟逻辑、D1-D4 证明真实内容数据经 ZFrame ConfigSystem（A-CONF-02 管线）进局。
// 数据链路：Assets/Configs/CSV/*.csv（注解表头）→ 生成行类/表类 → CFG_*Table.asset（已提交）
//   → AssetDatabase 加载 → ConfigService.Load → ColonyConfigAdapter 投影 → ColonyDataLoader.BuildWorld。
// 不改动 V1-V7；全部断言锚定真实 CSV 内容（行数/抽查字段/分布），不是烟雾测试。

using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Dialogue;
using Game.Domain.Colony.Quest;
using Game.Domain.Colony.Sim;
using NUnit.Framework;
using UnityEditor;
using ZFrame.Core;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace Game.Domain.Tests
{
    /// <summary>
    /// ConfigService 五张殖民地表 → ColonyWorldData 投影（1:1，无业务逻辑）。
    /// 放在测试侧的原因：Game.Domain.Colony 按架构矩阵只引用 ZFrame.Core（零引擎依赖，
    /// A-COL-01 条目），不能引用 ZFrame.Data（UnityEngine）；生产侧接线（Bootstrap）落地时
    /// 由其宿主程序集承担同一投影职责。
    /// </summary>
    public static class ColonyConfigAdapter
    {
        /// <summary>五张表全部加载后投影为建局输入；缺表即抛（fail-fast，不静默半建局）。</summary>
        public static ColonyWorldData ToWorldData(IConfigService config)
        {
            var data = new ColonyWorldData();
            foreach (var r in Require<NpcsConfigRow>(config))
            {
                data.Npcs.Add(new ColonyNpcInput
                {
                    Id = r.Id, Name = r.Name, Age = r.Age, Job = r.Job, Origin = r.Origin,
                    Want = r.Want, Wont = r.Wont, Conflict = r.Conflict, Secret = r.Secret,
                    Speech = r.Speech, RelationToPower = r.RelationToPower,
                });
            }

            foreach (var r in Require<PlacesConfigRow>(config))
            {
                data.Places.Add(new ColonyPlaceInput
                {
                    Id = r.Id, Name = r.Name, Type = r.Type, Population = r.Population,
                    Industry = r.Industry, Blurb = r.Blurb, Decay = r.Decay, Ongoing = r.Ongoing,
                });
            }

            foreach (var r in Require<PoliciesConfigRow>(config))
            {
                data.Policies.Add(new ColonyPolicyInput
                {
                    Id = r.Id, Name = r.Name, Desc = r.Desc, Benefit = r.Benefit, Cost = r.Cost,
                    WhoUnhappy = r.WhoUnhappy, Difficulty = r.Difficulty,
                    IsTrap = r.IsTrap, IsDarkMedicine = r.IsDarkMedicine,
                });
            }

            foreach (var r in Require<QuestsConfigRow>(config))
            {
                data.Quests.Add(new ColonyQuestInput
                {
                    Id = r.Id, Name = r.Name, Trigger = r.Trigger, Involved = r.Involved, Core = r.Core,
                    OptionA = r.OptionA, OptionB = r.OptionB, OptionC = r.OptionC,
                    Aftermath = r.Aftermath, DelayMonths = r.DelayMonths, WorldAdvances = r.WorldAdvances,
                });
            }

            foreach (var r in Require<DialogueConfigRow>(config))
            {
                data.Dialogue.Add(new ColonyDialogueInput { Role = r.Role, Topic = r.Topic, Line = r.Line });
            }

            return data;
        }

        private static IReadOnlyList<TRow> Require<TRow>(IConfigService config) where TRow : ConfigRow
        {
            var rows = config.All<TRow>();
            if (rows == null)
            {
                throw new InvalidOperationException(
                    $"ConfigService 未加载 {typeof(TRow).Name}（请先 Load 五张殖民地表资产）");
            }

            return rows;
        }
    }

    /// <summary>真实数据建局（TASK-A-COL-02 D1-D4）：种子固定 → 断言确定性可复现。</summary>
    public sealed class ColonyDataDrivenTests
    {
        // 与 V1-V7 同种子：随机流（关系网 / 人格启发式 / MF）确定性可复现。
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

        [TearDown]
        public void TearDown()
        {
            // EventBus 是进程级静态：清掉可能残留的排队事件（照抄 A-COL-01 fixture 纪律）。
            EventBus.FlushQueued();
        }

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（路径由产品代码推导，不手写）。</summary>
        private static void LoadTable<TRow>(ConfigService config, string stem) where TRow : ConfigRow
        {
            var csvPath = $"{CsvImporter.CsvRoot}/{stem}.csv";
            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            Assert.IsNotNull(table,
                $"殖民地表资产缺失：{assetPath}（应随任务提交；若缺失，按 A-CONF-02 菜单链路从 {csvPath} 重新导入）");
            config.Load(table);
        }

        /// <summary>每个用例独立建局（D3 会改写 NPC 状态，不共享世界实例）。</summary>
        private static GameWorld BuildWorld() => ColonyDataLoader.BuildWorld(Seed, _data);

        // ────────────────────────────── D1-D4 ──────────────────────────────

        /// <summary>D1：五表行数=100/120/40/12/300；字段抽查与主控 CSV 源一致；is_trap/is_dark_medicine 各 5；台词每 role 60 条。</summary>
        [Test]
        public void D1_LoadAllFiveTables()
        {
            Assert.AreEqual(100, _config.All<NpcsConfigRow>().Count, "npcs.csv 必须整表 100 行");
            Assert.AreEqual(120, _config.All<PlacesConfigRow>().Count, "places.csv 必须整表 120 行");
            Assert.AreEqual(40, _config.All<PoliciesConfigRow>().Count, "policies.csv 必须整表 40 行");
            Assert.AreEqual(12, _config.All<QuestsConfigRow>().Count, "quests.csv 必须整表 12 行");
            Assert.AreEqual(300, _config.All<DialogueConfigRow>().Count, "dialogue.csv 必须整表 300 行");

            // 抽查与主控 CSV 源逐字一致（防转码损坏）：npcs 第 1 行 = 郑大镐/矿工/54/殖民地第二代/怀疑。
            var npc0 = _data.Npcs[0];
            Assert.AreEqual("郑大镐", npc0.Name, "npcs[0].Name 必须与 CSV 源逐字一致");
            Assert.AreEqual("矿工", npc0.Job, "npcs[0].Job 必须与 CSV 源逐字一致");
            Assert.AreEqual(54, npc0.Age, "npcs[0].Age 必须与 CSV 源一致");
            Assert.AreEqual("殖民地第二代", npc0.Origin, "npcs[0].Origin 必须与 CSV 源逐字一致");
            Assert.AreEqual("怀疑", npc0.RelationToPower, "npcs[0].RelationToPower 必须与 CSV 源逐字一致");
            Assert.IsTrue(npc0.Want.Length > 0 && npc0.Wont.Length > 0 && npc0.Secret.Length > 0
                && npc0.Speech.Length > 0, "npcs[0] 的 want/wont/secret/speech 字段必须非空");

            // 挖坑 5 / 暗药 5（GLM 政策表的机检口径）。
            Assert.AreEqual(5, _data.Policies.Count(p => p.IsTrap), "is_trap 政策必须是 5 条");
            Assert.AreEqual(5, _data.Policies.Count(p => p.IsDarkMedicine), "is_dark_medicine 政策必须是 5 条");
            Assert.AreEqual(0, _data.Policies.Count(p => p.IsTrap && p.IsDarkMedicine),
                "挖坑与暗药不得兼任（同一政策要么伪装成好、要么伪装成糟）");

            // 台词 role 分布：A/B/C/D/E 各 60 条。
            foreach (var role in new[] { "A", "B", "C", "D", "E" })
            {
                Assert.AreEqual(60, _data.Dialogue.Count(d => d.Role == role),
                    $"台词 role {role} 必须是 60 条");
            }
        }

        /// <summary>D2：DataLoader 数据驱动建局 → 100 具名 / 3 地区 / 12 支线；「坟山北坡」在库且 DelayDays>0。</summary>
        [Test]
        public void D2_BuildWorldFromData()
        {
            var w = BuildWorld();
            Assert.AreEqual(100, w.Named.Count, "100 张 GLM 卡必须全部注册为具名 NPC");
            Assert.AreEqual(3, w.Regions.Length, "人口前三的地点必须建成 3 个地区");
            Assert.AreEqual(12, w.Quests.Library.Count, "12 则支线必须全部入库");

            // 人口前三的地区与 CSV 源一致（双河城 86000 / 残垣城 52000 / 青石城 38000）。
            Assert.AreEqual("双河城", w.Regions[0].Name, "人口第一的地区必须是双河城（places.csv）");
            Assert.AreEqual("残垣城", w.Regions[1].Name, "人口第二的地区必须是残垣城（places.csv）");
            Assert.AreEqual("青石城", w.Regions[2].Name, "人口第三的地区必须是青石城（places.csv）");
            Assert.AreEqual(3, w.Settlements.Count, "三个主地区各建一个聚落");
            Assert.AreEqual(86000, w.Settlements[0].Population, "双河城聚落人口必须取自 CSV（86000）");

            // 「坟山北坡」特例：GrievanceAbove 0.35 @ 主地区[1]，延迟 6 个月 = 180 天。
            var tomb = w.Quests.Library.FirstOrDefault(q => q.Name == "坟山北坡");
            Assert.IsNotNull(tomb, "「坟山北坡」必须在支线库中（quests.csv）");
            Assert.Greater(tomb.DelayDays, 0, "「坟山北坡」必须带延迟后果（delay_months=6 → 180 天）");
            Assert.AreEqual(180, tomb.DelayDays, "延迟 = delay_months×30（6 个月 → 180 天）");
            Assert.AreEqual(TriggerKind.GrievanceAbove, tomb.TKind, "「坟山北坡」必须按语义用怨气触发");
            Assert.AreEqual(0.35f, tomb.TArg, 1e-5f, "「坟山北坡」怨气阈值 0.35");
            Assert.AreEqual(w.Regions[1].Name, tomb.TRegion, "「坟山北坡」绑定主地区[1]");

            // 建局完整性：台词 300 条全部入 DialogueBank；非坟山支线按日程解锁（15+i*8）。
            Assert.AreEqual(300, w.Dialogue.Total, "300 条台词必须全部进入 DialogueBank");
            var schedule = w.Quests.Library.Where(q => q.TKind == TriggerKind.DayReached)
                .Select(q => q.TArg).ToList();
            Assert.AreEqual(11, schedule.Count, "除坟山北坡外 11 条支线按日程解锁");
            Assert.AreEqual(15f, schedule[0], 1e-5f, "日程解锁首条 = 15+i*8（i=0 → 15 天）");
            Assert.AreEqual(15f + 11 * 8f, schedule[10], 1e-5f, "日程解锁末条 = 15+11*8（i=11 → 103 天）");

            // 文化映射（§21 + Program.cs 同款）：难民 → Mix(Rural, Doctrine, 0.5)，Target=Doctrine。
            var refugees = w.Named.Where(n => n.Origin.Contains("难民")).ToList();
            Assert.AreEqual(21, refugees.Count, "难民卡必须 21 张（npcs.csv）");
            Assert.IsTrue(refugees.All(n => n.Culture.Target == CulturalFrame.LoyaltyTarget.Doctrine),
                "难民文化必须是 Mix(Rural, Doctrine, 0.5)（忠诚指向 Doctrine）");
            var boatPeople = w.Named.Where(n => n.Origin.Contains("船民")).ToList();
            Assert.AreEqual(23, boatPeople.Count, "船民卡必须 23 张（npcs.csv）");
            Assert.IsTrue(boatPeople.All(n => n.Culture.Target == CulturalFrame.LoyaltyTarget.Person),
                "船民文化必须是 Rural（忠诚指向 Person）");
        }

        /// <summary>D3：从表里找 IsMerchant → 怨气 0.6 → 抗议路由必含涨价 → 倍率 ∈(1.0,1.6]（真实商人端到端）。</summary>
        [Test]
        public void D3_RealMerchantEndToEnd()
        {
            var w = BuildWorld();
            NamedNpc merchant = w.Named.First(n => n.IsMerchant);
            // 商人身份来自 CSV：首个 IsMerchant = id 57 白汀（走私贩，船民）。
            Assert.AreEqual("白汀", merchant.Name, "首个商人必须是 npcs.csv id 57 白汀");
            Assert.AreEqual("走私贩", merchant.Job, "白汀的职业必须是走私贩");
            Assert.AreEqual(w.Regions[0].Name, w.RegionOf(merchant), "船民商人必须被 RegisterNamed 分到主地区[0]");

            merchant.Grievance = 0.6f;
            SilentProtest protest = ProtestRouter.ChooseProtest(merchant, merchant.Grievance);
            Assert.IsNotNull(protest, "走私贩（Neuroticism=0.65）必须选出抗议形式");
            merchant.Protest = protest;
            Assert.IsTrue(protest.Forms.Contains(ProtestForm.InflatePrice), "商人的抗议手段必须含涨价（§3.3）");
            Assert.IsTrue(protest.IsCovert, "抗议默认伪装——每一种都说得出理由");

            (float multiplier, string reason) = ProtestEffects.Price(merchant, w.Regions[0].Reputation());
            Assert.Greater(multiplier, 1.0f, "抗议必须真的涨价（怨气 0.6、地区声望 0）");
            Assert.LessOrEqual(multiplier, 1.6f, "涨幅钳制在 1.6 以内");
            Assert.IsNotNull(reason, "强抗议（>0.3）必须包装假理由，玩家看到的是供应问题");
        }

        /// <summary>D4：映射到预生成 role 的 NPC Speak 返回非空且带 tone 标签，正文来自台词表。</summary>
        [Test]
        public void D4_DialogueSpeakFromTable()
        {
            var w = BuildWorld();
            NamedNpc speaker = w.Named.First(n => DialogueBank.JobToRole.ContainsKey(n.Job));
            NamedNpc listener = w.Named.First(n => n.Id != speaker.Id);
            Assert.AreEqual("C", DialogueBank.JobToRole[speaker.Job], "首个可映射 NPC 应是矿工（role C，npcs.csv id 0）");

            string line = w.Dialogue.Speak(w, speaker, listener, "寒暄", isPublic: false, new Rng(Seed));
            Assert.IsFalse(string.IsNullOrEmpty(line), "Speak 必须返回非空台词");
            Assert.IsTrue(line.StartsWith("["), $"台词必须带 tone 标签（[ 开头），实际：{line}");
            Assert.IsFalse(line.Contains("不在预生成池"), "矿工在预生成池内，不得走模板兜底通道");

            // 正文必须真的来自台词表（role C / 寒暄 池），口癖后缀（15% 概率注入）剥离后比对。
            string tone = line.Substring(0, line.IndexOf(']') + 1);
            string body = line.Substring(tone.Length).TrimStart();
            int quirkAt = body.IndexOf("（口癖注入", StringComparison.Ordinal);
            if (quirkAt >= 0) body = body[..quirkAt].TrimEnd();
            var pool = _data.Dialogue.Where(d => d.Role == "C" && d.Topic == "寒暄").Select(d => d.Line).ToList();
            Assert.AreEqual(10, pool.Count, "role C 寒暄池必须 10 条（dialogue.csv）");
            Assert.IsTrue(pool.Contains(body), $"Speak 正文必须逐字来自台词表 C/寒暄 池，实际：{body}");
            Assert.IsTrue(tone.StartsWith("[TU_"), $"tone 标签必须是 TU_* 形态，实际：{tone}");
        }
    }
}
