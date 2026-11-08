namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// GOAP atom vocabulary of the repair slice (TASK-A-TECH-04) — the
    /// string constants the <see cref="NpcUtilityAgent"/> uses to describe
    /// its repair problem to <c>ZFrame.Gameplay.AI.GoapPlanner</c>. Atoms are
    /// true-when-present (STRIPS closed world); the planner is generic, this
    /// type is where the demo's meaning lives.
    /// </summary>
    public static class UtilityGoapAtoms
    {
        /// <summary>The NPC carries the repair tool (granted at the toolbox).</summary>
        public const string HasTool = "has_tool";

        /// <summary>The device is broken and repair is pending.</summary>
        public const string DeviceBroken = "device_broken";

        /// <summary>The device has been repaired (the planner's goal).</summary>
        public const string DeviceRepaired = "device_repaired";
    }
}
