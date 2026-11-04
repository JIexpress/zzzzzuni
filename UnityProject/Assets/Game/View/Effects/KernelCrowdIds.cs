// KernelCrowdIds.cs —— 千人演练场的显式 compId 与共享常量（TASK-A-R9-13）。
// 组件 ID 由 C# 侧显式分配 0..127（ZKernelWorld 契约），三个组件固定占用 0/1/2。

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
