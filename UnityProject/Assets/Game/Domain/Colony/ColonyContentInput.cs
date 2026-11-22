using System.Collections.Generic;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    // ────────────────────────────────────────────────────────────────────────
    // 第二批内容输入行（TASK-A-COL-09，plain C#，零引擎依赖）。
    // 与 Assets/Configs/CSV/ 三张注解 CSV（A-CONF-02 管线生成的 ColonyRelationsConfigRow /
    // ColonyTradesConfigRow / ColonyLifeConfigRow）一一对应，由上层 ConfigService 适配器
    // （ColonyConfigAdapter，宿主/测试两侧）做 1:1 投影后喂给三引擎。
    // Game.Domain.Colony 只引用 ZFrame.Core（架构矩阵 A-COL-01 条目），不引用 ZFrame.Data
    // （UnityEngine），所以这里定义自己的行形状——与 ColonyDataLoader 五个 *Input 同款纪律。
    // 数据源：C:\Users\1\WorkBuddy\杂项\参考资料\GLM产出\ 任务6（关系网 40 条）/ 任务7（贸易
    // 20 条）/ 任务9（人生节点 190 条），机检脚本 check_t6/t7/t9 全过；修头差异登记见任务日志。
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>colonyRelations.csv 一行（40 条 R01-R40：恩怨/人情债/师徒裂痕/表面亲密私下敌对）。</summary>
    public sealed class ColonyRelationEventInput
    {
        public string Id = "";          // R01..R40（源 event_id 1..40 的管线 Id 化）
        public int EventId;             // 源 event_id（1..40）
        public string NpcA = "";        // 「姓名（职业）」，逐字来自 100 卡（check_t6 契约）
        public string NpcB = "";
        public string PairType = "";    // 战友/同志/老朋友/死敌/表面亲密（各 8 条）
        public string Topic = "";       // 恩怨/人情债/师徒裂痕/表面亲密私下敌对/其他
        public string Event = "";       // 事件文本（≤60 字，一件具体的事）
        public float DA_Grievance;      // A 对管理层的怨气增量（±0.30、0.05 倍数）
        public float DA_Loyalty;        // A 忠诚增量
        public float DA_Org;            // A 组织度增量
        public float DB_Grievance;      // B 同款三项
        public float DB_Loyalty;
        public float DB_Org;
        public string Visibility = "";  // Public/Personal/Covert（19/15/6）
        public string Latent = "";      // 潜伏后果（Public 条目为「无」）
        public string Aftermath = "";   // 延迟后果（非「无」→ Delayed 队列，默认 30 天）
    }

    /// <summary>colonyTrades.csv 一行（20 条 T01-T20：五类各 4）。</summary>
    public sealed class ColonyTradeEventInput
    {
        public string Id = "";          // T01..T20（源首列 id，改名列名）
        public int EventId;             // 源 event_id（1..20）——CSV 无 trigger 列，触发日=EventId×5（登记待裁决）
        public string NpcA = "";        // 「姓名（职业）」
        public string NpcB = "";
        public string Places = "";      // 「地名、地名…」（≥2 个，逐字来自 120 地点总表）
        public string EventKind = "";   // 库存漂移/运输队失踪/价格联动/囤积居奇/走私暴露
        public string Event = "";       // 事件文本（≤60 字）
        public float DA_Grievance;      // 当事人六数值列（简报消费口径只限关系网引擎，贸易侧留痕不落状态——登记）
        public float DA_Loyalty;
        public float DA_Org;
        public float DB_Grievance;
        public float DB_Loyalty;
        public float DB_Org;
        public string PriceImpact = ""; // 「地名：货物 +N%；…」（10%~80%、5% 倍数）
        public string StockImpact = ""; // 「地名：货物库存 -N%；…」（10%~50%、5% 倍数）
        public string Visibility = "";  // Public/Personal/Covert（6/5/9）
        public string Latent = "";
    }

    /// <summary>colonyLife.csv 一行（190 条 L001-L190：100 人全覆盖、六类）。</summary>
    public sealed class ColonyLifeEventInput
    {
        public string Id = "";               // L001..L190
        public string Name = "";             // 当事人姓名（100 卡逐字）
        public string Npc = "";              // 「姓名（职业）」
        public string EventType = "";        // 结婚/丧亲/子女出生/升迁/旧伤复发/恩人归来
        public string Trigger = "";          // 「第 X-Y 天[，且其 grievance>0.5 时优先]」（190/190 有天数区间）
        public string Text = "";             // ≤40 字
        public string RewriteField = "";     // want/conflict/secret/无（非「无」145 条）
        public string RewriteText = "";      // 「从『X』改为『Y』」
        public string SettlementEffect = ""; // 含数字的聚落效果（平均参数 ±0.01~0.10 / 产能库存 ±5%~20% 持续 N 天）
    }

    /// <summary>三表打包（ColonyWorldData.Content 的载荷；建局时注入 ColonyContentEngines）。</summary>
    public sealed class ColonyContentBundle
    {
        public List<ColonyRelationEventInput> Relations = new();
        public List<ColonyTradeEventInput> Trades = new();
        public List<ColonyLifeEventInput> Life = new();
    }

    /// <summary>
    /// 三引擎共用的具名层查找（内容表按「姓名（职业）」引用具名层；100 卡名单唯一由
    /// check_t1 机检保证）。索引每次结算现建（具名层 ≤ 百级，E6 预算内），不缓存——
    /// Restore/具名化后无需失效通知。public 供测试侧复用（E 系按姓名取断言锚点）。
    /// </summary>
    public static class ColonyContentLookup
    {
        /// <summary>「姓名（职业）」→ 姓名（括号前缀；无括号原样返回）。</summary>
        public static string NameOf(string npc)
        {
            if (string.IsNullOrEmpty(npc)) return "";
            int open = npc.IndexOf('（');
            return open > 0 ? npc[..open] : npc;
        }

        /// <summary>具名层按姓名索引（同名后者覆盖——防御性；机检名单唯一）。</summary>
        public static Dictionary<string, NamedNpc> NameIndex(GameWorld w)
        {
            var idx = new Dictionary<string, NamedNpc>(w.Named.Count);
            foreach (var n in w.Named)
            {
                if (!string.IsNullOrEmpty(n.Name)) idx[n.Name] = n;
            }
            return idx;
        }

        /// <summary>visibility 字符串 → EventVisibility（非法值按 Personal 收敛——宁可见给玩家不可瞒报）。</summary>
        public static EventVisibility ParseVis(string v) => v switch
        {
            "Public" => EventVisibility.Public,
            "Covert" => EventVisibility.Covert,
            _ => EventVisibility.Personal,
        };
    }
}
