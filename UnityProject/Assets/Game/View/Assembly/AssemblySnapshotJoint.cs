namespace Game.View.Assembly
{
    /// <summary>
    /// Joint edge of an <see cref="AssemblySnapshot"/> (TASK-A-TECH-05):
    /// endpoints addressed as (part id, point index) on each side, replay
    /// order preserved through list order.
    /// </summary>
    public sealed class AssemblySnapshotJoint
    {
        /// <summary>Joint identity inside the snapshot (unique per snapshot).</summary>
        public string JointId { get; set; } = "";

        /// <summary>Part that moved into the dock (owner side).</summary>
        public string FromPartId { get; set; } = "";

        /// <summary>Docking point index on <see cref="FromPartId"/>.</summary>
        public int FromPointIndex { get; set; }

        /// <summary>Part that was already in place.</summary>
        public string ToPartId { get; set; } = "";

        /// <summary>Docking point index on <see cref="ToPartId"/>.</summary>
        public int ToPointIndex { get; set; }

        /// <summary><see cref="AssemblyJointKind"/> as int.</summary>
        public int Kind { get; set; }
    }
}
