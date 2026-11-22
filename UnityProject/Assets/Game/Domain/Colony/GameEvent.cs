using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Domain.Colony.Sim
{
    /// <summary>
    /// 事件可见性（异常行为 §4 + 指挥代理 §5 视野等级）。
    /// 框架纪律：不提供「一键查看所有抗议」——Covert 事件永不进玩家信息流，
    /// 只能靠观察动作 / 线人转成间接证据（§4.1 所有证据都是间接的，这不是 bug，是设计）。
    /// </summary>
    public enum EventVisibility : byte
    {
        Public,    // 人人可见：政策颁布、骚乱、大游行（可进 AI 报告）
        Personal,  // 在场才见：你亲自在这块地，或这里有你的线人
        Covert,    // 世界知道、玩家不知道：抗议酝酿、传闻池、地下串联
    }

    public enum EventKind : byte
    {
        Policy,        // 政策颁布与执行
        Protest,       // 无声抗议（涨价/怠工/伪造证明/拒绝征召）
        Contract,      // 信念契约：质疑 / 沉默 / 离开
        Uprising,      // 涌现链：组织者 / 骚乱 / 大游行
        Migration,     // 人口流动
        Market,        // 市场漂移、运输队
        Quest,         // 支线触发 / 结算 / 延迟回找
        Observation,   // 玩家观察动作的间接证据
        Intel,         // 情报买卖与过期
        Departure,     // NPC 离开
        Reputation,    // 声望变动
        Relation,      // 关系网事件（TASK-A-COL-09：内容表驱动，含 aftermath 延迟后果）
        Life,          // 人生节点（TASK-A-COL-09：具名档案改写与聚落效果）
    }

    public class GameEvent
    {
        public uint Day;
        public EventKind Kind;
        public string Where;
        public string Text;
        public EventVisibility Vis;
    }

    /// <summary>
    /// 事件日志。两条流：
    ///   PlayerFeed() —— 玩家视角（Public 恒可见；Personal 要在场或有线人；Covert 永不出现）
    ///   Truth()      —— 调试/回放用（demo 里并排打印，展示信息不对称）
    /// </summary>
    public class EventLog
    {
        private readonly List<GameEvent> _all = new();
        public IReadOnlyList<GameEvent> All => _all;

        public void Add(uint day, EventKind kind, string where, string text, EventVisibility vis)
            => _all.Add(new GameEvent { Day = day, Kind = kind, Where = where, Text = text, Vis = vis });

        public List<GameEvent> PlayerFeed(uint fromDay, string presentRegion, HashSet<string> informants)
            => _all.Where(e => e.Day >= fromDay && (
                   e.Vis == EventVisibility.Public
                   || (e.Vis == EventVisibility.Personal && (presentRegion == e.Where || informants.Contains(e.Where)))))
                   .ToList();

        public List<GameEvent> Since(uint day, EventVisibility? vis = null)
            => _all.Where(e => e.Day >= day && (vis == null || e.Vis == vis)).ToList();
    }
}
