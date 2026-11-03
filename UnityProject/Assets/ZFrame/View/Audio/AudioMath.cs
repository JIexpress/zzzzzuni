using System;

namespace ZFrame.View
{
    /// <summary>
    /// Pure C# audio math (TASK-A-M2-02 core piece, zero UnityEngine).
    ///
    /// Volume mapping between the framework's normalized 0..1 volume scale and
    /// AudioMixer's decibel scale. Mapping is logarithmic (20*log10) — the
    /// perceptually correct curve for loudness — with hard clamps:
    /// v &lt;= 0 → -80 dB (digital silence), v &gt;= 1 → 0 dB (full level).
    /// All methods are static pure functions and safe for EditMode tests.
    /// </summary>
    public static class AudioMath
    {
        /// <summary>Decibel value representing digital silence.</summary>
        public const float MinDb = -80f;

        /// <summary>Decibel value representing full volume.</summary>
        public const float MaxDb = 0f;

        /// <summary>
        /// Normalized linear volume (0..1) → decibels. Values below 0 clamp to
        /// <see cref="MinDb"/> (mute), values above 1 clamp to <see cref="MaxDb"/>.
        /// Monotonically non-decreasing over [0, 1].
        /// </summary>
        public static float LinearToDb(float volume01)
        {
            if (volume01 <= 0f)
            {
                return MinDb;
            }

            if (volume01 >= 1f)
            {
                return MaxDb;
            }

            // 20*log10(v) spans (-Inf, 0]; for v in (0,1) it is already
            // strictly below 0 dB and above -80 dB for v > 0.0001. The one
            // in-between safeguard keeps values that round to -Inf at -80.
            float db = 20f * (float)Math.Log10(volume01);
            return db < MinDb ? MinDb : db;
        }

        /// <summary>
        /// Decibels → normalized linear volume (0..1). Inverse of
        /// <see cref="LinearToDb"/>: below <see cref="MinDb"/> clamps to 0
        /// (mute), above <see cref="MaxDb"/> clamps to 1.
        /// </summary>
        public static float DbToLinear(float db)
        {
            if (db <= MinDb)
            {
                return 0f;
            }

            if (db >= MaxDb)
            {
                return 1f;
            }

            return (float)Math.Pow(10.0, db / 20.0);
        }
    }
}
