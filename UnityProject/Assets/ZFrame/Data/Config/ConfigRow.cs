using System;
using UnityEngine;

namespace ZFrame.Data.Config
{
    /// <summary>
    /// Base class for all config rows. <see cref="Id"/> is the unique key of
    /// a row inside its <see cref="ConfigTable{TRow}"/> and must be the first
    /// column of the source CSV.
    /// </summary>
    [Serializable]
    public abstract class ConfigRow
    {
        /// <summary>Unique row identifier within its table (CSV first column).</summary>
        [field: SerializeField]
        public string Id { get; set; } = "";
    }
}
