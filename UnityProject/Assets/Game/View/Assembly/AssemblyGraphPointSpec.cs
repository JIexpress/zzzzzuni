using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// One docking point of an <see cref="AssemblyGraphNode"/>, in the part's
    /// local space — the graph-level image of a scene
    /// <see cref="AttachmentPoint"/>. Order inside the node's point list is
    /// the address joints use (<see cref="AssemblyGraphJoint.FromPointIndex"/>),
    /// matching the scene-side <c>AssemblyPart.Points</c> indexing.
    /// </summary>
    public sealed class AssemblyGraphPointSpec
    {
        /// <summary>Docking class of the point.</summary>
        public AttachmentPointType Type { get; set; } = AttachmentPointType.Structural;

        /// <summary>Docking centre, part-local.</summary>
        public Vector3 LocalPosition { get; set; }

        /// <summary>Outward face normal, part-local.</summary>
        public Vector3 LocalForward { get; set; } = Vector3.forward;
    }
}
