using System.Collections.Generic;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 第二批内容引擎聚合（TASK-A-COL-09）：关系网/贸易/人生节点三引擎的统一挂点载体。
    /// GameWorld.Content 非 null 时由 GameWorld.DailyTick 驱动（Content 为 null 的世界
    /// 零开销、行为逐位不变——AutonomyEnabled 等既有开关语义不受影响）。
    /// 内容列表是建局输入（ColonyContentBundle，来自 ConfigService 投影），不是存档状态；
    /// 进度（三引擎的 Fired 集合）进存档——Capture 采集、Restore 以 <see cref="FromProgress"/>
    /// 重建（内容空表），调用方再 Attach 回填内容后继续模拟，已触发不重放。
    /// </summary>
    public sealed class ColonyContentEngines
    {
        public readonly ColonyRelationEngine Relations;
        public readonly ColonyTradeEngine Trades;
        public readonly ColonyLifeEngine Life;

        public ColonyContentEngines(ColonyContentBundle bundle)
        {
            bundle ??= new ColonyContentBundle();
            Relations = new ColonyRelationEngine(bundle.Relations);
            Trades = new ColonyTradeEngine(bundle.Trades);
            Life = new ColonyLifeEngine(bundle.Life);
        }

        /// <summary>Restore 通道：空内容 + 进度集合（读档世界不重放；内容由调用方 Attach 回填）。</summary>
        public static ColonyContentEngines FromProgress(
            IEnumerable<string> firedRelations, IEnumerable<string> firedTrades, IEnumerable<string> firedLife)
        {
            var engines = new ColonyContentEngines(new ColonyContentBundle());
            foreach (var id in firedRelations ?? new List<string>()) engines.Relations.Fired.Add(id);
            foreach (var id in firedTrades ?? new List<string>()) engines.Trades.Fired.Add(id);
            foreach (var id in firedLife ?? new List<string>()) engines.Life.Fired.Add(id);
            return engines;
        }

        /// <summary>Restore 后内容回填（进度集合不动）。</summary>
        public void Attach(ColonyContentBundle bundle)
        {
            if (bundle == null) return;
            Relations.Attach(bundle.Relations);
            Trades.Attach(bundle.Trades);
            Life.Attach(bundle.Life);
        }

        /// <summary>每日结算（GameWorld.DailyTick 挂点调用；顺序固定：关系 → 贸易 → 人生）。</summary>
        public void SettleDaily(GameWorld w)
        {
            Relations.SettleDaily(w);
            Trades.SettleDaily(w);
            Life.SettleDaily(w);
        }
    }
}
