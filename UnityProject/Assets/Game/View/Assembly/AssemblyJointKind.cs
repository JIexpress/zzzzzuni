namespace Game.View.Assembly
{
    /// <summary>
    /// Joint edge class of the assembly graph (TASK-A-TECH-05). The first
    /// slice only has the locked rigid bond of TASK-A-TECH-02; the enum
    /// exists so snapshots can carry the edge type without a format break
    /// when a later slice adds hinges / wheel axles.
    /// </summary>
    public enum AssemblyJointKind
    {
        /// <summary>All-six-DOF locked ConfigurableJoint bond (the tuned recipe).</summary>
        Rigid = 0,
    }
}
