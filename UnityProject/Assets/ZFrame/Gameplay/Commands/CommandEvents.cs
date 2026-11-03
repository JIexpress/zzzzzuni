namespace ZFrame.Gameplay
{
    /// <summary>
    /// Fired on the <see cref="ZFrame.Core.EventBus"/> after a command was
    /// applied by <see cref="ICommandManager.Execute"/> (TASK-A-R9-12). The
    /// payload is deliberately minimal — the command's stable
    /// <see cref="ICommand.Label"/> — so a HUD can show "执行：拾取木桶"
    /// without reaching into the command itself.
    /// </summary>
    public readonly struct CommandExecuted
    {
        /// <summary>Label of the applied command.</summary>
        public readonly string Label;

        /// <summary>Creates the event.</summary>
        public CommandExecuted(string label)
        {
            Label = label;
        }
    }

    /// <summary>
    /// Fired on the <see cref="ZFrame.Core.EventBus"/> after a command was
    /// reverted by <see cref="ICommandManager.Undo"/> (TASK-A-R9-12).
    /// HUD display target: "撤销：拾取木桶".
    /// </summary>
    public readonly struct CommandUndone
    {
        /// <summary>Label of the reverted command.</summary>
        public readonly string Label;

        /// <summary>Creates the event.</summary>
        public CommandUndone(string label)
        {
            Label = label;
        }
    }

    /// <summary>
    /// Fired on the <see cref="ZFrame.Core.EventBus"/> after a command was
    /// re-applied by <see cref="ICommandManager.Redo"/> (TASK-A-R9-12).
    /// HUD display target: "重做：拾取木桶".
    /// </summary>
    public readonly struct CommandRedone
    {
        /// <summary>Label of the re-applied command.</summary>
        public readonly string Label;

        /// <summary>Creates the event.</summary>
        public CommandRedone(string label)
        {
            Label = label;
        }
    }
}
