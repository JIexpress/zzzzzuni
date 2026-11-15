using UnityEngine;

namespace ZFrame.Data.Config
{
    /// <summary>
    /// Enemy config table. Authored asset: Assets/Configs/CFG_EnemyTable.asset
    /// (generated from Assets/Configs/CSV/enemy.csv by the CSV importer).
    /// </summary>
    [CreateAssetMenu(fileName = "CFG_EnemyTable", menuName = "ZFrame/CFG Enemy Table")]
    public class EnemyConfigTable : ConfigTable<EnemyConfigRow>
    {
    }
}
