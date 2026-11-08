// CrowdLodEntityState.cs —— 单实体 LOD 快照（TASK-A-AND-09，只读诊断口径）。
// 全部字段由管线在每次 View tick 从 kernel 游标读回的数据推导；
// 测试据此断言档位、位置连续性与 billboard 朝向。

using UnityEngine;

namespace Game.View.Crowd
{
    /// <summary>单个注册实体的 LOD 状态快照（只读）。</summary>
    public readonly struct CrowdLodEntityState
    {
        /// <summary>当前档位（L0..L2；L3 未实现，永不产出）。</summary>
        public CrowdLodLevel Level { get; }

        /// <summary>kernel 位置列读回的世界坐标（三档共用同一数据源）。</summary>
        public Vector3 Position { get; }

        /// <summary>朝向 yaw（度）——由 kernel 速度列推导（速度≈0 时保持上次值）。</summary>
        public float YawDeg { get; }

        /// <summary>billboard 旋转（仅 L2 有效：+Z 轴指向相机）。</summary>
        public Quaternion BillboardRotation { get; }

        /// <summary>
        /// 最近一次提交的实例矩阵（L1=TRS(kernel 位姿、速度朝向)；
        /// L2=TRS(kernel 位置、billboard 旋转)；L0 无矩阵表示，单位阵）。
        /// 与 <c>Graphics.DrawMeshInstanced</c> 提交同源——切换连续性/billboard
        /// 朝向断言的证据口径。
        /// </summary>
        public Matrix4x4 RepresentationMatrix { get; }

        /// <summary>剧情 NPC（重要性标记：永远 L0、永不降级）。</summary>
        public bool IsStoryNpc { get; }

        /// <summary>是否注册了 L0 全精度表示（无表示的实体最高只能到 L1）。</summary>
        public bool HasL0Representation { get; }

        /// <summary>构造快照。</summary>
        public CrowdLodEntityState(CrowdLodLevel level, Vector3 position, float yawDeg,
            Quaternion billboardRotation, Matrix4x4 representationMatrix,
            bool isStoryNpc, bool hasL0Representation)
        {
            Level = level;
            Position = position;
            YawDeg = yawDeg;
            BillboardRotation = billboardRotation;
            RepresentationMatrix = representationMatrix;
            IsStoryNpc = isStoryNpc;
            HasL0Representation = hasL0Representation;
        }
    }
}
