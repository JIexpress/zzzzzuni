namespace Game.Domain.Enemy
{
    /// <summary>Enemy AI states of the M4 slice (TASK-A-M4-01).</summary>
    public enum EnemyStateId
    {
        /// <summary>Standing still, looking around, waiting out the idle timer.</summary>
        Idle = 0,

        /// <summary>Walking the waypoint loop.</summary>
        Patrol = 1,

        /// <summary>Running at the player (distance-triggered).</summary>
        Chase = 2,

        /// <summary>Terminal state: no further transitions.</summary>
        Dead = 3,
    }
}
