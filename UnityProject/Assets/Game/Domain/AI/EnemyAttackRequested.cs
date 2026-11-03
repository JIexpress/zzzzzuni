using ZFrame.Gameplay;

namespace Game.Domain.AI
{
    /// <summary>
    /// Raised when the behaviour-tree enemy performs an attack (TASK-A-R8-03):
    /// the <c>Attack</c> branch of <see cref="EnemyBehaviorTree"/> fires it
    /// through the owner event AND the <see cref="ZFrame.Core.EventBus"/>,
    /// mirroring how <c>EnemyStateChanged</c> leaves <c>Game.Domain.Enemy</c>.
    /// Rate of fire is governed by the tree's attack cooldown, so subscribers
    /// can treat one event == one committed swing.
    /// </summary>
    public readonly struct EnemyAttackRequested
    {
        /// <summary>Enemy identity — subscribers filter on it.</summary>
        public readonly string EnemyId;

        /// <summary>Enemy position at the moment of the swing (2D plane).</summary>
        public readonly Vec2 EnemyPosition;

        /// <summary>Player position the enemy aimed at (2D plane).</summary>
        public readonly Vec2 PlayerPosition;

        /// <summary>Creates the event.</summary>
        public EnemyAttackRequested(string enemyId, Vec2 enemyPosition, Vec2 playerPosition)
        {
            EnemyId = enemyId;
            EnemyPosition = enemyPosition;
            PlayerPosition = playerPosition;
        }
    }
}
