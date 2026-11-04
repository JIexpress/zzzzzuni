// ZkEntity.cs —— DataKernel 实体代际句柄（blittable，TASK-B-R9-17）。
// 布局镜像：Native/zkernel/zkernel.h 的 ZkEntity / zkernel_abi.h 的 ZkEntityAbi
//（uint32 index + uint32 generation，8 字节）；原生侧 static_assert 锁定。

using System;
using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// DataKernel 实体代际句柄（blittable POD）。布局须与原生 ZkEntityAbi 逐字段
    /// 一致（公开字段为互斥例外：blittable 封送需要，同 ZrNative 既有 POD 约定）。
    /// index 在销毁后复用，generation 防 dangling——旧句柄经代际失配被原生侧拒绝。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZkEntity : IEquatable<ZkEntity>
    {
        /// <summary>实体槽位索引（销毁后复用）。</summary>
        public uint Index;

        /// <summary>代际：销毁 +1，旧句柄因代际不匹配被拒。</summary>
        public uint Generation;

        /// <summary>指示相等比较（index 与 generation 都相同才算同一实体）。</summary>
        public bool Equals(ZkEntity other)
        {
            return Index == other.Index && Generation == other.Generation;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is ZkEntity other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return unchecked((int)(Index * 397u) ^ (int)Generation);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"ZkEntity({Index}, g{Generation})";
        }

        /// <summary>相等运算符。</summary>
        public static bool operator ==(ZkEntity left, ZkEntity right)
        {
            return left.Equals(right);
        }

        /// <summary>不等运算符。</summary>
        public static bool operator !=(ZkEntity left, ZkEntity right)
        {
            return !left.Equals(right);
        }
    }
}
