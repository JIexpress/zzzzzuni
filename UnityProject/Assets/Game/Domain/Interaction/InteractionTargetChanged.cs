namespace Game.Domain.Interaction
{
    /// <summary>
    /// Raised when the nearest interactable under the player's aim changes.
    /// <see cref="HasTarget"/> = false means "nothing in range" — the View must
    /// hide the prompt. The View supplies the world position; the Domain only
    /// knows identity, so the event stays engine-free.
    /// </summary>
    public readonly struct InteractionTargetChanged
    {
        /// <summary>True when a target is in range.</summary>
        public readonly bool HasTarget;

        /// <summary>Description of the new target; <see cref="InteractInfo.None"/> when <see cref="HasTarget"/> is false.</summary>
        public readonly InteractInfo Info;

        /// <summary>Creates the event.</summary>
        public InteractionTargetChanged(bool hasTarget, InteractInfo info)
        {
            HasTarget = hasTarget;
            Info = info;
        }

        /// <summary>"Target lost" event.</summary>
        public static InteractionTargetChanged Cleared => new InteractionTargetChanged(false, InteractInfo.None);
    }
}
