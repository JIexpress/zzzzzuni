// ColonySaveBridge.cs —— TASK-A-COL-03：ColonySaveData ↔ ZFrame SaveSystem payload 通道的测试侧桥。
// 桥放测试侧是 A-COL-02 的 Adapter 先例（ColonyConfigAdapter）：Game.Domain.Colony 只引 ZFrame.Core，
// 不能引 ZFrame.Data（UnityEngine）；生产接线时由宿主程序集（Game.Bootstrap）承担同一职责。
// payload 通道：ISaveable.Save(SaveWriter) 写本模块的 JObject 节；Load(SaveReader) 读回——
// SaveSystem 负责 {version, meta, modules} 文件结构、原子写与三候选恢复，本桥只管 DTO ↔ JSON 字段映射。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Domain.Colony;
using Newtonsoft.Json.Linq;
using ZFrame.Data.Save;

namespace Game.Domain.Tests
{
    /// <summary>
    /// ColonySaveData 的 ISaveable 模块：把整个存档 DTO 作为本模块 payload 节写进/读出存档文件。
    /// SaveKey 是存档格式的迁移锚（不可中途改名）。
    /// </summary>
    public sealed class ColonySaveModule : ISaveable
    {
        /// <summary>本模块在存档文件 modules{} 里的节名。</summary>
        public const string Key = "colony-world";

        /// <summary>payload 根字段：DTO 树挂在它下面，将来模块内再加子结构互不冲突。</summary>
        public const string PayloadField = "world";

        private ColonySaveData _data;

        public ColonySaveModule(ColonySaveData initial = null) => _data = initial;

        /// <summary>当前持有的存档 DTO（Save 前由调用方塞入；Load 后为读回结果）。</summary>
        public ColonySaveData Data => _data;

        public string SaveKey => Key;

        public void Save(SaveWriter writer)
        {
            if (_data == null) throw new InvalidOperationException("ColonySaveModule.Save: 没有可写的存档 DTO");
            writer.Set(PayloadField, ColonySaveCodec.ToJToken(_data));
        }

        public void Load(SaveReader reader)
        {
            _data = reader.TryGet(PayloadField, out JToken token) && token is JObject obj
                ? ColonySaveCodec.FromJToken(obj)
                : null;   // 空节（模块后装）按"没有存档"处理，与 SaveSystem 语义一致
        }
    }

    /// <summary>
    /// ColonySaveData ↔ JToken 的手工字段映射（1:1，无业务逻辑）。不用反射序列化：
    /// 每个字段显式写出，浮点经 float→double 精确表示、JSON 往返逐位可复现（S1/S5 的字段级等价断言依赖它）。
    /// 映射缺一个字段 = 存档丢状态——S1/S5 的 DeepEquals 会把它抓出来。
    /// </summary>
    public static class ColonySaveCodec
    {
        // ── 标量读写助手（严格模式：缺字段即抛——丢字段的往返是 codec bug，必须炸在测试里）──
        private static JValue Req(JObject o, string key)
        {
            return o[key] as JValue
                ?? throw new InvalidOperationException($"ColonySaveCodec: 存档字段缺失或类型不符：{key}");
        }

        private static JToken W(float v) => new JValue(Convert.ToDouble(v, CultureInfo.InvariantCulture));
        private static JToken W(uint v) => new JValue((long)v);
        private static JToken W(byte v) => new JValue((long)v);
        private static JToken W(sbyte v) => new JValue((long)v);

        private static float RF(JObject o, string key) => Req(o, key).Value<float>();
        private static int RI(JObject o, string key) => Req(o, key).Value<int>();
        private static long RL(JObject o, string key) => Req(o, key).Value<long>();
        private static uint RU(JObject o, string key) => Req(o, key).Value<uint>();
        private static bool RB(JObject o, string key) => Req(o, key).Value<bool>();
        private static string RS(JObject o, string key) => Req(o, key).Value<string>() ?? "";

        // 可空字符串：具名 NPC 的骨架字段（组织者不填 → null）与契约 intelCarried（未离开 → null）
        // 的 null 性必须原样往返——强转空串会让读档世界悄悄变形。
        private static JToken SN(string v) => v == null ? JValue.CreateNull() : new JValue(v);
        private static string RN(JObject o, string key)
        {
            JValue v = Req(o, key);
            return v.Type == JTokenType.Null ? null : v.Value<string>();
        }

        private static JObject Obj(JObject o, string key) =>
            o[key] as JObject ?? throw new InvalidOperationException($"ColonySaveCodec: 缺少对象节 {key}");
        private static JArray Arr(JObject o, string key) =>
            o[key] as JArray ?? throw new InvalidOperationException($"ColonySaveCodec: 缺少数组节 {key}");

        /// <summary>宽容读：节缺失（旧存档）时按空数组处理——TASK-A-COL-09 进度三节的存档兼容。</summary>
        private static JArray ArrOpt(JObject o, string key) => o[key] as JArray ?? new JArray();

        private static string S(string v) => v ?? "";    // DTO 字符串字段不允许 JSON null 往返

        // ────────────────────────── 写出（DTO → JToken）──────────────────────────

        public static JObject ToJToken(ColonySaveData d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            var root = new JObject
            {
                ["worldSeed"] = new JValue(d.WorldSeed),
                ["day"] = new JValue((long)d.Day),
                ["backgroundSlotCount"] = d.BackgroundSlotCount,
            };

            root["profiledSlots"] = new JArray(d.ProfiledSlots.Select(s => new JObject
            {
                ["slotId"] = s.SlotId,
                ["flag"] = W(s.Flag),
                ["name"] = S(s.Name),
                ["job"] = S(s.Job),
                ["origin"] = S(s.Origin),
                ["baseLoyalty"] = W(s.BaseLoyalty),
                ["grievanceBias"] = W(s.GrievanceBias),
                ["speechTrait"] = S(s.SpeechTrait),
            }));

            root["named"] = new JArray(d.Named.Select(NamedJ));
            root["settlements"] = new JArray(d.Settlements.Select(s => new JObject
            {
                ["name"] = S(s.Name),
                ["population"] = s.Population,
                ["avgLoyalty"] = W(s.AvgLoyalty),
                ["avgGrievance"] = W(s.AvgGrievance),
                ["organization"] = W(s.Organization),
                ["leanMean"] = W(s.LeanMean),
                ["leanVariance"] = W(s.LeanVariance),
                ["foodSupply"] = W(s.FoodSupply),
                ["housing"] = W(s.Housing),
                ["security"] = W(s.Security),
                ["grievanceFloor"] = W(s.GrievanceFloor),
                ["namedNpcs"] = new JArray(s.NamedNpcs.Select(i => new JValue((long)i))),
            }));
            root["regions"] = new JArray(d.Regions.Select(r => new JObject
            {
                ["id"] = r.Id,
                ["name"] = S(r.Name),
                ["avgAttitude"] = W(r.AvgAttitude),
                ["policyExecution"] = W(r.PolicyExecution),
                ["policyBenefit"] = W(r.PolicyBenefit),
                ["deeds"] = new JArray(r.Deeds.Select(DeedJ)),
                ["rumors"] = new JArray(r.Rumors.Select(DeedJ)),
            }));

            var gov = d.Governor;
            root["governor"] = new JObject
            {
                ["production"] = W(gov.Production),
                ["tradeValue"] = W(gov.TradeValue),
                ["militaryDeed"] = W(gov.MilitaryDeed),
                ["credibility"] = W(gov.Credibility),
                ["promisesKept"] = gov.PromisesKept,
                ["promisesBroken"] = gov.PromisesBroken,
                ["repressionCount"] = gov.RepressionCount,
                ["intelPoints"] = gov.IntelPoints,         // TASK-A-COL-07：情报点随档（缺一处=存档丢状态）
                ["presentRegion"] = S(gov.PresentRegion),
                ["lastVisit"] = JObject.FromObject(gov.LastVisit),
                ["informants"] = new JArray(gov.Informants.Select(i => new JValue(i))),
                ["intel"] = new JArray(gov.Intel.Select(i => new JObject
                {
                    ["claim"] = S(i.Claim),
                    ["boughtDay"] = W(i.BoughtDay),
                    ["validDays"] = i.ValidDays,
                    ["forged"] = i.Forged,
                })),
            };

            root["delayed"] = new JArray(d.Delayed.Select(e => new JObject
            {
                ["dueDay"] = W(e.DueDay),
                ["kind"] = W(e.Kind),
                ["where"] = S(e.Where),
                ["text"] = S(e.Text),
                ["vis"] = W(e.Vis),
            }));
            root["market"] = new JArray(d.Market.Select(m => new JObject
            {
                ["name"] = S(m.Name),
                ["stock"] = W(m.Stock),
                ["stockBase"] = W(m.StockBase),
                ["dailyDrift"] = W(m.DailyDrift),
                ["basePrice"] = W(m.BasePrice),
                ["convoys"] = new JArray(m.Convoys.Select(c => new JValue(c))),
            }));
            root["edges"] = new JArray(d.Edges.Select(e => new JObject
            {
                ["a"] = e.A,
                ["b"] = e.B,
                ["affinity"] = W(e.Affinity),
                ["publicAffinity"] = W(e.PublicAffinity),
                ["type"] = W(e.Type),
                ["trust"] = W(e.Trust),
                ["debt"] = W(e.Debt),
            }));

            root["policies"] = new JArray(d.Policies.Select(p => new JObject
            {
                ["id"] = p.Id,
                ["name"] = S(p.Name),
                ["desc"] = S(p.Desc),
                ["benefit"] = S(p.Benefit),
                ["cost"] = S(p.Cost),
                ["whoUnhappy"] = S(p.WhoUnhappy),
                ["difficulty"] = S(p.Difficulty),
                ["isTrap"] = p.IsTrap,
                ["isDarkMedicine"] = p.IsDarkMedicine,
            }));
            root["quests"] = new JArray(d.Quests.Select(q => new JObject
            {
                ["name"] = S(q.Name),
                ["triggerText"] = S(q.TriggerText),
                ["involvedText"] = S(q.InvolvedText),
                ["coreText"] = S(q.CoreText),
                ["aftermathText"] = S(q.AftermathText),
                ["noInterventionText"] = S(q.NoInterventionText),
                ["options"] = new JArray(q.Options.Select(o => new JValue(o))),
                ["delayDays"] = q.DelayDays,
                ["worldAdvances"] = q.WorldAdvances,
                ["tKind"] = W(q.TKind),
                ["tArg"] = W(q.TArg),
                ["tRegion"] = S(q.TRegion),
                ["triggered"] = q.Triggered,
                ["resolved"] = q.Resolved,
                ["triggeredDay"] = W(q.TriggeredDay),
            }));
            root["questDelayed"] = new JArray(d.QuestDelayed.Select(x => new JObject
            {
                ["questIndex"] = x.QuestIndex,
                ["dueDay"] = W(x.DueDay),
            }));
            root["dialogue"] = new JArray(d.Dialogue.Select(l => new JObject
            {
                ["role"] = S(l.Role),
                ["topic"] = S(l.Topic),
                ["line"] = S(l.Line),
            }));

            // 第二批内容进度（TASK-A-COL-09）：三表已触发 Id 集合随档（Restore 不重放）。
            root["firedRelations"] = new JArray(d.FiredRelations.Select(x => new JValue(x)));
            root["firedTrades"] = new JArray(d.FiredTrades.Select(x => new JValue(x)));
            root["firedLife"] = new JArray(d.FiredLife.Select(x => new JValue(x)));
            return root;
        }

        private static JObject DeedJ(ColonySaveData.RegionStateDto.DeedState d) => new JObject
        {
            ["type"] = W(d.Type),
            ["magnitude"] = W(d.Magnitude),
            ["witnessCount"] = d.WitnessCount,
            ["isLegendary"] = d.IsLegendary,
        };

        private static JObject NamedJ(ColonySaveData.NamedNpcState n)
        {
            var o = new JObject
            {
                ["id"] = n.Id,
                ["name"] = S(n.Name),
                ["job"] = S(n.Job),
                ["origin"] = S(n.Origin),
                ["role"] = S(n.Role),
                ["loyalty"] = W(n.Loyalty),
                ["grievance"] = W(n.Grievance),
                ["organization"] = W(n.Organization),
                ["factionLean"] = W(n.FactionLean),
                ["maskStrength"] = W(n.MaskStrength),
                ["knownLoyalty"] = W(n.KnownLoyalty),
                ["lastObserved"] = W(n.LastObserved),
                ["recognitionScore"] = W(n.RecognitionScore),
                ["agreeableness"] = W(n.Agreeableness),
                ["neuroticism"] = W(n.Neuroticism),
                ["honestyHumility"] = W(n.HonestyHumility),
                ["conscientiousness"] = W(n.Conscientiousness),
                ["extraversion"] = W(n.Extraversion),
                ["status"] = W(n.Status),
                ["isMerchant"] = n.IsMerchant,
                ["fatigue"] = W(n.Fatigue),
                ["fear"] = W(n.Fear),
                ["quirk"] = S(n.Quirk),
                ["want"] = S(n.Want),
                ["conflict"] = S(n.Conflict),
                ["secret"] = S(n.Secret),
                ["speech"] = S(n.Speech),
                ["relationToPower"] = S(n.RelationToPower),
            };

            var c = n.Contract;
            o["contract"] = new JObject
            {
                ["coreBelief"] = S(c.CoreBelief),
                ["wonts"] = new JArray(c.Wonts.Select(w => new JValue(w))),
                ["tolerance"] = c.Tolerance,
                ["violations"] = c.Violations,
                ["daysInStage"] = c.DaysInStage,
                ["daysSinceViolation"] = c.DaysSinceViolation,
                ["stage"] = W(c.Stage),
                ["cooperation"] = W(c.Cooperation),
                ["leaveStyle"] = W(c.LeaveStyle),
                ["intelCarried"] = S(c.IntelCarried),
            };

            o["protest"] = n.Protest == null ? JValue.CreateNull() : new JObject
            {
                ["level"] = W(n.Protest.Level),
                ["duration"] = W(n.Protest.Duration),
                ["forms"] = new JArray(n.Protest.Forms.Select(f => new JValue((long)f))),
                ["isCovert"] = n.Protest.IsCovert,
                ["discoveredByPlayer"] = n.Protest.DiscoveredByPlayer,
            };

            var cu = n.Culture;
            o["culture"] = new JObject
            {
                ["offenseToPublicShame"] = W(cu.OffenseToPublicShame),
                ["offenseToContractBreak"] = W(cu.OffenseToContractBreak),
                ["offenseToDoctrine"] = W(cu.OffenseToDoctrine),
                ["expressiveness"] = W(cu.Expressiveness),
                ["patience"] = W(cu.Patience),
                ["recovery"] = W(cu.Recovery),
                ["collectivism"] = W(cu.Collectivism),
                ["formalityToStrangers"] = W(cu.FormalityToStrangers),
                ["target"] = W(cu.Target),
            };

            o["mf"] = new JArray(n.MF.Select(W));

            var img = n.Image;
            o["image"] = new JObject
            {
                ["entries"] = new JArray(img.Entries.Select(e => new JObject
                {
                    ["claim"] = S(e.Claim),
                    ["weight"] = W(e.Weight),
                    ["distortion"] = W(e.Distortion),
                    ["channel"] = S(e.Channel),
                    ["day"] = W(e.Day),
                })),
                ["beliefs"] = JObject.FromObject(img.Beliefs),
            };
            return o;
        }

        // ────────────────────────── 读回（JToken → DTO）──────────────────────────

        public static ColonySaveData FromJToken(JObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var d = new ColonySaveData
            {
                WorldSeed = Req(root, "worldSeed").Value<ulong>(),
                Day = RU(root, "day"),
                BackgroundSlotCount = RI(root, "backgroundSlotCount"),
            };

            foreach (var t in Arr(root, "profiledSlots"))
            {
                var o = (JObject)t;
                d.ProfiledSlots.Add(new ColonySaveData.ProfiledSlotState
                {
                    SlotId = RI(o, "slotId"),
                    Flag = (byte)RL(o, "flag"),
                    Name = RS(o, "name"),
                    Job = RS(o, "job"),
                    Origin = RS(o, "origin"),
                    BaseLoyalty = RF(o, "baseLoyalty"),
                    GrievanceBias = RF(o, "grievanceBias"),
                    SpeechTrait = RS(o, "speechTrait"),
                });
            }

            foreach (var t in Arr(root, "named")) d.Named.Add(NamedFrom((JObject)t));
            foreach (var t in Arr(root, "settlements"))
            {
                var o = (JObject)t;
                d.Settlements.Add(new ColonySaveData.SettlementState
                {
                    Name = RS(o, "name"),
                    Population = RI(o, "population"),
                    AvgLoyalty = RF(o, "avgLoyalty"),
                    AvgGrievance = RF(o, "avgGrievance"),
                    Organization = RF(o, "organization"),
                    LeanMean = RF(o, "leanMean"),
                    LeanVariance = RF(o, "leanVariance"),
                    FoodSupply = RF(o, "foodSupply"),
                    Housing = RF(o, "housing"),
                    Security = RF(o, "security"),
                    GrievanceFloor = RF(o, "grievanceFloor"),
                    NamedNpcs = Arr(o, "namedNpcs").Select(x => (int)((JValue)x).Value<long>()).ToList(),
                });
            }
            foreach (var t in Arr(root, "regions"))
            {
                var o = (JObject)t;
                d.Regions.Add(new ColonySaveData.RegionStateDto
                {
                    Id = RI(o, "id"),
                    Name = RS(o, "name"),
                    AvgAttitude = RF(o, "avgAttitude"),
                    PolicyExecution = RF(o, "policyExecution"),
                    PolicyBenefit = RF(o, "policyBenefit"),
                    Deeds = Arr(o, "deeds").Select(DeedFrom).ToList(),
                    Rumors = Arr(o, "rumors").Select(DeedFrom).ToList(),
                });
            }

            var g = Obj(root, "governor");
            d.Governor = new ColonySaveData.GovernorState
            {
                Production = RF(g, "production"),
                TradeValue = RF(g, "tradeValue"),
                MilitaryDeed = RF(g, "militaryDeed"),
                Credibility = RF(g, "credibility"),
                PromisesKept = RI(g, "promisesKept"),
                PromisesBroken = RI(g, "promisesBroken"),
                RepressionCount = RI(g, "repressionCount"),
                IntelPoints = RI(g, "intelPoints"),
                PresentRegion = RS(g, "presentRegion"),
                Intel = Arr(g, "intel").Select(t =>
                {
                    var o = (JObject)t;
                    return new ColonySaveData.GovernorState.IntelState
                    {
                        Claim = RS(o, "claim"), BoughtDay = RU(o, "boughtDay"),
                        ValidDays = RI(o, "validDays"), Forged = RB(o, "forged"),
                    };
                }).ToList(),
            };
            foreach (var kv in Obj(g, "lastVisit"))
                d.Governor.LastVisit[kv.Key] = ((JValue)kv.Value).Value<uint>();
            foreach (var t in Arr(g, "informants")) d.Governor.Informants.Add(((JValue)t).Value<string>());

            foreach (var t in Arr(root, "delayed"))
            {
                var o = (JObject)t;
                d.Delayed.Add(new ColonySaveData.DelayedEventState
                {
                    DueDay = RU(o, "dueDay"), Kind = (byte)RL(o, "kind"),
                    Where = RS(o, "where"), Text = RS(o, "text"), Vis = (byte)RL(o, "vis"),
                });
            }
            foreach (var t in Arr(root, "market"))
            {
                var o = (JObject)t;
                d.Market.Add(new ColonySaveData.MarketNodeState
                {
                    Name = RS(o, "name"), Stock = RF(o, "stock"), StockBase = RF(o, "stockBase"),
                    DailyDrift = RF(o, "dailyDrift"), BasePrice = RF(o, "basePrice"),
                    Convoys = Arr(o, "convoys").Select(x => ((JValue)x).Value<string>()).ToList(),
                });
            }
            foreach (var t in Arr(root, "edges"))
            {
                var o = (JObject)t;
                d.Edges.Add(new ColonySaveData.EdgeState
                {
                    A = RI(o, "a"), B = RI(o, "b"),
                    Affinity = RF(o, "affinity"), PublicAffinity = RF(o, "publicAffinity"),
                    Type = (byte)RL(o, "type"), Trust = RF(o, "trust"), Debt = RF(o, "debt"),
                });
            }

            foreach (var t in Arr(root, "policies"))
            {
                var o = (JObject)t;
                d.Policies.Add(new ColonySaveData.PolicyDefState
                {
                    Id = RI(o, "id"), Name = RS(o, "name"), Desc = RS(o, "desc"),
                    Benefit = RS(o, "benefit"), Cost = RS(o, "cost"),
                    WhoUnhappy = RS(o, "whoUnhappy"), Difficulty = RS(o, "difficulty"),
                    IsTrap = RB(o, "isTrap"), IsDarkMedicine = RB(o, "isDarkMedicine"),
                });
            }
            foreach (var t in Arr(root, "quests"))
            {
                var o = (JObject)t;
                d.Quests.Add(new ColonySaveData.QuestState
                {
                    Name = RS(o, "name"), TriggerText = RS(o, "triggerText"), InvolvedText = RS(o, "involvedText"),
                    CoreText = RS(o, "coreText"), AftermathText = RS(o, "aftermathText"),
                    NoInterventionText = RS(o, "noInterventionText"),
                    Options = Arr(o, "options").Select(x => ((JValue)x).Value<string>()).ToList(),
                    DelayDays = RI(o, "delayDays"), WorldAdvances = RB(o, "worldAdvances"),
                    TKind = (byte)RL(o, "tKind"), TArg = RF(o, "tArg"), TRegion = RS(o, "tRegion"),
                    Triggered = RB(o, "triggered"), Resolved = RB(o, "resolved"), TriggeredDay = RU(o, "triggeredDay"),
                });
            }
            foreach (var t in Arr(root, "questDelayed"))
            {
                var o = (JObject)t;
                d.QuestDelayed.Add(new ColonySaveData.QuestDelayedState
                {
                    QuestIndex = RI(o, "questIndex"), DueDay = RU(o, "dueDay"),
                });
            }
            foreach (var t in Arr(root, "dialogue"))
            {
                var o = (JObject)t;
                d.Dialogue.Add(new ColonySaveData.DialogueLineState
                {
                    Role = RS(o, "role"), Topic = RS(o, "topic"), Line = RS(o, "line"),
                });
            }

            // 第二批内容进度（TASK-A-COL-09）：读侧宽容——旧存档无这三节时按缺省空集读回
            // （存档兼容；写侧恒写出，新档往返仍严格）。
            foreach (var t in ArrOpt(root, "firedRelations")) d.FiredRelations.Add(((JValue)t).Value<string>());
            foreach (var t in ArrOpt(root, "firedTrades")) d.FiredTrades.Add(((JValue)t).Value<string>());
            foreach (var t in ArrOpt(root, "firedLife")) d.FiredLife.Add(((JValue)t).Value<string>());
            return d;
        }

        private static ColonySaveData.RegionStateDto.DeedState DeedFrom(JToken t)
        {
            var o = (JObject)t;
            return new ColonySaveData.RegionStateDto.DeedState
            {
                Type = (byte)RL(o, "type"), Magnitude = RF(o, "magnitude"),
                WitnessCount = RI(o, "witnessCount"), IsLegendary = RB(o, "isLegendary"),
            };
        }

        private static ColonySaveData.NamedNpcState NamedFrom(JObject o)
        {
            var n = new ColonySaveData.NamedNpcState
            {
                Id = RI(o, "id"),
                Name = RS(o, "name"), Job = RS(o, "job"), Origin = RS(o, "origin"), Role = RS(o, "role"),
                Loyalty = RF(o, "loyalty"), Grievance = RF(o, "grievance"), Organization = RF(o, "organization"),
                FactionLean = (sbyte)RL(o, "factionLean"), MaskStrength = RF(o, "maskStrength"),
                KnownLoyalty = RF(o, "knownLoyalty"), LastObserved = RU(o, "lastObserved"),
                RecognitionScore = RF(o, "recognitionScore"),
                Agreeableness = RF(o, "agreeableness"), Neuroticism = RF(o, "neuroticism"),
                HonestyHumility = RF(o, "honestyHumility"), Conscientiousness = RF(o, "conscientiousness"),
                Extraversion = RF(o, "extraversion"), Status = RF(o, "status"),
                IsMerchant = RB(o, "isMerchant"),
                Fatigue = RF(o, "fatigue"), Fear = RF(o, "fear"),
                Quirk = RS(o, "quirk"), Want = RS(o, "want"), Conflict = RS(o, "conflict"),
                Secret = RS(o, "secret"), Speech = RS(o, "speech"), RelationToPower = RS(o, "relationToPower"),
            };

            var c = Obj(o, "contract");
            n.Contract = new ColonySaveData.NamedNpcState.ContractState
            {
                CoreBelief = RS(c, "coreBelief"),
                Wonts = Arr(c, "wonts").Select(x => ((JValue)x).Value<string>()).ToList(),
                Tolerance = RI(c, "tolerance"), Violations = RI(c, "violations"),
                DaysInStage = RI(c, "daysInStage"), DaysSinceViolation = RI(c, "daysSinceViolation"),
                Stage = (byte)RL(c, "stage"), Cooperation = RF(c, "cooperation"),
                LeaveStyle = (byte)RL(c, "leaveStyle"), IntelCarried = RS(c, "intelCarried"),
            };

            n.Protest = o["protest"] is JObject p
                ? new ColonySaveData.NamedNpcState.ProtestState
                {
                    Level = RF(p, "level"), Duration = RF(p, "duration"),
                    Forms = Arr(p, "forms").Select(x => (int)((JValue)x).Value<long>()).ToList(),
                    IsCovert = RB(p, "isCovert"), DiscoveredByPlayer = RB(p, "discoveredByPlayer"),
                }
                : null;

            var cu = Obj(o, "culture");
            n.Culture = new ColonySaveData.NamedNpcState.CultureState
            {
                OffenseToPublicShame = RF(cu, "offenseToPublicShame"),
                OffenseToContractBreak = RF(cu, "offenseToContractBreak"),
                OffenseToDoctrine = RF(cu, "offenseToDoctrine"),
                Expressiveness = RF(cu, "expressiveness"),
                Patience = RF(cu, "patience"),
                Recovery = RF(cu, "recovery"),
                Collectivism = RF(cu, "collectivism"),
                FormalityToStrangers = RF(cu, "formalityToStrangers"),
                Target = (byte)RL(cu, "target"),
            };

            n.MF = Arr(o, "mf").Select(x => ((JValue)x).Value<float>()).ToArray();

            var img = Obj(o, "image");
            n.Image = new ColonySaveData.NamedNpcState.ImageState
            {
                Entries = Arr(img, "entries").Select(t =>
                {
                    var e = (JObject)t;
                    return new ColonySaveData.NamedNpcState.ImageState.EntryState
                    {
                        Claim = RS(e, "claim"), Weight = RF(e, "weight"), Distortion = RF(e, "distortion"),
                        Channel = RS(e, "channel"), Day = RU(e, "day"),
                    };
                }).ToList(),
            };
            foreach (var kv in Obj(img, "beliefs"))
                n.Image.Beliefs[kv.Key] = ((JValue)kv.Value).Value<float>();
            return n;
        }
    }
}
