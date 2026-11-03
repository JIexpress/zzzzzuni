namespace Game.Domain.Enemy
{
    /// <summary>
    /// Patrol node (TASK-A-M4-01): walks the waypoint loop at
    /// <see cref="EnemyTuning.PatrolSpeed"/> and advances the index on arrival.
    /// Aggro outranks patrol — the player entering ChaseRange interrupts the
    /// walk immediately. An empty route drops back to Idle.
    /// </summary>
    public sealed class EnemyPatrolState : EnemyStateBase
    {
        /// <inheritdoc />
        public override EnemyStateId StateId => EnemyStateId.Patrol;

        /// <inheritdoc />
        public override void Update(EnemyFsm owner, float deltaTime)
        {
            if (owner.Waypoints.Count == 0)
            {
                owner.RequestState(EnemyStateId.Idle);
                return;
            }

            if (owner.DistanceToPlayer <= owner.Tuning.ChaseRange)
            {
                owner.RequestState(EnemyStateId.Chase);
                return;
            }

            owner.MoveToward(owner.CurrentWaypoint, owner.Tuning.PatrolSpeed, deltaTime);
            if (owner.HasReachedWaypoint())
            {
                owner.AdvanceWaypoint();
            }
        }
    }
}
