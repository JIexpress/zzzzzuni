using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    public enum DeedType : byte { Purge, Rescue, Suppress, Build, Massacre }   // 讨伐/救援/镇压/建设/屠杀

    /// <summary>地区事迹（声望认知 §2.2）：关键字段是 WitnessCount。</summary>
    public class LocalDeed
    {
        public DeedType Type;
        public float Magnitude;     // 规模
        public int WitnessCount;    // 有多少人看见了
        public bool IsLegendary;    // 是否足够大，会变成传说

        public override string ToString() => $"{TypeCn()}×{Magnitude:0.0}（目击 {WitnessCount} 人）";
        public string TypeCn() => Type switch
        {
            DeedType.Purge => "讨伐", DeedType.Rescue => "救援", DeedType.Suppress => "镇压",
            DeedType.Build => "建设", DeedType.Massacre => "屠杀", _ => "?",
        };
    }

    /// <summary>
    /// 地区声望（声望认知 §2）：政策 + 事迹。
    /// 执行与有利分开算（§2.1 四象限），因为它们是独立的——"好政策 + 执行糟糕"产生的是失望，失望比愤怒更难修复。
    /// </summary>
    public class RegionState
    {
        public static int _nextId;
        public int Id = _nextId++;
        public string Name;
        public float AvgAttitude;            // 该地区对管理者的基准态度，供 NpcSlot 采样

        public float PolicyExecution;        // 政策执行到位吗 0..1
        public float PolicyBenefit;          // 政策对当地人有利吗 -1..1

        private readonly List<LocalDeed> _deeds = new();     // 有目击者 → 直接进地区声望
        private readonly List<LocalDeed> _rumorPool = new(); // 无目击者 → 只进传闻池，会被加工、失真

        public RegionState(string name, float avgAttitude)
        {
            Name = name;
            AvgAttitude = avgAttitude;
        }

        /// <summary>§2 公式：执行×0.35 + 有利×0.35 + 事迹。</summary>
        public float Reputation()
        {
            float deeds = 0f;
            foreach (var d in _deeds) deeds += DeedSign(d.Type) * d.Magnitude;
            foreach (var d in _rumorPool) deeds += DeedSign(d.Type) * d.Magnitude * 0.5f;   // 传闻打折且会失真
            return PolicyExecution * 0.35f + PolicyBenefit * 0.35f + deeds;
        }

        private static float DeedSign(DeedType t) => t switch
        {
            DeedType.Rescue => 1f, DeedType.Build => 0.8f, DeedType.Purge => -0.3f,
            DeedType.Suppress => -0.6f, DeedType.Massacre => -1f, _ => 0f,
        };

        /// <summary>同一件事，有人看见和没人看见，后果完全不同（§2.2）。</summary>
        public void AddDeed(LocalDeed d)
        {
            if (d.WitnessCount > 0) _deeds.Add(d);
            else _rumorPool.Add(d);
        }

        /// <summary>§2.1 四象限的自我描述。</summary>
        public string PolicyVerdict()
        {
            bool execGood = PolicyExecution >= 0.5f;
            bool benefGood = PolicyBenefit >= 0f;
            if (execGood && benefGood)  return "声望大涨：说到做到。";
            if (execGood && !benefGood) return "恐惧型声望：「他真干得出来。」";
            if (!execGood && benefGood) return "「说得好听，做不到。」——失望比愤怒更难修复。";
            return "「又蠢又坏。」";
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"【{Name}】声望 {Reputation():+0.00;-0.00} —— {PolicyVerdict()}");
            foreach (var d in _deeds) sb.Append($"\n  事迹(实证)：{d}");
            foreach (var d in _rumorPool) sb.Append($"\n  事迹(传闻池, 失真风险)：{d}");
            return sb.ToString();
        }
    }
}
