// ZrVisibilityFrame.cs —— 帧内可见性"录入 / 消费"账本（TASK-B-VIS-02）。
//
// 为什么需要它：B-VIS-01 的审计结论是"一次录入（viewMask）、多视图消费"，
// 但当时只有测试里的静态断言，没有运行期可证伪的账本。真视锥剔除接进来后，
// 最容易犯的错正是两类，而且都不会崩、只会悄悄变慢或画重：
//   1) 同一物件一帧内被录入多次（每视图各剔一次 = N×V 次录入）；
//   2) 同一 (物件, 视图) 被消费多次（批处理重复提交）。
// 本账本把这两类错误变成计数，配合 ZrSceneMath.CullToViewMask 使用：
//
//   var frame = new ZrVisibilityFrame();
//   frame.BeginFrame();
//   foreach (item) { frame.Record(item.id); var mask = CullToViewMask(...); }
//   for (view) foreach (item with bit) frame.TryConsume(item.id, view);
//   if (!frame.Validate(out var error)) { /* 断言/日志 */ }
//
// 计数口径（容易误读，写死在这里）：
//   · Recorded          = 本帧录入的"物件"个数 N（每物件恰好一次，与视图数无关）
//   · ConsumedForView(v)= 视图 v 实际消费的物件个数 M_v，必须 M_v <= N
//   · ConsumedTotal     = Σ M_v（多视图合计，允许 > N —— 这不是重复消费）
//   · 重复消费只按 (物件, 视图) 判：同一物件在**不同**视图各消费一次是正确行为。
//
// 本类只做记账，不碰 GPU/场景；未接入 ZRenderSceneBridge（B-VIS-02 明确"不挡路"，
// 接线属后续任务，避免改变现有画面与像素基线）。

using System.Collections.Generic;

namespace ZFrame.Render
{
    /// <summary>帧内可见性账本：录入 N、逐视图消费 M_v（M_v ≤ N）、(物件,视图) 唯一。</summary>
    internal sealed class ZrVisibilityFrame
    {
        private readonly HashSet<int> _recorded = new HashSet<int>();
        private readonly List<HashSet<int>> _consumedByView = new List<HashSet<int>>();
        private readonly List<int> _consumedCountByView = new List<int>();

        /// <summary>本帧录入的物件数 N（去重后的唯一物件数）。</summary>
        internal int Recorded
        {
            get { return _recorded.Count; }
        }

        /// <summary>重复录入次数（同一物件一帧内 Record 多次；应为 0）。</summary>
        internal int DuplicateRecords { get; private set; }

        /// <summary>重复消费次数（同一 (物件, 视图) 消费多次；应为 0）。</summary>
        internal int DuplicateConsumptions { get; private set; }

        /// <summary>越界消费次数（未录入就消费，或视图索引非法；应为 0）。</summary>
        internal int OrphanConsumptions { get; private set; }

        /// <summary>全部视图的消费合计（Σ M_v）。</summary>
        internal int ConsumedTotal
        {
            get
            {
                var total = 0;
                for (int view = 0; view < _consumedCountByView.Count; view++)
                {
                    total += _consumedCountByView[view];
                }

                return total;
            }
        }

        /// <summary>清空账本，开始新一帧（视图表随之重建）。</summary>
        internal void BeginFrame()
        {
            _recorded.Clear();
            _consumedByView.Clear();
            _consumedCountByView.Clear();
            DuplicateRecords = 0;
            DuplicateConsumptions = 0;
            OrphanConsumptions = 0;
        }

        /// <summary>录入一个待剔除物件（每物件每帧一次）。重复录入计入 DuplicateRecords。</summary>
        internal void Record(int itemId)
        {
            if (!_recorded.Add(itemId))
            {
                DuplicateRecords++;
            }
        }

        /// <summary>
        /// 视图 <paramref name="viewIndex"/> 消费物件 <paramref name="itemId"/>。
        /// 未录入 / 重复 / 视图索引非法 → 返回 false 并计数（不抛，判据交给 Validate）。
        /// </summary>
        internal bool TryConsume(int itemId, int viewIndex)
        {
            if (viewIndex < 0 || viewIndex >= ZrSceneMath.MaxViewCount)
            {
                OrphanConsumptions++;
                return false;
            }

            if (!_recorded.Contains(itemId))
            {
                OrphanConsumptions++;
                return false;
            }

            EnsureView(viewIndex);
            if (!_consumedByView[viewIndex].Add(itemId))
            {
                DuplicateConsumptions++;
                return false;
            }

            _consumedCountByView[viewIndex] = _consumedByView[viewIndex].Count;
            return true;
        }

        /// <summary>视图 v 的消费数 M_v（未出现的视图为 0）。</summary>
        internal int ConsumedForView(int viewIndex)
        {
            return viewIndex >= 0 && viewIndex < _consumedCountByView.Count
                ? _consumedCountByView[viewIndex]
                : 0;
        }

        /// <summary>
        /// 校验帧内不变量：无重复录入 / 无重复消费 / 无越界消费 / 每视图 M_v ≤ N。
        /// 返回 false 时 <paramref name="error"/> 给出首个违规的人类可读说明。
        /// </summary>
        internal bool Validate(out string error)
        {
            error = null;
            if (DuplicateRecords != 0)
            {
                error = $"重复录入 {DuplicateRecords} 次（每物件每帧只允许 Record 一次）";
                return false;
            }

            if (DuplicateConsumptions != 0)
            {
                error = $"重复消费 {DuplicateConsumptions} 次（同一 (物件,视图) 只允许消费一次）";
                return false;
            }

            if (OrphanConsumptions != 0)
            {
                error = $"越界消费 {OrphanConsumptions} 次（未录入的物件或非法视图索引）";
                return false;
            }

            for (int view = 0; view < _consumedCountByView.Count; view++)
            {
                if (_consumedCountByView[view] > Recorded)
                {
                    error = $"视图 {view} 消费 {_consumedCountByView[view]} > 录入 {Recorded}";
                    return false;
                }
            }

            return true;
        }

        private void EnsureView(int viewIndex)
        {
            while (_consumedByView.Count <= viewIndex)
            {
                _consumedByView.Add(new HashSet<int>());
                _consumedCountByView.Add(0);
            }
        }
    }
}
