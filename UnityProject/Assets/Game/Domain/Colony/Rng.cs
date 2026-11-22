using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 确定性随机数（splitmix64）。
    /// 规格《声望认知与人口》§4.4：种子固定 → 结果可复现，
    /// 同一个 NPC 无论什么时候去接触，生成出来的都是同一个人。
    /// 注意：必须是 class——按值传递 struct 会导致序列副本不回写（随机数冻结在旧状态）。
    /// </summary>
    public sealed class Rng
    {
        private ulong _s;

        public Rng(ulong seed) => _s = seed;

        /// <summary>hash(worldSeed, slotId)：把两个整数搅成一个种子。</summary>
        public static ulong Hash(ulong a, ulong b)
        {
            ulong z = a ^ (b + 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong NextU64()
        {
            _s += 0x9E3779B97F4A7C15UL;
            ulong z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>0..1，24 位精度。</summary>
        public float NextFloat01() => (NextU64() >> 40) / 16777216f;

        public float Range(float a, float b) => a + (b - a) * NextFloat01();
        public int RangeInt(int lo, int hi) => lo + (int)(NextFloat01() * (hi - lo)); // [lo, hi)
        public bool Chance(float p) => NextFloat01() < p;
        public T Pick<T>(IReadOnlyList<T> list) => list[RangeInt(0, list.Count)];
    }
}
