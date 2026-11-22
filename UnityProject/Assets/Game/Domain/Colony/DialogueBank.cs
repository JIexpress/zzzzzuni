using System;
using System.Collections.Generic;
using System.Linq;
using Game.Domain.Colony;
using Game.Domain.Colony.Sim;

namespace Game.Domain.Colony.Dialogue
{
    /// <summary>
    /// 台词库（数据来自 GLM 300 条烘焙台词）+ 语气合成（NPC规格 §13/§17）。
    /// 口癖不是人格：口癖只在 10%~20% 概率注入；
    /// 阶段二的沉默，比任何台词都有力（§17.2）。
    /// </summary>
    public class DialogueBank
    {
        private readonly Dictionary<(string role, string topic), List<string>> _lines = new();

        /// <summary>职业圈 → 烘焙角色映射（预生成池给"同类职业的中间圈"，规格 §9：中间圈对话用预生成片段池）。</summary>
        public static readonly Dictionary<string, string> JobToRole = new()
        {
            ["船坞装配工"] = "A", ["义体技师"] = "A", ["冶炼工"] = "A", ["货运飞行员"] = "A", ["轨道拖船驾驶员"] = "A",
            ["矿工"] = "C", ["废料回收商"] = "C",
            ["走私贩"] = "D", ["赌场老板"] = "D", ["酒保"] = "D",
            ["保安"] = "E", ["赏金猎人"] = "E",
            ["医生"] = "A", ["护士"] = "A", ["水质净化员"] = "A",
        };

        public void Load(IEnumerable<(string role, string topic, string line)> rows)
        {
            foreach (var (role, topic, line) in rows)
            {
                var key = (role, topic);
                if (!_lines.TryGetValue(key, out var l)) { l = new List<string>(); _lines[key] = l; }
                l.Add(line);
            }
        }

        /// <summary>TASK-A-COL-03：台词库随档走（Restore 的唯一输入是存档 DTO）——Capture 侧逐行读出，同程序集 internal。</summary>
        internal IEnumerable<(string role, string topic, string line)> AllLines()
        {
            foreach (var pair in _lines)
                foreach (var line in pair.Value)
                    yield return (pair.Key.role, pair.Key.topic, line);
        }

        public int Total => _lines.Values.Sum(l => l.Count);

        /// <summary>§13 语气合成：关系定基调，场合定正式度，状态定 rambling。同一 NPC 对玩家和对同事应像两个人。</summary>
        public string BuildTone(GameWorld w, NamedNpc sp, NamedNpc listener, bool isPublic)
        {
            float intimacy = 0f;
            var edge = w.Relations.EdgesOf(sp.Id).FirstOrDefault(e => e.A == listener.Id || e.B == listener.Id);
            if (edge != null) intimacy = edge.Affinity;
            float power = sp.Status - listener.Status;
            float formal = isPublic ? 0.8f : 0.2f;
            float strain = sp.Fatigue * 0.5f + sp.Fear * 0.3f;

            bool casual = intimacy > 0.5f && formal < 0.5f;
            bool clipped = power > 0 || formal > 0.7f;
            bool rambling = strain > 0.6f;

            return (casual, clipped, rambling) switch
            {
                (true, _, true) => "TU_TIRED",      // 熟人 + 累：话多跑题抱怨
                (true, _, false) => "TU_RELAXED",   // 熟人正常：松弛
                (false, true, _) => "TU_FORMAL",    // 上下级/公开：短句敬语
                (false, false, true) => "TU_STRAINED", // 陌生+紧绷：结巴回避
                _ => "TU_NEUTRAL",
            };
        }

        /// <summary>说一句话。沉默期 + 相关话题 → "……"（§17.2）。</summary>
        public string Speak(GameWorld w, NamedNpc sp, NamedNpc listener, string topic, bool isPublic, Rng rng)
        {
            // 沉默期：某些状态下，正确回应是沉默
            if (sp.Contract.Stage == Stage.Silent && (topic == "抱怨" || topic == "拒绝对话"))
                return "[TU_SILENT] ……";

            string tone = BuildTone(w, sp, listener, isPublic);
            string role = JobToRole.TryGetValue(sp.Job, out var r) ? r : null;
            if (role == null) return $"[{tone}] （{sp.Job}不在预生成池，走 LLM 通道或模板通道）";

            if (!_lines.TryGetValue((role, topic), out var pool))
                pool = _lines.TryGetValue((role, "寒暄"), out var p2) ? p2 : null;
            if (pool == null || pool.Count == 0) return $"[{tone}] ……";

            string line = pool[rng.RangeInt(0, pool.Count)];
            // 口癖只在 15% 概率注入（§13）
            bool quirk = rng.Chance(0.15f);
            return $"[{tone}] {line}" + (quirk ? $"  （口癖注入：{sp.Quirk}）" : "");
        }

        /// <summary>NPC 互相议论（§8.2 高性价比技巧）：玩家对「有人生活」的感受，最大来源是听到别人说话。</summary>
        public string Chatter(GameWorld w, NamedNpc a, NamedNpc b, Rng rng)
        {
            var feed = w.Log.PlayerFeed(Math.Max(1, w.Day - 7), "", new HashSet<string>());
            string ev = feed.Count > 0 ? feed[rng.RangeInt(0, feed.Count)].Text : "最近没什么新鲜事";
            return $"[{a.Name}] 「{Truncate(ev, 24)}……」\n[{b.Name}] 「谁知道呢。」\n[{a.Name}（发现你）] 哎，是你啊。";
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
    }
}
