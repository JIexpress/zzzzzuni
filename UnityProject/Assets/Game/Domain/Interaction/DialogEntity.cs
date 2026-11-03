using System;
using System.Collections.Generic;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Dialog template: an ordered line table advanced one line per interact
    /// (TASK-A-M4-01). The last line marks the dialog finished; further
    /// interacts are refused until <see cref="Restart"/>. Pure data — the View
    /// renders <see cref="CurrentLine"/> and advances on
    /// <see cref="DialogAdvanced"/>.
    ///
    /// TASK-A-R9-07: the template optionally carries the dialog's position on
    /// the 2D gameplay plane so <see cref="DialogAdvanced"/> can report where
    /// the conversation happened. The Domain never derives a position itself —
    /// the View that builds the entity hands one in; positionless dialogs
    /// (tests, abstract setups) keep reporting <see cref="Vec2.Zero"/>.
    /// </summary>
    public sealed class DialogEntity : IInteractable
    {
        private readonly string _id;
        private readonly string _speaker;
        private readonly string _prompt;
        private readonly string[] _lines;
        private readonly Vec2 _position;

        /// <summary>Creates a dialog.</summary>
        /// <param name="id">Stable identity, e.g. "npc-elder".</param>
        /// <param name="lines">Ordered line texts (at least one).</param>
        /// <param name="speaker">Speaker name for the View's nameplate.</param>
        /// <param name="prompt">Player-facing prompt.</param>
        /// <param name="position">Dialog position on the 2D plane (zero = unknown).</param>
        public DialogEntity(string id, IEnumerable<string> lines, string speaker = "NPC", string prompt = "按 E 对话",
            Vec2 position = default)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("DialogEntity: id must not be empty.", nameof(id));
            }

            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            var copy = new List<string>(lines);
            if (copy.Count == 0)
            {
                throw new ArgumentException("DialogEntity: needs at least one line.", nameof(lines));
            }

            _id = id;
            _lines = copy.ToArray();
            _speaker = speaker;
            _prompt = prompt;
            _position = position;
            LineIndex = -1; // nothing shown yet; first Interact shows line 0
        }

        /// <summary>Raised after every line advance (including the final one).</summary>
        public event Action<DialogAdvanced> DialogAdvanced;

        /// <summary>Stable identity.</summary>
        public string Id => _id;

        /// <summary>Speaker name.</summary>
        public string Speaker => _speaker;

        /// <summary>Dialog position on the 2D gameplay plane (<see cref="Vec2.Zero"/> when unknown).</summary>
        public Vec2 Position => _position;

        /// <summary>All lines, read-only.</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>Index of the visible line; -1 before the first interact.</summary>
        public int LineIndex { get; private set; }

        /// <summary>True once the last line has been shown.</summary>
        public bool IsFinished => LineIndex >= _lines.Length - 1;

        /// <summary>Text currently on screen ("" before the first interact).</summary>
        public string CurrentLine => LineIndex >= 0 ? _lines[LineIndex] : string.Empty;

        /// <inheritdoc />
        public InteractInfo Info => new InteractInfo(_id, IsFinished ? string.Empty : _prompt, "Dialog");

        /// <inheritdoc />
        public bool CanInteract(ActorId actor)
        {
            return !IsFinished;
        }

        /// <inheritdoc />
        public void Interact(ActorId actor)
        {
            Advance();
        }

        /// <summary>
        /// Shows the next line and raises <see cref="DialogAdvanced"/>. Returns
        /// false when the dialog is already finished.
        /// </summary>
        public bool Advance()
        {
            if (IsFinished)
            {
                return false;
            }

            LineIndex++;
            var evt = new DialogAdvanced(_id, LineIndex, _lines[LineIndex], IsFinished, _position);
            DialogAdvanced?.Invoke(evt);
            EventBus.Fire(evt);
            return true;
        }

        /// <summary>Rewinds to "not started" so the same NPC can be talked to again.</summary>
        public void Restart()
        {
            LineIndex = -1;
        }
    }
}
