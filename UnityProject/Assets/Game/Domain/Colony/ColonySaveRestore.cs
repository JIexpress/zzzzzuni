using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Quest;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 读档重建（TASK-A-COL-03）：Restore(ColonySaveData) → GameWorld，纯 Domain。
    /// §4.4：种子固定 → 结果可复现——
    ///   · 背景层目录用原种子重建：先重放建局流上「MF 抽取段」（卡数×6 次抽取），再按建局同式
    ///     构造 NpcDirectory，槽位采样（BaseAttitude/RegionId）与建局逐位一致；已实例化槽再经
    ///     RestoreSlot 回填存档标记位 + 档案覆盖值（覆盖值与种子重生成逐字段一致，S2 断言兜底）。
    ///   · 关系图按存档边重建（不走 AddFromCard——那条路会重新掷骰并改怨气）。
    ///   · 具名 NPC 全量真实状态逐字段回填；地区映射复用 RegisterNamed 的同一套规则
    ///     （DefaultRegionOf），组织者按聚落 namedNpcs 反推注册地。
    ///   · 日志从零重建：读档世界的事件流只含读档后事件，ColonyEventPump 游标从 0 起算
    ///     （复用旧 pump 时先 Reset()）。
    /// </summary>
    public static class ColonySaveRestore
    {
        public static GameWorld Restore(ColonySaveData d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));

            var w = new GameWorld(d.WorldSeed);
            w.Day = d.Day;

            // ① 地区：重建 + 回填声望输入（deeds/rumors 经 AddDeed 按 WitnessCount 路由，与运行时同一条路径）
            var regions = new RegionState[d.Regions.Count];
            int maxRegionId = -1;
            for (int i = 0; i < d.Regions.Count; i++)
            {
                var s = d.Regions[i];
                var r = new RegionState(s.Name, s.AvgAttitude)
                {
                    PolicyExecution = s.PolicyExecution,
                    PolicyBenefit = s.PolicyBenefit,
                };
                r.Id = s.Id;
                maxRegionId = Math.Max(maxRegionId, s.Id);
                foreach (var deed in s.Deeds) r.AddDeed(new LocalDeed
                {
                    Type = (DeedType)deed.Type, Magnitude = deed.Magnitude,
                    WitnessCount = deed.WitnessCount, IsLegendary = deed.IsLegendary,
                });
                foreach (var rumor in s.Rumors) r.AddDeed(new LocalDeed
                {
                    Type = (DeedType)rumor.Type, Magnitude = rumor.Magnitude,
                    WitnessCount = rumor.WitnessCount, IsLegendary = rumor.IsLegendary,
                });
                regions[i] = r;
            }
            w.Regions = regions;
            if (maxRegionId >= 0) RegionState._nextId = maxRegionId + 1;   // 静态计数器对齐存档，后续新地区不撞号

            // ② 聚落
            foreach (var s in d.Settlements)
            {
                var st = new Settlement(s.Name, s.Population, s.AvgLoyalty, s.AvgGrievance)
                {
                    Organization = s.Organization,
                    LeanMean = s.LeanMean,
                    LeanVariance = s.LeanVariance,
                    FoodSupply = s.FoodSupply,
                    Housing = s.Housing,
                    Security = s.Security,
                    GrievanceFloor = s.GrievanceFloor,
                };
                st.NamedNpcs.AddRange(s.NamedNpcs);
                w.Settlements.Add(st);
            }

            // ③ 具名层：全量回填 + 注册（不建边——边按存档重建）
            var organizerRegion = new Dictionary<int, string>();
            foreach (var st in w.Settlements)
                foreach (int id in st.NamedNpcs)
                    organizerRegion.TryAdd(id, st.Name);   // 组织者注册地 = 涌现它的聚落
            foreach (var s in d.Named)
            {
                var n = RestoreNpc(s);
                // 卡片 NPC 与 RegisterNamed 的 origin/文化规则同源；组织者（无 Origin 语义）走聚落反推
                string region = organizerRegion.TryGetValue(n.Id, out var org) ? org : w.DefaultRegionOf(n);
                w.RegisterSingle(n, region);
            }

            // ④ 关系图按存档边重建
            foreach (var e in d.Edges)
                w.Relations.Add(new RelationshipEdge
                {
                    A = e.A, B = e.B, Affinity = e.Affinity, PublicAffinity = e.PublicAffinity,
                    Type = (RelationType)e.Type, Trust = e.Trust, Debt = e.Debt,
                });

            // ⑤ 背景层：重放建局流 → 重建目录 → 回填已实例化槽
            int cardCount = d.Named.Count(x => x.Role != "Organizer");   // 建局流上每张卡抽 MF×6
            var rr = new Rng(d.WorldSeed ^ ColonyDataLoader.ContentRngSalt);
            for (int i = 0; i < cardCount * ColonyDataLoader.MoralFoundationDraws; i++) rr.NextFloat01();
            w.Background = new NpcDirectory(d.BackgroundSlotCount, d.WorldSeed, regions, rr);
            foreach (var s in d.ProfiledSlots)
            {
                w.Background.RestoreSlot(s.SlotId, s.Flag, new NpcProfile
                {
                    Name = s.Name, Job = s.Job, Origin = s.Origin,
                    BaseLoyalty = s.BaseLoyalty, GrievanceBias = s.GrievanceBias, SpeechTrait = s.SpeechTrait,
                });
            }

            // ⑥ 市场（BindRegions 的默认行情不重放——库存/价格/在途队全按存档）
            w.Market.Nodes.Clear();
            foreach (var s in d.Market)
                w.Market.Nodes.Add(new MarketNode
                {
                    Name = s.Name, Stock = s.Stock, StockBase = s.StockBase,
                    DailyDrift = s.DailyDrift, BasePrice = s.BasePrice,
                    Convoys = new List<string>(s.Convoys),
                });

            // ⑦ 管理者
            var g = d.Governor;
            w.Governor.Prestige.Production = g.Production;
            w.Governor.Prestige.TradeValue = g.TradeValue;
            w.Governor.Prestige.MilitaryDeed = g.MilitaryDeed;
            w.Governor.Prestige.PromisesKept = g.PromisesKept;
            w.Governor.Prestige.PromisesBroken = g.PromisesBroken;
            w.Governor.Prestige.RestoreCredibility(g.Credibility);
            w.Governor.RepressionCount = g.RepressionCount;
            w.Governor.PresentRegion = g.PresentRegion;
            w.Governor.RestoreIntelPoints(g.IntelPoints);   // TASK-A-COL-07：情报点随档回填（存档不丢点数）
            foreach (var kv in g.LastVisit) w.Governor.LastVisit[kv.Key] = kv.Value;
            foreach (var i in g.Informants) w.Governor.Informants.Add(i);
            foreach (var i in g.Intel)
                w.Governor.Intel.Add(new IntelReport
                {
                    Claim = i.Claim, BoughtDay = i.BoughtDay, ValidDays = i.ValidDays, Forged = i.Forged,
                });

            // ⑧ 内容库随档回填（登记待裁决节：Restore 的唯一输入是 DTO）
            w.PolicyLibrary = d.Policies.Select(p => new PolicyDef
            {
                Id = p.Id, Name = p.Name, Desc = p.Desc, Benefit = p.Benefit, Cost = p.Cost,
                WhoUnhappy = p.WhoUnhappy, Difficulty = p.Difficulty,
                IsTrap = p.IsTrap, IsDarkMedicine = p.IsDarkMedicine,
            }).ToList();

            w.Quests.Library = d.Quests.Select(q => new QuestDef
            {
                Name = q.Name, TriggerText = q.TriggerText, InvolvedText = q.InvolvedText,
                CoreText = q.CoreText, AftermathText = q.AftermathText, NoInterventionText = q.NoInterventionText,
                Options = new List<string>(q.Options), DelayDays = q.DelayDays, WorldAdvances = q.WorldAdvances,
                TKind = (TriggerKind)q.TKind, TArg = q.TArg, TRegion = q.TRegion,
                Triggered = q.Triggered, Resolved = q.Resolved, TriggeredDay = q.TriggeredDay,
            }).ToList();
            w.Quests.RestoreDelayed(d.QuestDelayed.Select(x => (x.QuestIndex, x.DueDay)));

            w.Dialogue.Load(d.Dialogue.Select(x => (x.Role, x.Topic, x.Line)));

            // ⑨ 延迟后果队列
            w.RestoreDelayedEntries(d.Delayed.Select(e =>
                (e.DueDay, (EventKind)e.Kind, e.Where, e.Text, (EventVisibility)e.Vis)));

            return w;
        }

        private static NamedNpc RestoreNpc(ColonySaveData.NamedNpcState s)
        {
            var n = new NamedNpc
            {
                Id = s.Id, Name = s.Name, Job = s.Job, Origin = s.Origin, Role = s.Role,
                Loyalty = s.Loyalty, Grievance = s.Grievance, Organization = s.Organization,
                FactionLean = s.FactionLean, MaskStrength = s.MaskStrength,
                KnownLoyalty = s.KnownLoyalty, LastObserved = s.LastObserved, RecognitionScore = s.RecognitionScore,
                Agreeableness = s.Agreeableness, Neuroticism = s.Neuroticism, HonestyHumility = s.HonestyHumility,
                Conscientiousness = s.Conscientiousness, Extraversion = s.Extraversion, Status = s.Status,
                IsMerchant = s.IsMerchant,
                Fatigue = s.Fatigue, Fear = s.Fear,
                Quirk = s.Quirk, Want = s.Want, Conflict = s.Conflict, Secret = s.Secret,
                Speech = s.Speech, RelationToPower = s.RelationToPower,
            };

            var c = s.Contract;
            if (c != null)
            {
                n.Contract = new BeliefContract
                {
                    CoreBelief = c.CoreBelief,
                    Wonts = new List<string>(c.Wonts),
                    Tolerance = c.Tolerance, Violations = c.Violations, DaysInStage = c.DaysInStage,
                    Stage = (Stage)c.Stage, Cooperation = c.Cooperation, LeaveStyle = (LeaveStyle)c.LeaveStyle,
                    IntelCarried = c.IntelCarried,
                };
                n.Contract.DaysSinceViolation = c.DaysSinceViolation;   // internal 通道：30/60 天升级线不断档
            }

            if (s.Protest != null)
            {
                n.Protest = new SilentProtest
                {
                    Level = s.Protest.Level, Duration = s.Protest.Duration,
                    Forms = new HashSet<ProtestForm>(s.Protest.Forms.Select(f => (ProtestForm)f)),
                    IsCovert = s.Protest.IsCovert, DiscoveredByPlayer = s.Protest.DiscoveredByPlayer,
                };
            }

            var cu = s.Culture;
            if (cu != null)
            {
                n.Culture = new CulturalFrame
                {
                    OffenseToPublicShame = cu.OffenseToPublicShame, OffenseToContractBreak = cu.OffenseToContractBreak,
                    OffenseToDoctrine = cu.OffenseToDoctrine, Expressiveness = cu.Expressiveness,
                    Patience = cu.Patience, Recovery = cu.Recovery, Collectivism = cu.Collectivism,
                    FormalityToStrangers = cu.FormalityToStrangers, Target = (CulturalFrame.LoyaltyTarget)cu.Target,
                };
            }

            for (int i = 0; i < 6 && i < s.MF.Length; i++) n.MF[i] = s.MF[i];

            var img = s.Image;
            if (img != null)
            {
                n.Image = new PlayerImage();
                foreach (var e in img.Entries)
                    n.Image.Entries.Add(new PlayerImage.Entry
                    {
                        Claim = e.Claim, Weight = e.Weight, Distortion = e.Distortion, Channel = e.Channel, Day = e.Day,
                    });
                foreach (var kv in img.Beliefs) n.Image.Beliefs[kv.Key] = kv.Value;
            }
            return n;
        }
    }
}
