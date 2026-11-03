namespace Game.Domain.Enemy
{
    /// <summary>
    /// Chase node (TASK-A-M4-01): runs at the last pumped player position.
    /// Hysteresis lives in the tuning: aggro happens at
    /// <see cref="EnemyTuning.ChaseRange"/> but the chase is only abandoned
    /// beyond <see cref="EnemyTuning.LoseRange"/>, so a player hovering at the
    /// boundary cannot make the enemy flip-flop every frame.
    /// </summary>
    public sealed class EnemyChaseState : EnemyStateBase
    {
        /// <inheritdoc />
        public override EnemyStateId StateId => EnemyStateId.Chase;

        /// <inheritdoc />
        public override void Update(EnemyFsm owner, float deltaTime)
        {
            if (owner.DistanceToPlayer > owner.Tuning.LoseRange)
            {
                owner.RequestState(owner.Waypoints.Count > 0 ? EnemyStateId.Patrol : EnemyStateId.Idle);
                return;
            }

            owner.MoveToward(owner.PlayerPosition, owner.Tuning.ChaseSpeed, deltaTime);
        }
    }
}
