// ZkMask.cs —— DataKernel 128 位组件掩码的托管构建器（TASK-B-R9-17）。
// 组件 ID 0..127：Lo = bit 0..63，Hi = bit 64..127（对应原生 Mask128 的 w[0]/w[1]，
// 高二字保留恒 0）。ABI 侧以 (maskLo, maskHi) 双 ulong 传递。

using System;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 128 位组件掩码构建器（组件 ID 0..127）。值类型，可链式构建：
    /// <c>ZkMask.Of(CompA).With(CompB)</c>。
    /// </summary>
    public struct ZkMask
    {
        /// <summary>低 64 位掩码（组件 ID 0..63）。</summary>
        public ulong Lo;

        /// <summary>高 64 位掩码（组件 ID 64..127）。</summary>
        public ulong Hi;

        /// <summary>置位一个组件 ID。</summary>
        /// <exception cref="ArgumentOutOfRangeException">compId 超出 0..127。</exception>
        public void Set(uint compId)
        {
            if (compId >= 128u)
            {
                throw new ArgumentOutOfRangeException(nameof(compId), "组件 ID 只允许 0..127");
            }

            if (compId < 64u)
            {
                Lo |= 1ul << (int)compId;
            }
            else
            {
                Hi |= 1ul << (int)(compId - 64u);
            }
        }

        /// <summary>测试组件 ID 是否置位（compId >= 128 恒 false）。</summary>
        public bool Has(uint compId)
        {
            if (compId >= 128u)
            {
                return false;
            }

            var bit = compId < 64u ? (Lo >> (int)compId) : (Hi >> (int)(compId - 64u));
            return (bit & 1ul) != 0ul;
        }

        /// <summary>单组件掩码。</summary>
        public static ZkMask Of(uint compId)
        {
            var m = default(ZkMask);
            m.Set(compId);
            return m;
        }

        /// <summary>返回追加了 compId 的副本（本值不变，链式构建用）。</summary>
        public ZkMask With(uint compId)
        {
            var m = this;
            m.Set(compId);
            return m;
        }

        /// <summary>置位个数（= 查询命中时的列数）。</summary>
        public int CountBits()
        {
            return PopCount(Lo) + PopCount(Hi);
        }

        private static int PopCount(ulong v)
        {
            var count = 0;
            while (v != 0ul)
            {
                v &= v - 1ul;
                ++count;
            }

            return count;
        }
    }
}
