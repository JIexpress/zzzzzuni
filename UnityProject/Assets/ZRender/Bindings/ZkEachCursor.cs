// ZkEachCursor.cs —— DataKernel 游标式批量迭代游标（blittable，TASK-B-R9-17）。
// 布局镜像：Native/zkernel/zkernel_abi.h 的 ZkEachCursor（48 字节定宽）。
// 游标由调用方持有（纯数据结构，原生侧不登记、无泄漏面）；正常消费经
// ZKernelWorld.EachBatches，一般无需手触本类型。

using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 批量迭代游标（blittable POD，48 字节；布局须与原生 ZkEachCursor 逐字段一致，
    /// 公开字段为 blittable 封送需要——同 ZrNative 既有 POD 约定）。
    /// 调用方不得手改字段（原生 Next 做防御性校验，损坏返回 ErrArgs）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZkEachCursor
    {
        /// <summary>Begin 时的世界布局版本（Next 比对，布局变更即作废游标）。</summary>
        public ulong LayoutVersion;

        /// <summary>查询掩码快照低 64 位（原生 withMask[0]）。</summary>
        public ulong MaskLo;

        /// <summary>查询掩码快照高 64 位（原生 withMask[1]）。</summary>
        public ulong MaskHi;

        /// <summary>下一个待消费的 archetype 下标。</summary>
        public uint ArchIndex;

        /// <summary>当前 archetype 内已消费到的 chunk 下标。</summary>
        public uint ChunkIndex;

        /// <summary>当前 archetype 剩余未交付行数。</summary>
        public uint RowsLeftArch;

        /// <summary>累计已交付行数（耗尽时 = 查询匹配总行数）。</summary>
        public uint RowsDelivered;

        /// <summary>游标状态（原生 ZK_CURSOR_*：0=NEW 1=ACTIVE 2=DONE）。</summary>
        public uint State;

        /// <summary>保留对齐（恒 0）。</summary>
        public uint Reserved;
    }
}
