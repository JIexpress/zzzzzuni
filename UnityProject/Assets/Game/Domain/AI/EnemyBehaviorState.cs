namespace Game.Domain.AI
{
    /// <summary>
    /// Observable behaviour of <see cref="EnemyBehaviorTree"/> in a given tick
    /// (TASK-A-R8-03): which top-level branch "owned" the last tick. This is
    /// the behaviour-tree counterpart of <c>Game.Domain.Enemy.EnemyStateId</c>
    /// — with one addition: <see cref="Attack"/>, which the FSM version never
    /// had. A branch sets it when it actually runs, so a tick that resumes a
    /// Running chase keeps reporting <see cref="Chase"/> until another branch
    /// takes over.
    /// </summary>
    public enum EnemyBehaviorState
    {
        /// <summary>No waypoints and nothing to react to: standing still.</summary>
        Idle = 0,

        /// <summary>Walking the waypoint loop.</summary>
        Patrol = 1,

        /// <summary>Running at the player (within vision, beyond attack range).</summary>
        Chase = 2,

        /// <summary>Within attack distance: swinging (event throttled by cooldown).</summary>
        Attack = 3,

        /// <summary>Terminal: the corpse does not think.</summary>
        Dead = 4,
    }
}
