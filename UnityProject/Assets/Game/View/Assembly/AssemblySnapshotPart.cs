using System.Collections.Generic;

namespace Game.View.Assembly
{
    /// <summary>
    /// Part node of an <see cref="AssemblySnapshot"/> (TASK-A-TECH-05):
    /// identity, kind, pose and the docking-point layout. Kind-specific
    /// parameters ride along as nullable fields (thrust for thrusters, fuel
    /// for tanks) so the rebuild reproduces behaviour, not just geometry.
    /// </summary>
    public sealed class AssemblySnapshotPart
    {
        /// <summary>Stable identity inside the snapshot (unique per snapshot).</summary>
        public string PartId { get; set; } = "part";

        /// <summary><see cref="AssemblyPartKind"/> as int — the factory creator to use.</summary>
        public int Kind { get; set; }

        /// <summary>World pose of the part origin.</summary>
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        public double PositionZ { get; set; }

        public double RotationX { get; set; }
        public double RotationY { get; set; }
        public double RotationZ { get; set; }
        public double RotationW { get; set; } = 1.0;

        /// <summary>Thrust setting (thruster kind only; null otherwise).</summary>
        public double? ThrustNewtons { get; set; }

        /// <summary>Fuel load (fuel-tank kind only; null otherwise).</summary>
        public double? FuelMassKg { get; set; }

        /// <summary>Dry hull mass (fuel-tank kind only; null otherwise).</summary>
        public double? DryMassKg { get; set; }

        /// <summary>Docking points, part-local, order = joint address.</summary>
        public List<AssemblySnapshotPoint> Points { get; } = new List<AssemblySnapshotPoint>();
    }
}
