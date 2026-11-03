using System;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Identity of the actor performing an interaction (player, NPC, scripted
    /// trigger). A value type so the logic layer never hands out object
    /// references that the View could mutate.
    /// </summary>
    public readonly struct ActorId : IEquatable<ActorId>
    {
        /// <summary>Raw identifier; negative values are reserved for the sentinels below.</summary>
        public readonly int Value;

        /// <summary>The local player (value 0).</summary>
        public static ActorId Player => new ActorId(0);

        /// <summary>No actor / system-initiated (value -1).</summary>
        public static ActorId None => new ActorId(-1);

        /// <summary>Creates an actor id.</summary>
        public ActorId(int value)
        {
            Value = value;
        }

        /// <summary>True when this is <see cref="None"/>.</summary>
        public bool IsNone => Value < 0;

        /// <inheritdoc />
        public bool Equals(ActorId other) => Value == other.Value;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is ActorId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Value;

        /// <inheritdoc />
        public override string ToString() => $"Actor({Value})";

        /// <summary>Value equality.</summary>
        public static bool operator ==(ActorId a, ActorId b) => a.Value == b.Value;

        /// <summary>Value inequality.</summary>
        public static bool operator !=(ActorId a, ActorId b) => a.Value != b.Value;
    }
}
