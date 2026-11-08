using System.Collections.Generic;
using System.Text;

namespace Game.View.Assembly
{
    /// <summary>
    /// Validation verdict of an <see cref="AssemblyGraph"/> (TASK-A-TECH-05).
    /// The three checks the slice demands: connectivity (one component),
    /// orphans (parts with no joint while other parts exist) and duplicate
    /// connections (a docking point used twice, or the same point pair
    /// bonded twice). <see cref="Errors"/> carries one human-readable line
    /// per finding so failures are diagnosable without a debugger.
    /// </summary>
    public sealed class AssemblyGraphValidation
    {
        /// <summary>True when no finding was raised.</summary>
        public bool IsValid => Errors.Count == 0;

        /// <summary>Number of connected components (0 for an empty graph).</summary>
        public int ComponentCount { get; set; }

        /// <summary>Part ids with no joint while the graph has other parts.</summary>
        public List<string> OrphanPartIds { get; } = new List<string>();

        /// <summary>One human-readable line per finding (empty = valid).</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>Single-line summary (test/diagnostic helper).</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("AssemblyGraphValidation{valid=").Append(IsValid)
                .Append(", components=").Append(ComponentCount)
                .Append(", orphans=").Append(OrphanPartIds.Count)
                .Append(", errors=").Append(Errors.Count).Append('}');
            foreach (string error in Errors)
            {
                sb.Append("\n  - ").Append(error);
            }

            return sb.ToString();
        }
    }
}
