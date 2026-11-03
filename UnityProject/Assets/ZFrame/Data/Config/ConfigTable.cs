using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFrame.Data.Config
{
    /// <summary>
    /// Generic ScriptableObject container for typed config rows. Rows are
    /// authored on the asset (or generated from CSV by the editor importer);
    /// runtime consumers go through <see cref="IConfigService"/> for O(1)
    /// lookups.
    /// </summary>
    public class ConfigTable<TRow> : ScriptableObject where TRow : ConfigRow
    {
        [SerializeField] private List<TRow> _rows = new List<TRow>();

        /// <summary>All rows in authoring order. Never null.</summary>
        public IReadOnlyList<TRow> Rows
        {
            get { return _rows; }
        }

        /// <summary>
        /// Appends a row programmatically (editor tooling / tests). Runtime
        /// code should treat tables as read-only data.
        /// </summary>
        public void AddRow(TRow row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            _rows.Add(row);
        }

        /// <summary>Removes every row (editor tooling / tests).</summary>
        public void ClearRows()
        {
            _rows.Clear();
        }
    }
}
