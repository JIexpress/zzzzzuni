namespace Game.Domain.Interaction
{
    /// <summary>
    /// Presentation-neutral description of an interactable, pushed to the View
    /// whenever it becomes the nearest target. Carries no engine types so the
    /// View can render it however it likes (world label, HUD, controller hint).
    /// </summary>
    public readonly struct InteractInfo
    {
        /// <summary>Stable identifier (scene-unique, e.g. "door-main").</summary>
        public readonly string Id;

        /// <summary>Player-facing prompt, e.g. "按 E 开门". Recomputed by the entity on every access.</summary>
        public readonly string Prompt;

        /// <summary>Template kind ("Door" / "Pickup" / "Dialog") for icon selection.</summary>
        public readonly string Kind;

        /// <summary>Creates an info record.</summary>
        public InteractInfo(string id, string prompt, string kind)
        {
            Id = id;
            Prompt = prompt;
            Kind = kind;
        }

        /// <summary>True when this describes a real target (an Id was supplied).</summary>
        public bool IsValid => !string.IsNullOrEmpty(Id);

        /// <summary>The "no target" value.</summary>
        public static InteractInfo None => default;

        /// <inheritdoc />
        public override string ToString() => IsValid ? $"{Kind}:{Id} \"{Prompt}\"" : "(none)";
    }
}
