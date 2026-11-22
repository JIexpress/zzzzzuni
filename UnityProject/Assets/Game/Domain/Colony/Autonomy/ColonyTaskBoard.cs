// ColonyTaskBoard.cs —— TASK-A-COL-06：Colony 域内的轻量任务板（方案 B，零新引用）。
// 仿照 Game.Domain.AI.Coordination.TaskBoard 的「候选 → 评分 → 分派」模式，但语义是社会行为：
// 每个具名 NPC 每天一块板、五个候选、每人自选——不是共享任务池（多人可同选抗议/传闻，
// 没有位置坐标与秒级过期，认领互斥在这里没有意义）。与 Game/AI 的 TaskBoard 的对应关系：
//   TaskBoardCandidates.CreateClaimActions  ↔ ColonyTaskBoard 的五候选构建
//   UtilityBrain.Tick（评分+阈值+并列取先）  ↔ Select（argmax + 候选序优先）
//   UtilityAction.Execute                   ↔ ColonyDailyBehavior.Apply（行为产出写状态+Covert 事件）
// 评分走 ColonyActionScorer（纯函数）；结构体零堆分配（每日 100 NPC × 30 天的热路径，T5 < 50ms）。

using System;
using System.Collections.Generic;

namespace Game.Domain.Colony.Autonomy
{
    /// <summary>一个具名 NPC 当日的自主行为任务板：候选 + 评分 + 分派。</summary>
    public struct ColonyTaskBoard
    {
        /// <summary>候选顺序 = 评分并列时的优先序（Work 最保守——平局让步给例行公事）。</summary>
        public static readonly ColonyAction[] CandidateOrder =
        {
            ColonyAction.Work, ColonyAction.Protest, ColonyAction.Recruit,
            ColonyAction.Gossip, ColonyAction.Rest,
        };

        /// <summary>候选数（测试钉死：五候选一个不少）。</summary>
        public const int CandidateCount = 5;

        private ColonyNpcContext _ctx;

        private ColonyTaskBoard(ColonyNpcContext ctx) => _ctx = ctx;

        /// <summary>为一份评分输入开板。</summary>
        public static ColonyTaskBoard For(ColonyNpcContext ctx) => new ColonyTaskBoard(ctx);

        /// <summary>某候选的效用分（透传 ColonyActionScorer——公式唯一出处）。</summary>
        public float ScoreOf(ColonyAction action) => ColonyActionScorer.Score(action, _ctx);

        /// <summary>
        /// 分派：最高分候选胜出；并列取候选序最前（确定性 tie-break，与 UtilityBrain 的
        /// 「earliest registered wins」同式）。分数为 NaN 的候选按无欲望处理（不参与比较）。
        /// </summary>
        public ColonyAction Select()
        {
            ColonyAction best = CandidateOrder[0];
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < CandidateOrder.Length; i++)
            {
                ColonyAction a = CandidateOrder[i];
                float s = ColonyActionScorer.Score(a, _ctx);
                if (float.IsNaN(s) || !(s > bestScore))
                {
                    continue;
                }

                bestScore = s;
                best = a;
            }

            return best;
        }

        /// <summary>完整排序（降分序、并列保持候选序）——开发者可解释性/测试用，会分配。</summary>
        public List<(ColonyAction action, float score)> Ranked()
        {
            var list = new List<(ColonyAction, float)>(CandidateCount);
            for (int i = 0; i < CandidateOrder.Length; i++)
            {
                list.Add((CandidateOrder[i], ColonyActionScorer.Score(CandidateOrder[i], _ctx)));
            }

            list.Sort(((ColonyAction action, float score) x, (ColonyAction action, float score) y) =>
            {
                int byScore = y.score.CompareTo(x.score);
                if (byScore != 0)
                {
                    return byScore;
                }

                return Array.IndexOf(CandidateOrder, x.action).CompareTo(Array.IndexOf(CandidateOrder, y.action));
            });
            return list;
        }
    }
}
