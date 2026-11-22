using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Quest;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    // ────────────────────────────────────────────────────────────────────────
    // 数据输入行（plain C#，零引擎依赖）。字段与 Assets/Configs/CSV/ 五张注解
    // CSV（A-CONF-02 管线生成的 NpcsConfigRow 等）一一对应，由上层
    // ConfigService 适配器做 1:1 投影后喂给 ColonyDataLoader。
    // Game.Domain.Colony 只引用 ZFrame.Core（架构矩阵 A-COL-01 条目），
    // 不引用 ZFrame.Data（UnityEngine），所以这里定义自己的行形状。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>places.csv 一行（120 行）。</summary>
    public sealed class ColonyPlaceInput
    {
        public string Id = "";
        public string Name = "";
        public string Type = "";
        public int Population;
        public string Industry = "";
        public string Blurb = "";
        public bool Decay;      // 源数据【衰败】标记
        public bool Ongoing;    // 源数据【进行中】标记
    }

    /// <summary>npcs.csv 一行（100 张 GLM 人设卡）。</summary>
    public sealed class ColonyNpcInput
    {
        public string Id = "";
        public string Name = "";
        public int Age;
        public string Job = "";
        public string Origin = "";
        public string Want = "";
        public string Wont = "";
        public string Conflict = "";
        public string Secret = "";
        public string Speech = "";
        public string RelationToPower = "";
    }

    /// <summary>policies.csv 一行（40 条）。</summary>
    public sealed class ColonyPolicyInput
    {
        public string Id = "";
        public string Name = "";
        public string Desc = "";
        public string Benefit = "";
        public string Cost = "";
        public string WhoUnhappy = "";
        public string Difficulty = "";
        public bool IsTrap;          // 挖坑：看起来好实际上很糟
        public bool IsDarkMedicine;  // 暗药：看起来糟实际上有奇效
    }

    /// <summary>quests.csv 一行（12 则支线）。</summary>
    public sealed class ColonyQuestInput
    {
        public string Id = "";
        public string Name = "";
        public string Trigger = "";
        public string Involved = "";
        public string Core = "";
        public string OptionA = "";
        public string OptionB = "";
        public string OptionC = "";
        public string Aftermath = "";
        public int DelayMonths;        // 「延迟：N 个月后」→ N
        public bool WorldAdvances;     // 不干预也推进（「世界不等你」）
    }

    /// <summary>dialogue.csv 一行（300 条烘焙台词）。</summary>
    public sealed class ColonyDialogueInput
    {
        public string Role = "";    // 预生成池角色 A~E（DialogueBank.JobToRole 映射目标）
        public string Topic = "";
        public string Line = "";
    }

    /// <summary>五数据源打包（ColonyDataLoader.BuildWorld 的唯一输入）。</summary>
    public sealed class ColonyWorldData
    {
        public List<ColonyNpcInput> Npcs = new();
        public List<ColonyPlaceInput> Places = new();
        public List<ColonyPolicyInput> Policies = new();
        public List<ColonyQuestInput> Quests = new();
        public List<ColonyDialogueInput> Dialogue = new();

        /// <summary>
        /// 第二批内容三表（TASK-A-COL-09：关系网 40/贸易 20/人生节点 190）。null = 未接内容
        /// （既有五表建局路径零变化、行为逐位不变）；非 null 时 BuildWorld 注入 GameWorld.Content
        /// 挂点。内容本身不入存档（进度集合才入——ColonySaveData.FiredRelations/Trades/Life）。
        /// </summary>
        public ColonyContentBundle Content;
    }

    /// <summary>
    /// 数据驱动建局：GLM 五数据源（100 NPC 卡 / 120 地点 / 40 政策 / 12 支线 / 300 台词）
    /// → GameWorld 组装。逻辑与 ColonyCore/App/Program.cs「建局 · GLM 五数据源 → 世界」段
    /// 及 A-COL-01 内存 fixture（ColonyVerticalLoopTests.BuildWorld）一致，只是数据来源
    /// 换成 ConfigService CSV 管线（TASK-A-COL-02）。
    ///
    /// 映射常量来源（均为迁移时已定种子值，实测调整）：
    ///   - 地区态度：Program.cs 建局段——type 含「城」+0.15 / 「堡」-0.15 / 「港」+0.10 /
    ///     其他 +0.05；【衰败】-0.05；【进行中】-0.10。
    ///   - 文化按出身：NPC规格第三部分 §21（文化不改参数，改映射；移民二代 = 两套系数插值）
    ///     + Program.cs 同款 switch。
    ///   - 人格/忠诚/怨气/信念契约：NpcSystem SeedLoader 启发式（GLM 卡文本特征 →
    ///     HEXACO 简化五轴 + relation_to_power 四态 + 24 职业信条表）。
    /// </summary>
    public static class ColonyDataLoader
    {
        // ── 映射常量（来源见类注释）──────────────────────────────────────

        /// <summary>地区基准态度（Program.cs 建局段）：type 关键字 → 基准值。</summary>
        private const float AttitudeCity = 0.15f;      // type 含「城」
        private const float AttitudeFort = -0.15f;     // type 含「堡」
        private const float AttitudePort = 0.10f;      // type 含「港」
        private const float AttitudeOther = 0.05f;
        private const float AttitudeDecayPenalty = -0.05f;   // 【衰败】
        private const float AttitudeOngoingPenalty = -0.10f; // 【进行中】

        /// <summary>聚落初值（Program.cs 建局段）：忠诚 0.55；怨气 0.35 + 衰败/进行中加压。</summary>
        private const float SettlementLoyalty = 0.55f;
        private const float SettlementGrievanceBase = 0.35f;
        private const float SettlementGrievanceDecay = 0.05f;
        private const float SettlementGrievanceOngoing = 0.10f;

        /// <summary>主城数量：人口前三建 RegionState+Settlement（Program.cs：Take(3)）。</summary>
        private const int MainPlaceCount = 3;

        /// <summary>
        /// 内容共享随机流的盐（原 Program.cs/A-COL-01 fixture 同款 seed^0xC0FFEE）。
        /// TASK-A-COL-03：存档 Restore 要重放这条流重建 NpcDirectory 槽位采样，盐值必须有唯一权威出处。
        /// </summary>
        public const ulong ContentRngSalt = 0xC0FFEEUL;

        /// <summary>建局流上每张卡的 MF 抽取次数（Range×6）——Restore 重放按 卡数×此值 消费同样的段。</summary>
        public const int MoralFoundationDraws = 6;

        /// <summary>背景层规模（Program.cs 建局段：十万背景层只是数字，8B/槽）。</summary>
        private const int BackgroundNpcCount = 100_000;

        /// <summary>商人职业（SeedLoader.MerchantJobs）：抗议手段是涨价（§3.3）。</summary>
        private static readonly HashSet<string> MerchantJobs = new() { "走私贩", "废料回收商", "赌场老板" };

        /// <summary>高地位职业（SeedLoader.HighStatusJobs）：status 采样区间不同。</summary>
        private static readonly HashSet<string> HighStatusJobs = new()
            { "医生", "教师", "记者", "神父", "会计", "农艺师", "义体技师", "赌场老板", "水质净化员" };

        /// <summary>relation_to_power 四态 → 初始真实状态（SeedLoader：种子值，实测调整）。</summary>
        private static readonly (string key, float loyalty, float grievance, int lean)[] RelationToPowerMap =
        {
            ("顺从", 0.65f, 0.20f, -20),
            ("怀疑", 0.45f, 0.40f, 0),
            ("敌意", 0.30f, 0.60f, 40),
        };
        private const float RelationDefaultLoyalty = 0.50f;    // SeedLoader default（无所谓）
        private const float RelationDefaultGrievance = 0.35f;
        private const int RelationDefaultLean = 0;

        /// <summary>职业信条表（SeedLoader.JobCreed，24 职业）：CoreBelief + 第二条 Won't。</summary>
        private static readonly Dictionary<string, (string belief, string wont2)> JobCreed = new()
        {
            ["矿工"] = ("巷道里的规矩比命硬", "绝不套着没检修的呼吸器下井"),
            ["冶炼工"] = ("炉子不看人脸色，只看料", "绝不在炉温没稳时开浇口"),
            ["船坞装配工"] = ("差一分都不能出坞", "绝不替没做过的事签字验收"),
            ["货运飞行员"] = ("货单就是承诺", "绝不超载飞夜航"),
            ["轨道拖船驾驶员"] = ("拖不动就说拖不动", "绝不为赶进度摘掉限位器"),
            ["水质净化员"] = ("水的事瞒不得", "绝不让超标水进管网"),
            ["医生"] = ("先救人，再谈规矩", "绝不拒收付不起钱的伤员"),
            ["护士"] = ("病床前没有官阶", "绝不执行口头医嘱"),
            ["教师"] = ("孩子不能只学听话", "绝不在成绩上动手脚"),
            ["厨师"] = ("饿着的人没法讲道理", "绝不把馊了的料端上桌"),
            ["酒保"] = ("吧台上人人平等", "绝不出卖客人的话"),
            ["神父"] = ("人可以倒，信仰不能", "绝不为权势破例主持仪式"),
            ["保安"] = ("守规矩的人不能被规矩坑", "绝不在巡逻表上补假记录"),
            ["赏金猎人"] = ("赏格要清白地拿", "绝不接没有凭证的悬赏"),
            ["走私贩"] = ("路是人走出来的", "绝不带不知道是什么的货"),
            ["废料回收商"] = ("没有废物，只有放错的东西", "绝不倒卖没消毒的医疗废料"),
            ["义体技师"] = ("改造之前先问疼不疼", "绝不做没签知情书的手术"),
            ["测绘员"] = ("图纸上不能有一笔编的", "绝不标注没走过的坐标"),
            ["通信员"] = ("信号断了要有人背", "绝不销毁原始通信记录"),
            ["农艺师"] = ("地不骗人，人才骗人", "绝不用绝育种子糊弄种植户"),
            ["会计"] = ("账平了，心才平", "绝不在没凭证的单子上签核"),
            ["记者"] = ("写下来的就得是真的", "绝不拿匿名稿直接头条"),
            ["妓女"] = ("身体是自己的，账也是", "绝不在没谈好价钱前上工"),
            ["赌场老板"] = ("台面要干净，手段在台下", "绝不让赌场的账过公家的手"),
        };
        private const string DefaultBelief = "守着手里这条命";             // SeedLoader 兜底
        private const string DefaultWont2 = "绝不为任何人赌上全部";

        // ── 建局 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 五数据源 → GameWorld（含文化/人格映射）。种子固定 → 全部随机流确定性可复现。
        /// </summary>
        public static GameWorld BuildWorld(ulong seed, ColonyWorldData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Places.Count < MainPlaceCount)
                throw new InvalidOperationException(
                    $"places 数据不足：建局需要人口前 {MainPlaceCount} 的地点，实际 {data.Places.Count}");

            var w = new GameWorld(seed);

            // ① 地点：人口前三建 RegionState+Settlement（Program.cs 建局段同式）
            var mainPlaces = data.Places.OrderByDescending(p => p.Population).Take(MainPlaceCount).ToList();
            var regions = mainPlaces.Select(RegionAttitudeOf).ToArray();
            w.BindRegions(regions);
            var mainNames = regions.Select(r => r.Name).ToArray();
            foreach (var p in mainPlaces)
            {
                w.Settlements.Add(new Settlement(p.Name, p.Population, SettlementLoyalty,
                    SettlementGrievanceBase
                    + (p.Decay ? SettlementGrievanceDecay : 0f)
                    + (p.Ongoing ? SettlementGrievanceOngoing : 0f)));
            }

            // ② NPC：SeedLoader 启发式映射 → 文化按出身（§21）→ MF/Quirk → 注册
            var npcs = new List<NamedNpc>(data.Npcs.Count);
            for (int i = 0; i < data.Npcs.Count; i++)
            {
                npcs.Add(BuildNpc(data.Npcs[i], i, seed));
            }

            var rr = new Rng(seed ^ ContentRngSalt);   // Program.cs 同款：文化/MF/背景层的共享随机流
            foreach (var n in npcs)
            {
                n.Culture = CultureOfOrigin(n.Origin);
                for (int i = 0; i < MoralFoundationDraws; i++) n.MF[i] = rr.Range(0.2f, 0.9f);
                n.Quirk = n.Speech.Length > 12 ? n.Speech[..12] : n.Speech;
            }

            w.RegisterNamed(npcs);
            w.Background = new NpcDirectory(BackgroundNpcCount, seed, regions, rr);

            // ③ 政策 / 支线 / 台词
            w.PolicyLibrary = data.Policies.Select(BuildPolicyDef).ToList();
            w.Quests.Library = data.Quests.Select((q, i) => BuildQuestDef(q, i, mainNames)).ToList();
            w.Dialogue.Load(data.Dialogue.Select(d => (d.Role, d.Topic, d.Line)));

            // ④ 第二批内容引擎（TASK-A-COL-09）：三表非 null 才注入挂点（五表路径零变化）
            if (data.Content != null) w.Content = new ColonyContentEngines(data.Content);

            return w;
        }

        // ── 地区（places）────────────────────────────────────────────────

        /// <summary>type 关键字 + 衰败/进行中 → 地区基准态度（Program.cs 建局段）。</summary>
        private static RegionState RegionAttitudeOf(ColonyPlaceInput p)
        {
            float attitude = p.Type.Contains("城") ? AttitudeCity
                : p.Type.Contains("堡") ? AttitudeFort
                : p.Type.Contains("港") ? AttitudePort
                : AttitudeOther;
            if (p.Decay) attitude += AttitudeDecayPenalty;
            if (p.Ongoing) attitude += AttitudeOngoingPenalty;
            return new RegionState(p.Name, attitude);
        }

        // ── 具名 NPC（npcs：SeedLoader 启发式）───────────────────────────

        private static NamedNpc BuildNpc(ColonyNpcInput c, int index, ulong seed)
        {
            // SeedLoader：每张卡一个独立 rng（Hash(worldSeed, i)）——同一张卡永远映射成同一个人。
            var rng = new Rng(Rng.Hash(seed, (ulong)index));

            // relation_to_power 四态 → 初始真实状态（SeedLoader：种子值，实测调整）
            float loyalty, grievance; int lean;
            var rel = RelationToPowerMap.FirstOrDefault(m => m.key == c.RelationToPower);
            if (rel.key != null)
            {
                (loyalty, grievance, lean) = (rel.loyalty, rel.grievance, rel.lean);
            }
            else
            {
                loyalty = RelationDefaultLoyalty; grievance = RelationDefaultGrievance; lean = RelationDefaultLean;
            }

            loyalty += rng.Range(-0.08f, 0.08f);
            grievance += rng.Range(-0.08f, 0.08f);

            // 人格轴（HEXACO 简化）：从卡的文本特征启发式映射，供抗议路由用（SeedLoader 同式）
            bool hasSecret = !string.IsNullOrEmpty(c.Secret);
            float neuroticism = (c.Conflict ?? "").Length > 30 ? 0.65f : 0.45f;
            float honesty = (c.Wont ?? "").Contains("签") || (c.Wont ?? "").Contains("假") ? 0.65f : rng.Range(0.2f, 0.5f);
            float extrav = (c.Speech ?? "").Contains("沉默") || (c.Speech ?? "").Contains("回避") || (c.Speech ?? "").Contains("反问")
                ? rng.Range(0.25f, 0.40f) : rng.Range(0.5f, 0.8f);
            float status = HighStatusJobs.Contains(c.Job) ? rng.Range(0.6f, 0.8f) : rng.Range(0.3f, 0.55f);

            var (belief, wont2) = JobCreed.TryGetValue(c.Job, out var creed)
                ? creed : (DefaultBelief, DefaultWont2);

            return new NamedNpc
            {
                Id = int.TryParse(c.Id, out int parsedId) ? parsedId : index,
                Name = c.Name,
                Job = c.Job,
                Origin = c.Origin,
                Loyalty = Math.Clamp(loyalty, 0f, 1f),
                Grievance = Math.Clamp(grievance, 0f, 1f),
                Organization = rng.Range(0.05f, 0.15f),
                FactionLean = (sbyte)Math.Clamp(lean + rng.RangeInt(-10, 10), -100, 100),
                MaskStrength = Math.Clamp(0.30f + (hasSecret ? 0.15f : 0f) + rng.Range(0f, 0.05f), 0f, 0.95f),
                Contract = new BeliefContract
                {
                    CoreBelief = belief,
                    Wonts = new List<string> { c.Wont, wont2 },   // MVP 2：一条 CoreBelief + 两条 Won't
                    Tolerance = 1,                                // §10：核心 NPC 给 1
                    // 离开方式由职业与态度决定（§4.3，SeedLoader 同式）
                    LeaveStyle = c.Job is "医生" or "教师" or "记者" or "农艺师" or "神父" or "通信员"
                        ? LeaveStyle.QuietTransfer
                        : c.Job is "船坞装配工" or "义体技师" or "冶炼工" or "轨道拖船驾驶员" or "水质净化员"
                        ? LeaveStyle.SlamDoor
                        : c.RelationToPower == "敌意" ? LeaveStyle.TurnYou : LeaveStyle.WriteItDown,
                },
                Agreeableness = rng.Range(0.3f, 0.8f),
                Neuroticism = neuroticism,
                HonestyHumility = honesty,
                Conscientiousness = 0.7f,     // 有明确 wont 的人有底线
                Extraversion = extrav,
                Status = status,
                IsMerchant = MerchantJobs.Contains(c.Job),
                Want = c.Want,
                Conflict = c.Conflict,
                Secret = c.Secret,
                Speech = c.Speech,
                RelationToPower = c.RelationToPower,
            };
        }

        /// <summary>
        /// 文化按出身分配（NPC规格第三部分 §21：移民二代 = 两套系数插值；Program.cs 同款 switch）：
        /// 船民 → Rural / 难民 → Mix(Rural, Doctrine, 0.5) / 地球 → Atomized / 其余 → Mix(Rural, Atomized, 0.5)。
        /// </summary>
        public static CulturalFrame CultureOfOrigin(string origin)
        {
            return origin switch
            {
                var o when o.Contains("船民") => CulturalFrame.Rural(),
                var o when o.Contains("难民") => CulturalFrame.Mix(CulturalFrame.Rural(), CulturalFrame.Doctrine(), 0.5f),
                var o when o.Contains("地球") => CulturalFrame.Atomized(),
                _ => CulturalFrame.Mix(CulturalFrame.Rural(), CulturalFrame.Atomized(), 0.5f),
            };
        }

        // ── 政策（policies）──────────────────────────────────────────────

        private static PolicyDef BuildPolicyDef(ColonyPolicyInput p)
        {
            return new PolicyDef
            {
                Id = int.TryParse(p.Id, out int id) ? id : 0,
                Name = p.Name,
                Desc = p.Desc,
                Benefit = p.Benefit,
                Cost = p.Cost,
                WhoUnhappy = p.WhoUnhappy,
                Difficulty = p.Difficulty,
                IsTrap = p.IsTrap,
                IsDarkMedicine = p.IsDarkMedicine,
            };
        }

        // ── 支线（quests）────────────────────────────────────────────────

        /// <summary>
        /// 支线映射（ColonyCore Data/DataLoader.LoadQuests 同式）：
        /// 「坟山北坡」特例按语义（GrievanceAbove 0.35 @ 主地区[1]），其余按日程解锁
        /// （DayReached 15 + i*8）；延迟 N 个月 → N×30 天；不干预推进的支线带兜底文本。
        /// </summary>
        private static QuestDef BuildQuestDef(ColonyQuestInput q, int index, string[] mainRegions)
        {
            var def = new QuestDef
            {
                Name = q.Name,
                TriggerText = q.Trigger,
                InvolvedText = q.Involved,
                CoreText = q.Core,
                AftermathText = q.Aftermath,
                NoInterventionText = q.WorldAdvances ? "局面向更坏的一侧继续发展。" : "",
                DelayDays = q.DelayMonths * 30,
                WorldAdvances = q.WorldAdvances,
                TRegion = q.Name == "坟山北坡" ? mainRegions[1] : mainRegions[index % mainRegions.Length],
                Options = new List<string> { q.OptionA, q.OptionB, q.OptionC },
            };
            (def.TKind, def.TArg) = q.Name == "坟山北坡"
                ? (TriggerKind.GrievanceAbove, 0.35f)
                : (TriggerKind.DayReached, 15f + index * 8f);
            return def;
        }
    }
}
