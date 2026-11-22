using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>
    /// 主角形象（规格第二部分 §14）：每个 NPC 自己合成的参数。
    /// 全局 Reputation 是错的——同一个人在不同人眼里不该一样。
    /// NPC 不读 player.Reputation，读自己的 image（§14.3）。
    /// </summary>
    public class PlayerImage
    {
        public struct Entry
        {
            public string Claim;       // 关于你的一句话
            public float Weight;       // 渠道基础权重
            public float Distortion;   // 失真度
            public string Channel;     // 亲历 / 亲密者转述 / 官方宣传 / 传闻 / 敌对宣传
            public uint Day;
        }

        /// <summary>§14.1 渠道与基础权重表。</summary>
        public const float W_WITNESS = 1.00f;      // 亲历：最可信，失真 0
        public const float W_CONFIDANT = 0.70f;    // 亲密者转述：失真 0.15
        public const float W_OFFICIAL = 0.50f;     // 官方宣传：失真 0.30
        public const float W_RUMOR = 0.30f;        // 传闻：失真最重，但传播最快
        public const float W_HOSTILE = 0.30f;      // 敌对宣传：方向相反

        public List<Entry> Entries = new();

        /// <summary>他相信你是什么样的人（trait → 0..1）。</summary>
        public Dictionary<string, float> Beliefs = new();

        /// <summary>§14.3：NPC 信任你，读自己的 image，不读全局声望。</summary>
        public float TrustInPlayer() => Beliefs.TryGetValue("可信任", out var v) ? v : 0.5f;

        /// <summary>§14.2 关系会改写权重：他信任这个人 → 那个人说的话权重上升。</summary>
        public void Receive(string claim, float baseWeight, float distortion, string channel, float sourceTrust, uint day)
        {
            float w = baseWeight * (0.5f + 0.5f * sourceTrust);
            Entries.Add(new Entry { Claim = claim, Weight = w, Distortion = distortion, Channel = channel, Day = day });
            // 失真越大，向"坏"的方向漂得越多——传了三手的话通常不是好话
            if (claim.Contains("好事") || claim.Contains("救人"))
                Beliefs["可信任"] = Math.Clamp(TrustInPlayer() + w * (1f - distortion) * 0.2f, 0f, 1f);
            else
                Beliefs["可信任"] = Math.Clamp(TrustInPlayer() - w * distortion * 0.3f, 0f, 1f);
        }

        /// <summary>对外一句话：他眼中你是什么人。</summary>
        public string Describe(NamedNpc observer)
        {
            float trust = TrustInPlayer();
            // §2.4 的语气表：他眼中的你决定他来找你时说什么
            if (trust > 0.7f) return $"{observer.Name} 信你（亲历占多数）。";
            if (trust > 0.45f) return $"{observer.Name} 对你半信半疑（传闻为主）。";
            return $"{observer.Name} 不信你（听到的都是坏话）。";
        }
    }
}
