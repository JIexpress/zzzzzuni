// ColonyAction.cs —— TASK-A-COL-06：具名 NPC 的每日自主行为候选集（效用 AI 接组织度涌现）。
// 「NPC 不是按预设跑，而是自己找事」：每个具名 NPC 每天用 ColonyActionScorer 从这五个候选里
// 按效用评分自选一个（ColonyTaskBoard），选中行为的产出写回模拟状态 + EventLog（Covert 为主）。
// 候选语义（简报钉死）：
//   Work    —— 默认基线：产出/配合（Cooperation 已有，行为化留接口——选中不写状态）。
//   Protest —— 怨气×人格：复用 ProtestRouter 结果作为候选强度（已有机制的行为化入口）。
//   Recruit —— 仅组织者：向怨气>0.4 的关系边邻居招募（组织度传导行为化）。
//   Gossip  —— GossipRate 高者：沿关系边传播 PlayerImage 传闻（信息传导行为化入口）。
//   Rest    —— 低存在感行为，分数兜底（其余候选全被人格/状态压住时的落点）。

namespace Game.Domain.Colony.Autonomy
{
    /// <summary>具名 NPC 的每日自主行为候选。枚举顺序 = 评分并列时的优先序（Work 最保守）。</summary>
    public enum ColonyAction : byte
    {
        Work,       // 例行劳作：产出/配合（行为化接口留白，见 ColonyDailyBehavior.Apply）
        Protest,    // 无声抗议酝酿：复用 ProtestRouter（怨气×人格路由）
        Recruit,    // 地下招募：组织者向怨气邻居串联（组织度传导）
        Gossip,     // 私下传闻：沿最高信任关系边传播 PlayerImage
        Rest,       // 休息/观望：低存在感兜底
    }
}
