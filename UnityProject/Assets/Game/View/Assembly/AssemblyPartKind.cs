namespace Game.View.Assembly
{
    /// <summary>
    /// Buildable part class of the assembly system (TASK-A-TECH-05). The kind
    /// is the serialisation identity of a part node: the snapshot stores it
    /// and <see cref="AssemblyRebuilder"/> maps it back onto the matching
    /// <see cref="AssemblyPartFactory"/> creator. Values line up with the
    /// factory's creator set; every physical part carries one via
    /// <see cref="AssemblyPart.Kind"/>.
    /// </summary>
    public enum AssemblyPartKind
    {
        /// <summary>1 m structural cube (TASK-A-TECH-02).</summary>
        Cube = 0,

        /// <summary>Thruster module (TASK-A-TECH-02).</summary>
        Thruster = 1,

        /// <summary>Wheel module (TASK-A-TECH-02).</summary>
        Wheel = 2,

        /// <summary>2 m structural beam (TASK-A-TECH-05).</summary>
        StructuralBeam = 3,

        /// <summary>Fuel tank with consumable mass (TASK-A-TECH-05).</summary>
        FuelTank = 4,

        /// <summary>Explosive-bolt separator wafer (TASK-A-TECH-05).</summary>
        Separator = 5,

        /// <summary>Fuel line pipe — docks only onto fuel ports (TASK-A-TECH-05).</summary>
        FuelLine = 6,
    }
}
