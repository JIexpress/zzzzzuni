// ColonyVerticalLoopTests.cs —— TASK-A-COL-01：ColonyCore 迁入 Game.Domain.Colony 的「政策→抗议→报告」垂直闭环。
// 闭环：颁布政策 → 文化加权怨气 → 商人涨价抗议 → 玩家观察取得间接证据 → 延迟后果到账 → 事件经 ZFrame EventBus 发布。
// fixture 全部内存种子数据（不读外部文件）；种子固定 → 断言确定性可复现。
// 编排步骤对照原 ColonyCore/App/Program.cs 第 1~15 天剧本（该宿主不迁入，其职责由本 fixture 承担）。

using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Quest;
using Game.Domain.Colony.Sim;
using NUnit.Framework;
using ZFrame.Core;

namespace Game.Domain.Tests
{
    /// <summary>
    /// 殖民地模拟最小垂直闭环（TASK-A-COL-01 V1-V7）。
    /// 所有断言都是可证伪的真实断言：阈值、枚举、文本来源全部来自迁入代码的行为，不是烟雾测试。
    /// </summary>
    public sealed class ColonyVerticalLoopTests
    {
        // 固定种子：全部随机流（关系网建网 / 观察发现概率 / 市场漂移）确定性可复现。
        private const ulong Seed = 20261122UL;

        [TearDown]
        public void TearDown()
        {
            // EventBus 是进程级静态：清掉可能残留的排队事件，保证用例隔离（照抄 EventBusTests 纪律）。
            EventBus.FlushQueued();
        }

        // ────────────────────────────── 内存 fixture ──────────────────────────────

        /// <summary>建局：3 地点+聚落、8 具名 NPC、2 政策（挖坑/暗药）、1 支线（坟山北坡式）、背景层目录。</summary>
        private static GameWorld BuildWorld()
        {
            var w = new GameWorld(Seed);
            var regions = new[]
            {
                new RegionState("双河渡", 0.10f),   // 港口贸易枢纽（商人所在地）
                new RegionState("黑水堡", -0.15f),  // 衰退矿堡（支线与镇压的舞台）
                new RegionState("三号港", 0.05f),   // 船坞装配工所在地
            };
            w.BindRegions(regions);
            w.Settlements.Add(new Settlement("双河渡", 9000, 0.55f, 0.35f));
            w.Settlements.Add(new Settlement("黑水堡", 4200, 0.55f, 0.40f));
            w.Settlements.Add(new Settlement("三号港", 3000, 0.55f, 0.30f));

            var npcs = BuildNpcs();
            var rr = new Rng(Seed ^ 0xC0FFEEUL);
            foreach (var n in npcs)
            {
                n.Culture = n.Origin switch          // 文化按出身分配（demo 同款；二代 = 插值）
                {
                    var o when o.Contains("船民") => CulturalFrame.Rural(),
                    var o when o.Contains("难民") => CulturalFrame.Mix(CulturalFrame.Rural(), CulturalFrame.Doctrine(), 0.5f),
                    var o when o.Contains("地球") => CulturalFrame.Atomized(),
                    _ => CulturalFrame.Mix(CulturalFrame.Rural(), CulturalFrame.Atomized(), 0.5f),
                };
                for (int i = 0; i < 6; i++) n.MF[i] = rr.Range(0.2f, 0.9f);
                n.Quirk = n.Speech.Length > 12 ? n.Speech[..12] : n.Speech;
            }
            w.RegisterNamed(npcs);   // 按 origin 分地区 + 建关系网（每人 5~15 条边）
            w.Background = new NpcDirectory(1000, Seed, regions, rr);

            w.PolicyLibrary.Add(new PolicyDef
            {
                Id = 1,
                Name = "矿脉加速开采令",
                Desc = "矿队三班倒，产量立刻上去。",
                Benefit = "月产量上涨三成",
                Cost = "巷道支撑被削减，塌方只是时间问题",
                WhoUnhappy = "商人囤货抬价，矿工被压着三班倒",
                Difficulty = "低",
                IsTrap = true,
            });
            w.PolicyLibrary.Add(new PolicyDef
            {
                Id = 2,
                Name = "净水厂强制翻新",
                Desc = "全城停水三天，水质检测全公开。",
                Benefit = "肠道病发病率下降",
                Cost = "三天没人洗澡，怨声载道",
                WhoUnhappy = "酒保与厨师的生意停摆",
                Difficulty = "高",
                IsDarkMedicine = true,
            });

            w.Quests.Library.Add(new QuestDef
            {
                Name = "坟山北坡",
                TriggerText = "北坡的坟又被人翻出一座，死者家属堵在议事厅门口。",
                CoreText = "先人安息之地与矿脉延伸之争，两边都没有退路。",
                AftermathText = "矿脉塌了三条巷道，死者家属里的年轻人开始公开练拳。",
                NoInterventionText = "家属自己把坟迁了，但从此议事厅开门没人敢再坐着说话。",
                Options = new List<string>
                {
                    "顺着矿队，坟山北坡让给矿脉",
                    "程序正确：先勘探再迁坟，两边都得等",
                    "折中：矿脉绕行，工期延后一年",
                },
                DelayDays = 180,
                WorldAdvances = true,
                TKind = TriggerKind.GrievanceAbove,
                TArg = 0.35f,
                TRegion = "黑水堡",
            });
            return w;
        }

        /// <summary>8 具名 NPC：0=船坞装配工（信念契约主角）、1=走私贩（IsMerchant）、其余任意填充。契约必须齐全（每日结算会推进）。</summary>
        private static List<NamedNpc> BuildNpcs()
        {
            return new List<NamedNpc>
            {
                new NamedNpc
                {
                    Id = 0, Name = "陈九斤", Job = "船坞装配工", Origin = "地球",
                    Loyalty = 0.55f, Grievance = 0.20f, MaskStrength = 0.30f,
                    Agreeableness = 0.40f, Neuroticism = 0.60f, HonestyHumility = 0.65f,
                    Conscientiousness = 0.80f, Extraversion = 0.40f, Status = 0.40f,
                    Want = "让船坞的每一条焊缝都经得起风暴",
                    Conflict = "产量指标与安检规程的拉扯",
                    Secret = "他偷偷改装了净水泵的计量阀",
                    Speech = "短句，爱打比方", RelationToPower = "敬而远之",
                    Contract = new BeliefContract
                    {
                        CoreBelief = "设备安全不能拿来换产量",
                        Wonts = new List<string> { "绝不在安检记录上签字放行", "绝不拆别人的备件去修自己的工位" },
                        Tolerance = 1,
                        // 初始 0.90：关系已有裂痕。简报阈值「35 天后 <0.9」依赖沉默期衰减
                        //（第 31 天才进沉默期 → 35 天时只衰减 4 次），1.0 起始只能到 0.96。
                        Cooperation = 0.90f,
                    },
                },
                new NamedNpc
                {
                    Id = 1, Name = "申屠百川", Job = "走私贩", Origin = "船民", IsMerchant = true,
                    Loyalty = 0.45f, Grievance = 0.20f, MaskStrength = 0.30f,
                    Agreeableness = 0.30f, Neuroticism = 0.40f, HonestyHumility = 0.50f,
                    Conscientiousness = 0.50f, Extraversion = 0.60f, Status = 0.60f,
                    Want = "一条谁也查不到的补给线",
                    Conflict = "生意做大与港口严查",
                    Secret = "他把三号泊位的灯标挪过两次",
                    Speech = "长句，先给结论", RelationToPower = "有利可图就合作",
                    Contract = new BeliefContract
                    {
                        CoreBelief = "水路就是命",
                        Wonts = new List<string> { "绝不对船民动手", "绝不坏行情" },
                    },
                },
                new NamedNpc
                {
                    Id = 2, Name = "李大镐", Job = "矿工", Origin = "殖民地第二代",
                    Loyalty = 0.50f, Grievance = 0.30f, MaskStrength = 0.30f,
                    Agreeableness = 0.50f, Neuroticism = 0.55f, HonestyHumility = 0.55f,
                    Conscientiousness = 0.60f, Extraversion = 0.50f, Status = 0.30f,
                    Want = "下个月把父亲的抚恤金讨回来", Conflict = "矿队编制与工伤记录",
                    Secret = "他藏着一截断裂的支撑钢梁", Speech = "反问句多", RelationToPower = "记仇",
                    Contract = new BeliefContract { CoreBelief = "井下的人要互相盯着后背", Wonts = new List<string> { "绝不丢下被困的人", "绝不瞒报塌方" } },
                },
                new NamedNpc
                {
                    Id = 3, Name = "王春梅", Job = "厨师", Origin = "难民",
                    Loyalty = 0.60f, Grievance = 0.25f, MaskStrength = 0.30f,
                    Agreeableness = 0.65f, Neuroticism = 0.45f, HonestyHumility = 0.60f,
                    Conscientiousness = 0.70f, Extraversion = 0.55f, Status = 0.35f,
                    Want = "给弟弟凑一笔离开殖民地的船票钱", Conflict = "饭堂配给与黑市差价",
                    Secret = "她多报了两份饭堂人头", Speech = "轻快，答非所问", RelationToPower = "讨好",
                    Contract = new BeliefContract { CoreBelief = "锅里得有人人有份", Wonts = new List<string> { "绝不克扣难民份饭", "绝不给镇压队伍做饭" } },
                },
                new NamedNpc
                {
                    Id = 4, Name = "赵永年", Job = "保安", Origin = "地球",
                    Loyalty = 0.70f, Grievance = 0.15f, MaskStrength = 0.30f,
                    Agreeableness = 0.45f, Neuroticism = 0.35f, HonestyHumility = 0.55f,
                    Conscientiousness = 0.75f, Extraversion = 0.45f, Status = 0.45f,
                    Want = "攒够积分调回内环", Conflict = "命令与良心",
                    Secret = "他压下过一份斗殴报告", Speech = "军事化，数字精确", RelationToPower = "服从",
                    Contract = new BeliefContract { CoreBelief = "秩序先于人情", Wonts = new List<string> { "绝不对未抵抗者动手", "绝不收商人的保护费" } },
                },
                new NamedNpc
                {
                    Id = 5, Name = "计晚棠", Job = "义体技师", Origin = "船民",
                    Loyalty = 0.50f, Grievance = 0.20f, MaskStrength = 0.30f,
                    Agreeableness = 0.55f, Neuroticism = 0.50f, HonestyHumility = 0.45f,
                    Conscientiousness = 0.65f, Extraversion = 0.40f, Status = 0.50f,
                    Want = "开一间不挂招牌的修理铺", Conflict = "专利配额与手艺人的生存",
                    Secret = "她给走私船修过密封件", Speech = "长句，先给结论", RelationToPower = "保持距离",
                    Contract = new BeliefContract { CoreBelief = "每一具身体都该被修好", Wonts = new List<string> { "绝不装来路不明的义体", "绝不给侦探提供客人名单" } },
                },
                new NamedNpc
                {
                    Id = 6, Name = "费蔓青", Job = "会计", Origin = "地球",
                    Loyalty = 0.55f, Grievance = 0.20f, MaskStrength = 0.30f,
                    Agreeableness = 0.50f, Neuroticism = 0.40f, HonestyHumility = 0.70f,
                    Conscientiousness = 0.85f, Extraversion = 0.35f, Status = 0.45f,
                    Want = "把账做到任何人查不出毛病", Conflict = "两本账的诱惑",
                    Secret = "她发现过一笔去向不明的维修款", Speech = "短句，爱打比方", RelationToPower = "只认制度",
                    Contract = new BeliefContract { CoreBelief = "账目必须经得起追溯", Wonts = new List<string> { "绝不签假账", "绝不替任何人平账" } },
                },
                new NamedNpc
                {
                    Id = 7, Name = "苏云旗", Job = "酒保", Origin = "难民",
                    Loyalty = 0.50f, Grievance = 0.25f, MaskStrength = 0.30f,
                    Agreeableness = 0.60f, Neuroticism = 0.50f, HonestyHumility = 0.50f,
                    Conscientiousness = 0.55f, Extraversion = 0.75f, Status = 0.40f,
                    Want = "让酒馆成为没人盘查的地方", Conflict = "消息贩子与守口如瓶",
                    Secret = "他偷偷给地下广播留过后门", Speech = "轻快，答非所问", RelationToPower = "人人都熟",
                    Contract = new BeliefContract { CoreBelief = "吧台之上人人平等", Wonts = new List<string> { "绝不向密探出卖客人", "绝不在酒里动手脚" } },
                },
            };
        }

        // ────────────────────────────── V1-V7 ──────────────────────────────

        /// <summary>V1：颁布挖坑政策（执行 0.4）→ 失望型判词；31 天后延迟爆雷入 EventLog。</summary>
        [Test]
        public void V1_PolicyEnact_DelayedBlowup()
        {
            var w = BuildWorld();
            var trap = w.PolicyLibrary.First(p => p.IsTrap);

            PolicySystem.Enact(w, trap, w.Regions[2].Name, execution: 0.4f);

            string verdict = w.Regions[2].PolicyVerdict();
            Assert.IsTrue(verdict.Contains("说得好听"), $"好政策+执行糟糕必须给「失望」判词（§2.1），实际：{verdict}");
            Assert.AreEqual(0.4f, w.Regions[2].PolicyExecution, 1e-5f, "执行度必须落到地区状态上");
            Assert.AreEqual(0.6f, w.Regions[2].PolicyBenefit, 1e-5f, "挖坑政策的短期「有利」伪装必须写进地区");
            Assert.AreEqual(2, w.Log.All.Count(e => e.Kind == EventKind.Policy), "颁布(公开) + 不满人群(潜伏) 各一条");

            w.AdvanceDays(31);

            GameEvent blowup = w.Log.All.FirstOrDefault(e => e.Kind == EventKind.Policy && e.Text.Contains("一个月"));
            Assert.IsNotNull(blowup, "挖坑政策 30 天延迟后果必须到账进 EventLog");
            Assert.AreEqual(31u, blowup.Day, "爆雷应恰好在 dueDay=颁布日+30 入账");
            Assert.AreEqual(EventVisibility.Public, blowup.Vis, "爆雷是公共事件（当初颂德的人都还在）");
            Assert.AreEqual(w.Regions[2].Name, blowup.Where, "爆雷落在颁布地区");
        }

        /// <summary>V2：商人怨气 ≥0.6 → 抗议路由必选涨价；倍率 ∈ (1.0, 1.6] 且带假理由。</summary>
        [Test]
        public void V2_MerchantProtest_PriceAndPretext()
        {
            var w = BuildWorld();
            NamedNpc merchant = w.Named.First(n => n.IsMerchant);
            Assert.AreEqual("双河渡", w.RegionOf(merchant), "船民必须分到港口地区（RegisterNamed 的 origin 规则）");

            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), w.RegionOf(merchant), execution: 0.4f);
            Assert.Greater(merchant.Grievance, 0.20f, "「谁会不满」必须经文化加权把商人怨气推高");

            merchant.Grievance = Math.Max(merchant.Grievance, 0.6f);   // demo 同款：怨气下限 0.6
            Assert.GreaterOrEqual(merchant.Grievance, 0.6f, "前提：商人怨气 ≥0.6");

            SilentProtest protest = ProtestRouter.ChooseProtest(merchant, merchant.Grievance);
            Assert.IsNotNull(protest, "低宜人性商人必须选出抗议形式");
            merchant.Protest = protest;
            Assert.IsTrue(protest.Forms.Contains(ProtestForm.InflatePrice), "商人的抗议手段必须是涨价（§3.3）");
            Assert.IsTrue(protest.IsCovert, "抗议默认伪装——每一种都说得出理由");
            Assert.AreEqual(0.6f, protest.Level, 1e-5f, "抗议强度 = 怨气钳制到 0..1");

            (float multiplier, string reason) = ProtestEffects.Price(merchant, w.Regions[0].Reputation());
            Assert.Greater(multiplier, 1.0f, "抗议必须真的涨价");
            Assert.LessOrEqual(multiplier, 1.6f, "涨幅钳制在 1.6 以内（涨一倍玩家立刻识破）");
            Assert.AreEqual("最近供应紧张", reason, "强抗议（>0.3）必须包装假理由，玩家看到的是供应问题");
        }

        /// <summary>V3：观察动作 ≤6 次 → 拿到间接证据（DiscoveredByPlayer）+ Personal 事件；证据不可指控。</summary>
        [Test]
        public void V3_Observation_YieldsIndirectEvidence()
        {
            var w = BuildWorld();
            NamedNpc merchant = w.Named.First(n => n.IsMerchant);
            merchant.Grievance = 0.6f;
            merchant.Protest = ProtestRouter.ChooseProtest(merchant, merchant.Grievance);
            Assert.IsNotNull(merchant.Protest, "前提：商人在酝酿伪装抗议");

            w.Governor.PresentRegion = w.RegionOf(merchant);
            bool found = false;
            for (int t = 0; t < 6 && !found; t++) found = w.Governor.Observe(w, merchant);

            Assert.IsTrue(found, "守 6 次内必须守到一次间接证据（种子固定 → 本用例确定性）");
            Assert.IsTrue(merchant.Protest.DiscoveredByPlayer, "发现必须落成 DiscoveredByPlayer");
            GameEvent evidence = w.Log.All.FirstOrDefault(e =>
                e.Kind == EventKind.Observation && e.Vis == EventVisibility.Personal && e.Text.Contains("守到半夜"));
            Assert.IsNotNull(evidence, "识破瞬间必须产生一条 Personal 观察事件");
            Assert.AreEqual(w.RegionOf(merchant), evidence.Where, "观察事件落在商人所在地区");
            Assert.IsTrue(evidence.Text.Contains("无法指控"), "§4.1：所有证据都是间接的，怀疑成立但无法指控");
        }

        /// <summary>V4：违背 Wont → 质疑；35 天 → 沉默且配合度衰减；再 30 天 → 离开带走情报；阶段三不可逆。</summary>
        [Test]
        public void V4_BeliefContract_SilenceThenLeave()
        {
            var w = BuildWorld();
            NamedNpc eng = w.Named.First(n => n.Job == "船坞装配工");
            Assert.AreEqual(Stage.Aligned, eng.Contract.Stage, "初始：认同");

            eng.Contract.OnViolation();   // 玩家坐实了他 Wont 里的事
            Assert.AreEqual(Stage.Doubt, eng.Contract.Stage, "违背 Wont → 阶段一质疑");
            Assert.AreEqual(1, eng.Contract.Violations);

            w.AdvanceDays(35);
            Assert.AreEqual(Stage.Silent, eng.Contract.Stage, "30 天不修复必须滑入沉默期（最难察觉）");
            Assert.Less(eng.Contract.Cooperation, 0.9f, "沉默期配合度每天 -0.01，35 天后必须 <0.9");

            w.AdvanceDays(30);
            Assert.AreEqual(Stage.Left, eng.Contract.Stage, "再 30 天不修复必须离开（阶段三）");
            Assert.AreEqual(eng.Secret, eng.Contract.IntelCarried, "离开的人必须带走他的 Secret（MVP 5）");
            GameEvent departure = w.Log.All.FirstOrDefault(e => e.Kind == EventKind.Departure && e.Text.Contains(eng.Name));
            Assert.IsNotNull(departure, "离开必须进 EventLog");
            Assert.AreEqual(EventVisibility.Personal, departure.Vis, "离开事件是在场才见的 Personal");

            eng.Contract.OnViolation();
            Assert.AreEqual(Stage.Left, eng.Contract.Stage, "阶段三不可逆：离开后再违背仍是 Left");
            Assert.AreEqual(1, eng.Contract.Violations, "Left 后 OnViolation 直接返回，不再累计");
        }

        /// <summary>V5：RepressionCount=3 → 14 天平均面具变厚；两次 Apply(Harsh) → 组织者从参数里长出来 + 怨气地板 >0。</summary>
        [Test]
        public void V5_Repression_SpawnsOrganizer()
        {
            var w = BuildWorld();
            Settlement sB = w.SettlementOf("黑水堡");
            Assert.IsNotNull(sB, "fixture 必须含黑水堡聚落");
            w.Governor.RepressionCount = 3;   // 连续镇压：恐怖统治

            float maskBefore = w.Named.Average(n => n.MaskStrength);
            sB.Apply(Decision.Harsh);   // 镇压集会
            sB.Apply(Decision.Harsh);   // 镇压余波：组织度 +0.3，仇恨不可逆
            w.AdvanceDays(14);

            float maskAfter = w.Named.Average(n => n.MaskStrength);
            Assert.Greater(maskAfter, maskBefore, "恐怖统治（terror 0.045/天）必须让平均面具变厚");
            Assert.Greater(sB.Organization, Settlement.THRESHOLD_A, "两次强硬后组织度必须越过阈值 A（0.35）");
            Assert.Greater(sB.GrievanceFloor, 0f, "仇恨不可逆：两次镇压必须抬出怨气地板");

            NamedNpc organizer = w.Named.FirstOrDefault(n => n.Role == "Organizer");
            Assert.IsNotNull(organizer, "周结后组织者必须从参数里长出来（§6.3）");
            Assert.Greater(organizer.Organization, 0f, "组织者必须继承聚落组织度");
            GameEvent uprising = w.Log.All.FirstOrDefault(e => e.Kind == EventKind.Uprising);
            Assert.IsNotNull(uprising, "组织者涌现必须记一条 Uprising 事件");
            Assert.AreEqual(EventVisibility.Covert, uprising.Vis, "地下网络对玩家不可见（§23 内敛文化）");
        }

        /// <summary>V6：信息不对称——PlayerFeed 数量 < 全量，Covert 永不进玩家信息流。</summary>
        [Test]
        public void V6_InformationAsymmetry()
        {
            var w = BuildWorld();
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), w.Regions[0].Name, execution: 0.4f);

            NamedNpc merchant = w.Named.First(n => n.IsMerchant);
            merchant.Grievance = Math.Max(merchant.Grievance, 0.6f);
            merchant.Protest = ProtestRouter.ChooseProtest(merchant, merchant.Grievance);
            (float multiplier, string reason) = ProtestEffects.Price(merchant, w.Regions[0].Reputation());
            w.Log.Add(w.Day, EventKind.Protest, w.RegionOf(merchant),
                $"商人 {merchant.Name} 的货价 ×{multiplier:0.00}（{reason}）", EventVisibility.Public);

            List<GameEvent> feed = w.Log.PlayerFeed(1, "", new HashSet<string>());
            Assert.Less(feed.Count, w.Log.All.Count, "信息不对称是框架纪律：可见事件必须严格少于真实事件");
            Assert.IsFalse(feed.Any(e => e.Vis == EventVisibility.Covert), "Covert 事件永不进玩家信息流（§4）");
            GameEvent covert = w.Log.All.First(e => e.Vis == EventVisibility.Covert);
            Assert.IsFalse(feed.Any(e => e.Text == covert.Text), "潜伏政策的「不满人群」明细绝不能泄漏给玩家");
            Assert.IsTrue(feed.Any(e => e.Kind == EventKind.Policy && e.Text.Contains("颁布")), "公开颁布必须在信息流里（正对照，防过滤 vacuous）");
            Assert.IsTrue(feed.Any(e => e.Kind == EventKind.Protest), "涨价是玩家看得到的表象");
        }

        /// <summary>V7：ColonyEventPump 把 GameWorld 事件发布到 ZFrame EventBus；Day/Text 与 EventLog 一致；游标只前进。</summary>
        [Test]
        public void V7_EventBusBridge()
        {
            var w = BuildWorld();
            var received = new List<ColonyEventDto>();
            var pump = new ColonyEventPump();

            // ① 政策：挖坑给商人所在的港口，暗药给黑水堡（demo 第 1 天剧本）
            NamedNpc merchant = w.Named.First(n => n.IsMerchant);
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsTrap), w.RegionOf(merchant), execution: 0.4f);
            PolicySystem.Enact(w, w.PolicyLibrary.First(p => p.IsDarkMedicine), "黑水堡", execution: 0.9f);

            // ② 文化加权怨气 → 商人涨价抗议（抗议入日志是编排层职责，与 demo 宿主同款）
            merchant.Grievance = Math.Max(merchant.Grievance, 0.6f);
            merchant.Protest = ProtestRouter.ChooseProtest(merchant, merchant.Grievance);
            (float multiplier, string reason) = ProtestEffects.Price(merchant, w.Regions[0].Reputation());
            w.Log.Add(w.Day, EventKind.Protest, w.RegionOf(merchant),
                $"商人 {merchant.Name} 的货价 ×{multiplier:0.00}（{reason}）", EventVisibility.Public);

            // ③ 玩家观察取得间接证据（Personal 事件）
            w.Governor.PresentRegion = w.RegionOf(merchant);
            for (int t = 0; t < 6 && !merchant.Protest.DiscoveredByPlayer; t++) w.Governor.Observe(w, merchant);

            // ④⑤ 桥接：EventLog 新事件 → ZFrame EventBus（Publish 在 ZFrame 的实际 API 名为 Fire，立即派发）
            using (EventBus.Subscribe<ColonyEventDto>(dto => received.Add(dto)))
            {
                pump.Pump(w);          // 第 1 天事件：政策×4 + 抗议 + 观察
                w.AdvanceDays(31);     // 延迟后果到账
                pump.Pump(w);          // 爆雷事件入桥
            }

            Assert.GreaterOrEqual(received.Count, 2, "桥必须把 EventLog 的新事件发布到 EventBus");
            Assert.GreaterOrEqual(received.Count(dto => dto.Kind == "Policy"), 1, "至少一条 Policy 事件");
            Assert.GreaterOrEqual(received.Count(dto => dto.Kind == "Protest"), 1, "至少一条 Protest 事件");
            Assert.IsTrue(received.Any(dto => dto.Text.Contains("一个月")), "延迟爆雷事件也必须经 EventBus 发布");

            foreach (ColonyEventDto dto in received)
            {
                GameEvent src = w.Log.All.FirstOrDefault(e => e.Day == dto.Day && e.Text == dto.Text);
                Assert.IsNotNull(src, $"每条 DTO 必须能在 EventLog 找到同源事件：[{dto.Day}] {dto.Text}");
                Assert.AreEqual(dto.Kind, src.Kind.ToString(), "Kind 必须与 EventLog 一致");
                Assert.AreEqual(dto.Visibility, (byte)src.Vis, "可见性必须与 EventLog 一致");
                Assert.AreEqual(dto.Where, src.Where, "地点必须与 EventLog 一致");
            }

            int before = received.Count;
            pump.Pump(w);
            Assert.AreEqual(before, received.Count, "游标只前进：重复 Pump 不得重发旧事件");
            Assert.AreEqual(w.Log.All.Count, pump.Consumed, "Consumed 必须等于已发布的日志条数");
        }
    }
}
