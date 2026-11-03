using ZFrame.Gameplay;

namespace Game.Domain.Enemy
{
    /// <summary>
    /// Terminal node (TASK-A-M4-01). Dead has no outgoing transition in the
    /// table AND vetoes every request from inside the state, so "kill twice"
    /// or "resurrect by request" cannot happen by accident.
    /// </summary>
    public sealed class EnemyDeadState : EnemyStateBase
    {
        /// <inheritdoc />
        public override EnemyStateId StateId => EnemyStateId.Dead;

        /// <inheritdoc />
        public override void Update(EnemyFsm owner, float deltaTime)
        {
            // Terminal: the corpse does not think.
        }

        /// <inheritdoc />
        public override bool CanTransition(EnemyFsm owner, IState<EnemyFsm> next)
        {
            return false;
        }
    }
}
