// ZkWorldStats.cs —— DataKernel World 统计快照（blittable，TASK-B-R9-17）。
// 布局镜像：Native/zkernel/zkernel_abi.h 的 ZkWorldStatsAbi（40 字节）。
// 注意与原生 C++ 的 ZkWorldStats（仅迁移三计数）不同：ABI 版按简报要求
// 扩充 entityCount / chunkCount（ZK_WorldStats 内逐字段搬运，非内存镜像）。

using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// World 统计快照（blittable POD，40 字节；公开字段为 blittable 封送需要——
    /// 同 ZrNative 既有 POD 约定）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZkWorldStats
    {
        /// <summary>存活实体数。</summary>
        public ulong EntityCount;

        /// <summary>chunk 总数（全部 archetype 合计）。</summary>
        public ulong ChunkCount;

        /// <summary>成功迁移次数（Add/Remove 及批量；不含 Spawn/Destroy）。</summary>
        public ulong Migrations;

        /// <summary>迁移路径物理搬行次数（迁移实体行 + swap-back 收行）。</summary>
        public ulong RowsMoved;

        /// <summary>迁移路径 memcpy/memset 字节总数。</summary>
        public ulong BytesMoved;
    }
}
