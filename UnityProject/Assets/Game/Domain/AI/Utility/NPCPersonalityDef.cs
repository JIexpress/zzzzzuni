using System;
using UnityEngine;
using ZFrame.Data.Config;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// One NPC's personality weights (TASK-A-TECH-04) — a row of the
    /// <c>CFG_NpcPersonality</c> table
    /// (<c>Assets/Configs/CFG_NpcPersonality.asset</c>, authored from
    /// <c>Assets/Configs/CSV/npc_personality.csv</c> through the A-M1-02
    /// ConfigTable pipeline). Four trait weights in 0.5..2.0 (enforced by
    /// <see cref="Range"/> and the config validator):
    ///
    /// - <see cref="FatigueWeight"/>: how strongly tiredness drives Rest
    /// - <see cref="SocialWeight"/>: how strongly company drives Follow
    /// - <see cref="CuriosityWeight"/>: how strongly findings drive Pickup
    /// - <see cref="BraveryWeight"/>: how strongly roaming drives Patrol
    ///
    /// The user's ask, verbatim: "每个人的都不一样，或者叫权重" — each NPC
    /// ranks the same world differently because its weights differ. All 1.0
    /// = balanced = exactly the pre-personality behaviour (the brain's
    /// weighting becomes identity), which keeps A-TECH-01 compatible.
    /// The <see cref="UtilityBrain"/> multiplies raw desire scores by the
    /// trait weight of the action (multiplication, not power modulation —
    /// monotone, weight 1 is identity, 0-stay-0, and the configured range
    /// keeps any weight from flipping a ranking by more than 4x).
    /// </summary>
    [Serializable]
    public class NPCPersonalityDef : ConfigRow
    {
        /// <summary>Rest desire multiplier (reactions to own fatigue).</summary>
        [field: Range(0.5f, 2f)]
        [field: SerializeField]
        public float FatigueWeight { get; set; } = 1f;

        /// <summary>Follow desire multiplier (desire for company).</summary>
        [field: Range(0.5f, 2f)]
        [field: SerializeField]
        public float SocialWeight { get; set; } = 1f;

        /// <summary>Pickup desire multiplier (investigate and collect).</summary>
        [field: Range(0.5f, 2f)]
        [field: SerializeField]
        public float CuriosityWeight { get; set; } = 1f;

        /// <summary>Patrol desire multiplier (roam far).</summary>
        [field: Range(0.5f, 2f)]
        [field: SerializeField]
        public float BraveryWeight { get; set; } = 1f;

        /// <summary>True when every trait weight of both rows matches exactly (divergence tests use this).</summary>
        public bool SameWeightsAs(NPCPersonalityDef other)
        {
            if (other == null)
            {
                return false;
            }

            return FatigueWeight.Equals(other.FatigueWeight)
                   && SocialWeight.Equals(other.SocialWeight)
                   && CuriosityWeight.Equals(other.CuriosityWeight)
                   && BraveryWeight.Equals(other.BraveryWeight);
        }
    }
}
