namespace Game.Domain.Interaction
{
    /// <summary>
    /// Composition root of the interaction slice (TASK-A-M4-01): one
    /// <see cref="InteractionRegistry"/> plus the player's <see cref="Inventory"/>, shared by every interactable in a
    /// session. Pure C# — the View asks <see cref="Shared"/> for the registry
    /// instead of owning one, so proxies created by different scene objects
    /// (door, pickup, NPC) all land in the same table.
    ///
    /// Tests build their own instances; <see cref="ResetShared"/> drops the
    /// process-wide one (scene reload / test teardown).
    /// </summary>
    public sealed class InteractionWorld
    {
        private static InteractionWorld _shared;

        /// <summary>Creates a world for <paramref name="player"/>.</summary>
        public InteractionWorld(ActorId player)
        {
            Player = player;
            Registry = new InteractionRegistry();
            Inventory = new Inventory();
        }

        /// <summary>Creates a world whose actor is <see cref="ActorId.Player"/>.</summary>
        public InteractionWorld()
            : this(ActorId.Player)
        {
        }

        /// <summary>Process-wide instance, created on first access.</summary>
        public static InteractionWorld Shared => _shared ?? (_shared = new InteractionWorld());

        /// <summary>Every interactable in this world, keyed by identity.</summary>
        public InteractionRegistry Registry { get; }

        /// <summary>The single player inventory pickups feed into.</summary>
        public Inventory Inventory { get; }

        /// <summary>Actor used when the player activates something.</summary>
        public ActorId Player { get; }

        /// <summary>Drops the shared instance (its registry stops receiving events).</summary>
        public static void ResetShared()
        {
            _shared?.Registry.Clear();
            _shared = null;
        }

        /// <summary>Activates the current aim target as <see cref="Player"/>. False when nothing is in range or it refuses.</summary>
        public bool Interact()
        {
            return Registry.InteractNearest(Player);
        }
    }
}
