using System;
using System.Collections.Generic;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Typed, table-driven state machine (pattern library, design doc §3.4;
    /// teaching reference: course M14-71). Engine-free by design — no
    /// MonoBehaviour, no coroutines: the owner pumps <see cref="Tick"/>.
    ///
    /// Guarantees:
    ///  - Every successful transition runs the outgoing state's
    ///    <see cref="IState{TOwner}.Exit"/> exactly once, BEFORE the incoming
    ///    state's <see cref="IState{TOwner}.Enter"/> (Exit runs in a try scope,
    ///    so an Enter that throws cannot swallow it).
    ///  - Illegal transitions are REJECTED (return false, no Enter/Exit):
    ///    unknown state, self transition, a pair missing from the transition
    ///    table, a veto from the current state's CanTransition, or a nested
    ///    ChangeState issued from inside Enter/Exit/Update.
    ///  - State changes only happen through <see cref="ChangeState"/> /
    ///    <see cref="Start"/>; <see cref="Current"/> is read-only.
    ///
    /// Transition policy: strict by default (only pairs registered via
    /// <see cref="AddTransition(Type, Type)"/> are allowed). Pass
    /// <c>allowUnlistedTransitions: true</c> for free-form machines; the
    /// per-state veto still applies.
    /// </summary>
    /// <typeparam name="TOwner">Context object passed to every state callback.</typeparam>
    public sealed class Fsm<TOwner>
    {
        private readonly TOwner _owner;
        private readonly bool _allowUnlistedTransitions;
        private readonly Dictionary<Type, IState<TOwner>> _states = new Dictionary<Type, IState<TOwner>>();
        private readonly HashSet<(Type From, Type To)> _transitions = new HashSet<(Type From, Type To)>();

        private IState<TOwner> _current;
        private bool _transitioning;

        /// <summary>Creates an empty machine. States/transitions must be registered before <see cref="Start"/>.</summary>
        /// <param name="owner">Context handed to every state callback.</param>
        /// <param name="allowUnlistedTransitions">When true, any registered state may transition to any other.</param>
        public Fsm(TOwner owner, bool allowUnlistedTransitions = false)
        {
            _owner = owner;
            _allowUnlistedTransitions = allowUnlistedTransitions;
        }

        /// <summary>Current state, or null before <see cref="Start"/>. Read-only: use ChangeState.</summary>
        public IState<TOwner> Current => _current;

        /// <summary>True once <see cref="Start"/> ran and no terminal teardown happened.</summary>
        public bool IsRunning => _current != null;

        /// <summary>Number of registered states (diagnostics/tests).</summary>
        public int StateCount => _states.Count;

        /// <summary>Number of registered transitions (diagnostics/tests).</summary>
        public int TransitionCount => _transitions.Count;

        /// <summary>Raised AFTER a successful transition (previous may be null for the initial Enter).</summary>
        public event Action<IState<TOwner>, IState<TOwner>> StateChanged;

        /// <summary>Raised when <see cref="ChangeState"/> rejects a transition; carries the blocked pair for diagnostics.</summary>
        public event Action<IState<TOwner>, IState<TOwner>> TransitionRejected;

        /// <summary>Registers a state instance. Duplicate type registration throws — one instance per state type.</summary>
        public void AddState(IState<TOwner> state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            Type type = state.GetType();
            if (_states.ContainsKey(type))
            {
                throw new InvalidOperationException($"Fsm: state '{type.Name}' is already registered.");
            }

            _states[type] = state;
        }

        /// <summary>Allows the transition <typeparamref name="TFrom"/> → <typeparamref name="TTo"/>. Idempotent.</summary>
        public void AddTransition<TFrom, TTo>()
            where TFrom : IState<TOwner>
            where TTo : IState<TOwner>
        {
            AddTransition(typeof(TFrom), typeof(TTo));
        }

        /// <summary>Allows the transition from → to (both must be registered states). Idempotent.</summary>
        public void AddTransition(Type from, Type to)
        {
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            if (to == null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            if (!_states.ContainsKey(from) || !_states.ContainsKey(to))
            {
                throw new InvalidOperationException(
                    $"Fsm: transition {from.Name} → {to.Name} references an unregistered state (register states first).");
            }

            _transitions.Add((from, to));
        }

        /// <summary>
        /// Enters the initial state (its Enter runs once, no Exit). Returns
        /// false when the state is unknown or the machine already started.
        /// </summary>
        public bool Start<TState>()
            where TState : IState<TOwner>
        {
            return Start(typeof(TState));
        }

        /// <summary>Enters the initial state by type. Returns false when unknown or already started.</summary>
        public bool Start(Type initialStateType)
        {
            if (_current != null || initialStateType == null)
            {
                return false;
            }

            if (!_states.TryGetValue(initialStateType, out IState<TOwner> initial))
            {
                return false;
            }

            _transitioning = true;
            try
            {
                _current = initial;
                initial.Enter(_owner);
            }
            finally
            {
                _transitioning = false;
            }

            StateChanged?.Invoke(null, initial);
            return true;
        }

        /// <summary>True when <paramref name="next"/> may become current right now (no side effects).</summary>
        public bool CanTransition(IState<TOwner> next)
        {
            if (next == null || !_states.ContainsKey(next.GetType()))
            {
                return false;
            }

            if (ReferenceEquals(next, _current))
            {
                return false; // re-entering the current state is not a transition
            }

            if (_current != null &&
                !_allowUnlistedTransitions &&
                !_transitions.Contains((_current.GetType(), next.GetType())))
            {
                return false;
            }

            return _current == null || _current.CanTransition(_owner, next);
        }

        /// <summary>
        /// Switches to <paramref name="next"/>. On success: current.Exit →
        /// current = next → next.Enter → <see cref="StateChanged"/>. On
        /// rejection nothing runs and false is returned.
        /// </summary>
        public bool ChangeState(IState<TOwner> next)
        {
            if (!CanTransition(next) || _transitioning)
            {
                TransitionRejected?.Invoke(_current, next);
                return false;
            }

            _transitioning = true;
            IState<TOwner> previous = _current;
            try
            {
                previous?.Exit(_owner);
                _current = next;
                next.Enter(_owner);
            }
            finally
            {
                _transitioning = false;
            }

            StateChanged?.Invoke(previous, next);
            return true;
        }

        /// <summary>Switches to the registered state of type <typeparamref name="TState"/>.</summary>
        public bool ChangeState<TState>()
            where TState : IState<TOwner>
        {
            return _states.TryGetValue(typeof(TState), out IState<TOwner> state) && ChangeState(state);
        }

        /// <summary>Switches to the registered state of type <paramref name="stateType"/>.</summary>
        public bool ChangeState(Type stateType)
        {
            return stateType != null &&
                   _states.TryGetValue(stateType, out IState<TOwner> state) &&
                   ChangeState(state);
        }

        /// <summary>Pumps the current state. Safe before Start (no-op).</summary>
        public void Tick(float deltaTime)
        {
            _current?.Update(_owner, deltaTime);
        }
    }
}
