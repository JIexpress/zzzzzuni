using ZFrame.Gameplay;

namespace Game.Domain.Enemy
{
    /// <summary>
    /// Raised after an <see cref="EnemyFsm"/> transition. The initial state
    /// (when the FSM starts) is announced with Previous == Current so Views can
    /// initialise from the same handler.
    /// </summary>
    public readonly struct EnemyStateChanged
    {
        /// <summary>Enemy identity — Views filter on it.</summary>
        public readonly string EnemyId;

        /// <summary>State left (equals <see cref="Current"/> for the initial announcement).</summary>
        public readonly EnemyStateId Previous;

        /// <summary>State entered.</summary>
        public readonly EnemyStateId Current;

        /// <summary>
        /// Enemy position on the 2D gameplay plane at the moment of the
        /// transition (TASK-A-R9-07 minimal event extension, ItemPickedUp
        /// precedent: presentation layers turn it into a death burst without
        /// owning a position lookup). <see cref="Vec2.Zero"/> when the event
        /// was created without a position (3-arg constructor).
        /// </summary>
        public readonly Vec2 Position;

        /// <summary>Creates the event (positionless form; <see cref="Position"/> becomes <see cref="Vec2.Zero"/>).</summary>
        public EnemyStateChanged(string enemyId, EnemyStateId previous, EnemyStateId current)
        {
            EnemyId = enemyId;
            Previous = previous;
            Current = current;
            Position = Vec2.Zero;
        }

        /// <summary>Creates the event with the enemy's plane position.</summary>
        public EnemyStateChanged(string enemyId, EnemyStateId previous, EnemyStateId current, Vec2 position)
        {
            EnemyId = enemyId;
            Previous = previous;
            Current = current;
            Position = position;
        }
    }
}
