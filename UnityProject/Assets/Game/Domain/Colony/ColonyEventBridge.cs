using Game.Domain.Colony.Sim;
using ZFrame.Core;

namespace Game.Domain.Colony
{
    /// <summary>
    /// Colony 事件桥（TASK-A-COL-01）：把 <see cref="GameWorld"/> 事件日志里的新事件
    /// 推到 ZFrame EventBus（Fire 立即派发；Publish 在 ZFrame 的实际 API 名为 Fire）。
    ///
    /// 用法：每 tick（每次 AdvanceDays / 手动 Log 写入）之后调用一次 <see cref="Pump"/>。
    /// 桥只读 <c>world.Log.All</c>（游标只前进），不改 GameWorld 任何公开行为。
    /// 未消费的事件不会丢失——下次 Pump 会从上次游标继续补发。
    /// </summary>
    public sealed class ColonyEventPump
    {
        private int _consumed;

        /// <summary>已经转换并发布过的事件数（诊断用）。</summary>
        public int Consumed => _consumed;

        /// <summary>
        /// 把 <paramref name="world"/> 日志中自上次 Pump 之后新增的事件逐条转换成
        /// <see cref="ColonyEventDto"/> 并 <c>EventBus.Fire</c>（立即派发）。
        /// </summary>
        public void Pump(GameWorld world)
        {
            var all = world.Log.All;
            for (int i = _consumed; i < all.Count; i++)
            {
                GameEvent e = all[i];
                EventBus.Fire(new ColonyEventDto
                {
                    Day = e.Day,
                    Kind = e.Kind.ToString(),
                    Where = e.Where,
                    Text = e.Text,
                    Visibility = (byte)e.Vis,
                });
            }
            _consumed = all.Count;
        }

        /// <summary>回退游标（如重放世界后想重新发布全部事件时使用）。</summary>
        public void Reset() => _consumed = 0;
    }
}
