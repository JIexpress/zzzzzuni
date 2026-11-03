namespace Game.Domain.Enemy
{
    /// <summary>
    /// Idle node (TASK-A-M4-01): stands still, chases the player the moment it
    /// comes within <see cref="EnemyTuning.ChaseRange"/>, otherwise starts
    /// patrolling once <see cref="EnemyTuning.IdleSeconds"/> elapsed AND the
    /// enemy actually has waypoints (an enemy without a route never patrols).
    /// </summary>
    public sealed class EnemyIdleState : EnemyStateBase
    {
        /// <inheritdoc />
        public override EnemyStateId StateId => EnemyStateId.Idle;

        /// <inheritdoc />
        public override void Enter(EnemyFsm owner)
        {
            owner.IdleElapsed = 0f;
        }

        /// <inheritdoc />
        public override void Update(EnemyFsm owner, float deltaTime)
        {
            if (owner.DistanceToPlayer <= owner.Tuning.ChaseRange)
            {
                owner.RequestState(EnemyStateId.Chase);
                return;
            }

            owner.IdleElapsed += deltaTime;
            if (owner.IdleElapsed >= owner.Tuning.IdleSeconds && owner.Waypoints.Count > 0)
            {
                owner.RequestState(EnemyStateId.Patrol);
            }
        }
    }
}
