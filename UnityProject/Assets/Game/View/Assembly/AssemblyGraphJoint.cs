namespace Game.View.Assembly
{
    /// <summary>
    /// Joint edge of the <see cref="AssemblyGraph"/> (TASK-A-TECH-05): a bond
    /// between two docking points, addressed as (part id, point index) on
    /// each side. The moving side of the original dock is
    /// <see cref="FromPartId"/> — mirroring
    /// <see cref="AssemblyJoint.OwnerPart"/> — so replay
    /// (<see cref="AssemblyBuilder.Connect"/>) reproduces the original
    /// alignment sequence exactly.
    /// </summary>
    public sealed class AssemblyGraphJoint
    {
        /// <summary>Unique identity inside one graph (auto-assigned when omitted).</summary>
        public string JointId { get; set; } = "";

        /// <summary>Part that moved into the dock (owner side of the scene joint).</summary>
        public string FromPartId { get; set; } = "";

        /// <summary>Index of the docking point on <see cref="FromPartId"/>.</summary>
        public int FromPointIndex { get; set; }

        /// <summary>Part that was already in place (connected-body side).</summary>
        public string ToPartId { get; set; } = "";

        /// <summary>Index of the docking point on <see cref="ToPartId"/>.</summary>
        public int ToPointIndex { get; set; }

        /// <summary>Edge class (rigid lock for this slice).</summary>
        public AssemblyJointKind Kind { get; set; } = AssemblyJointKind.Rigid;
    }
}
