namespace Game.View.Assembly
{
    /// <summary>
    /// Docking class of an <see cref="AttachmentPoint"/> (TASK-A-TECH-02).
    /// Two points can dock when at least one of them is
    /// <see cref="Structural"/>, or when their types match — so a
    /// <see cref="ThrusterMount"/> only accepts a structural face or another
    /// thruster mount, never a wheel hub.
    /// </summary>
    public enum AttachmentPointType
    {
        /// <summary>Generic structural face; docks with anything.</summary>
        Structural = 0,

        /// <summary>Mounting face of a thruster module.</summary>
        ThrusterMount = 1,

        /// <summary>Axle hub of a wheel module.</summary>
        WheelHub = 2,
    }
}
