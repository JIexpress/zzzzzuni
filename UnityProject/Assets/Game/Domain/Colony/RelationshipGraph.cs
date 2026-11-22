using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Domain.Colony
{
    /// <summary>关系类型（§15.3）：决定「断了会怎样」。</summary>
    public enum RelationType : byte { Kin, Colleague, Friend, Mentor, Feud, Trade }

    /// <summary>
    /// 一条关系边（§15.1）：「立场」与「亲密」是两个独立维度，
    /// Affinity 与 PublicAffinity 分开，才有「表面亲密」。
    /// 一条边同时是信息通道、情绪通道、组织通道（§15.4）。
    /// </summary>
    public class RelationshipEdge
    {
        public int A, B;
        public float Affinity;         // 真实好感 -1..1
        public float PublicAffinity;   // 表面关系（旁人看到的）
        public RelationType Type;
        public float Trust;            // 信任度 → 决定信息传播的权重与失真
        public float Debt;             // 人情债

        public string TypeCn() => Type switch
        {
            RelationType.Kin => "血缘", RelationType.Colleague => "同事", RelationType.Friend => "朋友",
            RelationType.Mentor => "师徒", RelationType.Feud => "仇敌", RelationType.Trade => "交易", _ => "?",
        };
    }

    /// <summary>
    /// 人际关系网络（§15）：关系是图，不是树。
    /// 具名层存完整图（每人 5~15 条边，§15.5）；聚落层只存 InternalConnectivity 标量；背景层不存。
    /// 每日结算三条传导通道（§15.4）：PropagateInfo / PropagateMood / PropagateOrg。
    /// </summary>
    public class RelationshipGraph
    {
        private readonly Dictionary<int, List<RelationshipEdge>> _adj = new();

        public void Add(RelationshipEdge e)
        {
            AddAdj(e.A, e); AddAdj(e.B, e);
        }

        private void AddAdj(int id, RelationshipEdge e)
        {
            if (!_adj.TryGetValue(id, out var l)) { l = new List<RelationshipEdge>(); _adj[id] = l; }
            l.Add(e);
        }

        public IReadOnlyList<RelationshipEdge> EdgesOf(int id) => _adj.TryGetValue(id, out var l) ? l : Array.Empty<RelationshipEdge>();

        public int EdgeCount => _adj.Values.Sum(l => l.Count) / 2;
        public IEnumerable<RelationshipEdge> AllEdges => _adj.Values.SelectMany(l => l).GroupBy(e => (Math.Min(e.A, e.B), Math.Max(e.A, e.B))).Select(g => g.First());

        private static int _fakeIntimateCount;   // MVP 10 只做一个样本

        /// <summary>
        /// 建网（MVP 9/10）：每人 5~15 条边（§15.5），新具名 NPC 连向已存在的人保证连通。
        /// 类型启发：同职业→同事；立场近→朋友；少量血缘/师徒/仇敌/交易。
        /// 第一个同职业对强制做成「表面亲密、私下敌对」样本（MVP 10：逢年过节吃饭，背地里举报）。
        /// </summary>
        public void AddFromCard(NamedNpc n, IReadOnlyDictionary<int, NamedNpc> existing, Rng rng)
        {
            if (existing.Count == 0) return;
            int k = Math.Min(rng.RangeInt(5, 16), existing.Count);
            var others = existing.Keys.Where(id => id != n.Id).ToList();
            if (others.Count == 0) return;
            for (int i = 0; i < k; i++)
            {
                int other = others[rng.RangeInt(0, others.Count)];
                var o = existing[other];

                bool sameJob = o.Job == n.Job;
                bool makeFake = sameJob && _fakeIntimateCount == 0;   // MVP 10：第一个同职业对做样本
                if (makeFake) _fakeIntimateCount++;

                RelationType type =
                    makeFake ? RelationType.Colleague
                    : sameJob ? RelationType.Colleague
                    : rng.Chance(0.40f) ? RelationType.Colleague
                    : rng.Chance(0.42f) ? RelationType.Friend
                    : rng.Chance(0.25f) ? RelationType.Trade
                    : rng.Chance(0.17f) ? RelationType.Feud
                    : rng.Chance(0.5f) ? RelationType.Mentor : RelationType.Kin;

                float affinity = type == RelationType.Feud ? rng.Range(-1f, -0.5f)
                    : type == RelationType.Kin ? rng.Range(0.4f, 0.9f)
                    : rng.Range(-0.3f, 0.9f);
                if (makeFake)
                {
                    // 立场相反 + 表面亲密（§15.2：反/高（表面）= 亲戚/同事：逢年过节吃饭，背地里举报）
                    affinity = rng.Range(-1f, -0.5f);
                    n.Grievance = Math.Clamp(n.Grievance + 0.1f, 0f, 1f);
                }
                Add(new RelationshipEdge
                {
                    A = n.Id, B = other,
                    Affinity = affinity,
                    PublicAffinity = makeFake ? rng.Range(0.6f, 0.9f) : Math.Clamp(affinity + rng.Range(-0.15f, 0.15f), -1f, 1f),
                    Type = type,
                    Trust = Math.Clamp(0.5f + affinity * 0.4f + rng.Range(-0.1f, 0.1f), 0f, 1f),
                    Debt = rng.Chance(0.2f) ? rng.Range(0.1f, 0.5f) : 0f,
                });
            }
        }

        /// <summary>§2.3 谁来找你：取目标 Trust 最高的两人，优先选「敢说话」的（低宜人性 × 高地位）。</summary>
        public NamedNpc ChooseIntervener(NamedNpc target, IReadOnlyDictionary<int, NamedNpc> named)
        {
            var candidates = EdgesOf(target.Id)
                .Where(e => e.Type is RelationType.Kin or RelationType.Friend or RelationType.Mentor)
                .OrderByDescending(e => e.Trust)
                .Select(e => e.A == target.Id ? e.B : e.A)
                .Distinct()
                .Where(id => named.ContainsKey(id))
                .Take(2)
                .Select(id => named[id]);
            return candidates
                .OrderByDescending(n => (1 - n.Agreeableness) * n.Status)
                .FirstOrDefault();
        }

        /// <summary>
        /// §15.4 每日结算：一条边同时走三条通道。
        /// 信息传导：权重 ← Trust，失真 ← 1 - Trust；情绪传导：传染率 ← Affinity；组织传导：组织度在连通子图内累加。
        /// </summary>
        public void PropagateDaily(IReadOnlyDictionary<int, NamedNpc> named, uint day, List<string> log, Rng rng)
        {
            foreach (var e in AllEdges)
            {
                if (!named.TryGetValue(e.A, out var a) || !named.TryGetValue(e.B, out var b)) continue;

                // ── 情绪传导：不满沿边传染（亲近的人更容易被影响）——异常行为 §3.6 同款
                var src = a.Protest?.Level > b.Protest?.Level ? a : b;
                var dst = src == a ? b : a;
                if (src.Protest != null && src.Protest.Level > 0.4f && e.Affinity > 0.5f)
                {
                    float contagion = src.Protest.Level * 0.15f * e.Affinity;
                    dst.Grievance = Math.Clamp(dst.Grievance + contagion, 0f, 1f);
                }

                // ── 信息传导：好事/坏事沿边传播，写入对方 PlayerImage
                var talker = rng.Chance(0.5f) ? a : b;
                var listener = talker == a ? b : a;
                if (rng.Chance(0.05f) && listener.Image != null)
                {
                    bool good = talker.Loyalty > 0.6f;
                    string claim = good ? "听说管理者救人" + "的好事" : "听说管理者干了坏事";
                    float distortion = 1f - e.Trust;      // 失真 ← 1 - Trust
                    listener.Image.Receive(claim, good ? PlayerImage.W_CONFIDANT : PlayerImage.W_RUMOR,
                        distortion, "传闻", e.Trust, day);
                }

                // ── 组织传导：反对者沿边串联（两人怨气都高 → 组织度在连通子图内累加）
                if (a.Grievance > 0.5f && b.Grievance > 0.5f && e.Type is not RelationType.Trade)
                {
                    float orgGain = Math.Min(a.Grievance, b.Grievance) * 0.008f;
                    a.Organization = Math.Clamp(a.Organization + orgGain, 0f, 1f);
                    b.Organization = Math.Clamp(b.Organization + orgGain, 0f, 1f);
                }
            }
        }
    }
}
