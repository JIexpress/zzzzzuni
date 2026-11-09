using System;

namespace Game.Domain.Survival
{
    /// <summary>TASK-A-LOOP-01：最小可玩循环的阶段。</summary>
    public enum SurvivalPhase
    {
        Idle,     // 未开始（场景加载后的默认态；不刷怪、不显示 HUD）
        Running,  // 计时中
        Won,      // 撑满 90s
        Lost,     // 火堆耐久归零
    }

    /// <summary>
    /// TASK-A-LOOP-01：守住火堆的**纯逻辑状态机**（引擎无关，EditMode 可断言）。
    ///
    /// 规则：撑满 <see cref="DurationSeconds"/> 秒即胜利；火堆耐久归零即失败；
    /// 结束后一切输入（Tick / 伤害 / 刷波）都不再改变状态（幂等，可安全重复调用）。
    ///
    /// 波次曲线写成纯函数 <see cref="WaveSize"/> / <see cref="WaveInterval"/>：
    /// 越往后数量越多、间隔越短——压力曲线是数值设计，不是魔法数散落在 MonoBehaviour 里。
    /// </summary>
    public sealed class SurvivalLoopState
    {
        /// <summary>默认时长（秒）。</summary>
        public const float DefaultDurationSeconds = 90f;

        /// <summary>默认火堆耐久。</summary>
        public const int DefaultFireHp = 100;

        /// <summary>第一波的间隔（秒）。</summary>
        public const float FirstWaveDelaySeconds = 6f;

        /// <summary>波次间隔的下限（秒）——再短就变成压力墙而不是曲线。</summary>
        public const float MinWaveIntervalSeconds = 3f;

        /// <summary>每过一波，间隔缩短的比例。</summary>
        public const float WaveIntervalDecay = 0.85f;

        private float _nextWaveAt;
        private bool _wavePending;

        public SurvivalLoopState(float durationSeconds = DefaultDurationSeconds,
            int maxFireHp = DefaultFireHp)
        {
            if (durationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            }

            if (maxFireHp <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFireHp));
            }

            DurationSeconds = durationSeconds;
            MaxFireHp = maxFireHp;
            FireHp = maxFireHp;
            Phase = SurvivalPhase.Idle;
            _nextWaveAt = FirstWaveDelaySeconds;
        }

        /// <summary>总时长（秒，构造后不变）。</summary>
        public float DurationSeconds { get; }

        /// <summary>火堆最大耐久。</summary>
        public int MaxFireHp { get; }

        /// <summary>已过时间（秒；结束后冻结）。</summary>
        public float ElapsedSeconds { get; private set; }

        /// <summary>剩余时间（秒，不小于 0）。</summary>
        public float RemainingSeconds => Math.Max(0f, DurationSeconds - ElapsedSeconds);

        /// <summary>火堆当前耐久。</summary>
        public int FireHp { get; private set; }

        /// <summary>当前阶段。</summary>
        public SurvivalPhase Phase { get; private set; }

        /// <summary>已经触发的波次数（从 1 开始；未开始为 0）。</summary>
        public int WaveIndex { get; private set; }

        /// <summary>本轮已击杀的敌人数（TASK-A-LOOP-02；重开清零）。</summary>
        public int Kills { get; private set; }

        /// <summary>是否已结束（胜利或失败）。</summary>
        public bool IsFinished => Phase == SurvivalPhase.Won || Phase == SurvivalPhase.Lost;

        /// <summary>开始计时（只在 Idle 生效；重复调用无副作用）。</summary>
        public void Start()
        {
            if (Phase != SurvivalPhase.Idle)
            {
                return;
            }

            Phase = SurvivalPhase.Running;
        }

        /// <summary>
        /// 推进时间。非 Running 阶段完全不动（结束后冻结，幂等）。
        /// 到点判胜；火堆在外部伤害路径里判负。
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (Phase != SurvivalPhase.Running || deltaSeconds <= 0f)
            {
                return;
            }

            ElapsedSeconds += deltaSeconds;

            if (ElapsedSeconds >= DurationSeconds)
            {
                ElapsedSeconds = DurationSeconds;
                Phase = SurvivalPhase.Won;
                return;
            }

            // 波次到点：只置一次 pending，由表现层消费后清掉。
            while (ElapsedSeconds >= _nextWaveAt)
            {
                _wavePending = true;
                WaveIndex++;
                _nextWaveAt += WaveInterval(WaveIndex);
            }
        }

        /// <summary>
        /// 火堆受伤。只在 Running 生效；耐久归零即失败。
        /// 返回 true 表示这次伤害被接受。
        /// </summary>
        public bool ApplyFireDamage(int amount)
        {
            if (Phase != SurvivalPhase.Running || amount <= 0)
            {
                return false;
            }

            FireHp -= amount;
            if (FireHp <= 0)
            {
                FireHp = 0;
                Phase = SurvivalPhase.Lost;
            }

            return true;
        }

        /// <summary>取走一次待刷波次（表现层每帧调用；没有则返回 false）。</summary>
        public bool TryConsumeWave()
        {
            if (!_wavePending || Phase != SurvivalPhase.Running)
            {
                return false;
            }

            _wavePending = false;
            return true;
        }

        /// <summary>
        /// 记一次击杀（TASK-A-LOOP-02）。只在 Running 生效，返回是否计入。
        /// **不改变 Phase、不推进时间**：击杀是记账，不是流程推进。
        /// </summary>
        public bool RegisterKill()
        {
            if (Phase != SurvivalPhase.Running)
            {
                return false;
            }

            Kills++;
            return true;
        }

        /// <summary>第 <paramref name="waveIndex"/> 波的敌人数量（1 起，单调不减）。</summary>
        public static int WaveSize(int waveIndex)
        {
            if (waveIndex <= 0)
            {
                return 0;
            }

            // 1,2,3,4,5,6... 每 3 波多 1 个：1,1,1,2,2,2,3...（线性压力，不做指数爆炸）
            return 1 + (waveIndex - 1) / 3;
        }

        /// <summary>第 <paramref name="waveIndex"/> 波之后到下一波的间隔（秒，单调不增）。</summary>
        public static float WaveInterval(int waveIndex)
        {
            if (waveIndex <= 0)
            {
                return FirstWaveDelaySeconds;
            }

            double interval = FirstWaveDelaySeconds * Math.Pow(WaveIntervalDecay, waveIndex - 1);
            return (float)Math.Max(MinWaveIntervalSeconds, interval);
        }

        /// <summary>回到初始态（重开用）。</summary>
        public void Reset()
        {
            ElapsedSeconds = 0f;
            FireHp = MaxFireHp;
            Phase = SurvivalPhase.Idle;
            WaveIndex = 0;
            Kills = 0;
            _nextWaveAt = FirstWaveDelaySeconds;
            _wavePending = false;
        }
    }
}
