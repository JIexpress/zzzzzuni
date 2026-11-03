using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the PipelineManager tier switching (TASK-A-M2-03):
    /// quality level follows the tier, the active RenderPipelineAsset matches
    /// the tier asset, the grade Volume activates the tier profile, the
    /// EventBus carries TierChangedEvent, and the runtime cross-pipeline
    /// switch guard behaves. Mutating tests restore the previous quality
    /// level / PlayerPrefs in teardown.
    /// </summary>
    public sealed class PipelineManagerTests
    {
        private int _initialQualityLevel;
        private int _initialStoredTier;
        private bool _hadStoredTier;

        [SetUp]
        public void SetUp()
        {
            _initialQualityLevel = QualitySettings.GetQualityLevel();
            _hadStoredTier = PlayerPrefs.HasKey(TierMapper.SettingsKey);
            _initialStoredTier = PlayerPrefs.GetInt(TierMapper.SettingsKey, -1);
        }

        [TearDown]
        public void TearDown()
        {
            if (QualitySettings.GetQualityLevel() != _initialQualityLevel &&
                _initialQualityLevel >= 0 && _initialQualityLevel < QualitySettings.names.Length)
            {
                QualitySettings.SetQualityLevel(_initialQualityLevel, false);
            }

            if (_hadStoredTier)
            {
                PlayerPrefs.SetInt(TierMapper.SettingsKey, _initialStoredTier);
            }
            else
            {
                PlayerPrefs.DeleteKey(TierMapper.SettingsKey);
            }

            PlayerPrefs.Save();

            if (PipelineManager.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(PipelineManager.Instance.gameObject);
            }
        }

        // ---- tier table -------------------------------------------------------

        [Test]
        public void TierTable_ResolvesFromResources_WithThreeRows()
        {
            var table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assert.That(table, Is.Not.Null, $"Resources/{PipelineManager.TableResourcePath} must exist");
            Assert.That(table.TierCount, Is.EqualTo((int)QualityTier.Count));

            Assert.That(table.GetPipelineAsset(QualityTier.Low).name, Is.EqualTo("URP_Low"));
            Assert.That(table.GetPipelineAsset(QualityTier.Medium).name, Is.EqualTo("URP_Medium"));
            Assert.That(table.GetPipelineAsset(QualityTier.High).name, Is.EqualTo("URP_High"));
            Assert.That(table.GetVolumeProfile(QualityTier.Low).name, Is.EqualTo("Vol_Low"));
            Assert.That(table.GetVolumeProfile(QualityTier.Medium).name, Is.EqualTo("Vol_Medium"));
            Assert.That(table.GetVolumeProfile(QualityTier.High).name, Is.EqualTo("Vol_High"));
        }

        [Test]
        public void TierTable_OutOfRangeTier_FallsBackToMediumRow()
        {
            var table = Resources.Load<PipelineTierTable>(PipelineManager.TableResourcePath);
            Assert.That(table, Is.Not.Null);

            Assert.That(table.GetPipelineAsset((QualityTier)99).name, Is.EqualTo("URP_Medium"));
            Assert.That(table.GetVolumeProfile((QualityTier)(-1)).name, Is.EqualTo("Vol_Medium"));
        }

        // ---- SetTier switching -------------------------------------------------

        [Test]
        public void SetTier_SwitchesQualityLevelAndPipeline_NoRestart()
        {
            Assume.That(QualitySettings.names.Length, Is.EqualTo((int)QualityTier.Count),
                "QualitySettings Low/Medium/High must be set up before this test");

            foreach (QualityTier tier in new[] { QualityTier.Low, QualityTier.Medium, QualityTier.High })
            {
                PipelineManager.SetTier(tier);

                Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo((int)tier),
                    "quality level must follow the tier");
                Assert.That(QualitySettings.names[QualitySettings.GetQualityLevel()],
                    Is.EqualTo(tier.ToString()));
                Assert.That(PipelineManager.CurrentTier, Is.EqualTo(tier));

                RenderPipelineAsset current = GraphicsSettings.currentRenderPipeline;
                Assert.That(current, Is.Not.Null, "URP must be the active pipeline");
                Assert.That(current.name, Is.EqualTo(TierMapper.ToPipelineAssetKey(tier)),
                    "active pipeline asset must be the tier asset");
            }
        }

        [Test]
        public void SetTier_ActivatesTierVolumeProfile_OnGlobalVolume()
        {
            Assume.That(QualitySettings.names.Length, Is.EqualTo((int)QualityTier.Count));

            PipelineManager.SetTier(QualityTier.High);

            var manager = PipelineManager.Instance;
            Assert.That(manager, Is.Not.Null);
            Assert.That(manager.GradeVolume, Is.Not.Null, "grade volume must be mounted");
            Assert.That(manager.GradeVolume.isGlobal, Is.True, "grade volume must be global");
            Assert.That(manager.CurrentGradeProfile, Is.Not.Null);
            Assert.That(manager.CurrentGradeProfile.name, Is.EqualTo("Vol_High"));

            PipelineManager.SetTier(QualityTier.Low);
            Assert.That(manager.CurrentGradeProfile.name, Is.EqualTo("Vol_Low"));
        }

        [Test]
        public void SetTier_OutOfRangeTier_NormalizesToMedium()
        {
            Assume.That(QualitySettings.names.Length, Is.EqualTo((int)QualityTier.Count));

            PipelineManager.SetTier((QualityTier)42);

            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Medium));
            Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo((int)QualityTier.Medium));
        }

        [Test]
        public void SetTier_PersistsTierUnderPlayerPrefsKey()
        {
            Assume.That(QualitySettings.names.Length, Is.EqualTo((int)QualityTier.Count));

            PipelineManager.SetTier(QualityTier.High);
            Assert.That(PlayerPrefs.GetInt(TierMapper.SettingsKey, -1), Is.EqualTo((int)QualityTier.High));

            PipelineManager.SetTier(QualityTier.Low);
            Assert.That(PlayerPrefs.GetInt(TierMapper.SettingsKey, -1), Is.EqualTo((int)QualityTier.Low));
        }

        // ---- EventBus ----------------------------------------------------------

        [Test]
        public void SetTier_FiresTierChangedEvent_OnEventBus()
        {
            Assume.That(QualitySettings.names.Length, Is.EqualTo((int)QualityTier.Count));

            PipelineManager.SetTier(QualityTier.Medium); // normalize starting point
            var events = new List<TierChangedEvent>();
            IDisposable subscription = EventBus.Subscribe<TierChangedEvent>(events.Add);

            try
            {
                PipelineManager.SetTier(QualityTier.High);
                Assert.That(events, Has.Count.EqualTo(1), "one actual change -> exactly one event");
                Assert.That(events[0].From, Is.EqualTo(QualityTier.Medium));
                Assert.That(events[0].To, Is.EqualTo(QualityTier.High));

                PipelineManager.SetTier(QualityTier.High); // idempotent re-apply
                Assert.That(events, Has.Count.EqualTo(1), "no change -> no event");
            }
            finally
            {
                subscription.Dispose();
            }
        }

        // ---- runtime cross-pipeline guard ---------------------------------------

        [Test]
        public void EditorSwitchGuard_ForbiddenWhilePlaying()
        {
            Assert.That(RenderPipelineSelector.IsEditorSwitchAllowed(true), Is.False,
                "play mode must never allow cross-pipeline switching");
            Assert.That(RenderPipelineSelector.IsEditorSwitchAllowed(false), Is.True,
                "editor edit mode is the only allowed context");
        }

        [Test]
        public void SelectToEditor_UnknownKey_ThrowsWithoutTouchingSettings()
        {
            RenderPipelineAsset before = GraphicsSettings.currentRenderPipeline;

            Assert.Throws<ArgumentException>(
                () => RenderPipelineSelector.SelectToEditor("NonExistentPipeline"));

            // The failed call must not have changed the active pipeline.
            Assert.That(GraphicsSettings.currentRenderPipeline, Is.SameAs(before));
        }
    }
}
