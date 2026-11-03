namespace ZFrame.Gameplay
{
    /// <summary>
    /// The reversible-operation stack (command pattern, TASK-A-R9-12). Owns
    /// the execute/undo/redo bookkeeping for <see cref="ICommand"/>s;
    /// consumers register an implementation with the service locator
    /// (<c>Services.Register&lt;ICommandManager&gt;(new CommandManager())</c>)
    /// and gameplay code resolves it to push operations — e.g. the pickup
    /// template executes every <c>PickupCommand</c> through this seam so a
    /// mis-pick can be undone.
    ///
    /// Threading: main-thread only, like every ZFrame gameplay service.
    /// Semantic contract (see CommandManager for the concrete behaviour):
    ///   * Execute pushes onto the undo stack and CLEARS the redo stack
    ///     (classic double-stack semantics — a new timeline abandons the redo
    ///     chain),
    ///   * Undo moves the newest applied command to the redo stack,
    ///   * Redo re-executes the newest redo entry and moves it back,
    ///   * Clear drops both stacks (scene switches; applied state stays).
    /// </summary>
    public interface ICommandManager
    {
        /// <summary>Commands currently recorded for undo (newest last).</summary>
        int UndoDepth { get; }

        /// <summary>Commands currently re-appliable by Redo (newest last).</summary>
        int RedoDepth { get; }

        /// <summary>True when <see cref="Undo"/> would revert something.</summary>
        bool CanUndo { get; }

        /// <summary>True when <see cref="Redo"/> would re-apply something.</summary>
        bool CanRedo { get; }

        /// <summary>
        /// Runs <paramref name="command"/> once and records it as undoable.
        /// Throws when <paramref name="command"/> is null or when the call is
        /// re-entered from inside a command callback (fail-fast, see
        /// CommandManager).
        /// </summary>
        void Execute(ICommand command);

        /// <summary>
        /// Reverts the newest command. False when nothing is undoable; true
        /// means the command's Undo completed and a Redo can re-apply it.
        /// </summary>
        bool Undo();

        /// <summary>
        /// Re-applies the newest undone command. False when the redo chain is
        /// empty (a fresh Execute always empties it).
        /// </summary>
        bool Redo();

        /// <summary>
        /// Drops every recorded command (both stacks) without touching applied
        /// state — scene switches / new game. No events are fired.
        /// </summary>
        void Clear();
    }
}
