namespace ZFrame.Gameplay
{
    /// <summary>
    /// One reversible operation (command pattern, TASK-A-R9-12). Implementations
    /// capture everything needed to apply and revert the operation, so the
    /// <see cref="CommandManager"/> can replay them through
    /// <see cref="ICommandManager.Undo"/> / <see cref="ICommandManager.Redo"/>
    /// without knowing what they do — undo/redo, operation replay and
    /// command-transport scenarios all hang off this one seam.
    ///
    /// Implementation contract (the manager relies on it):
    ///   * <see cref="Execute"/> and <see cref="Undo"/> must be IDEMPOTENT and
    ///     safe to re-enter — the manager calls each exactly once per stack
    ///     transition, but redo re-runs <see cref="Execute"/> after an earlier
    ///     undo, and defensive implementations should no-op on a state that is
    ///     already applied/reverted instead of double-applying.
    ///   * <see cref="Label"/> must stay stable for the command's whole life:
    ///     it keys the CommandExecuted/CommandUndone/CommandRedone bus events
    ///     that HUDs display ("撤销：拾取木桶").
    ///   * A throwing Execute/Undo is fail-fast: the manager does NOT record a
    ///     command whose Execute threw, and a command whose Undo throws is
    ///     dropped from both stacks (see CommandManager docs).
    /// </summary>
    public interface ICommand
    {
        /// <summary>Player-facing description shown in undo/redo HUD feedback.</summary>
        string Label { get; }

        /// <summary>Applies the operation. Called by Execute and by Redo.</summary>
        void Execute();

        /// <summary>Reverts exactly what the last Execute applied. Called by Undo.</summary>
        void Undo();
    }
}
