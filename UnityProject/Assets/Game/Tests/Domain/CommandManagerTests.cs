using System;
using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Command stack tests (TASK-A-R9-12): the full execute/undo/redo order,
    /// the classic double-stack semantics (a new execute after an undo kills
    /// the redo chain), rolling capacity, event payloads and Clear.
    /// </summary>
    public sealed class CommandManagerTests
    {
        private sealed class RecordingCommand : ICommand
        {
            private readonly string _name;
            private readonly Action _onExecute;

            public RecordingCommand(string name, List<string> log = null, Action onExecute = null)
            {
                _name = name;
                _onExecute = onExecute;
                Log = log;
            }

            public List<string> Log { get; }

            public int ExecuteCount { get; private set; }

            public int UndoCount { get; private set; }

            public string Label => _name;

            public void Execute()
            {
                ExecuteCount++;
                Log?.Add("exec:" + _name);
                _onExecute?.Invoke();
            }

            public void Undo()
            {
                UndoCount++;
                Log?.Add("undo:" + _name);
            }
        }

        private readonly List<string> _events = new List<string>();
        private readonly List<IDisposable> _tokens = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            _tokens.Add(EventBus.Subscribe<CommandExecuted>(evt => _events.Add("executed|" + evt.Label)));
            _tokens.Add(EventBus.Subscribe<CommandUndone>(evt => _events.Add("undone|" + evt.Label)));
            _tokens.Add(EventBus.Subscribe<CommandRedone>(evt => _events.Add("redone|" + evt.Label)));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            _events.Clear();
        }

        [Test]
        public void Execute_RunsCommand_AndRecordsItOnTheUndoStack()
        {
            var manager = new CommandManager();
            var command = new RecordingCommand("A");

            manager.Execute(command);

            Assert.AreEqual(1, command.ExecuteCount, "Execute must run the command exactly once");
            Assert.AreEqual(0, command.UndoCount, "Execute must not undo");
            Assert.AreEqual(1, manager.UndoDepth);
            Assert.AreEqual(0, manager.RedoDepth);
            Assert.IsTrue(manager.CanUndo);
            Assert.IsFalse(manager.CanRedo, "a fresh execute must not be redoable");
        }

        [Test]
        public void ExecuteUndoRedo_FullCycle_RunsInOrder()
        {
            var manager = new CommandManager();
            var log = new List<string>();
            var a = new RecordingCommand("A", log);
            var b = new RecordingCommand("B", log);

            manager.Execute(a);
            manager.Execute(b);
            Assert.IsTrue(manager.Undo());
            Assert.IsTrue(manager.Redo());

            CollectionAssert.AreEqual(
                new[] { "exec:A", "exec:B", "undo:B", "exec:B" },
                log,
                "redo re-runs Execute (the command sees a second Execute, never a bare Redo hook)");
            Assert.AreEqual(2, b.ExecuteCount, "executed once, undone, re-executed once");
            Assert.AreEqual(1, b.UndoCount);
            Assert.AreEqual(2, manager.UndoDepth, "A and the redone B are both on the undo stack");
            Assert.AreEqual(0, manager.RedoDepth);
        }

        [Test]
        public void Undo_AndRedo_OnEmptyStacks_ReturnFalse()
        {
            var manager = new CommandManager();

            Assert.IsFalse(manager.Undo(), "empty manager");
            Assert.IsFalse(manager.Redo(), "empty manager");

            manager.Execute(new RecordingCommand("A"));
            Assert.IsFalse(manager.Redo(), "execute alone never creates redo entries");
        }

        [Test]
        public void Execute_AfterUndo_ClearsTheRedoStack_ClassicDoubleStackTrap()
        {
            var manager = new CommandManager();
            var a = new RecordingCommand("A");
            var b = new RecordingCommand("B");
            var c = new RecordingCommand("C");

            manager.Execute(a);
            manager.Execute(b);
            Assert.IsTrue(manager.Undo(), "precondition: B is undone");
            Assert.IsTrue(manager.CanRedo, "precondition: B is redoable");

            // The trap: "undo, then do something else" starts a NEW timeline —
            // the abandoned B must be gone from redo for good.
            manager.Execute(c);

            Assert.IsFalse(manager.CanRedo, "a new execute must clear the redo chain");
            Assert.AreEqual(0, manager.RedoDepth);
            Assert.IsFalse(manager.Redo(), "Redo on the severed chain returns false");
            Assert.AreEqual(1, b.UndoCount, "the undo itself DID happen before the new timeline");
            Assert.AreEqual(1, b.ExecuteCount, "the abandoned command is never re-applied (no extra Execute from a redo)");
            Assert.AreEqual(2, manager.UndoDepth, "A and C remain undoable");
        }

        [Test]
        public void Capacity_RollsOldestOut_WithoutUndoingThem()
        {
            var manager = new CommandManager(3);
            var log = new List<string>();
            var commands = new[]
            {
                new RecordingCommand("c1", log),
                new RecordingCommand("c2", log),
                new RecordingCommand("c3", log),
                new RecordingCommand("c4", log),
                new RecordingCommand("c5", log),
            };

            foreach (RecordingCommand command in commands)
            {
                manager.Execute(command);
            }

            Assert.AreEqual(3, manager.UndoDepth, "only the newest three stay in the window");
            Assert.AreEqual(3, manager.Capacity);

            Assert.IsTrue(manager.Undo());
            Assert.IsTrue(manager.Undo());
            Assert.IsTrue(manager.Undo());
            Assert.IsFalse(manager.Undo(), "c1/c2 rolled out of the window — nothing left to undo");

            CollectionAssert.AreEqual(
                new[] { "exec:c1", "exec:c2", "exec:c3", "exec:c4", "exec:c5", "undo:c5", "undo:c4", "undo:c3" },
                log,
                "rolling drops the oldest WITHOUT undoing them (applied state stays; they just leave undo reach)");
            Assert.AreEqual(0, commands[0].UndoCount, "c1 left the window, was never undone");
            Assert.AreEqual(0, commands[1].UndoCount, "c2 left the window, was never undone");
            Assert.AreEqual(1, commands[2].UndoCount);
            Assert.AreEqual(3, manager.RedoDepth, "the three undone commands are redoable");
        }

        [Test]
        public void Constructor_RejectsCapacityBelowOne()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandManager(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandManager(-1));
            Assert.DoesNotThrow(() => new CommandManager(1), "capacity 1 is legal");
        }

        [Test]
        public void Execute_NullCommand_Throws()
        {
            var manager = new CommandManager();
            Assert.Throws<ArgumentNullException>(() => manager.Execute(null));
            Assert.AreEqual(0, manager.UndoDepth, "nothing recorded");
        }

        [Test]
        public void Events_CarryTheLabel_InStackOrder()
        {
            var manager = new CommandManager();

            manager.Execute(new RecordingCommand("A"));
            manager.Execute(new RecordingCommand("B"));
            manager.Undo();
            manager.Redo();

            CollectionAssert.AreEqual(
                new[] { "executed|A", "executed|B", "undone|B", "redone|B" },
                _events,
                "one event per stack transition, each carrying the command label");
        }

        [Test]
        public void Clear_EmptiesBothStacks_AndFiresNoEvents()
        {
            var manager = new CommandManager();
            manager.Execute(new RecordingCommand("A"));
            manager.Undo();
            _events.Clear();

            manager.Clear();

            Assert.AreEqual(0, manager.UndoDepth);
            Assert.AreEqual(0, manager.RedoDepth);
            Assert.IsFalse(manager.CanUndo);
            Assert.IsFalse(manager.CanRedo);
            Assert.IsFalse(manager.Undo());
            Assert.IsFalse(manager.Redo());
            Assert.AreEqual(0, _events.Count, "Clear itself fires no per-command events");
        }

        [Test]
        public void ReentrantExecute_FromInsideACommand_Throws()
        {
            var manager = new CommandManager();
            var nested = new RecordingCommand("nested");
            RecordingCommand reentrant = null;
            reentrant = new RecordingCommand("A", null, () => manager.Execute(nested));

            Assert.Throws<InvalidOperationException>(
                () => manager.Execute(reentrant),
                "interleaved timelines would corrupt the stack invariants — fail fast");

            Assert.AreEqual(0, manager.UndoDepth, "a command whose Execute threw is never recorded");
            Assert.AreEqual(1, reentrant.ExecuteCount, "the outer command's own body ran before the exception");
            Assert.AreEqual(0, nested.ExecuteCount, "the nested call was rejected before running any side effect");
        }

        [Test]
        public void ThrowingUndo_DropsTheCommandFromBothStacks()
        {
            var manager = new CommandManager();
            var log = new List<string>();
            var good = new RecordingCommand("good", log);
            var bad = new ThrowingCommand("bad");

            manager.Execute(good);
            manager.Execute(bad);

            Assert.Throws<InvalidOperationException>(() => manager.Undo(), "fail-fast, no silent fake undo");
            Assert.IsFalse(manager.CanRedo, "the throwing command was NOT pushed onto the redo stack");
            Assert.AreEqual(1, manager.UndoDepth, "the throwing command left the undo stack; 'good' below it stays");

            Assert.IsTrue(manager.Undo(), "the surviving 'good' stays reachable and undoable");
            CollectionAssert.AreEqual(
                new[] { "exec:good", "undo:good" },
                log,
                "no undo:bad line — the failed revert is dropped, never faked (ThrowingCommand logs nothing)");
        }

        private sealed class ThrowingCommand : ICommand
        {
            private readonly string _name;

            public ThrowingCommand(string name)
            {
                _name = name;
            }

            public string Label => _name;

            public void Execute()
            {
            }

            public void Undo()
            {
                throw new InvalidOperationException(_name + ": undo failed");
            }
        }
    }
}
