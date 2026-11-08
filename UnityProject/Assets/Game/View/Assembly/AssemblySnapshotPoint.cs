namespace Game.View.Assembly
{
    /// <summary>
    /// Docking point of an <see cref="AssemblySnapshotPart"/> in serialised
    /// form (TASK-A-TECH-05). Doubles are the JSON storage type: float ->
    /// double is lossless, so a round-trip restores the original floats
    /// exactly and the rebuild replay starts bit-identical.
    /// </summary>
    public sealed class AssemblySnapshotPoint
    {
        /// <summary>Docking class (<see cref="AttachmentPointType"/> as int).</summary>
        public int Type { get; set; }

        /// <summary>Docking centre, part-local.</summary>
        public double LocalPositionX { get; set; }
        public double LocalPositionY { get; set; }
        public double LocalPositionZ { get; set; }

        /// <summary>Outward face normal, part-local.</summary>
        public double LocalForwardX { get; set; }
        public double LocalForwardY { get; set; }
        public double LocalForwardZ { get; set; }
    }
}
