// CrowdLodLimits.cs —— 人群四档 LOD 的数量硬上限（TASK-A-AND-09，用户绑定约束）。
// 硬上限是**常量而非可调配置**：L0≤20 / L1≤50 来自 ROADMAP 方向四的用户绑定
// 档位表，允许配置等于允许违约。超限策略（报告选型）：
//   L0 超限 → 自动降级最远的非剧情 L0 到 L1（剧情 NPC 永不降级）；
//   L1 超限 → 自动降级最远的 L1 到 L2；
//   L2 预算耗尽 → 拒绝注册（Register 返回 false；L3 播片未实现，无处可降）。
// 总注册数上限 = MaxL2：每个已注册实体必然落在 L0..L2 之一，L2 是最宽的桶，
// 总数 ≤ MaxL2 ⇒ 任何降级都有落位（L2 桶永不因降级溢出）。

namespace Game.View.Crowd
{
    /// <summary>人群 LOD 数量硬上限（用户绑定约束，断言锁死，不可配置）。</summary>
    public static class CrowdLodLimits
    {
        /// <summary>L0 全精度 NPC 硬上限（用户约束：≤20）。</summary>
        public const int MaxL0 = 20;

        /// <summary>L1 低模人形硬上限（用户约束：≤50）。</summary>
        public const int MaxL1 = 50;

        /// <summary>
        /// L2 billboard 面片预算上限（用户约束量级：数百；512 同时是总注册数上限，
        /// 且 &lt; <c>KernelCrowdIds.MaxInstancesPerDraw</c>(1023)，单次 DrawMeshInstanced 可提交）。
        /// </summary>
        public const int MaxL2 = 512;
    }
}
