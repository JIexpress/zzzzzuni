using System;

namespace ZFrame.Gameplay
{
    /// <summary>
    /// Minimal 2D vector for the engine-free gameplay layer. The logic layer
    /// (ZFrame.Gameplay + Game.Domain) is scanned for engine tokens, and a
    /// truly dependency-free layer must not reference UnityEngine.Vector2
    /// either — the View layer converts at the boundary instead.
    ///
    /// Only what gameplay math needs: add/subtract/scale, distance,
    /// normalization and MoveTowards. Immutable value type.
    /// </summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        /// <summary>Horizontal component.</summary>
        public readonly float X;

        /// <summary>Vertical component (2D "Z" of a top-down plane).</summary>
        public readonly float Y;

        /// <summary>(0, 0).</summary>
        public static Vec2 Zero => new Vec2(0f, 0f);

        /// <summary>(1, 1).</summary>
        public static Vec2 One => new Vec2(1f, 1f);

        /// <summary>Creates a vector.</summary>
        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Squared length (cheap comparisons).</summary>
        public float SqrMagnitude => (X * X) + (Y * Y);

        /// <summary>Length.</summary>
        public float Magnitude => (float)Math.Sqrt(SqrMagnitude);

        /// <summary>Unit vector; returns <see cref="Zero"/> for a zero-length vector.</summary>
        public Vec2 Normalized
        {
            get
            {
                float magnitude = Magnitude;
                return magnitude > 1e-6f ? new Vec2(X / magnitude, Y / magnitude) : Zero;
            }
        }

        /// <summary>Euclidean distance between two points.</summary>
        public static float Distance(Vec2 a, Vec2 b)
        {
            return (a - b).Magnitude;
        }

        /// <summary>
        /// Moves <paramref name="current"/> toward <paramref name="target"/> by
        /// at most <paramref name="maxDelta"/> (never overshoots).
        /// </summary>
        public static Vec2 MoveTowards(Vec2 current, Vec2 target, float maxDelta)
        {
            if (maxDelta <= 0f)
            {
                return current;
            }

            Vec2 delta = target - current;
            float distance = delta.Magnitude;
            if (distance <= maxDelta || distance <= 1e-6f)
            {
                return target;
            }

            return current + (delta * (maxDelta / distance));
        }

        /// <summary>Component-wise sum.</summary>
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

        /// <summary>Component-wise difference.</summary>
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

        /// <summary>Uniform scale.</summary>
        public static Vec2 operator *(Vec2 value, float scale) => new Vec2(value.X * scale, value.Y * scale);

        /// <summary>Uniform scale (scalar first).</summary>
        public static Vec2 operator *(float scale, Vec2 value) => value * scale;

        /// <summary>Exact component equality (use <see cref="Distance"/> for tolerances).</summary>
        public static bool operator ==(Vec2 a, Vec2 b) => a.X.Equals(b.X) && a.Y.Equals(b.Y);

        /// <summary>Exact component inequality.</summary>
        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        /// <inheritdoc />
        public bool Equals(Vec2 other) => this == other;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }
}
