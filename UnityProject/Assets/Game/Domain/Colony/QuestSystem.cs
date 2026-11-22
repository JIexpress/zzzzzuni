using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Quest
{
    /// <summary>支线触发谓词（正式版由内容侧配表；demo 用名字/文本映射）。</summary>
    public enum TriggerKind : byte { DayReached, GrievanceAbove, OrganizationAbove }

    /// <summary>一条支线（数据来自 GLM 12 则：三选项全有代价、延迟回找、世界不等你）。</summary>
    public class QuestDef
    {
        public string Name, TriggerText, InvolvedText, CoreText, AftermathText, NoInterventionText;
        public List<string> Options = new();   // A/B/C
        public int DelayDays;                  // 「延迟：N个月后」→ N×30
        public bool WorldAdvances;             // 玩家不干预也会推进
        public TriggerKind TKind;
        public float TArg;
        public string TRegion;

        public bool Triggered, Resolved;
        public uint TriggeredDay;
    }

    public class QuestSystem
    {
        public List<QuestDef> Library = new();
        private readonly List<(QuestDef q, uint dueDay)> _delayed = new();

        // TASK-A-COL-03：支线延迟回找队列随档走（读档世界「世界不等你」的到期回找不能凭空消失）。
        internal IReadOnlyList<(QuestDef q, uint dueDay)> DelayedEntries => _delayed;

        /// <summary>按库序号回填延迟队列（Capture 侧以 Library 下标登记支线）。</summary>
        internal void RestoreDelayed(IEnumerable<(int questIndex, uint dueDay)> entries)
        {
            _delayed.Clear();
            foreach (var (index, due) in entries)
            {
                if (index < 0 || index >= Library.Count)
                    throw new ArgumentOutOfRangeException(nameof(entries), $"延迟回找队列引用了不存在的支线下标 {index}");
                _delayed.Add((Library[index], due));
            }
        }

        public void CheckTriggers(GameWorld w)
        {
            foreach (var q in Library)
            {
                if (q.Resolved) continue;

                // 「世界不等你」：已触发但玩家不理 → 世界自己往前走（规格：至少 3 个剧情不干预也推进）
                if (q.Triggered && !q.Resolved && q.WorldAdvances && w.Day - q.TriggeredDay > 14)
                {
                    q.Resolved = true;
                    var s = w.SettlementOf(q.TRegion);
                    if (s != null) s.AvgGrievance = Math.Clamp(s.AvgGrievance + 0.05f, 0f, 1f);
                    w.Log.Add(w.Day, EventKind.Quest, q.TRegion,
                        $"你什么都没做，事情自己走到了下一步：{q.NoInterventionText}", EventVisibility.Personal);
                    if (q.DelayDays > 0) _delayed.Add((q, w.Day + (uint)q.DelayDays));
                    continue;
                }

                if (q.Triggered) continue;
                bool ok = q.TKind switch
                {
                    TriggerKind.DayReached => w.Day >= (uint)q.TArg,
                    TriggerKind.GrievanceAbove => w.SettlementOf(q.TRegion)?.AvgGrievance >= q.TArg,
                    TriggerKind.OrganizationAbove => w.SettlementOf(q.TRegion)?.Organization >= q.TArg,
                    _ => false,
                };
                if (!ok) continue;

                q.Triggered = true;
                q.TriggeredDay = w.Day;
                w.Log.Add(w.Day, EventKind.Quest, q.TRegion,
                    $"支线触发《{q.Name}》：{q.TriggerText} ｜ 冲突核心：{q.CoreText}", EventVisibility.Personal);
            }
        }

        /// <summary>玩家选择。三个选项都有代价，没有完美解（规格硬性要求）。</summary>
        public void Resolve(GameWorld w, QuestDef q, int option /*0=A,1=B,2=C*/)
        {
            if (!q.Triggered || q.Resolved) return;
            q.Resolved = true;
            var s = w.SettlementOf(q.TRegion);
            string choice = option < q.Options.Count ? q.Options[option] : "";

            // 通用结算骨架：A=顺了强势一方；B=程序正确但两边都有亏损；C=折中（都不满意但不爆炸）。
            // 正式版：代价明细由内容侧配表（选项文本 → 参数），这里按选项号给框架级效果。
            if (s != null)
            {
                switch (option)
                {
                    case 0:
                        s.AvgGrievance = Math.Clamp(s.AvgGrievance + 0.10f, 0f, 1f);
                        break;
                    case 1:
                        s.LeanVariance = Math.Clamp(s.LeanVariance + 0.05f, 0f, 1f);
                        w.Governor.Prestige.TradeValue = Math.Clamp(w.Governor.Prestige.TradeValue - 0.05f, 0f, 1f);
                        break;
                    case 2:
                        s.AvgGrievance = Math.Clamp(s.AvgGrievance + 0.04f, 0f, 1f);
                        break;
                }
            }
            w.Log.Add(w.Day, EventKind.Quest, q.TRegion,
                $"《{q.Name}》结算（选项 {(char)('A' + option)}）：{Truncate(choice, 60)}…… 代价已入账。", EventVisibility.Personal);
            if (q.DelayDays > 0)
            {
                _delayed.Add((q, w.Day + (uint)q.DelayDays));
                w.Log.Add(w.Day, EventKind.Quest, q.TRegion,
                    $"这件事会在约 {q.DelayDays} 天后回来找你。", EventVisibility.Covert);   // 玩家不知道具体哪天
            }
        }

        private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

        /// <summary>延迟回找到账（规格：后果会以什么方式回来找玩家）。</summary>
        public void TickDue(GameWorld w)
        {
            foreach (var (q, due) in _delayed.Where(d => d.dueDay <= w.Day).ToList())
            {
                _delayed.Remove((q, due));
                w.Log.Add(w.Day, EventKind.Quest, q.TRegion,
                    $"旧账当众翻出——《{q.Name}》的后遗症来了：{q.AftermathText}", EventVisibility.Public);
            }
        }
    }
}
