using System;
using System.Collections.Generic;
using ZFrame.Core;

namespace ZFrame.Data.Config
{
    /// <summary>
    /// Dictionary-backed <see cref="IConfigService"/>. Lookup index is
    /// (row type, id) -> row, built once per Load; Get/All are O(1) after
    /// that. Duplicate ids inside one table are last-wins (the editor
    /// ConfigValidator flags them as authoring errors).
    /// </summary>
    public sealed class ConfigService : IConfigService
    {
        // O(1) lookup index: (row type, id) -> row.
        private readonly Dictionary<(Type rowType, string id), ConfigRow> _index =
            new Dictionary<(Type, string), ConfigRow>();

        // Row lists per table type, stored as List<TRow> (boxed) so All<TRow>
        // can return the authoring-order list without copying.
        private readonly Dictionary<Type, object> _lists =
            new Dictionary<Type, object>();

        /// <inheritdoc />
        public void Load<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            var rowType = typeof(TRow);
            if (_lists.ContainsKey(rowType))
            {
                // Reload semantics: replace the previous registration.
                RemoveType(rowType);
            }

            var list = new List<TRow>(table.Rows.Count);
            foreach (var row in table.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Id))
                {
                    ZLog.Warn("Config", $"table {rowType.Name}: skipping null/empty-Id row");
                    continue;
                }

                _index[(rowType, row.Id)] = row;
                list.Add(row);
            }

            _lists[rowType] = list;
        }

        /// <inheritdoc />
        public TRow Get<TRow>(string id) where TRow : ConfigRow
        {
            if (id != null && _index.TryGetValue((typeof(TRow), id), out var row))
            {
                return (TRow)row;
            }

            return null;
        }

        /// <inheritdoc />
        public IReadOnlyList<TRow> All<TRow>() where TRow : ConfigRow
        {
            return _lists.TryGetValue(typeof(TRow), out var list) ? (IReadOnlyList<TRow>)list : null;
        }

        /// <inheritdoc />
        public void Reload<TRow>(ConfigTable<TRow> table) where TRow : ConfigRow
        {
            Load(table);
        }

        /// <inheritdoc />
        public void Clear()
        {
            _index.Clear();
            _lists.Clear();
        }

        private void RemoveType(Type rowType)
        {
            _lists.Remove(rowType);
            var staleKeys = new List<(Type, string)>();
            foreach (var key in _index.Keys)
            {
                if (key.Item1 == rowType)
                {
                    staleKeys.Add(key);
                }
            }

            foreach (var key in staleKeys)
            {
                _index.Remove(key);
            }
        }
    }
}
