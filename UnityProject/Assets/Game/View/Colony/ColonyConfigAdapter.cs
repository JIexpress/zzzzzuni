// ColonyConfigAdapter.cs —— TASK-A-COL-05：生产侧的 ConfigService → ColonyWorldData 投影。
// A-COL-02 架构裁决原文（HANDOFF）：「ConfigService→DTO 投影放测试侧 ColonyConfigAdapter，
// 生产接线时宿主程序集承担——Domain 保持零配置资产依赖。」本类就是那份生产侧承担：
// 投影逻辑与 Game.Domain.Tests.ColonyConfigAdapter 1:1（两侧刻意独立存在，不合并——
// 测试侧在 Editor-only 测试程序集，本类在运行时宿主程序集）。
// 表资产加载（AssetDatabase）是编辑器通道：编辑器 Play 模式（含 PlayMode 测试）成立；
// 真机 build 的资源路径（Resources/Addressables）登记为后续工作（见任务日志风险节）。

using System;
using System.Collections.Generic;
using Game.Domain.Colony;
using ZFrame.Data.Config;
#if UNITY_EDITOR
using UnityEditor;
using ZFrame.Data.ConfigEditor;
#endif

namespace Game.View.Colony
{
    /// <summary>GLM 五数据源 → 建局输入的 1:1 投影（A-COL-02 先例的生产侧副本）。</summary>
    public static class ColonyConfigAdapter
    {
        /// <summary>
        /// 五张表全部加载后投影为建局输入；缺表即抛（fail-fast，不静默半建局）。
        /// 与测试侧 ColonyConfigAdapter.ToWorldData 逐字段一致（A-COL-02 D1 抽查口径的投影源）。
        /// </summary>
        public static ColonyWorldData ToWorldData(IConfigService config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

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

        /// <summary>
        /// 第二批内容三表（TASK-A-COL-09：colonyRelations/colonyTrades/colonyLife）→
        /// ColonyContentBundle 投影（1:1，无业务逻辑）。调用方把它塞进 ColonyWorldData.Content
        /// 后，ColonyDataLoader.BuildWorld 注入 GameWorld.Content 挂点（三引擎每日结算）。
        /// 与五表 ToWorldData 分离：五表调用方（含生产宿主）不强制接内容——
        /// ColonyWorldData.Content 为 null 时建局行为与 A-COL-02 逐位一致。
        /// </summary>
        public static ColonyContentBundle ToContentBundle(IConfigService config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var bundle = new ColonyContentBundle();
            foreach (var r in Require<ColonyRelationsConfigRow>(config))
            {
                bundle.Relations.Add(new ColonyRelationEventInput
                {
                    Id = r.Id, EventId = r.EventId,
                    NpcA = r.NpcA, NpcB = r.NpcB, PairType = r.PairType, Topic = r.Topic, Event = r.Event,
                    DA_Grievance = r.DAGrievance, DA_Loyalty = r.DALoyalty, DA_Org = r.DAOrg,
                    DB_Grievance = r.DBGrievance, DB_Loyalty = r.DBLoyalty, DB_Org = r.DBOrg,
                    Visibility = r.Visibility, Latent = r.Latent, Aftermath = r.Aftermath,
                });
            }

            foreach (var r in Require<ColonyTradesConfigRow>(config))
            {
                bundle.Trades.Add(new ColonyTradeEventInput
                {
                    Id = r.Id, EventId = r.EventId,
                    NpcA = r.NpcA, NpcB = r.NpcB, Places = r.Places, EventKind = r.EventKind, Event = r.Event,
                    DA_Grievance = r.DAGrievance, DA_Loyalty = r.DALoyalty, DA_Org = r.DAOrg,
                    DB_Grievance = r.DBGrievance, DB_Loyalty = r.DBLoyalty, DB_Org = r.DBOrg,
                    PriceImpact = r.PriceImpact, StockImpact = r.StockImpact,
                    Visibility = r.Visibility, Latent = r.Latent,
                });
            }

            foreach (var r in Require<ColonyLifeConfigRow>(config))
            {
                bundle.Life.Add(new ColonyLifeEventInput
                {
                    Id = r.Id, Name = r.Name, Npc = r.Npc, EventType = r.EventType,
                    Trigger = r.Trigger, Text = r.Text,
                    RewriteField = r.RewriteField, RewriteText = r.RewriteText,
                    SettlementEffect = r.SettlementEffect,
                });
            }

            return bundle;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器通道：五张 CFG_*Table 资产按 A-CONF-02 约定路径加载进共享 ConfigService
        /// （Services.Get&lt;IConfigService&gt;，Load 的同型重载语义 = 就地替换不换 GUID），
        /// 再投影为建局输入。真机 build 无 AssetDatabase——由调用方 fail-soft（宿主禁用模拟并
        /// 报错，不崩游戏）。
        /// </summary>
        public static ColonyWorldData LoadWorldData(IConfigService config)
        {
            LoadTable<NpcsConfigRow>(config, "npcs");
            LoadTable<PlacesConfigRow>(config, "places");
            LoadTable<PoliciesConfigRow>(config, "policies");
            LoadTable<QuestsConfigRow>(config, "quests");
            LoadTable<DialogueConfigRow>(config, "dialogue");
            return ToWorldData(config);
        }

        /// <summary>
        /// 编辑器通道：第二批内容三表资产加载 + 投影（TASK-A-COL-09）。缺表即抛
        /// （fail-fast，与 LoadWorldData 同纪律）。E 系数据驱动测试与后续宿主接线共用。
        /// </summary>
        public static ColonyContentBundle LoadContentBundle(IConfigService config)
        {
            LoadTable<ColonyRelationsConfigRow>(config, "colonyRelations");
            LoadTable<ColonyTradesConfigRow>(config, "colonyTrades");
            LoadTable<ColonyLifeConfigRow>(config, "colonyLife");
            return ToContentBundle(config);
        }

        /// <summary>按 A-CONF-02 约定路径加载已提交的表资产（路径由产品代码推导，不手写）。</summary>
        private static void LoadTable<TRow>(IConfigService config, string stem) where TRow : ConfigRow
        {
            var csvPath = $"{CsvImporter.CsvRoot}/{stem}.csv";
            var assetPath = CsvExporter.TableAssetPathFor(csvPath);
            var table = AssetDatabase.LoadAssetAtPath<ConfigTable<TRow>>(assetPath);
            if (table == null)
            {
                throw new InvalidOperationException(
                    $"殖民地表资产缺失：{assetPath}（应随 A-COL-02 提交；缺失时按 A-CONF-02 菜单链路从 {csvPath} 重新导入）");
            }

            config.Load(table);
        }
#endif

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
}
