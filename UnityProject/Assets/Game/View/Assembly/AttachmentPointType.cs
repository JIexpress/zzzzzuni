namespace Game.View.Assembly
{
    /// <summary>
    /// Docking class of an <see cref="AttachmentPoint"/> (TASK-A-TECH-02,
    /// extended in TASK-A-TECH-05). Two points can dock when at least one of
    /// them is <see cref="Structural"/>, when their types match, or when
    /// they form the dedicated <see cref="FuelLine"/>-to-
    /// <see cref="FuelPort"/> pair — so a <see cref="ThrusterMount"/> only
    /// accepts a structural face or another thruster mount, never a wheel
    /// hub, and a fuel line only accepts a fuel port (or a structural face).
    /// </summary>
    public enum AttachmentPointType
    {
        /// <summary>Generic structural face; docks with anything.</summary>
        Structural = 0,

        /// <summary>Mounting face of a thruster module.</summary>
        ThrusterMount = 1,

        /// <summary>Axle hub of a wheel module.</summary>
        WheelHub = 2,

        /// <summary>Fuel outlet of a tank module (TASK-A-TECH-05).</summary>
        FuelPort = 3,

        /// <summary>End fitting of a fuel line pipe — docks only with fuel ports (TASK-A-TECH-05).</summary>
        FuelLine = 4,
    }
}
