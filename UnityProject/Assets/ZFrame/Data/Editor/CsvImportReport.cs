#if UNITY_EDITOR
using System.Collections.Generic;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>Outcome summary of one CSV import run (errors are collected, never thrown).</summary>
    public sealed class CsvImportReport
    {
        /// <summary>All problems found in one pass (bad types, empty Id, unknown columns...).</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>Ids that existed in the table but not in the CSV (kept, reported, not deleted).</summary>
        public List<string> MissingInCsv { get; } = new List<string>();

        /// <summary>Rows appended to the table.</summary>
        public int Added { get; set; }

        /// <summary>Existing rows updated by Id.</summary>
        public int Updated { get; set; }

        /// <summary>True when no errors were collected.</summary>
        public bool Ok
        {
            get { return Errors.Count == 0; }
        }

        /// <summary>Human readable one-line summary.</summary>
        public override string ToString()
        {
            return $"added={Added}, updated={Updated}, missingInCsv={MissingInCsv.Count}, errors={Errors.Count}";
        }
    }
}
#endif
