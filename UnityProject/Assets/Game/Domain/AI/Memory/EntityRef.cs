using System;

namespace Game.Domain.AI.Memory
{
    /// <summary>
    /// Identity of whoever an episode is attributed to (TASK-A-AI-01) — a
    /// value type carrying a stable string id, so a memory outlives the
    /// entity it refers to and the logic layer never hands out object
    /// references (same reasoning as <c>Game.Domain.Interaction.ActorId</c>,
    /// which this type can be built from via <see cref="FromActor"/>).
    /// Pure C#: no engine types.
    /// </summary>
    public readonly struct EntityRef : IEquatable<EntityRef>
    {
        /// <summary>The local player's well-known source id.</summary>
        public const string PlayerId = "player";

        private readonly string _id;

        /// <summary>Creates a source reference. Throws on null/empty id — an attribution to nobody is a wiring bug.</summary>
        public EntityRef(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("EntityRef: id must not be empty.", nameof(id));
            }

            _id = id;
        }

        /// <summary>Stable identifier (e.g. "player", an enemy id, "actor-0").</summary>
        public string Id => _id ?? string.Empty;

        /// <summary>The local player as a memory source.</summary>
        public static EntityRef Player => new EntityRef(PlayerId);

        /// <summary>Builds a source from an interaction actor id (deterministic string form "actor-&lt;value&gt;").</summary>
        public static EntityRef FromActor(Interaction.ActorId actor)
        {
            return new EntityRef(actor.IsNone ? "actor-none" : "actor-" + actor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>True when both references carry the same id (ordinal).</summary>
        public bool Equals(EntityRef other)
        {
            return string.Equals(Id, other.Id, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is EntityRef other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Id.GetHashCode(StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "EntityRef(" + Id + ")";
        }

        /// <summary>Value equality (ordinal id comparison).</summary>
        public static bool operator ==(EntityRef a, EntityRef b)
        {
            return a.Equals(b);
        }

        /// <summary>Value inequality.</summary>
        public static bool operator !=(EntityRef a, EntityRef b)
        {
            return !a.Equals(b);
        }
    }
}
