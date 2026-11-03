using ZFrame.Gameplay;

namespace Game.Domain.Interaction
{
    /// <summary>Raised each time a <see cref="DialogEntity"/> advances one line.</summary>
    public readonly struct DialogAdvanced
    {
        /// <summary>Dialog identity.</summary>
        public readonly string DialogId;

        /// <summary>Zero-based index of the line now displayed.</summary>
        public readonly int LineIndex;

        /// <summary>Text of the line now displayed.</summary>
        public readonly string Line;

        /// <summary>True when this was the last line — further interacts are refused.</summary>
        public readonly bool IsFinished;

        /// <summary>
        /// Dialog position on the 2D gameplay plane (TASK-A-R9-07 minimal event
        /// extension, ItemPickedUp precedent: presentation layers turn the
        /// conversation-completion moment into feedback without owning a
        /// position lookup). <see cref="Vec2.Zero"/> when the dialog was
        /// created without a position.
        /// </summary>
        public readonly Vec2 Position;

        /// <summary>Creates the event (positionless form; <see cref="Position"/> becomes <see cref="Vec2.Zero"/>).</summary>
        public DialogAdvanced(string dialogId, int lineIndex, string line, bool isFinished)
        {
            DialogId = dialogId;
            LineIndex = lineIndex;
            Line = line;
            IsFinished = isFinished;
            Position = Vec2.Zero;
        }

        /// <summary>Creates the event with the dialog's plane position.</summary>
        public DialogAdvanced(string dialogId, int lineIndex, string line, bool isFinished, Vec2 position)
        {
            DialogId = dialogId;
            LineIndex = lineIndex;
            Line = line;
            IsFinished = isFinished;
            Position = position;
        }
    }
}
