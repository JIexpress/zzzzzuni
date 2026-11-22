using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>
    /// Colony 存档 DTO（TASK-A-COL-03）——《声望认知与人口》§4.4：种子固定 → 结果可复现，
    /// 存档只带「哪些 slot 已实例化」+ 已实例化的覆盖值 + 各层真实状态。纯数据零行为，
    /// 序列化由桥接层（生产侧宿主程序集 / 测试侧 ColonySaveCodec）承担，本程序集零 ZFrame.Data 依赖。
    ///
    /// 逐字段覆盖简报清单：worldSeed、day、背景层已实例化 slot+档案字段、具名 NPC 全量真实状态
    /// （loyalty/grievance/organization/factionLean/maskStrength/knownLoyalty/lastObserved/recognitionScore
    /// + 契约 stage/violations/daysInStage/cooperation/intelCarried + protest level/forms/isCovert/discovered
    /// + culture 八浮点 + MF 六权重 + image.entries/beliefs）、聚落参数（含 grievanceFloor/leanMean/
    /// leanVariance/namedNpcs）、地区（policyExecution/policyBenefit/deeds/rumorPool）、governor
    /// （production/tradeValue/militaryDeed/credibility/promises/repressionCount/lastVisit/informants/intel）、
    /// 延迟后果队列（dueDay/kind/where/text/vis）、市场（stock/basePrice/convoys）、关系边
    /// （affinity/publicAffinity/type/trust/debt）。
    ///
    /// 简报清单之外的三节（Restore 的唯一输入是本 DTO，缺了它们读档世界行为残缺，已登记待主控裁决）：
    ///   Policies / Quests(+QuestDelayed) / Dialogue——内容库不可从种子重导（来自 ConfigService 数据），
    ///   不随档走则读档后支线全空、政策不可颁布、「世界不等你」到期回找丢失。
    /// </summary>
    public sealed class ColonySaveData
    {
        public ulong WorldSeed;
        public uint Day;

        // ── 背景层（§4.2：无档案 8B/槽——目录本体不入档，只带已实例化槽）──
        public int BackgroundSlotCount;
        public List<ProfiledSlotState> ProfiledSlots = new();

        /// <summary>一个被玩家实例化过的背景槽：标记位原样 + 档案覆盖值（§4.4）。</summary>
        public sealed class ProfiledSlotState
        {
            public int SlotId;
            public byte Flag;                 // NpcSlot.Flag 原样（bit0 有档案 / bit1 已具名）
            public string Name = "";
            public string Job = "";
            public string Origin = "";
            public float BaseLoyalty;
            public float GrievanceBias;
            public string SpeechTrait = "";
        }

        // ── 具名层（全量真实状态 + 身份/人格：身份与人格来自卡片数据，不可从种子重导）──
        public List<NamedNpcState> Named = new();

        public sealed class NamedNpcState
        {
            public int Id;
            public string Name = "";
            public string Job = "";
            public string Origin = "";
            public string Role = "";

            // 真实状态（玩家永远看不到）
            public float Loyalty;
            public float Grievance;
            public float Organization;
            public sbyte FactionLean;
            public float MaskStrength;

            // 玩家认知
            public float KnownLoyalty;
            public uint LastObserved;
            public float RecognitionScore;

            // 人格（抗议路由 / 干预者选择需要）
            public float Agreeableness, Neuroticism, HonestyHumility, Conscientiousness, Extraversion;
            public float Status;
            public bool IsMerchant;

            // 骨架与状态
            public float Fatigue, Fear;
            public string Quirk = "";
            public string Want = "";
            public string Conflict = "";
            public string Secret = "";
            public string Speech = "";
            public string RelationToPower = "";

            public ContractState Contract = new();
            public ProtestState Protest;                       // 无抗议时为 null
            public CultureState Culture = new();
            public float[] MF = new float[6];
            public ImageState Image = new();

            /// <summary>信念契约（简报点名 stage/violations/daysInStage/cooperation/intelCarried；内容字段随档走否则契约行为残缺）。</summary>
            public sealed class ContractState
            {
                public string CoreBelief = "";
                public List<string> Wonts = new();
                public int Tolerance;
                public int Violations;
                public int DaysInStage;
                public int DaysSinceViolation;                 // 30/60 天升级线（private 字段，internal 通道存取）
                public byte Stage;                             // Stage
                public float Cooperation;
                public byte LeaveStyle;                        // LeaveStyle
                public string IntelCarried = "";
            }

            /// <summary>无声抗议（简报点名 level/forms/isCovert/discovered）。</summary>
            public sealed class ProtestState
            {
                public float Level;
                public float Duration;
                public List<int> Forms = new();                // ProtestForm
                public bool IsCovert;
                public bool DiscoveredByPlayer;
            }

            /// <summary>文化框架（简报点名八浮点；Target 决定忠诚指向，一并随档）。</summary>
            public sealed class CultureState
            {
                public float OffenseToPublicShame, OffenseToContractBreak, OffenseToDoctrine;
                public float Expressiveness, Patience, Recovery, Collectivism, FormalityToStrangers;
                public byte Target;                            // CulturalFrame.LoyaltyTarget
            }

            /// <summary>他眼中你是什么人（简报点名 image.entries/beliefs）。</summary>
            public sealed class ImageState
            {
                public List<EntryState> Entries = new();
                public Dictionary<string, float> Beliefs = new();

                public sealed class EntryState
                {
                    public string Claim = "";
                    public float Weight;
                    public float Distortion;
                    public string Channel = "";
                    public uint Day;
                }
            }
        }

        // ── 聚落层（简报点名 grievanceFloor/leanMean/leanVariance/namedNpcs）──
        public List<SettlementState> Settlements = new();

        public sealed class SettlementState
        {
            public string Name = "";
            public int Population;
            public float AvgLoyalty;
            public float AvgGrievance;
            public float Organization;
            public float LeanMean;
            public float LeanVariance;
            public float FoodSupply, Housing, Security;
            public float GrievanceFloor;
            public List<int> NamedNpcs = new();
        }

        // ── 地区（简报点名 policyExecution/policyBenefit/deeds/rumorPool）──
        public List<RegionStateDto> Regions = new();

        public sealed class RegionStateDto
        {
            public int Id;
            public string Name = "";
            public float AvgAttitude;
            public float PolicyExecution;
            public float PolicyBenefit;
            public List<DeedState> Deeds = new();
            public List<DeedState> Rumors = new();

            public sealed class DeedState
            {
                public byte Type;                              // DeedType
                public float Magnitude;
                public int WitnessCount;
                public bool IsLegendary;
            }
        }

        // ── 管理者（简报点名 production/tradeValue/militaryDeed/credibility/promises/
        //    repressionCount/lastVisit/informants/intel 含 boughtDay/validDays/forged；
        //    TASK-A-COL-07 增 intelPoints——存档不丢点数）──
        public GovernorState Governor = new();

        public sealed class GovernorState
        {
            public float Production, TradeValue, MilitaryDeed, Credibility;
            public int IntelPoints;                        // TASK-A-COL-07：情报点余额随档
            public int PromisesKept, PromisesBroken;
            public int RepressionCount;
            public string PresentRegion = "";
            public Dictionary<string, uint> LastVisit = new();
            public List<string> Informants = new();
            public List<IntelState> Intel = new();

            public sealed class IntelState
            {
                public string Claim = "";
                public uint BoughtDay;
                public int ValidDays;
                public bool Forged;
            }
        }

        // ── 延迟后果队列（简报点名 dueDay/kind/where/text/vis）──
        public List<DelayedEventState> Delayed = new();

        public sealed class DelayedEventState
        {
            public uint DueDay;
            public byte Kind;                                  // EventKind
            public string Where = "";
            public string Text = "";
            public byte Vis;                                   // EventVisibility
        }

        // ── 市场（简报点名 stock/basePrice/convoys；StockBase/DailyDrift 是漂移公式的输入，一并随档）──
        public List<MarketNodeState> Market = new();

        public sealed class MarketNodeState
        {
            public string Name = "";
            public float Stock, StockBase, DailyDrift, BasePrice;
            public List<string> Convoys = new();
        }

        // ── 关系边（简报点名 affinity/publicAffinity/type/trust/debt）──
        public List<EdgeState> Edges = new();

        public sealed class EdgeState
        {
            public int A, B;
            public float Affinity, PublicAffinity, Trust, Debt;
            public byte Type;                                  // RelationType
        }

        // ── 内容库（简报清单之外，登记待裁决：Restore 唯一输入是本 DTO）──
        public List<PolicyDefState> Policies = new();

        public sealed class PolicyDefState
        {
            public int Id;
            public string Name = "", Desc = "", Benefit = "", Cost = "", WhoUnhappy = "", Difficulty = "";
            public bool IsTrap, IsDarkMedicine;
        }

        public List<QuestState> Quests = new();
        public List<QuestDelayedState> QuestDelayed = new();

        public sealed class QuestState
        {
            public string Name = "", TriggerText = "", InvolvedText = "", CoreText = "", AftermathText = "", NoInterventionText = "";
            public List<string> Options = new();
            public int DelayDays;
            public bool WorldAdvances;
            public byte TKind;                                 // TriggerKind
            public float TArg;
            public string TRegion = "";
            public bool Triggered, Resolved;
            public uint TriggeredDay;
        }

        public sealed class QuestDelayedState
        {
            public int QuestIndex;
            public uint DueDay;
        }

        public List<DialogueLineState> Dialogue = new();

        public sealed class DialogueLineState
        {
            public string Role = "";
            public string Topic = "";
            public string Line = "";
        }
    }
}
