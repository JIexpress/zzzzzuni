using System;
using System.Collections.Generic;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Economy
{
    /// <summary>
    /// 市场状态表（真实感路线 ④）：不需要模拟物流，只要一张表。
    /// 玩家在某处看到价格变了——那就是「世界在动」的全部证据，他不需要看到过程。
    /// </summary>
    public class MarketNode
    {
        public string Name;
        public float Stock;          // 库存
        public float StockBase;      // 初始库存（归一化用）
        public float DailyDrift;     // 每天 -3% 这种
        public float BasePrice;
        public List<string> Convoys = new();   // 在路上的运输队（失踪了也是一行字）
    }

    public class MarketState
    {
        public List<MarketNode> Nodes = new();

        public MarketNode Add(string name, float stock, float drift, float basePrice)
        {
            var n = new MarketNode { Name = name, Stock = stock, StockBase = stock, DailyDrift = drift, BasePrice = basePrice, Convoys = { "运输队一号", "运输队二号" } };
            Nodes.Add(n);
            return n;
        }

        /// <summary>每日漂移：库存降 → 价格涨；偶发运输队失踪（Personal 可见性——不在场就只看到价格变了）。</summary>
        public void DailyDrift(GameWorld w, Rng rng)
        {
            foreach (var n in Nodes)
            {
                n.Stock = Math.Max(0f, n.Stock * (1f - n.DailyDrift));
                if (n.Convoys.Count > 0 && rng.Chance(0.01f))
                {
                    var lost = n.Convoys[rng.RangeInt(0, n.Convoys.Count)];
                    n.Convoys.Remove(lost);
                    w.Log.Add(w.Day, EventKind.Market, n.Name, $"{lost}已经失踪 6 小时了。", EventVisibility.Personal);
                }
            }
            // 联动：A 减产 → B 收购价上抬
            if (Nodes.Count >= 2 && rng.Chance(0.05f))
            {
                var src = Nodes[0];
                var dst = Nodes[1];
                float shortage = 1f - src.Stock / src.StockBase;
                dst.BasePrice = dst.BasePrice * (1f + shortage * 0.004f);   // 温和漂移
            }
        }

        /// <summary>一行状态（真实感路线 ③ 的样子）。</summary>
        public string Snapshot()
        {
            var lines = new List<string>();
            foreach (var n in Nodes)
                lines.Add($"{n.Name}  库存 {n.Stock:0}（{n.Stock / n.StockBase:0%}）  收购价 {n.BasePrice:0.0}/单位  在路上 {n.Convoys.Count} 支");
            return string.Join("\n  ", lines);
        }
    }
}
