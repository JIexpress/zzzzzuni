// KernelCrowdIds.cs —— 千人演练场的显式 compId 与共享常量（TASK-A-R9-13 建，
// TASK-A-AND-08 扩充 Health/Alive 两列与生命调参常量）。
// 组件 ID 由 C# 侧显式分配 0..127（ZKernelWorld 契约），五个组件固定占用 0..4。

namespace Game.View.Effects
{
    /// <summary>演练场的 compId 分配与布局常量（单一事实源）。</summary>
    public static class KernelCrowdIds
    {
        /// <summary>位置组件 ID。</summary>
        public const uint Position = 0u;

        /// <summary>速度组件 ID。</summary>
        public const uint Velocity = 1u;

        /// <summary>标签组件 ID。</summary>
        public const uint Tag = 2u;

        /// <summary>生命组件 ID（TASK-A-AND-08）。</summary>
        public const uint Health = 3u;

        /// <summary>存活组件 ID（TASK-A-AND-08，bool 组件口径：byte 0/1）。</summary>
        public const uint Alive = 4u;

        /// <summary>
        /// 行为查询掩码的 compId 升序位序 = 组件注册序 0,1,2,3,4 →
        /// 游标 columns[j] 与 compId j 一一对应（EachBatches 契约：列按查询
        /// 掩码升序 compId 位序交付），原地读写按此取列。
        /// </summary>
        public const uint BehaviorColumnCount = 5u;

        /// <summary>初始生命基数（按实体序号铺开，保证确定性）。</summary>
        public const float InitialHealthBase = 12f;

        /// <summary>初始生命铺开跨度：初始值 = Base + (序号 % Spread)。</summary>
        public const uint InitialHealthSpread = 25u;

        /// <summary>存活期生命衰减速率（每秒）。初始 12..36 → 自然死亡需 120..360s。</summary>
        public const float DecayPerSecond = 0.1f;

        /// <summary>死亡期生命回复速率（每秒）。</summary>
        public const float RespawnHealPerSecond = 5f;

        /// <summary>死亡到重生时长（秒）。死亡瞬间 Health 置为 -Delay·Heal，回复到 0 即重生。</summary>
        public const float RespawnDelaySeconds = 2f;

        /// <summary>实体序号的初始生命（确定性；重生复用同一公式）。</summary>
        public static float InitialHealth(uint tag)
        {
            return InitialHealthBase + (tag % InitialHealthSpread);
        }

        /// <summary>默认实体数（千人演练场）。</summary>
        public const int DefaultEntityCount = 1000;

        /// <summary>
        /// 一次 Graphics.DrawMeshInstanced 的实例数上限（Unity 2022 文档 1023）。
        /// 1000 &lt;= 上限 → 单次提交；超过时按此值分批，DrawCall 呈台阶而非线性增长。
        /// </summary>
        public const int MaxInstancesPerDraw = 1023;

        /// <summary>群体圆盘半径（米）。</summary>
        public const float FieldRadius = 9f;

        /// <summary>单个实例立方体的边长（米）。</summary>
        public const float InstanceScale = 0.12f;

        /// <summary>黄金角（弧度），用于把线性序号铺成均匀圆盘。</summary>
        public const float GoldenAngle = 2.39996323f;
    }
}
