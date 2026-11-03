using System;
using System.Collections.Generic;
using ZFrame.Core;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Default <see cref="ICommandManager"/>: two explicit stacks (execute
    /// history / redo chain) over <see cref="ICommand"/>s, a rolling capacity
    /// window on the history, and one EventBus event per stack transition
    /// (TASK-A-R9-12). Pure C#.
    ///
    /// Semantics
    /// ---------
    /// * Execute: <c>command.Execute()</c> runs first, THEN the command is
    ///   recorded — a command whose Execute throws is never recorded (its
    ///   partial side effects belong to the command itself, fail-fast).
    /// * Undo/Redo move entries between the stacks; the entry is popped from
    ///   its source stack before the callback runs. A callback that throws
    ///   leaves the entry on NO stack (dropped, state stays as-is) and the
    ///   exception propagates — nothing half-recorded survives.
    /// * Double-stack semantics: any new Execute clears the redo chain — after
    ///   "undo, then do something else" the abandoned timeline cannot be
    ///   redone (the classic undo/redo trap, locked by tests).
    /// * Capacity: the undo stack keeps at most <see cref="Capacity"/> entries;
    ///   the OLDEST is dropped (without undoing it) once the window overflows,
    ///   so long sessions cannot grow the stack without bound. RedoDepth never
    ///   exceeds Capacity by construction (every redo entry came from the
    ///   undo stack).
    /// * Clear empties both stacks silently (no per-command events): used on
    ///   scene switches, where commands referencing the previous scene's
    ///   entities must not be undoable.
    /// * Re-entrancy: Execute/Undo/Redo called from inside a command callback
    ///   (or an event handler reacting to the stack's own bus events) throw —
    ///   interleaved timelines would corrupt the stack invariants. Composite
    ///   operations should be one ICommand that drives its children itself.
    ///
    /// Events: <see cref="CommandExecuted"/>, <see cref="CommandUndone"/>,
    /// <see cref="CommandRedone"/> (each carries the command label) via the
    /// <see cref="EventBus"/>.
    /// </summary>
    public sealed class CommandManager : ICommandManager
    {
        /// <summary>Default undo-history window when no capacity is given.</summary>
        public const int DefaultCapacity = 64;

        private readonly int _capacity;
        private readonly List<ICommand> _undoStack = new List<ICommand>();
        private readonly List<ICommand> _redoStack = new List<ICommand>();
        private bool _busy;

        /// <summary>Creates the manager with a fixed undo-history window (&gt;= 1).</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> &lt; 1.</exception>
        public CommandManager(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity), capacity, "CommandManager: capacity must be >= 1.");
            }

            _capacity = capacity;
        }

        /// <summary>Size of the rolling undo window.</summary>
        public int Capacity => _capacity;

        /// <inheritdoc />
        public int UndoDepth => _undoStack.Count;

        /// <inheritdoc />
        public int RedoDepth => _redoStack.Count;

        /// <inheritdoc />
        public bool CanUndo => _undoStack.Count > 0;

        /// <inheritdoc />
        public bool CanRedo => _redoStack.Count > 0;

        /// <inheritdoc />
        public void Execute(ICommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            EnterGuard("Execute");
            try
            {
                command.Execute();
                _undoStack.Add(command);
                _redoStack.Clear(); // 双栈经典语义：新的执行断开重做链
                TrimToCapacity();
                EventBus.Fire(new CommandExecuted(command.Label));
            }
            finally
            {
                _busy = false;
            }
        }

        /// <inheritdoc />
        public bool Undo()
        {
            if (_undoStack.Count == 0)
            {
                return false;
            }

            EnterGuard("Undo");
            try
            {
                ICommand command = PopLast(_undoStack);
                command.Undo();
                _redoStack.Add(command);
                EventBus.Fire(new CommandUndone(command.Label));
            }
            finally
            {
                _busy = false;
            }

            return true;
        }

        /// <inheritdoc />
        public bool Redo()
        {
            if (_redoStack.Count == 0)
            {
                return false;
            }

            EnterGuard("Redo");
            try
            {
                ICommand command = PopLast(_redoStack);
                command.Execute();
                _undoStack.Add(command);
                TrimToCapacity();
                EventBus.Fire(new CommandRedone(command.Label));
            }
            finally
            {
                _busy = false;
            }

            return true;
        }

        /// <inheritdoc />
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }

        private void EnterGuard(string operation)
        {
            if (_busy)
            {
                throw new InvalidOperationException(
                    $"CommandManager: reentrant {operation} detected. Commands and their stack " +
                    "events must not drive Execute/Undo/Redo back into the same manager — " +
                    "compose operations into a single ICommand instead.");
            }

            _busy = true;
        }

        /// <summary>Rolling window: drop the OLDEST history entry (it stays applied, it just leaves undo reach).</summary>
        private void TrimToCapacity()
        {
            while (_undoStack.Count > _capacity)
            {
                _undoStack.RemoveAt(0);
            }
        }

        private static ICommand PopLast(List<ICommand> stack)
        {
            int last = stack.Count - 1;
            ICommand command = stack[last];
            stack.RemoveAt(last);
            return command;
        }
    }
}
