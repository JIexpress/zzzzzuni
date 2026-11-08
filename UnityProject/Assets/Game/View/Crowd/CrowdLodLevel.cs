// CrowdLodLevel.cs —— 人群精度四档枚举（TASK-A-AND-09，用户绑定约束档位表）。
// L0≤20 / L1≤50 是硬上限（<see cref="CrowdLodLimits"/>，断言锁死）；
// L2 为 billboard 面片（预算数百）；L3 只保留枚举位与边界文档，不实现播片。

namespace Game.View.Crowd
{
    /// <summary>
    /// 人群渲染精度四档（ROADMAP 方向四用户约束表）。
    /// 档位只改变「表示法」，不改数据源——所有档位的实体状态统一从
    /// DataKernel（<c>ZKernelWorld</c>，A-AND-08 迁移后的群体列）游标读取。
    /// </summary>
    public enum CrowdLodLevel
    {
        /// <summary>
        /// 全精度骨骼动画 NPC（A-ANIM-01 的 <c>NpcAnimatorDriver</c> 群体）。
        /// 硬上限 20（<see cref="CrowdLodLimits.MaxL0"/>）：超出自动降级最远者，
        /// 剧情 NPC（重要性标记）永不被降级。
        /// </summary>
        L0FullPrecision = 0,

        /// <summary>
        /// 程序化低模人形（几百面，见 <see cref="CrowdLodLowPolyHumanoid"/>）。
        /// 硬上限 50（<see cref="CrowdLodLimits.MaxL1"/>）：超出自动降级最远者。
        /// </summary>
        L1LowPoly = 1,

        /// <summary>
        /// Impostor billboard 面片（面向相机的程序化剪影四边形，预算数百）。
        /// 无更低实现档位（L3 未实现），预算耗尽时注册被拒绝。
        /// </summary>
        L2Billboard = 2,

        /// <summary>
        /// 播片（pre-rendered video impostor）——**本任务不实现**（简报明确：
        /// 留接口与边界文档，播片实现留待过场任务）。当前管线的分配器永不
        /// 产出本档位；档位分配的距离映射最高只到 <see cref="L2Billboard"/>。
        /// 边界文档（实现入口与前置条件）见施工报告与 <see cref="CrowdLODPipeline"/>
        /// 的 <c>L3VideoClip</c> 边界注释。
        /// </summary>
        L3VideoClip = 3,
    }
}
