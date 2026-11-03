using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// Pure-logic coverage of the tier matrix mapping (TASK-A-M2-03): full
    /// enum coverage, out-of-range fallback to Medium, asset key mapping and
    /// stored-value parsing.
    /// </summary>
    public sealed class TierMapperTests
    {
        // ---- full enum coverage --------------------------------------------

        [Test]
        public void IsValid_CoversAllRealTiers()
        {
            Assert.That(TierMapper.IsValid(QualityTier.Low), Is.True);
            Assert.That(TierMapper.IsValid(QualityTier.Medium), Is.True);
            Assert.That(TierMapper.IsValid(QualityTier.High), Is.True);
            Assert.That(TierMapper.IsValid(QualityTier.Count), Is.False);
            Assert.That(TierMapper.IsValid((QualityTier)(-1)), Is.False);
        }

        [Test]
        public void FromSettingsValue_AllValidIndices_MapToSameEnum()
        {
            Assert.That(TierMapper.FromSettingsValue(0), Is.EqualTo(QualityTier.Low));
            Assert.That(TierMapper.FromSettingsValue(1), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromSettingsValue(2), Is.EqualTo(QualityTier.High));
        }

        [Test]
        public void FromSettingsValue_OutOfRange_FallsBackToMedium()
        {
            Assert.That(TierMapper.FromSettingsValue(-1), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromSettingsValue(3), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromSettingsValue(int.MinValue), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromSettingsValue(int.MaxValue), Is.EqualTo(QualityTier.Medium));
        }

        [Test]
        public void ToSettingsValue_RoundTrips_AllTiers()
        {
            foreach (QualityTier tier in new[] { QualityTier.Low, QualityTier.Medium, QualityTier.High })
            {
                Assert.That(
                    TierMapper.FromSettingsValue(TierMapper.ToSettingsValue(tier)),
                    Is.EqualTo(tier));
            }
        }

        [Test]
        public void Normalize_OutOfRangeTiers_CollapseToMedium()
        {
            Assert.That(TierMapper.Normalize(QualityTier.Count), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.Normalize((QualityTier)99), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.Normalize((QualityTier)(-5)), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.Normalize(QualityTier.High), Is.EqualTo(QualityTier.High));
        }

        // ---- asset keys ------------------------------------------------------

        [Test]
        public void ToPipelineAssetKey_MatchesGeneratedAssets()
        {
            Assert.That(TierMapper.ToPipelineAssetKey(QualityTier.Low), Is.EqualTo("URP_Low"));
            Assert.That(TierMapper.ToPipelineAssetKey(QualityTier.Medium), Is.EqualTo("URP_Medium"));
            Assert.That(TierMapper.ToPipelineAssetKey(QualityTier.High), Is.EqualTo("URP_High"));
        }

        [Test]
        public void ToVolumeProfileKey_MatchesGeneratedProfiles()
        {
            Assert.That(TierMapper.ToVolumeProfileKey(QualityTier.Low), Is.EqualTo("Vol_Low"));
            Assert.That(TierMapper.ToVolumeProfileKey(QualityTier.Medium), Is.EqualTo("Vol_Medium"));
            Assert.That(TierMapper.ToVolumeProfileKey(QualityTier.High), Is.EqualTo("Vol_High"));
        }

        [Test]
        public void ToKeys_OutOfRangeTier_FallsBackToMedium()
        {
            Assert.That(TierMapper.ToPipelineAssetKey(QualityTier.Count), Is.EqualTo("URP_Medium"));
            Assert.That(TierMapper.ToVolumeProfileKey((QualityTier)(-1)), Is.EqualTo("Vol_Medium"));
        }

        // ---- name / stored-value parsing --------------------------------------

        [Test]
        public void TryParseName_CaseInsensitive()
        {
            Assert.That(TierMapper.TryParseName("Low", out QualityTier low), Is.True);
            Assert.That(low, Is.EqualTo(QualityTier.Low));
            Assert.That(TierMapper.TryParseName("HIGH", out QualityTier high), Is.True);
            Assert.That(high, Is.EqualTo(QualityTier.High));
            Assert.That(TierMapper.TryParseName("medium", out QualityTier medium), Is.True);
            Assert.That(medium, Is.EqualTo(QualityTier.Medium));
        }

        [Test]
        public void TryParseName_Invalid_ReturnsFalseWithMediumOut()
        {
            Assert.That(TierMapper.TryParseName("Ultra", out QualityTier tier), Is.False);
            Assert.That(tier, Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.TryParseName(null, out _), Is.False);
            Assert.That(TierMapper.TryParseName("", out _), Is.False);
        }

        [Test]
        public void FromStoredValue_NumericForm_Works()
        {
            Assert.That(TierMapper.FromStoredValue("0"), Is.EqualTo(QualityTier.Low));
            Assert.That(TierMapper.FromStoredValue("2"), Is.EqualTo(QualityTier.High));
            Assert.That(TierMapper.FromStoredValue("-3"), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromStoredValue("7"), Is.EqualTo(QualityTier.Medium));
        }

        [Test]
        public void FromStoredValue_NameForm_Works()
        {
            Assert.That(TierMapper.FromStoredValue("High"), Is.EqualTo(QualityTier.High));
            Assert.That(TierMapper.FromStoredValue("low"), Is.EqualTo(QualityTier.Low));
        }

        [Test]
        public void FromStoredValue_GarbageOrEmpty_FallsBackToMedium()
        {
            Assert.That(TierMapper.FromStoredValue("garbage"), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromStoredValue(""), Is.EqualTo(QualityTier.Medium));
            Assert.That(TierMapper.FromStoredValue(null), Is.EqualTo(QualityTier.Medium));
        }

        // ---- constants ---------------------------------------------------------

        [Test]
        public void Count_EqualsThree()
        {
            Assert.That((int)QualityTier.Count, Is.EqualTo(3));
        }
    }
}
