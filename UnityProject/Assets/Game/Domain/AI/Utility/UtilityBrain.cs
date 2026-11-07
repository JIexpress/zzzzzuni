using System;
using System.Collections.Generic;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// Utility-AI brain (TASK-A-TECH-01): every Tick scores ALL registered
    /// candidate actions against the pumped <see cref="NPCWorldState"/>, picks
    /// the highest score and executes it; below the selection threshold it
    /// idles. This is the piece a behaviour tree cannot do — the NPC ranks its
    /// own options instead of following fixed branches.
    ///
    /// Determinism contract (the falsifiable core):
    /// - Same state + same time → same ranking and same winner (score
    ///   functions are pure, iteration follows registration order).
    /// - Ties break to the EARLIEST REGISTERED action — explicit, stable, and
    ///   asserted in tests (no RNG, no dictionary-order surprises).
    /// - Cooldowns exclude an action from selection until
    ///   <c>time >= lastExecute + CooldownSeconds</c>; the clock is the
    ///   caller-pumped absolute time, never a wall clock.
    /// </summary>
    public sealed class UtilityBrain
    {
        /// <summary>Name reported/selected when no candidate clears the threshold.</summary>
        public const string IdleActionName = "Idle";

        /// <summary>Default selection threshold: winners under this score idle instead of acting.</summary>
        public const float DefaultSelectionThreshold = 0.05f;

        private readonly List<UtilityAction> _actions = new List<UtilityAction>();
        private readonly Dictionary<string, float> _lastExecutedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<ScoredAction> _ranking = new List<ScoredAction>();
        private readonly float _selectionThreshold;
        private string _lastSelected = IdleActionName;

        /// <summary>
        /// Creates a brain. <paramref name="selectionThreshold"/> must be >= 0:
        /// winners scoring below it idle (0 = any non-negative score acts).
        /// </summary>
        public UtilityBrain(float selectionThreshold = DefaultSelectionThreshold)
        {
            if (selectionThreshold < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(selectionThreshold), "UtilityBrain: threshold must be >= 0.");
            }

            _selectionThreshold = selectionThreshold;
        }

        /// <summary>One scored candidate as exposed by <see cref="LastRanking"/>.</summary>
        public readonly struct ScoredAction
        {
            /// <summary>Candidate name.</summary>
            public readonly string Name;

            /// <summary>Raw score from the candidate's score function.</summary>
            public readonly float Value;

            /// <summary>Builds one ranked entry (brain-internal + tests).</summary>
            public ScoredAction(string name, float value)
            {
                Name = name;
                Value = value;
            }
        }

        /// <summary>Registered candidate count.</summary>
        public int ActionCount => _actions.Count;

        /// <summary>Selection threshold (winners under it idle).</summary>
        public float SelectionThreshold => _selectionThreshold;

        /// <summary>Name of the action chosen/executed on the last tick, or <see cref="IdleActionName"/>.</summary>
        public string LastSelected => _lastSelected;

        /// <summary>
        /// Ranking of the last tick — available candidates only (cooldown
        /// excluded), descending by score, ties in registration order. Null
        /// before the first tick.
        /// </summary>
        public IReadOnlyList<ScoredAction> LastRanking => _ranking;

        /// <summary>
        /// Registers a candidate. Registration order is the tie-break priority
        /// (earliest wins equal scores) — the caller's declared preference.
        /// Duplicate names throw: a ranking of ambiguous names is unusable.
        /// </summary>
        public void Add(UtilityAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            for (int i = 0; i < _actions.Count; i++)
            {
                if (string.Equals(_actions[i].Name, action.Name, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"UtilityBrain: duplicate action name '{action.Name}'.", nameof(action));
                }
            }

            _actions.Add(action);
        }

        /// <summary>True when <paramref name="name"/> may not run at <paramref name="time"/> (cooldown active).</summary>
        public bool IsOnCooldown(string name, float time)
        {
            return _lastExecutedAt.TryGetValue(name, out float executedAt)
                   && time < executedAt + CooldownOf(name);
        }

        /// <summary>
        /// Scores all available candidates, executes the winner (or idles) and
        /// returns its name. Non-finite scores are treated as absent desire
        /// (NaN/falsehood must not win by comparison quirks).
        /// </summary>
        /// <param name="state">The pumped world snapshot to score against.</param>
        /// <param name="time">Absolute brain clock in seconds (caller-owned, monotonic in practice).</param>
        public string Tick(NPCWorldState state, float time)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _ranking.Clear();
            for (int i = 0; i < _actions.Count; i++)
            {
                UtilityAction action = _actions[i];
                if (IsOnCooldown(action.Name, time))
                {
                    continue;
                }

                float score = action.Score(state);
                if (float.IsNaN(score))
                {
                    continue;
                }

                _ranking.Add(new ScoredAction(action.Name, score));
            }

            // Stable descending sort (insertion — n is a handful of actions):
            // equal scores keep registration order, so LastRanking reads
            // exactly like the tie-break rule the winner scan uses.
            for (int i = 1; i < _ranking.Count; i++)
            {
                ScoredAction entry = _ranking[i];
                int j = i - 1;
                while (j >= 0 && _ranking[j].Value < entry.Value)
                {
                    _ranking[j + 1] = _ranking[j];
                    j--;
                }

                _ranking[j + 1] = entry;
            }

            // Strictly-greater scan in registration order ⇒ earliest registered
            // wins ties (the deterministic tie-break the tests pin down).
            int bestIndex = -1;
            float bestScore = _selectionThreshold; // below/at threshold ⇒ Idle
            for (int i = 0; i < _ranking.Count; i++)
            {
                if (_ranking[i].Value > bestScore)
                {
                    bestScore = _ranking[i].Value;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                _lastSelected = IdleActionName;
                return IdleActionName;
            }

            string winner = _ranking[bestIndex].Name;
            ActionOf(winner).Execute();
            _lastExecutedAt[winner] = time;
            _lastSelected = winner;
            return winner;
        }

        private float CooldownOf(string name)
        {
            for (int i = 0; i < _actions.Count; i++)
            {
                if (string.Equals(_actions[i].Name, name, StringComparison.Ordinal))
                {
                    return _actions[i].CooldownSeconds;
                }
            }

            return 0f;
        }

        private UtilityAction ActionOf(string name)
        {
            for (int i = 0; i < _actions.Count; i++)
            {
                if (string.Equals(_actions[i].Name, name, StringComparison.Ordinal))
                {
                    return _actions[i];
                }
            }

            throw new InvalidOperationException($"UtilityBrain: unknown action '{name}'.");
        }
    }
}
