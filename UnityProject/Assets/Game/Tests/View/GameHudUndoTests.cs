using Game.Domain.Interaction;
using Game.View.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace Game.View.Tests
{
    /// <summary>
    /// The GameHud undo controls (TASK-A-R9-12): the on-screen button drives
    /// the shared <see cref="ICommandManager"/> through the exact same
    /// <c>TryUndo</c> path as the PC-side Ctrl+Z poll (EditMode cannot
    /// synthesize keys — A-AND-04 precedent — so the tests press the UGUI
    /// button via onClick.Invoke), and the undo hint caption mirrors the
    /// CommandExecuted/Undone/Redone bus events.
    /// </summary>
    public sealed class GameHudUndoTests
    {
        private UIRoot _root;
        private CommandManager _commands;
        private bool _managerRegistered;

        [SetUp]
        public void SetUp()
        {
            UIRoot.ResetForTests();
            _root = UIRoot.EnsureCreated();
        }

        [TearDown]
        public void TearDown()
        {
            if (_managerRegistered)
            {
                Services.Unregister<ICommandManager>();
                _managerRegistered = false;
            }

            _commands = null;

            foreach (UIRoot root in Object.FindObjectsOfType<UIRoot>(true))
            {
                Object.DestroyImmediate(root.gameObject);
            }

            foreach (EventSystem eventSystem in Object.FindObjectsOfType<EventSystem>())
            {
                Object.DestroyImmediate(eventSystem.gameObject);
            }

            UIRoot.ResetForTests();
        }

        private GameHud BuildHud(out Canvas hud)
        {
            var host = new GameObject("GameHud");
            GameHud gameHud = host.AddComponent<GameHud>();
            gameHud.Build(_root);
            hud = _root.GetLayerCanvas(UIConsts.UILayer.HUD);
            return gameHud;
        }

        [Test]
        public void UndoButton_Click_UndoesThroughTheSharedManager_AndShowsTheLabel()
        {
            _commands = new CommandManager();
            Services.Register<ICommandManager>(_commands);
            _managerRegistered = true;

            RecordingCommand command = new RecordingCommand("拾取 gold×3");
            _commands.Execute(command);
            Assert.AreEqual(1, command.ExecuteCount, "precondition: the command is applied");

            GameHud gameHud = BuildHud(out Canvas hud);
            Button undoButton = hud.transform.Find("UndoButton").GetComponent<Button>();

            undoButton.onClick.Invoke();

            Assert.AreEqual(1, command.UndoCount, "the button press reached the shared command stack");
            Assert.IsFalse(_commands.CanUndo);
            Assert.AreEqual("撤销：拾取 gold×3", hud.transform.Find("UndoHint").GetComponent<Text>().text,
                "the hint caption mirrors the CommandUndone bus event");

            gameHud.Shutdown();
            Object.DestroyImmediate(gameHud.gameObject);
        }

        [Test]
        public void UndoButton_Click_OnEmptyStack_ShowsFallbackText()
        {
            _commands = new CommandManager();
            Services.Register<ICommandManager>(_commands);
            _managerRegistered = true;

            GameHud gameHud = BuildHud(out Canvas hud);
            Button undoButton = hud.transform.Find("UndoButton").GetComponent<Button>();

            undoButton.onClick.Invoke();

            Assert.AreEqual("没有可撤销的操作", hud.transform.Find("UndoHint").GetComponent<Text>().text);

            gameHud.Shutdown();
            Object.DestroyImmediate(gameHud.gameObject);
        }

        [Test]
        public void CommandEvents_DriveTheHintCaption()
        {
            GameHud gameHud = BuildHud(out Canvas hud);
            Text hint = hud.transform.Find("UndoHint").GetComponent<Text>();
            Assert.AreEqual(string.Empty, hint.text, "the hint starts blank");

            EventBus.Fire(new CommandExecuted("拾取 gold×3"));
            Assert.AreEqual("执行：拾取 gold×3", hint.text);

            EventBus.Fire(new CommandUndone("拾取 gold×3"));
            Assert.AreEqual("撤销：拾取 gold×3", hint.text);

            EventBus.Fire(new CommandRedone("拾取 gold×3"));
            Assert.AreEqual("重做：拾取 gold×3", hint.text);

            gameHud.Shutdown();
            Object.DestroyImmediate(gameHud.gameObject);
        }

        [Test]
        public void ItemReturned_ReSyncsThePickupCounter()
        {
            GameHud gameHud = BuildHud(out Canvas hud);
            Text counter = hud.transform.Find("PickupCounter").GetComponent<Text>();
            Assert.AreEqual("拾取计数：0", counter.text);

            EventBus.Fire(new ItemReturned(ActorId.Player, "gold", 2, 5));

            Assert.AreEqual("拾取计数：5", counter.text,
                "an undone pickup must not leave a stale total on the HUD");

            gameHud.Shutdown();
            Object.DestroyImmediate(gameHud.gameObject);
        }

        private sealed class RecordingCommand : ICommand
        {
            private readonly string _label;

            public RecordingCommand(string label)
            {
                _label = label;
            }

            public int ExecuteCount { get; private set; }

            public int UndoCount { get; private set; }

            public string Label => _label;

            public void Execute()
            {
                ExecuteCount++;
            }

            public void Undo()
            {
                UndoCount++;
            }
        }
    }
}
