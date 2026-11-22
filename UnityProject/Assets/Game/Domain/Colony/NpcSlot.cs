using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 背景层 NPC 槽位 —— 规格索引「最该先动手的三件事」第 1 条 + 《声望认知与人口》§4。
    /// 十几万人不建对象，每人只占这 8 字节；没接触的 NPC 不需要"存在"。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NpcSlot
    {
        public float BaseAttitude;   // 4B：从地区民意采样得来（§4.1）
        public ushort RegionId;      // 2B
        public byte Flag;            // bit0：是否有档案；bit1：是否已具名
        private byte _pad;           // 凑满 8 字节

        public const byte F_PROFILED = 1;   // 有档案（交互过）
        public const byte F_NAMED   = 2;    // 已具名化（§6.3）

        public bool Profiled { readonly get => (Flag & F_PROFILED) != 0; set => Flag = value ? (byte)(Flag | F_PROFILED) : (byte)(Flag & ~F_PROFILED); }
        public bool Named   { readonly get => (Flag & F_NAMED) != 0;   set => Flag = value ? (byte)(Flag | F_NAMED)   : (byte)(Flag & ~F_NAMED); }
    }

    /// <summary>
    /// 有接触后才创建的档案（§4.1 的 NpcProfile）。
    /// 用「地区参数 + 随机种子」确定性生成，存档只需存"哪些 slot 已实例化"。
    /// </summary>
    public class NpcProfile
    {
        public string Name;
        public string Job;
        public string Origin;        // 地球 / 殖民地第二代 / 船民 / 难民
        public float BaseLoyalty;    // 由 BaseAttitude 派生
        public float GrievanceBias;
        public string SpeechTrait;

        public override string ToString() => $"{Name}（{Job}，{Origin}）";
    }

    /// <summary>
    /// 按需实例化的目录：10 万 NPC（无档案）≈ 0.8 MB（§4.2）。
    /// 触发点（§4.3）：看见 → 只渲染不建档案；说话 / 被做了什么 / 卷入事件 → 实例化。
    /// </summary>
    public class NpcDirectory
    {
        private readonly NpcSlot[] _slots;
        private readonly Dictionary<int, NpcProfile> _profiles = new();
        private readonly ulong _worldSeed;

        public static readonly string[] Origins = { "地球", "殖民地第二代", "船民", "难民" };
        private static readonly string[] Surnames = { "陈", "李", "王", "赵", "申屠", "计", "费", "祁", "凌", "屠", "苏", "郑", "唐", "桂", "华", "昝" };
        private static readonly string[] GivenNames = { "九斤", "百川", "无算", "连雪", "熔", "绛", "永年", "蔓青", "春梅", "大镐", "晚棠", "云旗", "一甲", "九章" };
        private static readonly string[] Jobs = { "矿工", "船坞装配工", "货运飞行员", "水质净化员", "厨师", "酒保", "保安", "废料回收商", "义体技师", "测绘员", "通信员", "农艺师", "会计", "记者" };
        private static readonly string[] Speeches = { "短句，爱打比方", "长句，先给结论", "反问句多", "轻快，答非所问", "军事化，数字精确" };

        public NpcDirectory(int count, ulong worldSeed, RegionState[] regions, Rng rng)
        {
            _worldSeed = worldSeed;
            _slots = new NpcSlot[count];
            for (int i = 0; i < count; i++)
            {
                var reg = regions[rng.RangeInt(0, regions.Length)];
                _slots[i] = new NpcSlot
                {
                    // BaseAttitude 从地区民意采样（§4.1）
                    BaseAttitude = reg.AvgAttitude + rng.Range(-0.2f, 0.2f),
                    RegionId = (ushort)reg.Id,
                };
            }
        }

        public int Count => _slots.Length;
        public int ProfiledCount { get { int n = 0; foreach (var s in _slots) if (s.Profiled) n++; return n; } }

        /// <summary>玩家远远看见：否（只渲染，不建档案）（§4.3）。</summary>
        public void Seen(int slotId) { /* 故意什么都不做 */ }

        /// <summary>玩家说话 / 对他做了什么 / 他卷入事件：是（实例化）。</summary>
        public NpcProfile EnsureProfile(int slotId)
        {
            var slot = _slots[slotId];
            if (slot.Profiled) return _profiles[slotId];

            // 触发实例化：用种子确定性生成（§4.3）——同一个 NPC 永远生成同一个
            var rng = new Rng(Rng.Hash(_worldSeed, (ulong)slotId));
            var p = GenerateProfile(slot.RegionId, rng, slot.BaseAttitude);
            slot.Profiled = true;
            _slots[slotId] = slot;
            _profiles[slotId] = p;
            return p;
        }

        private static NpcProfile GenerateProfile(ushort regionId, Rng rng, float baseAttitude)
        {
            return new NpcProfile
            {
                Name = rng.Pick(Surnames) + rng.Pick(GivenNames),
                Job = rng.Pick(Jobs),
                Origin = rng.Pick(Origins),
                // 态度越差，忠诚基准越低、怨气偏置越高
                BaseLoyalty = Math.Clamp(0.5f + baseAttitude * 0.4f + rng.Range(-0.1f, 0.1f), 0f, 1f),
                GrievanceBias = Math.Clamp(-baseAttitude * 0.3f + rng.Range(0f, 0.2f), 0f, 1f),
                SpeechTrait = rng.Pick(Speeches),
            };
        }

        /// <summary>§4.2 存储估算：无档案 8B/槽，档案约 +400B，具名约 +2KB。</summary>
        public (long slotBytes, long profileBytes, long estimatedTotal) StorageEstimate()
        {
            long slotBytes = Marshal.SizeOf<NpcSlot>() * (long)_slots.Length;
            long profileBytes = _profiles.Count * 400L;
            return (slotBytes, profileBytes, slotBytes + profileBytes);
        }
    }
}
