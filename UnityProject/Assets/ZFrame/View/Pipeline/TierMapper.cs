using System;

namespace ZFrame.View
{
    /// <summary>
    /// Pure, engine-free mapping core of the pipeline tier matrix
    /// (TASK-A-M2-03 design point 3): settings value <-> enum <-> asset key.
    ///
    /// Contract:
    ///  - the enum value IS the QualitySettings level index (Low=0/Medium=1/High=2);
    ///  - every out-of-range input falls back to <see cref="QualityTier.Medium"/>;
    ///  - asset keys follow the generated assets under Assets/Settings/Pipeline/:
    ///    URP_Low/URP_Medium/URP_High (+ *_Renderer) and Vol_Low/Vol_Medium/Vol_High.
    ///
    /// Kept free of UnityEngine calls so the whole matrix can be unit-tested
    /// without a pipeline or engine objects.
    /// </summary>
    public static class TierMapper
    {
        /// <summary>PlayerPrefs key the manager persists the last tier under.</summary>
        public const string SettingsKey = "ZFrame.QualityTier";

        /// <summary>Pipeline asset key prefix (URP_Low.asset etc.).</summary>
        public const string PipelineAssetKeyPrefix = "URP_";

        /// <summary>Volume profile key prefix (Vol_Low.asset etc.).</summary>
        public const string VolumeProfileKeyPrefix = "Vol_";

        /// <summary>Fallback tier for every invalid input.</summary>
        public static QualityTier DefaultTier => QualityTier.Medium;

        /// <summary>True when <paramref name="tier"/> is one of Low/Medium/High.</summary>
        public static bool IsValid(QualityTier tier)
        {
            return tier >= QualityTier.Low && tier < QualityTier.Count;
        }

        /// <summary>Out-of-range tiers collapse to <see cref="DefaultTier"/> (Medium).</summary>
        public static QualityTier Normalize(QualityTier tier)
        {
            return IsValid(tier) ? tier : DefaultTier;
        }

        /// <summary>
        /// Maps a persisted settings value (PlayerPrefs int / quality level
        /// index) to a tier; any out-of-range value falls back to Medium.
        /// </summary>
        public static QualityTier FromSettingsValue(int value)
        {
            return value >= (int)QualityTier.Low && value < (int)QualityTier.Count
                ? (QualityTier)value
                : DefaultTier;
        }

        /// <summary>Inverse of <see cref="FromSettingsValue"/>; invalid tiers collapse to Medium first.</summary>
        public static int ToSettingsValue(QualityTier tier)
        {
            return (int)Normalize(tier);
        }

        /// <summary>QualitySettings level index for the tier (== enum value).</summary>
        public static int ToQualityLevel(QualityTier tier)
        {
            return ToSettingsValue(tier);
        }

        /// <summary>Pipeline asset key, e.g. <c>"URP_Medium"</c> (without extension).</summary>
        public static string ToPipelineAssetKey(QualityTier tier)
        {
            return PipelineAssetKeyPrefix + Normalize(tier);
        }

        /// <summary>Volume profile key, e.g. <c>"Vol_High"</c> (without extension).</summary>
        public static string ToVolumeProfileKey(QualityTier tier)
        {
            return VolumeProfileKeyPrefix + Normalize(tier);
        }

        /// <summary>
        /// Parses a tier name ("Low"/"Medium"/"High", case-insensitive).
        /// Returns false for anything else (caller decides the fallback).
        /// </summary>
        public static bool TryParseName(string name, out QualityTier tier)
        {
            if (Enum.TryParse(name, ignoreCase: true, out tier) && IsValid(tier))
            {
                return true;
            }

            tier = DefaultTier;
            return false;
        }

        /// <summary>
        /// Reads a persisted string (PlayerPrefs string flavor or config):
        /// accepts both the numeric form ("2") and the name form ("High").
        /// Out-of-range/unparsable values fall back to Medium.
        /// </summary>
        public static QualityTier FromStoredValue(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return DefaultTier;
            }

            if (int.TryParse(stored, out int numeric))
            {
                return FromSettingsValue(numeric);
            }

            return TryParseName(stored, out QualityTier tier) ? tier : DefaultTier;
        }
    }
}
