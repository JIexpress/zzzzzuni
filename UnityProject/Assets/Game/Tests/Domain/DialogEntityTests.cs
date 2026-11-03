using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using NUnit.Framework;
using ZFrame.Core;

namespace Game.Domain.Tests
{
    /// <summary>
    /// Dialog template tests (TASK-A-M4-01): line-by-line advance, the
    /// finished latch, restart and the <see cref="DialogAdvanced"/> payload.
    /// </summary>
    public sealed class DialogEntityTests
    {
        private IDisposable _token;
        private readonly List<DialogAdvanced> _busEvents = new List<DialogAdvanced>();

        [SetUp]
        public void SetUp()
        {
            _token = EventBus.Subscribe<DialogAdvanced>(evt => _busEvents.Add(evt));
        }

        [TearDown]
        public void TearDown()
        {
            _token?.Dispose();
            _token = null;
            _busEvents.Clear();
        }

        private static DialogEntity CreateDialog()
        {
            return new DialogEntity("npc-elder", new[] { "第一行", "第二行" }, "旅人", "按 E 对话");
        }

        [Test]
        public void NewDialog_HasNoVisibleLine()
        {
            DialogEntity dialog = CreateDialog();

            Assert.AreEqual(-1, dialog.LineIndex);
            Assert.AreEqual(string.Empty, dialog.CurrentLine);
            Assert.IsFalse(dialog.IsFinished);
            Assert.AreEqual(2, dialog.Lines.Count);
            Assert.AreEqual("旅人", dialog.Speaker);
            Assert.AreEqual("Dialog", dialog.Info.Kind);
        }

        [Test]
        public void Interact_ShowsFirstLine_AndFiresEvent()
        {
            DialogEntity dialog = CreateDialog();

            dialog.Interact(ActorId.Player);

            Assert.AreEqual(0, dialog.LineIndex);
            Assert.AreEqual("第一行", dialog.CurrentLine);
            Assert.IsFalse(dialog.IsFinished);
            Assert.AreEqual(1, _busEvents.Count);
            Assert.AreEqual("npc-elder", _busEvents[0].DialogId);
            Assert.AreEqual(0, _busEvents[0].LineIndex);
            Assert.AreEqual("第一行", _busEvents[0].Line);
            Assert.IsFalse(_busEvents[0].IsFinished);
        }

        [Test]
        public void Interact_ThroughAllLines_ThenRefuses()
        {
            DialogEntity dialog = CreateDialog();

            dialog.Interact(ActorId.Player);
            dialog.Interact(ActorId.Player);

            Assert.IsTrue(dialog.IsFinished, "最后一行播完即结束");
            Assert.IsTrue(_busEvents[1].IsFinished);
            Assert.IsFalse(dialog.CanInteract(ActorId.Player));
            Assert.AreEqual(string.Empty, dialog.Info.Prompt, "结束后不再提示");

            dialog.Interact(ActorId.Player);

            Assert.AreEqual(1, dialog.LineIndex, "结束后再交互不得越界");
            Assert.AreEqual(2, _busEvents.Count, "结束后不得再发事件");
        }

        [Test]
        public void Advance_WhenFinished_ReturnsFalse()
        {
            DialogEntity dialog = CreateDialog();
            dialog.Advance();
            dialog.Advance();

            Assert.IsFalse(dialog.Advance());
        }

        [Test]
        public void Restart_RewindsToStart()
        {
            DialogEntity dialog = CreateDialog();
            dialog.Advance();
            dialog.Advance();

            dialog.Restart();

            Assert.AreEqual(-1, dialog.LineIndex);
            Assert.IsFalse(dialog.IsFinished);
            Assert.IsTrue(dialog.CanInteract(ActorId.Player));

            dialog.Advance();
            Assert.AreEqual("第一行", dialog.CurrentLine);
        }

        [Test]
        public void SingleLineDialog_IsFinishedAfterOneInteract()
        {
            var dialog = new DialogEntity("npc-single", new[] { "只有一句" });

            dialog.Interact(ActorId.Player);

            Assert.IsTrue(dialog.IsFinished);
            Assert.IsTrue(_busEvents[0].IsFinished);
        }

        [Test]
        public void Constructor_RejectsInvalidArguments()
        {
            Assert.Throws<ArgumentException>(() => new DialogEntity(string.Empty, new[] { "x" }));
            Assert.Throws<ArgumentNullException>(() => new DialogEntity("npc", null));
            Assert.Throws<ArgumentException>(() => new DialogEntity("npc", new string[0]));
        }

        [Test]
        public void Lines_AreCopiedAtConstruction()
        {
            var source = new List<string> { "a", "b" };
            var dialog = new DialogEntity("npc", source);

            source.Add("c");

            Assert.AreEqual(2, dialog.Lines.Count, "构造后外部改动不得影响对话表");
        }
    }
}
