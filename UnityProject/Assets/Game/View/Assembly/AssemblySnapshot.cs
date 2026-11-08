using System.Collections.Generic;

namespace Game.View.Assembly
{
    /// <summary>
    /// Serialised assembly — the shareable "craft file" of the assembly
    /// system (TASK-A-TECH-05): a full vehicle snapshot (parts + joints)
    /// written to <c>persistentDataPath/assemblies/{name}.json</c>. A plain
    /// data contract (POCO properties only) so Newtonsoft round-trips it
    /// without Unity-type quirks; <see cref="AssemblySnapshotCodec"/> does
    /// the graph &lt;-&gt; snapshot mapping.
    /// </summary>
    public sealed class AssemblySnapshot
    {
        /// <summary>Format version of this file (see <see cref="AssemblySnapshotCodec.CurrentVersion"/>).</summary>
        public int Version { get; set; } = AssemblySnapshotCodec.CurrentVersion;

        /// <summary>Human-facing craft name (also the file name).</summary>
        public string Name { get; set; } = "assembly";

        /// <summary>UTC unix seconds when the snapshot was written (provenance only).</summary>
        public long SavedAtUnixSeconds { get; set; }

        /// <summary>Part nodes, insertion order preserved.</summary>
        public List<AssemblySnapshotPart> Parts { get; } = new List<AssemblySnapshotPart>();

        /// <summary>Joint edges, replay order preserved.</summary>
        public List<AssemblySnapshotJoint> Joints { get; } = new List<AssemblySnapshotJoint>();
    }
}
