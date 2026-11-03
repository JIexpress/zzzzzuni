using System;

namespace ZFrame.Data.Config
{
    /// <summary>Sample config row shipped as reference table + CSV.</summary>
    [Serializable]
    public class EnemyConfigRow : ConfigRow
    {
        /// <summary>Max hit points.</summary>
        [field: UnityEngine.Range(1, 10000)]
        [field: UnityEngine.SerializeField]
        public int Hp { get; set; }

        /// <summary>Movement speed in units/second.</summary>
        [field: UnityEngine.Range(0f, 100f)]
        [field: UnityEngine.SerializeField]
        public float Speed { get; set; }

        /// <summary>Damage per hit.</summary>
        [field: UnityEngine.Range(0, 1000)]
        [field: UnityEngine.SerializeField]
        public int Damage { get; set; }
    }
}
