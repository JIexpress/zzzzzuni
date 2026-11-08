using System;
using System.Collections.Generic;
using System.Text;
using Game.Domain.AI.Memory;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// Enemy coordination, minimal version (TASK-A-AI-02): assigns attackers
    /// to targets so that 全员不打同一个 — each target takes at most
    /// <see cref="Assign"/>'s capacity attackers (default 1), and leftover
    /// attackers get NO target (explicitly null, not "also attack the busy
    /// one"). Pure static function: candidates in, plan out, nothing mutated,
    /// no clock — the falsifiable 分派不重叠 invariant.
    ///
    /// Selection rule (deterministic): every (attacker, target) pair is scored
    /// by an EFFECTIVE distance
    /// <c>distance / (1 + <see cref="NpcAttitudeMath.AngerAttackBoost"/> × anger)</c>
    /// — 按+态度: closer pairs win, and at equal raw distance an ANGRIER
    /// attacker effectively stands closer and wins the target (态度分派).
    /// Pairs are taken greedily best-first; ties break to the earliest
    /// attacker, then the earliest target (input order = tie-break order, same
    /// discipline as the utility brain's registration-order tie-break).
    ///
    /// Kernel boundary (A-AND-08): this type never touches kernel bindings —
    /// the host reads crowd positions from the DataKernel (每帧单次游标读)
    /// and pumps them in as plain <see cref="TargetCandidate"/> structs. The
    /// assignment itself only reads that snapshot (kernel 数据读——只读).
    /// </summary>
    public static class AttackAssignment
    {
        /// <summary>Default per-target capacity: one attacker per enemy (不重叠 by default).</summary>
        public const int DefaultMaxAttackersPerTarget = 1;

        /// <summary>One attacker: stable id, plane position, current anger 0..1 (clamped; 0 = calm).</summary>
        public readonly struct AttackerCandidate
        {
            /// <summary>Stable id (e.g. the NPC id; duplicate ids throw).</summary>
            public readonly string Id;

            /// <summary>Plane position (pumped from the agent or the kernel crowd read).</summary>
            public readonly Vec2 Position;

            /// <summary>Attitude anger 0..1 (clamped on construction).</summary>
            public readonly float Anger;

            /// <summary>Creates a candidate; null/empty id throws, anger is clamped into 0..1.</summary>
            public AttackerCandidate(string id, Vec2 position, float anger)
            {
                if (string.IsNullOrEmpty(id))
                {
                    throw new ArgumentException("AttackerCandidate: id must not be empty.", nameof(id));
                }

                Id = id;
                Position = position;
                Anger = anger < 0f ? 0f : anger > 1f ? 1f : anger;
            }
        }

        /// <summary>One attackable target: stable id and plane position.</summary>
        public readonly struct TargetCandidate
        {
            /// <summary>Stable id (e.g. the kernel crowd entity id; duplicate ids throw).</summary>
            public readonly string Id;

            /// <summary>Plane position (pumped from the agent or the kernel crowd read).</summary>
            public readonly Vec2 Position;

            /// <summary>Creates a candidate; null/empty id throws.</summary>
            public TargetCandidate(string id, Vec2 position)
            {
                if (string.IsNullOrEmpty(id))
                {
                    throw new ArgumentException("TargetCandidate: id must not be empty.", nameof(id));
                }

                Id = id;
                Position = position;
            }
        }

        /// <summary>
        /// The assignment answer: per-attacker target id (or null when nothing
        /// was free), plus per-target load for diagnostics. Immutable once
        /// produced.
        /// </summary>
        public sealed class Plan
        {
            private readonly List<string> _targetByAttacker;
            private readonly List<string> _attackerIds;

            internal Plan(List<string> attackerIds, List<string> targetByAttacker)
            {
                _attackerIds = attackerIds;
                _targetByAttacker = targetByAttacker;
            }

            /// <summary>Attacker ids in input order (parallel to <see cref="TargetOf"/>/indexing).</summary>
            public IReadOnlyList<string> AttackerIds => _attackerIds;

            /// <summary>Assigned target id per attacker in input order (null = no target; holds position instead).</summary>
            public IReadOnlyList<string> TargetOf => _targetByAttacker;

            /// <summary>Number of attackers that received a target.</summary>
            public int AssignedCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < _targetByAttacker.Count; i++)
                    {
                        if (_targetByAttacker[i] != null)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }

            /// <summary>True when <paramref name="attackerId"/> received a target (unknown id → false).</summary>
            public bool IsAssigned(string attackerId)
            {
                return IndexOf(attackerId) >= 0 && _targetByAttacker[IndexOf(attackerId)] != null;
            }

            /// <summary>The target id assigned to <paramref name="attackerId"/>, or null when unassigned / unknown.</summary>
            public string TargetOfId(string attackerId)
            {
                int index = IndexOf(attackerId);
                return index >= 0 ? _targetByAttacker[index] : null;
            }

            /// <summary>Debug form: "npc-a->enemy-2; npc-b->(none)".</summary>
            public string Format()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _attackerIds.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append("; ");
                    }

                    sb.Append(_attackerIds[i]).Append("->");
                    sb.Append(_targetByAttacker[i] ?? "(none)");
                }

                return sb.ToString();
            }

            private int IndexOf(string attackerId)
            {
                for (int i = 0; i < _attackerIds.Count; i++)
                {
                    if (string.Equals(_attackerIds[i], attackerId, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        private sealed class PairCandidate
        {
            public readonly int AttackerIndex;
            public readonly int TargetIndex;
            public readonly float EffectiveDistance;

            public PairCandidate(int attackerIndex, int targetIndex, float effectiveDistance)
            {
                AttackerIndex = attackerIndex;
                TargetIndex = targetIndex;
                EffectiveDistance = effectiveDistance;
            }
        }

        /// <summary>
        /// Assigns attackers to targets (see type doc for the rule). Returns a
        /// plan whose <see cref="Plan.TargetOf"/> list parallels the attacker
        /// input order. Throws on duplicate ids or a capacity below 1 — a
        /// capacity of 0 would mean "attack nothing", which is a caller bug,
        /// not an assignment result.
        /// </summary>
        public static Plan Assign(
            IReadOnlyList<AttackerCandidate> attackers,
            IReadOnlyList<TargetCandidate> targets,
            int maxAttackersPerTarget = DefaultMaxAttackersPerTarget)
        {
            if (attackers == null)
            {
                throw new ArgumentNullException(nameof(attackers));
            }

            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            if (maxAttackersPerTarget < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAttackersPerTarget), "AttackAssignment.Assign: capacity must be >= 1.");
            }

            EnsureUniqueIds(attackers, targets);

            var attackerIds = new List<string>(attackers.Count);
            for (int i = 0; i < attackers.Count; i++)
            {
                attackerIds.Add(attackers[i].Id);
            }

            var pairs = new List<PairCandidate>(attackers.Count * targets.Count);
            for (int a = 0; a < attackers.Count; a++)
            {
                for (int t = 0; t < targets.Count; t++)
                {
                    float distance = Vec2.Distance(attackers[a].Position, targets[t].Position);
                    float angerFactor = 1f + (NpcAttitudeMath.AngerAttackBoost * attackers[a].Anger);
                    pairs.Add(new PairCandidate(a, t, distance / angerFactor));
                }
            }

            // Total-order sort (effective distance, then attacker index, then
            // target index): deterministic regardless of List.Sort stability —
            // equal keys can only come from the same (a, t) pair.
            pairs.Sort(ComparePairs);

            var targetByAttacker = new List<string>(attackers.Count);
            for (int i = 0; i < attackers.Count; i++)
            {
                targetByAttacker.Add(null);
            }

            var attackerTaken = new bool[attackers.Count];
            var targetLoad = new int[targets.Count];
            for (int p = 0; p < pairs.Count; p++)
            {
                PairCandidate pair = pairs[p];
                if (attackerTaken[pair.AttackerIndex] || targetLoad[pair.TargetIndex] >= maxAttackersPerTarget)
                {
                    continue;
                }

                attackerTaken[pair.AttackerIndex] = true;
                targetLoad[pair.TargetIndex]++;
                targetByAttacker[pair.AttackerIndex] = targets[pair.TargetIndex].Id;
            }

            return new Plan(attackerIds, targetByAttacker);
        }

        private static int ComparePairs(PairCandidate x, PairCandidate y)
        {
            int byDistance = x.EffectiveDistance.CompareTo(y.EffectiveDistance);
            if (byDistance != 0)
            {
                return byDistance;
            }

            int byAttacker = x.AttackerIndex.CompareTo(y.AttackerIndex);
            return byAttacker != 0 ? byAttacker : x.TargetIndex.CompareTo(y.TargetIndex);
        }

        private static void EnsureUniqueIds(IReadOnlyList<AttackerCandidate> attackers, IReadOnlyList<TargetCandidate> targets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < attackers.Count; i++)
            {
                if (!seen.Add(attackers[i].Id))
                {
                    throw new ArgumentException($"AttackAssignment.Assign: duplicate attacker id '{attackers[i].Id}'.", nameof(attackers));
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (!seen.Add(targets[i].Id))
                {
                    throw new ArgumentException($"AttackAssignment.Assign: duplicate id '{targets[i].Id}' across attackers/targets.", nameof(targets));
                }
            }
        }
    }
}
