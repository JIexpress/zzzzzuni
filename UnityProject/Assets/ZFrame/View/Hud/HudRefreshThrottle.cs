using System;

namespace ZFrame.View
{
    /// <summary>
    /// 文本刷新节流。每帧重写 HUD 文本本身会进入 GC / 布局开销，
    /// 让"测量工具"污染被测帧率；任务要求 2-4 Hz，这里把合法区间钉死在
    /// <see cref="MinIntervalSeconds"/>=0.5s（2 Hz）到 <see cref="MaxIntervalSeconds"/>=0.25s（4 Hz）之间。
    /// </summary>
    public sealed class HudRefreshThrottle
    {
        /// <summary>2 Hz（最慢允许）。</summary>
        public const float MinIntervalSeconds = 0.5f;

        /// <summary>4 Hz（最快允许）。</summary>
        public const float MaxIntervalSeconds = 0.25f;

        /// <summary>默认 4 Hz。</summary>
        public const float DefaultIntervalSeconds = MaxIntervalSeconds;

        private readonly double _intervalSeconds;
        private bool _hasConsumed;
        private double _lastConsumedAt;

        public HudRefreshThrottle(float intervalSeconds = DefaultIntervalSeconds)
        {
            if (float.IsNaN(intervalSeconds) || float.IsInfinity(intervalSeconds)
                || intervalSeconds < MaxIntervalSeconds || intervalSeconds > MinIntervalSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(intervalSeconds),
                    intervalSeconds,
                    "HUD refresh interval must stay within 2-4 Hz (0.25s..0.5s) so the instrument does not distort the frame it measures.");
            }

            _intervalSeconds = intervalSeconds;
        }

        /// <summary>刷新间隔（秒）。</summary>
        public double IntervalSeconds => _intervalSeconds;

        /// <summary>
        /// 消耗一次刷新配额：距上次刷新不足间隔时返回 false。
        /// 首次调用返回 true；时间回退（时钟被重置）时也返回 true，避免永久卡死。
        /// </summary>
        public bool TryConsume(double nowSeconds)
        {
            if (_hasConsumed && nowSeconds >= _lastConsumedAt
                && nowSeconds - _lastConsumedAt < _intervalSeconds)
            {
                return false;
            }

            _hasConsumed = true;
            _lastConsumedAt = nowSeconds;
            return true;
        }

        /// <summary>清空节流状态（下次调用必刷新）。</summary>
        public void Reset()
        {
            _hasConsumed = false;
            _lastConsumedAt = 0.0;
        }
    }
}
