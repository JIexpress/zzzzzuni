using System.Collections.Generic;

namespace ZFrame.Data.Config
{
    /// <summary>
    /// Runtime config access. Tables are loaded once (or reloaded on hot
    /// reload); queries are dictionary-backed O(1) lookups.
    /// </summary>
    public interface IConfigService
    {
        /// <summary>
        /// Registers (or replaces) a table under its row type. Re-loading a
        /// table type replaces its previous contents (reload semantics).
        /// </summary>
        void Load<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow;

        /// <summary>Row by id, or null when the table is unknown / id missing.</summary>
        TRow Get<TRow>(string id) where TRow : ConfigRow;

        /// <summary>All rows of the loaded table, or null when not loaded.</summary>
        IReadOnlyList<TRow> All<TRow>() where TRow : ConfigRow;

        /// <summary>Alias of <see cref="Load{TRow}"/> — kept for editor hot-reload call sites.</summary>
        void Reload<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow;

        /// <summary>Drops all loaded tables.</summary>
        void Clear();
    }
}
