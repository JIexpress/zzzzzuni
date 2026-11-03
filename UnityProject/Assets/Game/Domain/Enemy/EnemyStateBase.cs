using ZFrame.Gameplay;

namespace Game.Domain.Enemy
{
    /// <summary>
    /// Shared plumbing for the enemy states (TASK-A-M4-01): maps every node to
    /// an <see cref="EnemyStateId"/> so <see cref="EnemyFsm"/> can publish
    /// <see cref="EnemyStateChanged"/> without the states knowing about events.
    ///
    /// Defaults: Enter/Exit are no-ops and <see cref="CanTransition"/> allows
    /// everything — the transition TABLE in <see cref="EnemyFsm"/> is the
    /// authority; <see cref="EnemyDeadState"/> is the only state that vetoes.
    /// </summary>
    public abstract class EnemyStateBase : IState<EnemyFsm>
    {
        /// <summary>Public identity of this node.</summary>
        public abstract EnemyStateId StateId { get; }

        /// <inheritdoc />
        public virtual void Enter(EnemyFsm owner)
        {
        }

        /// <inheritdoc />
        public virtual void Exit(EnemyFsm owner)
        {
        }

        /// <inheritdoc />
        public abstract void Update(EnemyFsm owner, float deltaTime);

        /// <inheritdoc />
        public virtual bool CanTransition(EnemyFsm owner, IState<EnemyFsm> next)
        {
            return true;
        }
    }
}
