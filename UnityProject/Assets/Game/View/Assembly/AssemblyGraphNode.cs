using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Part node of the <see cref="AssemblyGraph"/> (TASK-A-TECH-05): the
    /// pure-data image of a scene <see cref="AssemblyPart"/> — identity,
    /// kind, world pose and the local docking-point layout. The graph is the
    /// authoritative description of a vehicle; the scene parts are its
    /// physical realisation.
    /// </summary>
    public sealed class AssemblyGraphNode
    {
        /// <summary>Stable identity of the part inside one graph (unique per graph).</summary>
        public string PartId { get; set; } = "part";

        /// <summary>Which factory creator rebuilds this part.</summary>
        public AssemblyPartKind Kind { get; set; } = AssemblyPartKind.Cube;

        /// <summary>World position of the part origin.</summary>
        public Vector3 Position { get; set; }

        /// <summary>World orientation of the part.</summary>
        public Quaternion Rotation { get; set; } = Quaternion.identity;

        /// <summary>Docking points in part-local space (order = joint address).</summary>
        public List<AssemblyGraphPointSpec> Points { get; } = new List<AssemblyGraphPointSpec>();
    }
}
