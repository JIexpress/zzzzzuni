using System;
using UnityEngine;

namespace Game.Domain.World
{
    /// <summary>
    /// TASK-A-WORLD-01：世界坐标（double 三分量，单位米）。
    ///
    /// 为什么不用 Vector3：Unity 的 Transform 是 float32，100km 处最小刻度约 7.8mm，
    /// 顶点抖动就是这么来的。世界的「绝对坐标」必须用 double 存，只在渲染时
    /// 减去浮点原点（<see cref="WorldOriginMath.ToUnity"/>）再降到 float。
    ///
    /// 与 Unity 的互转**只有一处实现**（<see cref="WorldOriginMath"/>）：本类型刻意
    /// 不提供 `(float)X` 之类的最简转换，避免误用「先转 float 再减偏移」的顺序。
    /// </summary>
    [Serializable]
    public struct WorldPosition : IEquatable<WorldPosition>
    {
        public double X;
        public double Y;
        public double Z;

        public static readonly WorldPosition Zero = new WorldPosition(0.0, 0.0, 0.0);

        public WorldPosition(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>模长平方（double，避免开方丢失）。</summary>
        public double SqrMagnitude => X * X + Y * Y + Z * Z;

        /// <summary>模长（米）。</summary>
        public double Magnitude => Math.Sqrt(SqrMagnitude);

        public static WorldPosition operator +(WorldPosition a, WorldPosition b)
            => new WorldPosition(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static WorldPosition operator -(WorldPosition a, WorldPosition b)
            => new WorldPosition(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static WorldPosition operator -(WorldPosition a)
            => new WorldPosition(-a.X, -a.Y, -a.Z);

        public static WorldPosition operator *(WorldPosition a, double scale)
            => new WorldPosition(a.X * scale, a.Y * scale, a.Z * scale);

        public static WorldPosition operator *(double scale, WorldPosition a) => a * scale;

        /// <summary>两点欧氏距离（米，double）。</summary>
        public static double Distance(WorldPosition a, WorldPosition b) => (a - b).Magnitude;

        /// <summary>换算到当前浮点原点的 Unity 坐标（顺序铁律在 <see cref="WorldOriginMath.ToUnity"/>）。</summary>
        public Vector3 ToUnity(WorldPosition originOffset) => WorldOriginMath.ToUnity(this, originOffset);

        /// <summary>由 Unity 坐标 + 浮点原点还原世界坐标。</summary>
        public static WorldPosition FromUnity(Vector3 unityPosition, WorldPosition originOffset)
            => WorldOriginMath.ToWorld(unityPosition, originOffset);

        public bool Equals(WorldPosition other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object obj) => obj is WorldPosition other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(WorldPosition a, WorldPosition b) => a.Equals(b);

        public static bool operator !=(WorldPosition a, WorldPosition b) => !a.Equals(b);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
