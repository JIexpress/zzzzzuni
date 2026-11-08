// CrowdLodConfig.cs —— 档位分配的距离阈值与迟滞带（TASK-A-AND-09）。
// 数量硬上限不在这里（见 <see cref="CrowdLodLimits"/>，常量不可配置）；
// 本类只负责**距离驱动的迟滞参数**：升档阈值（Up）严于降档阈值（Down），
// 两阈值之间是迟滞带——实体位于带内时保持当前档位，边界往返不振荡。

using System;

namespace Game.View.Crowd
{
    /// <summary>
    /// LOD 距离阈值配置（与相机的平面距离，米）。
    /// 约束：<c>L0UpDistance &lt; L0DownDistance</c>、<c>L1UpDistance &lt; L1DownDistance</c>
    /// （差值即迟滞带宽度，必须为正），构造时校验。
    /// </summary>
    public sealed class CrowdLodConfig
    {
        /// <summary>创建默认配置：L0 迟滞带 13..17 m（宽 4），L1 迟滞带 36..44 m（宽 8）。</summary>
        public CrowdLodConfig()
        {
        }

        /// <summary>L1→L0 升档阈值：距离低于此值才升回全精度（默认 13 m）。</summary>
        public float L0UpDistance { get; set; } = 13f;

        /// <summary>L0→L1 降档阈值：距离高于此值才降级（默认 17 m）。</summary>
        public float L0DownDistance { get; set; } = 17f;

        /// <summary>L2→L1 升档阈值（默认 36 m）。</summary>
        public float L1UpDistance { get; set; } = 36f;

        /// <summary>L1→L2 降档阈值（默认 44 m）。</summary>
        public float L1DownDistance { get; set; } = 44f;

        /// <summary>校验迟滞带合法性（Up &lt; Down 且为正距离），非法抛出。</summary>
        public void Validate()
        {
            if (L0UpDistance <= 0f || L1UpDistance <= 0f)
            {
                throw new ArgumentException("升档阈值必须 > 0");
            }

            if (L0UpDistance >= L0DownDistance)
            {
                throw new ArgumentException(
                    $"L0 迟滞带非法：Up({L0UpDistance}) 必须 < Down({L0DownDistance})");
            }

            if (L1UpDistance >= L1DownDistance)
            {
                throw new ArgumentException(
                    $"L1 迟滞带非法：Up({L1UpDistance}) 必须 < Down({L1DownDistance})");
            }
        }
    }
}
