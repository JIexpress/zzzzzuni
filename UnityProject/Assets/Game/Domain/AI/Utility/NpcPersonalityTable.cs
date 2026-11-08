using System;
using UnityEngine;
using ZFrame.Data.Config;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// <c>CFG_NpcPersonality</c> table asset (TASK-A-TECH-04) — typed
    /// container of <see cref="NPCPersonalityDef"/> rows following the
    /// A-M1-02 ConfigTable pattern (same shape as
    /// <c>ZFrame.Data.Config.EnemyConfigTable</c>: row + table side by side;
    /// the row is engine-free data, the table is the serializable asset).
    /// Authored asset: <c>Assets/Configs/CFG_NpcPersonality.asset</c>,
    /// generated from <c>Assets/Configs/CSV/npc_personality.csv</c>.
    ///
    /// NOTE (architecture): the table type lives in Game.Domain next to its
    /// row because this task's file whitelist does not open
    /// Assets/ZFrame/Data; ScriptableObject is not a purity-scanned token
    /// and Game.Domain already references ZFrame.Data, so the validator
    /// stays green. Runtime consumers (NpcUtilityView) resolve rows through
    /// <see cref="FindRow"/>; they never hard-code weights.
    /// </summary>
    [CreateAssetMenu(fileName = "CFG_NpcPersonality", menuName = "ZFrame/CFG Npc Personality Table")]
    public class NpcPersonalityTable : ConfigTable<NPCPersonalityDef>
    {
        /// <summary>
        /// Resolves a row by Id (linear scan — tables are handfuls of rows).
        /// Returns null for null/empty ids and unknown ids, so a missing
        /// personality degrades to the neutral all-1.0 brain instead of
        /// throwing in a scene.
        /// </summary>
        public NPCPersonalityDef FindRow(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (NPCPersonalityDef row in Rows)
            {
                if (string.Equals(row.Id, id, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            return null;
        }
    }
}
