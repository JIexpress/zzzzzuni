namespace Game.Domain.Interaction
{
    /// <summary>
    /// One raycast candidate fed to <see cref="InteractionRegistry.PickNearest"/>:
    /// the interactable plus its distance from the aim origin. The View builds
    /// these from physics hits; the Domain picks the winner so the selection
    /// rule stays unit-testable without a scene.
    /// </summary>
    public readonly struct InteractionCandidate
    {
        /// <summary>The candidate target (may be null — filtered out).</summary>
        public readonly IInteractable Target;

        /// <summary>Distance from the aim origin, in metres.</summary>
        public readonly float Distance;

        /// <summary>Creates a candidate.</summary>
        public InteractionCandidate(IInteractable target, float distance)
        {
            Target = target;
            Distance = distance;
        }
    }
}
