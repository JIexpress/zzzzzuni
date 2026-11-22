using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony.Dialogue;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Quest;

namespace Game.Domain.Colony.Sim
{
    /// <summary>
    /// 世界聚合根。三层规模、三种节奏（NPC规格 §2）：
    ///   具名（几百）每天 · 聚落（几千）每周 · 背景（十几万）只是数字。
    /// 性能原则：玩家看不见的，一律不模拟。
    /// </summary>
    public class GameWorld
    {
        public uint Day = 1;
        public Rng Rng;
        /// <summary>建局原种子（TASK-A-COL-03）：存档要带它——读档后 NpcDirectory 的确定性实例化与流重放都以它为根。</summary>
        public readonly ulong WorldSeed;
        public readonly List<string> DailyLog = new();      // 每日系统级事件（契约推进等）

        public RegionState[] Regions;
        public List<Settlement> Settlements = new();
        public List<NamedNpc> Named = new();
        public Dictionary<int, NamedNpc> NamedById = new();
        public NpcDirectory Background;
        public RelationshipGraph Relations = new();
        public EventLog Log = new();
        public MarketState Market = new();
        public Governor Governor = new();
        public QuestSystem Quests = new();
        public DialogueBank Dialogue = new();
        public TeachingSystem Teaching = new();
        public List<PolicyDef> PolicyLibrary = new();

        private readonly List<(uint dueDay, EventKind kind, string where, string text, EventVisibility vis)> _delayed = new();
        private readonly Dictionary<int, uint> _migrantUntil = new();
        private readonly Dictionary<int, string> _npcRegion = new();

        public GameWorld(ulong seed)
        {
            WorldSeed = seed;
            Rng = new Rng(seed);
        }

        // ── 建局 ──────────────────────────────────────────────
        public void BindRegions(RegionState[] regions)
        {
            Regions = regions;
            Market.Add(regions[0].Name, 4200f, 0.03f, 88f);   // 真实感④：矿区在减产
            Market.Add(regions[1].Name, 9000f, 0.005f, 80f);
        }

        public void RegisterNamed(IEnumerable<NamedNpc> npcs)
        {
            foreach (var n in npcs)
            {
                Named.Add(n);
                NamedById[n.Id] = n;
                _npcRegion[n.Id] = DefaultRegionOf(n);
                Relations.AddFromCard(n, NamedById, Rng);   // 见 RelationshipGraph 扩展：每人 5~15 条边
            }
        }

        /// <summary>
        /// 按 origin 分地区：船民去港口，难民去堡，教义文化进堡，其余进大城。
        /// TASK-A-COL-03：RegisterNamed 与存档 Restore 共用同一份实现，两条路径永不漂移。
        /// </summary>
        public string DefaultRegionOf(NamedNpc n) => n switch
        {
            _ when n.Culture.Target == CulturalFrame.LoyaltyTarget.Doctrine => Regions[1].Name,
            _ when n.Origin.StartsWith("船") || n.Origin.Contains("船民") => Regions[0].Name,
            _ when n.Origin.Contains("难民") => Regions[1].Name,
            _ => Regions[Regions.Length > 2 ? 2 : 0].Name,
        };

        public string RegionOf(NamedNpc n) => _npcRegion.TryGetValue(n.Id, out var r) ? r : Regions[0].Name;
        public Settlement SettlementOf(string name) => Settlements.FirstOrDefault(s => s.Name == name);

        // ── 延迟后果（真实感三支柱：不可逆的事会回来找你）──
        public void Delayed(uint dueDay, EventKind kind, string where, string text, EventVisibility vis)
            => _delayed.Add((dueDay, kind, where, text, vis));

        // TASK-A-COL-03：延迟后果队列是简报点名的存档字段——Capture 读、Restore 写，同程序集 internal。
        internal IReadOnlyList<(uint dueDay, EventKind kind, string where, string text, EventVisibility vis)> DelayedEntries
            => _delayed;

        internal void RestoreDelayedEntries(
            IEnumerable<(uint dueDay, EventKind kind, string where, string text, EventVisibility vis)> entries)
        {
            _delayed.Clear();
            foreach (var e in entries) _delayed.Add(e);
        }

        // ── 时间推进 ──────────────────────────────────────────
        public void AdvanceDays(int days)
        {
            for (int i = 0; i < days; i++)
            {
                DailyTick();
                if (Day % 7 == 0) WeekTick();
                if (Day % 30 == 0) MonthTick();
                Day++;
            }
        }

        private void DailyTick()
        {
            DailyLog.Clear();
            // 具名层：契约推进 / 面具裂缝 / 离开事件（每天，§2）
            foreach (var n in Named)
            {
                DailyLog.Clear();
                if (!n.DailyTick(Day, Rng, DailyLog)) continue;
                foreach (var line in DailyLog)
                    Log.Add(Day, line.Contains("带走的情报") ? EventKind.Departure : EventKind.Contract, RegionOf(n), line, EventVisibility.Personal);
            }

            // 关系网三通道：信息 / 情绪 / 组织（§15.4 每日结算）
            Relations.PropagateDaily(NamedById, Day, DailyLog, Rng);

            // 市场：状态表每天在动（真实感④——不看它，它也在动）
            Market.DailyDrift(this, Rng);

            // 面具动力学（§3 决定因素）
            MaskDynamics();

            // 支线：触发检查 + 到期回找 + 「世界不等你」
            Quests.CheckTriggers(this);
            Quests.TickDue(this);

            // 延迟后果到账
            foreach (var d in _delayed.Where(d => d.dueDay <= Day).ToList())
            {
                Log.Add(Day, d.kind, d.where, d.text, d.vis);
                _delayed.Remove(d);
            }
        }

        /// <summary>NPC规格 §3：恐怖统治 → 面具变厚「秩序井然」；亲自下基层 / 信息流通 → 面具被撕开。</summary>
        private void MaskDynamics()
        {
            float terror = Math.Min(0.06f, Governor.RepressionCount * 0.015f);
            foreach (var n in Named)
            {
                float infoFlow = n.GossipRate * 0.02f;
                float visit = 0f;
                var reg = RegionOf(n);
                if (Governor.LastVisit.TryGetValue(reg, out uint d) && Day - d <= 7) visit = 0.03f;
                n.MaskStrength = Math.Clamp(n.MaskStrength + terror - infoFlow - visit, 0f, 0.95f);
            }
        }

        private void WeekTick()
        {
            // 聚落：每周统计方程（§2）
            foreach (var s in Settlements)
            {
                s.WeeklyTick(Rng);
                var organizer = s.TrySpawnOrganizer(90_000 + s.Population, Rng);
                if (organizer != null)
                {
                    RegisterSingle(organizer, s.Name);
                    Log.Add(Day, EventKind.Uprising, s.Name,
                        $"组织度越过阈值 A（{Settlement.THRESHOLD_A}）：出现了第一个组织者——{organizer.Name}。他不是设计出来的，是从参数里长出来的。",
                        EventVisibility.Covert);   // 地下网络，玩家看不到（§23 内敛文化）
                }
            }

            // 人口流动（声望认知 §5.2）：每周 30~80 人从背景层流向枢纽，停 3~14 天
            int count = Rng.RangeInt(30, 81);
            for (int i = 0; i < count; i++)
            {
                int slot = Rng.RangeInt(0, Background.Count);
                _migrantUntil[slot] = Day + (uint)Rng.RangeInt(3, 15);
            }
            foreach (var k in _migrantUntil.Where(kv => kv.Value <= Day).Select(kv => kv.Key).ToList())
                _migrantUntil.Remove(k);
            Log.Add(Day, EventKind.Migration, "贸易枢纽", $"本周 {count} 人从各地涌入枢纽（在途 {_migrantUntil.Count} 人）。",
                EventVisibility.Personal);

            // 「他认识你，你不认识他」（§5.3）：知道你的概率 = 家乡声望×0.6 + 全局声望×0.3
            var reg = Regions[Rng.RangeInt(0, Regions.Length)];
            float knowsYou = Math.Clamp(((reg.Reputation() + 1f) / 2f) * 0.6f + Governor.Prestige.GlobalPrestige * 0.3f, 0f, 1f);
            if (Rng.Chance(knowsYou * 0.3f))
                Log.Add(Day, EventKind.Reputation, reg.Name,
                    "有个边境来的人认出了你——点头的样子说明他家乡流传着你的故事。你不知道他是谁。",
                    EventVisibility.Personal);
        }

        private void MonthTick()
        {
            Governor.Prestige.MonthlyDecay();
            Governor.Prestige.SettleCredibility();
            Log.Add(Day, EventKind.Reputation, "全局",
                $"月度声望结算：全局 {Governor.Prestige.GlobalPrestige:0.00} → 你现在被称为 {Governor.Prestige.Title()}（信誉半衰期回归：{Governor.Prestige.Credibility:0.00}）",
                EventVisibility.Public);
        }

        /// <summary>
        /// 单个具名 NPC 注册（不建关系边——组织者不走卡片建网）。
        /// TASK-A-COL-03：Restore 按存档重建具名层时复用同一入口（RegionOf 的地区映射随之成立）。
        /// </summary>
        public void RegisterSingle(NamedNpc n, string region)
        {
            Named.Add(n);
            NamedById[n.Id] = n;
            _npcRegion[n.Id] = region;
        }
    }
}
