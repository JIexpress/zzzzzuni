namespace ZFrame.Gameplay
{
    /// <summary>
    /// One node of a typed state machine (pattern library, design doc §3.4).
    /// Implementations are usually stateless singletons owned by the FSM;
    /// all mutable data lives on <typeparamref name="TOwner"/> so states stay
    /// trivially shareable and testable.
    ///
    /// Lifecycle contract (enforced by <see cref="Fsm{TOwner}"/>):
    ///   Start / ChangeState → <see cref="Enter"/>
    ///   every Tick          → <see cref="Update"/>
    ///   leaving the state   → <see cref="Exit"/> (always called, exactly once)
    /// </summary>
    /// <typeparam name="TOwner">Context object the state reads/writes.</typeparam>
    public interface IState<TOwner>
    {
        /// <summary>Called once when the state becomes current.</summary>
        void Enter(TOwner owner);

        /// <summary>
        /// Called once when the state stops being current. The FSM guarantees
        /// Enter and Exit are always paired — Exit runs even when the next
        /// state's Enter throws.
        /// </summary>
        void Exit(TOwner owner);

        /// <summary>Per-frame body. Only the current state receives ticks.</summary>
        void Update(TOwner owner, float deltaTime);

        /// <summary>
        /// Per-transition veto evaluated on the CURRENT state before leaving it.
        /// Return false to reject the transition (Exit/Enter are then not called).
        /// The FSM already rejects same-state and unlisted transitions, so this
        /// is for owner-dependent guards (e.g. "cannot leave while staggered").
        /// </summary>
        bool CanTransition(TOwner owner, IState<TOwner> next);
    }
}
