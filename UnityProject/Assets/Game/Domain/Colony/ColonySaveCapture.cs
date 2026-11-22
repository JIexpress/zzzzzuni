using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony.Dialogue;
using Game.Domain.Colony.Economy;
using Game.Domain.Colony.Policy;
using Game.Domain.Colony.Player;
using Game.Domain.Colony.Quest;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 存档采集（TASK-A-COL-03）：Capture(GameWorld) → ColonySaveData，纯 Domain。
    /// 逐字段覆盖简报清单的状态；只读世界，不改任何模拟行为。
    /// 序列化（DTO → payload）由桥接层承担，本程序集零 ZFrame.Data 依赖。
    /// </summary>
    public static class ColonySaveCapture
    {
        public static ColonySaveData Capture(GameWorld world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (world.Regions == null || world.Background == null)
                throw new InvalidOperationException("Capture: 世界尚未建局（Regions/Background 为空）——存档只对完成建局的世界有意义");

            var d = new ColonySaveData
            {
                WorldSeed = world.WorldSeed,
                Day = world.Day,
                BackgroundSlotCount = world.Background.Count,
            };

            CaptureBackground(world, d);
            foreach (var n in world.Named) d.Named.Add(CaptureNpc(n));
            foreach (var s in world.Settlements) d.Settlements.Add(CaptureSettlement(s));
            foreach (var r in world.Regions) d.Regions.Add(CaptureRegion(r));
            d.Governor = CaptureGovernor(world.Governor);

            foreach (var e in world.DelayedEntries)
                d.Delayed.Add(new ColonySaveData.DelayedEventState
                {
                    DueDay = e.dueDay, Kind = (byte)e.kind, Where = e.where, Text = e.text, Vis = (byte)e.vis,
                });

            foreach (var node in world.Market.Nodes)
                d.Market.Add(new ColonySaveData.MarketNodeState
                {
                    Name = node.Name, Stock = node.Stock, StockBase = node.StockBase,
                    DailyDrift = node.DailyDrift, BasePrice = node.BasePrice,
                    Convoys = new List<string>(node.Convoys),
                });

            // 关系边：AllEdges 已按 (min,max) 去重；Restore 按同一集合重建图。
            foreach (var e in world.Relations.AllEdges)
                d.Edges.Add(new ColonySaveData.EdgeState
                {
                    A = e.A, B = e.B, Affinity = e.Affinity, PublicAffinity = e.PublicAffinity,
                    Type = (byte)e.Type, Trust = e.Trust, Debt = e.Debt,
                });

            // 内容库随档（简报清单之外，登记待主控裁决——Restore 的唯一输入是 DTO）。
            foreach (var p in world.PolicyLibrary)
                d.Policies.Add(new ColonySaveData.PolicyDefState
                {
                    Id = p.Id, Name = p.Name, Desc = p.Desc, Benefit = p.Benefit, Cost = p.Cost,
                    WhoUnhappy = p.WhoUnhappy, Difficulty = p.Difficulty,
                    IsTrap = p.IsTrap, IsDarkMedicine = p.IsDarkMedicine,
                });

            for (int i = 0; i < world.Quests.Library.Count; i++)
            {
                var q = world.Quests.Library[i];
                d.Quests.Add(new ColonySaveData.QuestState
                {
                    Name = q.Name, TriggerText = q.TriggerText, InvolvedText = q.InvolvedText,
                    CoreText = q.CoreText, AftermathText = q.AftermathText, NoInterventionText = q.NoInterventionText,
                    Options = new List<string>(q.Options), DelayDays = q.DelayDays, WorldAdvances = q.WorldAdvances,
                    TKind = (byte)q.TKind, TArg = q.TArg, TRegion = q.TRegion,
                    Triggered = q.Triggered, Resolved = q.Resolved, TriggeredDay = q.TriggeredDay,
                });
            }
            foreach (var (q, due) in world.Quests.DelayedEntries)
            {
                int index = world.Quests.Library.IndexOf(q);
                if (index < 0) throw new InvalidOperationException("Capture: 延迟回找队列引用了不在库中的支线");
                d.QuestDelayed.Add(new ColonySaveData.QuestDelayedState { QuestIndex = index, DueDay = due });
            }

            foreach (var (role, topic, line) in world.Dialogue.AllLines())
                d.Dialogue.Add(new ColonySaveData.DialogueLineState { Role = role, Topic = topic, Line = line });

            return d;
        }

        // ── 背景层：§4.4——只带「已实例化标记 + 档案覆盖值」──
        private static void CaptureBackground(GameWorld world, ColonySaveData d)
        {
            foreach (int slotId in world.Background.ProfiledSlotIds)
            {
                if (!world.Background.TryGetProfile(slotId, out var p))
                    continue;   // 标记位在而档案不在：理论上不可达（EnsureProfile 同时写两处），宁缺勿炸
                var slot = world.Background.GetSlot(slotId);
                d.ProfiledSlots.Add(new ColonySaveData.ProfiledSlotState
                {
                    SlotId = slotId,
                    Flag = slot.Flag,
                    Name = p.Name,
                    Job = p.Job,
                    Origin = p.Origin,
                    BaseLoyalty = p.BaseLoyalty,
                    GrievanceBias = p.GrievanceBias,
                    SpeechTrait = p.SpeechTrait,
                });
            }
        }

        private static ColonySaveData.NamedNpcState CaptureNpc(NamedNpc n)
        {
            if (n.Contract == null) throw new InvalidOperationException($"Capture: 具名 NPC {n.Name} 的信念契约为空（每日结算依赖它，建局必须给全）");
            if (n.Image == null) throw new InvalidOperationException($"Capture: 具名 NPC {n.Name} 的 PlayerImage 为空");

            var s = new ColonySaveData.NamedNpcState
            {
                Id = n.Id, Name = n.Name, Job = n.Job, Origin = n.Origin, Role = n.Role,
                Loyalty = n.Loyalty, Grievance = n.Grievance, Organization = n.Organization,
                FactionLean = n.FactionLean, MaskStrength = n.MaskStrength,
                KnownLoyalty = n.KnownLoyalty, LastObserved = n.LastObserved, RecognitionScore = n.RecognitionScore,
                Agreeableness = n.Agreeableness, Neuroticism = n.Neuroticism, HonestyHumility = n.HonestyHumility,
                Conscientiousness = n.Conscientiousness, Extraversion = n.Extraversion, Status = n.Status,
                IsMerchant = n.IsMerchant,
                Fatigue = n.Fatigue, Fear = n.Fear,
                Quirk = n.Quirk, Want = n.Want, Conflict = n.Conflict, Secret = n.Secret,
                Speech = n.Speech, RelationToPower = n.RelationToPower,
            };

            var c = n.Contract;
            s.Contract = new ColonySaveData.NamedNpcState.ContractState
            {
                CoreBelief = c.CoreBelief,
                Wonts = new List<string>(c.Wonts ?? new List<string>()),
                Tolerance = c.Tolerance, Violations = c.Violations, DaysInStage = c.DaysInStage,
                DaysSinceViolation = c.DaysSinceViolation,
                Stage = (byte)c.Stage, Cooperation = c.Cooperation, LeaveStyle = (byte)c.LeaveStyle,
                IntelCarried = c.IntelCarried,
            };

            if (n.Protest != null)
            {
                s.Protest = new ColonySaveData.NamedNpcState.ProtestState
                {
                    Level = n.Protest.Level, Duration = n.Protest.Duration,
                    Forms = n.Protest.Forms.Select(f => (int)f).ToList(),
                    IsCovert = n.Protest.IsCovert, DiscoveredByPlayer = n.Protest.DiscoveredByPlayer,
                };
            }

            var cu = n.Culture;
            s.Culture = new ColonySaveData.NamedNpcState.CultureState
            {
                OffenseToPublicShame = cu.OffenseToPublicShame, OffenseToContractBreak = cu.OffenseToContractBreak,
                OffenseToDoctrine = cu.OffenseToDoctrine, Expressiveness = cu.Expressiveness,
                Patience = cu.Patience, Recovery = cu.Recovery, Collectivism = cu.Collectivism,
                FormalityToStrangers = cu.FormalityToStrangers, Target = (byte)cu.Target,
            };

            s.MF = (float[])n.MF.Clone();

            s.Image = new ColonySaveData.NamedNpcState.ImageState
            {
                Entries = n.Image.Entries.Select(e => new ColonySaveData.NamedNpcState.ImageState.EntryState
                {
                    Claim = e.Claim, Weight = e.Weight, Distortion = e.Distortion, Channel = e.Channel, Day = e.Day,
                }).ToList(),
                Beliefs = new Dictionary<string, float>(n.Image.Beliefs),
            };
            return s;
        }

        private static ColonySaveData.SettlementState CaptureSettlement(Settlement s) =>
            new ColonySaveData.SettlementState
            {
                Name = s.Name, Population = s.Population,
                AvgLoyalty = s.AvgLoyalty, AvgGrievance = s.AvgGrievance, Organization = s.Organization,
                LeanMean = s.LeanMean, LeanVariance = s.LeanVariance,
                FoodSupply = s.FoodSupply, Housing = s.Housing, Security = s.Security,
                GrievanceFloor = s.GrievanceFloor,
                NamedNpcs = new List<int>(s.NamedNpcs),
            };

        private static ColonySaveData.RegionStateDto CaptureRegion(RegionState r) =>
            new ColonySaveData.RegionStateDto
            {
                Id = r.Id, Name = r.Name, AvgAttitude = r.AvgAttitude,
                PolicyExecution = r.PolicyExecution, PolicyBenefit = r.PolicyBenefit,
                Deeds = r.Deeds.Select(CaptureDeed).ToList(),
                Rumors = r.Rumors.Select(CaptureDeed).ToList(),
            };

        private static ColonySaveData.RegionStateDto.DeedState CaptureDeed(LocalDeed d) =>
            new ColonySaveData.RegionStateDto.DeedState
            {
                Type = (byte)d.Type, Magnitude = d.Magnitude, WitnessCount = d.WitnessCount, IsLegendary = d.IsLegendary,
            };

        private static ColonySaveData.GovernorState CaptureGovernor(Governor g)
        {
            var s = new ColonySaveData.GovernorState
            {
                Production = g.Prestige.Production, TradeValue = g.Prestige.TradeValue,
                MilitaryDeed = g.Prestige.MilitaryDeed, Credibility = g.Prestige.Credibility,
                PromisesKept = g.Prestige.PromisesKept, PromisesBroken = g.Prestige.PromisesBroken,
                RepressionCount = g.RepressionCount, PresentRegion = g.PresentRegion,
                LastVisit = new Dictionary<string, uint>(g.LastVisit),
                Informants = g.Informants.ToList(),
            };
            foreach (var i in g.Intel)
                s.Intel.Add(new ColonySaveData.GovernorState.IntelState
                {
                    Claim = i.Claim, BoughtDay = i.BoughtDay, ValidDays = i.ValidDays, Forged = i.Forged,
                });
            return s;
        }
    }
}
