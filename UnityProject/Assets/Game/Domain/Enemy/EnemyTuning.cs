using System;

namespace Game.Domain.Enemy
{
    /// <summary>
    /// Tuning knobs for the enemy FSM, injected at construction so the state
    /// logic stays data-driven (no magic numbers in the states). Immutable.
    /// </summary>
    public readonly struct EnemyTuning
    {
        /// <summary>Distance at or below which the enemy switches to Chase.</summary>
        public readonly float ChaseRange;

        /// <summary>Distance above which a chasing enemy gives up (must be &gt;= ChaseRange to avoid flip-flopping).</summary>
        public readonly float LoseRange;

        /// <summary>Metres per second while patrolling.</summary>
        public readonly float PatrolSpeed;

        /// <summary>Metres per second while chasing.</summary>
        public readonly float ChaseSpeed;

        /// <summary>Seconds spent idling before starting to patrol.</summary>
        public readonly float IdleSeconds;

        /// <summary>Distance under which a waypoint counts as reached.</summary>
        public readonly float ArriveEpsilon;

        /// <summary>Creates a tuning block. Throws on nonsensical values (fail fast at setup time).</summary>
        public EnemyTuning(
            float chaseRange,
            float loseRange,
            float patrolSpeed,
            float chaseSpeed,
            float idleSeconds,
            float arriveEpsilon = 0.1f)
        {
            if (chaseRange < 0f || loseRange < chaseRange)
            {
                throw new ArgumentOutOfRangeException(nameof(loseRange), "EnemyTuning: LoseRange must be >= ChaseRange >= 0.");
            }

            if (patrolSpeed < 0f || chaseSpeed < 0f || idleSeconds < 0f || arriveEpsilon < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(patrolSpeed), "EnemyTuning: speeds/timers must be >= 0.");
            }

            ChaseRange = chaseRange;
            LoseRange = loseRange;
            PatrolSpeed = patrolSpeed;
            ChaseSpeed = chaseSpeed;
            IdleSeconds = idleSeconds;
            ArriveEpsilon = arriveEpsilon;
        }

        /// <summary>Slice defaults: 4 m aggro, 6 m give-up, 1.5/3 m·s⁻¹, 1 s idle.</summary>
        public static EnemyTuning Default => new EnemyTuning(4f, 6f, 1.5f, 3f, 1f);
    }
}
