using System;
using System.Collections.Generic;
using ZFrame.Gameplay;

namespace Game.Domain.AI.Coordination
{
    /// <summary>
    /// Pure ring-formation math (TASK-A-AI-02): the i-th member of a squad of
    /// n stands <paramref name="radius"/> metres from the leader at angle
    /// <c>startAngle + 2π·i/n</c>. Slot positions are UNIQUE BY CONSTRUCTION
    /// (distinct indices in [0, n) map to distinct angles modulo 2π), so two
    /// squadmates can never occupy the same formation slot — the falsifiable
    /// 编队槽唯一 invariant asserted in tests. Same leader/index/count/radius
    /// in, same position out: no clock, no state, no randomness.
    /// </summary>
    public static class FormationSlot
    {
        /// <summary>Two slot positions closer than this are considered colliding (float-noise guard in tests).</summary>
        public const float DistinctEpsilon = 1e-4f;

        /// <summary>
        /// Computes one ring slot. <paramref name="index"/> must be in
        /// [0, <paramref name="slotCount"/>), <paramref name="slotCount"/> >= 1,
        /// <paramref name="radius"/> finite and >= 0 — violations throw
        /// (fail-fast wiring guards). Angle zero points along +X; the caller's
        /// View rotates the ring with the leader's facing if it wants.
        /// </summary>
        public static Vec2 Calculate(Vec2 leader, int index, int slotCount, float radius, float startAngleRadians = 0f)
        {
            if (slotCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(slotCount), "FormationSlot: slotCount must be >= 1.");
            }

            if (index < 0 || index >= slotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "FormationSlot: index must be in [0, slotCount).");
            }

            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), "FormationSlot: radius must be a finite value >= 0.");
            }

            float angle = startAngleRadians + ((float)Math.PI * 2f * index / slotCount);
            return new Vec2(leader.X + ((float)Math.Cos(angle) * radius), leader.Y + ((float)Math.Sin(angle) * radius));
        }

        /// <summary>
        /// True when every position in the list is pairwise farther apart than
        /// <see cref="DistinctEpsilon"/> — the direct assertion of the slot
        /// uniqueness invariant (tests feed the assigned ring positions here).
        /// </summary>
        public static bool AreDistinct(IReadOnlyList<Vec2> positions)
        {
            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            for (int i = 0; i < positions.Count; i++)
            {
                for (int j = i + 1; j < positions.Count; j++)
                {
                    if (Vec2.Distance(positions[i], positions[j]) <= DistinctEpsilon)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
