using System;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// One candidate behaviour of a utility brain (TASK-A-TECH-01): a stable
    /// name, a pure score function over <see cref="NPCWorldState"/>, the
    /// delegate that performs the behaviour and an optional cooldown.
    ///
    /// Score functions must be PURE (same state in — same score out): the
    /// falsifiable guarantee of utility AI is "same world state, same ranking",
    /// so no scoring delegate may mutate the state or read a clock. Time is
    /// pumped in by the brain's Tick for cooldown bookkeeping only.
    ///
    /// Execution model: the winner's <see cref="_execute"/> runs once per
    /// selecting tick — continuous behaviours (walking home, resting) simply
    /// win several ticks in a row and advance one step per tick. Delegates are
    /// created once by the owner (method groups / ctor closures), never
    /// per tick.
    /// </summary>
    public sealed class UtilityAction
    {
        private readonly Func<NPCWorldState, float> _score;
        private readonly Action _execute;

        /// <summary>
        /// Creates an action. Throws on null name/score/execute (fail fast) —
        /// a candidate without a score function or an effect is a wiring bug.
        /// </summary>
        /// <param name="name">Stable identity used in rankings and assertions (e.g. "Rest").</param>
        /// <param name="score">Pure function: world state → score (any float; the brain compares raw values).</param>
        /// <param name="execute">Effect of winning one tick (mutates the owning agent, fires events...).</param>
        /// <param name="cooldownSeconds">Minimum seconds between two executions; 0 = may win every tick.</param>
        public UtilityAction(string name, Func<NPCWorldState, float> score, Action execute, float cooldownSeconds = 0f)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("UtilityAction: name must not be empty.", nameof(name));
            }

            _score = score ?? throw new ArgumentNullException(nameof(score));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));

            if (cooldownSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cooldownSeconds), "UtilityAction: cooldown must be >= 0.");
            }

            Name = name;
            CooldownSeconds = cooldownSeconds;
        }

        /// <summary>Stable identity (rankings, logs, assertions).</summary>
        public string Name { get; }

        /// <summary>Minimum seconds between two executions (0 = no cooldown).</summary>
        public float CooldownSeconds { get; }

        /// <summary>
        /// Scores the action against <paramref name="state"/>. Pure by contract:
        /// implementations must not mutate the state or depend on wall clocks.
        /// </summary>
        public float Score(NPCWorldState state)
        {
            return _score(state);
        }

        /// <summary>Performs one step of the behaviour (called by the brain on the winning tick).</summary>
        public void Execute()
        {
            _execute();
        }
    }
}
