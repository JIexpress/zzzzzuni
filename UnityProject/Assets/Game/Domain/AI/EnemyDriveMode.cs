namespace Game.Domain.AI
{
    /// <summary>
    /// Which brain an <see cref="EnemyController"/> drives (TASK-A-R8-03).
    /// Both implementations are kept side by side on purpose (brief: 保留 Fsm
    /// 版对照) so behaviour can be swapped per enemy without code changes.
    /// </summary>
    public enum EnemyDriveMode
    {
        /// <summary>
        /// The behaviour tree (<see cref="EnemyBehaviorTree"/>) — the default.
        /// Adds the Attack behaviour with an <see cref="EnemyAttackRequested"/>
        /// event; branch priorities replace the FSM transition table.
        /// </summary>
        BehaviorTree = 0,

        /// <summary>
        /// The M4-01 reference FSM (<c>Game.Domain.Enemy.EnemyFsm</c>).
        /// Kept as the control group: table-driven transitions, no attack.
        /// </summary>
        Fsm = 1,
    }
}
