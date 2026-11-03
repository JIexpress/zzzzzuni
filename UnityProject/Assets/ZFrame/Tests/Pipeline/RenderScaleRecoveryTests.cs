using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// EditMode coverage of the DRS crash self-healing layer (TASK-A-R9-02):
    /// journal-before-write ordering, boot recovery, G3 (never overwrite a
    /// value somebody else changed), journal consumption on every path,
    /// corruption containment and the compounding cut.
    ///
    /// Hard constraint: NO test may write the real <c>URP_*.asset</c> files.
    /// Every asset here is a throwaway
    /// <c>ScriptableObject.CreateInstance&lt;UniversalRenderPipelineAsset&gt;()</c>
    /// reached through the injected <see cref="FakeTarget"/>; the journal is
    /// always pointed at a per-test temp directory.
    /// </summary>
    public sealed class RenderScaleRecoveryTests
    {
        private const string AssetName = "URP_High";
        private const float OriginalScale = 1f;
        private const float WrittenScale = 0.75f;

        /// <summary>Marker every "the journal is gone" warning carries (round-2 contract wording).</summary>
        private const string JournalFailureMarker = "crash recovery unavailable";

        private string _root;
        private string _logRoot;
        private readonly List<string> _warnings = new List<string>();
        private Action<LogLevel, string, string> _logHook;
        private FakeTarget _target;
        private FrameGovernorHost _host;
        private FakeTelemetry _telemetry;
        private int _initialQualityLevel;
        private int _initialStoredTier;
        private bool _hadStoredTier;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zframe_r9_02_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _logRoot = Path.Combine(Path.GetTempPath(), "zframe_r9_02_log_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_logRoot);

            _initialQualityLevel = QualitySettings.GetQualityLevel();
            _hadStoredTier = PlayerPrefs.HasKey(TierMapper.SettingsKey);
            _initialStoredTier = PlayerPrefs.GetInt(TierMapper.SettingsKey, -1);

            _target = new FakeTarget();
            _telemetry = new FakeTelemetry();
            _warnings.Clear();
            _logHook = (level, channel, message) =>
            {
                if (level == LogLevel.Warn)
                {
                    _warnings.Add(message);
                }
            };
            ZLog.OnLog += _logHook;
        }

        [TearDown]
        public void TearDown()
        {
            if (_logHook != null)
            {
                ZLog.OnLog -= _logHook;
                _logHook = null;
            }

            if (_host != null)
            {
                _host.Shutdown();
                UnityEngine.Object.DestroyImmediate(_host.gameObject);
                _host = null;
            }

            if (_target != null)
            {
                _target.Dispose();
                _target = null;
            }

            if (_initialQualityLevel >= 0 && _initialQualityLevel < QualitySettings.names.Length &&
                QualitySettings.GetQualityLevel() != _initialQualityLevel)
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

            TryDeleteDirectory(_root);
            TryDeleteDirectory(_logRoot);
        }

        // ---- G1: the crash simulation -------------------------------------------------

        [Test]
        public void CrashJournal_RestoresOriginal_DeletesJournal_AndWarns()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, WrittenScale); // disk after the crash
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_root);
            Assert.IsTrue(journal.Save(OneEntry(0, AssetName, OriginalScale, WrittenScale)));
            Assert.IsTrue(File.Exists(journal.FilePath), "precondition: the crashed session left a journal");

            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_root, _target);

            // Evidence line (printed into the NUnit xml output block).
            TestContext.WriteLine(
                $"crash: journal(original=1.00, lastWritten=0.75) + asset=0.75 -> status={report.Status}, " +
                $"restored={report.RestoredCount}, asset={asset.renderScale:0.00}, journalExists={File.Exists(journal.FilePath)}");

            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "the crashed session's renderScale must be healed back to the user's 1.0");
            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Restored));
            Assert.That(report.RestoredCount, Is.EqualTo(1));
            Assert.That(File.Exists(journal.FilePath), Is.False,
                "recovery consumes the journal — the same crash must never be replayed");
            Assert.That(_warnings.Any(w => w.Contains("did not shut down cleanly") && w.Contains("0.75") && w.Contains("1.00")),
                Is.True, "the heal must be diagnosable in the log: " + string.Join(" | ", _warnings));
        }

        // ---- G2: the normal path does not regress --------------------------------------

        [Test]
        public void NormalShutdown_RestoresAsset_AndLeavesNoJournal()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_logRoot);
            NewHost(journal);

            _host.SetEnabled(true);
            Pump(2.2f); // one hysteresis window: first RenderScale rung

            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f), "precondition: the governor dirtied the asset");
            Assert.That(File.Exists(journal.FilePath), Is.True, "a journal must exist while an asset is dirty");

            _host.Shutdown();

            TestContext.WriteLine(
                $"normal path: drove asset to 0.95 (journal existed={true}), Shutdown() -> asset={asset.renderScale:0.00}, " +
                $"journalExists={File.Exists(journal.FilePath)}");

            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f), "clean shutdown still restores the original");
            Assert.That(File.Exists(journal.FilePath), Is.False, "a clean shutdown must leave NO journal behind (G2)");
        }

        // ---- G3: never overwrite somebody else's change --------------------------------

        [Test]
        public void Recovery_WhenCurrentDiffersFromLastWritten_DoesNotWriteAnything()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, 0.9f); // third party changed it
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_root);
            Assert.IsTrue(journal.Save(OneEntry(0, AssetName, OriginalScale, WrittenScale)));

            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_root, _target);

            TestContext.WriteLine(
                $"G3: journal lastWritten=0.75, disk=0.90 -> status={report.Status}, writes=[{string.Join(",", _target.Writes)}], " +
                $"asset={asset.renderScale:0.00}, journalExists={File.Exists(journal.FilePath)}");

            Assert.That(asset.renderScale, Is.EqualTo(0.9f).Within(1e-5f), "G3: the value on disk must be left alone");
            Assert.That(_target.Writes, Is.Empty, "G3: not a single write may reach the asset");
            Assert.That(_target.Writes, Has.No.Member(OriginalScale), "G3: 1.0 must never be written over 0.9");
            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Abandoned));
            Assert.That(report.AbandonedCount, Is.EqualTo(1));
            Assert.That(File.Exists(journal.FilePath), Is.False, "the abandoned path consumes the journal too");
            Assert.That(_warnings.Any(w => w.Contains("NOT restoring") && w.Contains("0.90")),
                Is.True, "G3 must warn explicitly: " + string.Join(" | ", _warnings));
        }

        // ---- ordering: the note is durable before the asset changes ---------------------

        [Test]
        public void Journal_IsAlreadyOnDisk_BeforeTheFirstRenderScaleWrite()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_logRoot);
            _target.JournalProbe = () => File.Exists(journal.FilePath)
                ? File.ReadAllText(journal.FilePath)
                : null;
            NewHost(journal);

            _host.SetEnabled(true);
            Pump(2.2f);

            TestContext.WriteLine(
                $"order: writes=[{string.Join(",", _target.Writes)}], " +
                $"journalAtFirstWrite={(_target.JournalAtFirstWrite == null ? "MISSING" : "present")}");

            Assert.That(_target.Writes.Count, Is.EqualTo(1), "one rung walked -> exactly one asset write");
            Assert.That(_target.Writes[0], Is.EqualTo(0.95f).Within(1e-4f));
            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f));
            Assert.That(_target.JournalAtFirstWrite, Is.Not.Null,
                "the recovery journal must already exist when the FIRST renderScale write happens");
            Assert.That(_target.JournalAtFirstWrite, Does.Contain("\"originalScale\": 1.0"),
                "the note must carry the original value: " + _target.JournalAtFirstWrite);
            Assert.That(_target.JournalAtFirstWrite, Does.Contain("\"lastWrittenScale\": 0.95"),
                "the note must carry the value about to be written: " + _target.JournalAtFirstWrite);
            Assert.That(_target.JournalAtFirstWrite, Does.Contain("\"assetName\": \"" + AssetName + "\""));
        }

        // ---- corruption containment ---------------------------------------------------

        [Test]
        public void CorruptJournal_DoesNotThrow_IsQuarantined_BootContinues()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, WrittenScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_root);
            File.WriteAllText(journal.FilePath, "{ this is not a journal ");

            RenderScaleRecoveryReport report = null;
            Assert.DoesNotThrow(() => report = RenderScaleRecovery.Recover(_root, _target),
                "a broken journal must never break the boot");

            TestContext.WriteLine(
                $"corrupt: status={report.Status}, quarantined='{Path.GetFileName(report.QuarantinedPath)}', " +
                $"writes=[{string.Join(",", _target.Writes)}], fixedPathExists={File.Exists(journal.FilePath)}");
            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Corrupt));
            Assert.That(_target.Writes, Is.Empty, "an unreadable journal means 'unknown' — nothing is guessed");
            Assert.That(asset.renderScale, Is.EqualTo(WrittenScale).Within(1e-5f));
            Assert.That(File.Exists(journal.FilePath), Is.False, "the corrupt file must leave the fixed path");
            Assert.That(Directory.GetFiles(_root, RenderScaleRecoveryJournal.FileName + ".corrupt_*").Length,
                Is.EqualTo(1), "the broken file is kept for diagnosis under .corrupt_<stamp>");

            // ... and the next boot is a clean "no journal" boot: never a loop.
            RenderScaleRecoveryReport second = RenderScaleRecovery.Recover(_root, _target);
            Assert.That(second.Status, Is.EqualTo(RenderScaleRecoveryStatus.NoJournal));
        }

        // ---- compounding cut ------------------------------------------------------------

        [Test]
        public void TwoCrashCycles_BaselineStaysAtOne_NoCompoundingShrink()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_root);
            var adoptedBaselines = new List<float>();

            for (int round = 0; round < 2; round++)
            {
                float adopted = asset.renderScale; // what the governor would adopt this session
                adoptedBaselines.Add(adopted);

                // The governor's write order: note first, asset second.
                Assert.IsTrue(journal.Save(OneEntry(0, AssetName, adopted, WrittenScale)));
                _target.WriteScale(asset, WrittenScale);
                Assert.That(asset.renderScale, Is.EqualTo(WrittenScale).Within(1e-5f));

                // Crash: no Shutdown, no in-memory restore. Only the next boot heals.
                RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_root, _target);

                Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Restored), "round " + round);
                Assert.That(asset.renderScale, Is.EqualTo(adopted).Within(1e-5f),
                    "round " + round + ": the healed value must be that session's baseline");
                Assert.That(File.Exists(journal.FilePath), Is.False, "round " + round + ": journal consumed");
            }

            CollectionAssert.AreEqual(new[] { OriginalScale, OriginalScale }, adoptedBaselines,
                "both sessions must adopt 1.0 — 0.75 then 0.5625 is exactly the compounding this task kills");
            Assert.That(_target.Writes.Contains(0.5625f), Is.False, "no compounded 0.75 -> 0.5625 step may ever happen");

            TestContext.WriteLine(
                $"compounding: adopted baselines=[{string.Join(",", adoptedBaselines)}], writes=[{string.Join(",", _target.Writes)}]");

            // The governor itself must build its ladder from the healed 1.0.
            NewHost(new RenderScaleRecoveryJournal(_logRoot));
            _host.SetEnabled(true);
            Assert.That(_host.Governor.Ladder.OriginalRenderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "after self-healing the ladder baseline is the user's 1.0");
            Pump(2.2f);
            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "first rung from a 1.0 baseline is 0.95; a dirty 0.75 baseline would give 0.7125");
        }

        // ---- degradation: journal cannot be written -------------------------------------
        //
        // Round 1 let the measure land anyway ("crash recovery is DISABLED for this
        // session"); the acceptance ruled that a FAIL — a dirty asset nobody recorded
        // becomes the next session's baseline. The contract is now "no note, no
        // write", and T1/T2/T3 are its witnesses.

        [Test]
        public void JournalWriteFailure_DisablesRenderScaleMeasure_ButOtherMeasuresLand()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            RenderScaleRecoveryJournal journal = BlockedJournal();

            NewHost(journal);
            _host.SetEnabled(true);
            Pump(15f); // deep enough to leave the whole RenderScale band

            TestContext.WriteLine(
                $"journal write failure: asset={asset.renderScale:0.00} (measure skipped), " +
                $"tier={PipelineManager.CurrentTier}, writes=[{string.Join(",", _target.Writes)}], " +
                $"journalWarnings={JournalFailureWarnings}");

            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "an asset may not be dirtied while its original value is unrecorded");
            Assert.That(_target.Writes, Is.Empty, "not a single renderScale write may happen without a journal");
            Assert.That(PipelineManager.CurrentTier, Is.EqualTo(QualityTier.Low),
                "the governor itself keeps governing — only the RenderScale measure is disabled");
            Assert.That(JournalFailureWarnings, Is.EqualTo(1),
                "journal failures warn exactly once: " + string.Join(" | ", _warnings));
        }

        /// <summary>
        /// T1 (round-2 contract 1+2): no durable note = no write. The original
        /// value only lives in memory here, so a crash would adopt the dirty value
        /// as the user's baseline — the exact defect the acceptance reported.
        /// </summary>
        [Test]
        public void JournalSaveFails_DoesNotWriteAsset()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            RenderScaleRecoveryJournal journal = BlockedJournal();

            NewHost(journal);
            _host.SetEnabled(true);
            Pump(2.2f); // the rung that would write 0.95

            TestContext.WriteLine(
                $"T1: journalExists={File.Exists(journal.FilePath)}, asset={asset.renderScale:0.00}, " +
                $"writes=[{string.Join(",", _target.Writes)}], writeAttempts={_target.Attempts}, " +
                $"rungs={_host.Governor.Level}, journalWarnings={JournalFailureWarnings}");

            Assert.That(_host.Governor.Level, Is.EqualTo(1),
                "precondition: the ladder DID step — the RenderScale landing is what got skipped");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T1: the asset must stay at its original value when the journal cannot be written");
            Assert.That(_target.Writes, Is.Empty, "T1: no renderScale write may reach the asset");
            Assert.That(_target.Attempts, Is.EqualTo(0), "T1: not even a (refused) write attempt is allowed");
            Assert.That(JournalFailureWarnings, Is.EqualTo(1),
                "T1: exactly one warning: " + string.Join(" | ", _warnings));
            Assert.That(_warnings.Any(w => w.Contains("DRS disabled")), Is.True,
                "T1: the warning must state that DRS is off for the session: " + string.Join(" | ", _warnings));
        }

        /// <summary>
        /// T2 (round-2 contract 2, the secondary damage): first Save succeeds, the
        /// next one fails. A stale lastWritten on disk would send the next boot down
        /// the G3 branch ("changed by something else") and refuse to heal — the asset
        /// would stay dirty forever. The stale entry must be invalidated AND the
        /// already-landed value rolled back.
        /// </summary>
        [Test]
        public void JournalSaveFailsAfterFirstSuccess_InvalidatesStaleEntry()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_logRoot);
            NewHost(journal);
            _host.SetEnabled(true);
            Pump(2.2f);

            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "precondition: the first rung landed together with its note");
            Assert.That(File.ReadAllText(journal.FilePath), Does.Contain("\"lastWrittenScale\": 0.95"),
                "precondition: the note on disk says 0.95");

            // Disk fills up: the atomic write can no longer even create its .tmp
            // (a directory occupies that path), which is the acceptance's
            // "first Save succeeds, later ones fail" timeline.
            Directory.CreateDirectory(journal.TempFilePath);
            Pump(2.2f); // the rung that would write 0.90

            JournalReadStatus onDisk = journal.TryRead(out List<RenderScaleJournalEntry> entries);
            bool staleEntryOnDisk = onDisk == JournalReadStatus.Ok &&
                entries.Any(e => Mathf.Abs(e.LastWrittenScale - 0.95f) < 1e-4f);

            TestContext.WriteLine(
                $"T2: staleEntry(lastWritten=0.95)={staleEntryOnDisk}, asset={asset.renderScale:0.00}, " +
                $"journalExists={File.Exists(journal.FilePath)}, writes=[{string.Join(",", _target.Writes)}], " +
                $"journalWarnings={JournalFailureWarnings}");

            Assert.That(staleEntryOnDisk, Is.False,
                "T2: a stale lastWritten=0.95 would be read as a third-party edit on the next boot (G3) and never healed");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T2: the value this session already landed is rolled back to the session baseline");
            Assert.That(_target.Writes, Does.Contain(OriginalScale),
                "T2: the rollback is a real write back to 1.0, not just a bookkeeping change");
            Assert.That(JournalFailureWarnings, Is.EqualTo(1),
                "T2: still exactly one warning: " + string.Join(" | ", _warnings));

            // Next boot: nothing stale left to trip over ⇒ no "permanently 0.95".
            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_logRoot, _target);
            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.NoJournal),
                "T2: the invalidated journal must not look like an unhealed crash");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T2: the asset must not be pinned at the dirty value after the crash");
        }

        /// <summary>
        /// T3 (round-2 contract 2): the session stops trying instead of failing on
        /// every tick. The whole ladder is walked while the journal is dead — the
        /// asset stays at the original value and the warning count stays at one.
        /// </summary>
        [Test]
        public void JournalSaveFails_GovernanceDoesNotLoop()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            RenderScaleRecoveryJournal journal = BlockedJournal();

            NewHost(journal);
            _host.SetEnabled(true);
            Pump(21f); // 42 ticks: 10 rungs, i.e. 9 further failed journal writes without the guard

            TestContext.WriteLine(
                $"T3: asset={asset.renderScale:0.00}, writes=[{string.Join(",", _target.Writes)}], " +
                $"writeAttempts={_target.Attempts}, rungs={_host.Governor.Level}/{_host.Governor.Ladder.Count}, " +
                $"tier={PipelineManager.CurrentTier}, journalWarnings={JournalFailureWarnings}");

            Assert.That(_host.Governor.Level, Is.EqualTo(_host.Governor.Ladder.Count),
                "precondition: the whole ladder was walked (many ticks, many failed journal writes)");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T3: the asset stays at its original value for the whole session");
            Assert.That(_target.Writes, Is.Empty, "T3: no renderScale write, ever, without a journal");
            Assert.That(JournalFailureWarnings, Is.EqualTo(1),
                "T3: exactly one warning per session — never one per tick: " + string.Join(" | ", _warnings));
        }

        /// <summary>
        /// T4 (round-3 legacy 1): when the note write fails AND the rollback write
        /// is refused, the asset keeps a dirty value — so the journal entry must
        /// survive, because it is the only thing that can heal it. The old code
        /// dropped the entry unconditionally and left exactly that dead end:
        /// <c>asset=0.95 journalExists=False nextBootStatus=NoJournal</c>.
        /// </summary>
        [Test]
        public void RollbackRefused_KeepsJournalEntry_AndNextBootHeals()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_logRoot);
            NewHost(journal);
            _host.SetEnabled(true);
            Pump(2.2f); // rung 1 lands 0.95 together with its note

            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "precondition: the first rung landed and is journalled");

            // Both halves of the degradation at once: the disk refuses the note (a
            // directory occupies the .tmp path) and the platform refuses the
            // rollback (read-only asset, the Player-build case).
            Directory.CreateDirectory(journal.TempFilePath);
            _target.RefuseWrites = true;
            Pump(2.2f); // rung 2 (0.90): note fails, rollback fails

            JournalReadStatus onDisk = journal.TryRead(out List<RenderScaleJournalEntry> entries);
            RenderScaleJournalEntry kept = entries != null && entries.Count == 1 ? entries[0] : null;
            int entryCount = entries == null ? -1 : entries.Count;
            float keptLastWritten = kept == null ? float.NaN : kept.LastWrittenScale;

            TestContext.WriteLine(
                $"T4: readStatus={onDisk}, entries={entryCount}, keptLastWritten={keptLastWritten:0.00}, " +
                $"asset={asset.renderScale:0.00}, journalExists={File.Exists(journal.FilePath)}, " +
                $"writes=[{string.Join(",", _target.Writes)}], journalWarnings={JournalFailureWarnings}");

            Assert.That(asset.renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "T4: precondition — the refused rollback leaves the dirty value on the asset");
            Assert.That(File.Exists(journal.FilePath), Is.True,
                "T4: a dirty asset without its note can never be healed — the entry must be kept");
            Assert.That(onDisk, Is.EqualTo(JournalReadStatus.Ok));
            Assert.That(kept, Is.Not.Null, "T4: exactly the one entry that describes the dirty asset");
            Assert.That(kept.LastWrittenScale, Is.EqualTo(asset.renderScale).Within(1e-5f),
                "T4: the kept entry must describe the value the asset actually holds, otherwise G3 rejects the heal");
            Assert.That(kept.OriginalScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T4: the user's value must still be recoverable from the entry");
            Assert.That(_warnings.Any(w => w.Contains("rollback refused") && w.Contains("journal entry kept")), Is.True,
                "T4: keeping the entry must be diagnosable: " + string.Join(" | ", _warnings));

            // Next boot, on a healthy disk with a writable asset.
            Directory.Delete(journal.TempFilePath);
            _target.RefuseWrites = false;
            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_logRoot, _target);

            TestContext.WriteLine(
                $"T4 nextBoot: status={report.Status}, restored={report.RestoredCount}, " +
                $"asset={asset.renderScale:0.00}, journalExists={File.Exists(journal.FilePath)}");

            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Restored),
                "T4: the kept entry must heal — not be read as a third-party edit (G3)");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T4: no permanent dirty value: the next boot restores the user's 1.0");
            Assert.That(File.Exists(journal.FilePath), Is.False, "T4: the heal consumes the journal");
        }

        /// <summary>
        /// T5 (round-3 legacy 2): the journal file protects EVERY asset the
        /// governor dirtied, so a failed re-save after one asset was invalidated
        /// must leave the file alone. Deleting it (old behaviour) would strip the
        /// protection from the other assets — a state nothing can recover from.
        /// </summary>
        [Test]
        public void JournalResaveFails_DoesNotWipeOtherAssetsEntries()
        {
            UniversalRenderPipelineAsset first = _target.Add("URP_High", OriginalScale);
            UniversalRenderPipelineAsset second = _target.Add("URP_Medium", OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_logRoot);
            NewHost(journal);
            _host.SetEnabled(true);
            Pump(2.2f); // rung 1 -> URP_High 0.95, journalled

            Assert.That(first.renderScale, Is.EqualTo(0.95f).Within(1e-4f),
                "precondition: the first asset is dirty and journalled");

            // Points the fake at the asset the pipeline swaps in between rungs
            // (PipelineManager does this on a tier change).
            _target.ActiveIndex = 1;
            Pump(2.2f); // rung 2 (0.90) -> URP_Medium, a second entry in the same file

            Assert.That(second.renderScale, Is.EqualTo(0.90f).Within(1e-4f),
                "precondition: the second asset is dirty and journalled");

            JournalReadStatus before = journal.TryRead(out List<RenderScaleJournalEntry> beforeEntries);
            Assert.That(before, Is.EqualTo(JournalReadStatus.Ok));
            Assert.That(beforeEntries.Count, Is.EqualTo(2),
                "precondition: the one file protects both assets");
            string fileBefore = File.ReadAllText(journal.FilePath);

            // Now the re-save of the remaining entries fails — the very failure
            // that used to wipe the whole file: rung 3 (0.85) cannot be journalled
            // on URP_Medium, whose rollback DOES land, so the invalidation path runs.
            Directory.CreateDirectory(journal.TempFilePath);
            Pump(2.2f);

            bool fileUnchanged = File.Exists(journal.FilePath) && File.ReadAllText(journal.FilePath) == fileBefore;
            TestContext.WriteLine(
                $"T5: journalExists={File.Exists(journal.FilePath)}, fileUnchanged={fileUnchanged}, " +
                $"assetHigh={first.renderScale:0.00}, assetMedium={second.renderScale:0.00}, " +
                $"writes=[{string.Join(",", _target.Writes)}], journalWarnings={JournalFailureWarnings}");

            Assert.That(second.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T5: precondition — the rollback landed, so the invalidation path really ran");
            Assert.That(File.Exists(journal.FilePath), Is.True,
                "T5: a failed re-save must not delete the file — URP_High is still dirty and depends on it");

            JournalReadStatus after = journal.TryRead(out List<RenderScaleJournalEntry> afterEntries);
            Assert.That(after, Is.EqualTo(JournalReadStatus.Ok), "T5: the file must still be a valid journal");
            Assert.That(afterEntries.Any(e => e.AssetName == "URP_High" && Mathf.Abs(e.LastWrittenScale - 0.95f) < 1e-4f),
                Is.True, "T5: the other asset's entry must survive: " +
                string.Join(" | ", afterEntries.ConvertAll(e => e.AssetName + "@" + e.LastWrittenScale.ToString("0.00"))));
            Assert.That(File.ReadAllText(journal.FilePath), Is.EqualTo(fileBefore),
                "T5: the file is left exactly as it was — nothing is rewritten or truncated");

            // ... and it is worth keeping: the dirty asset heals, while the entry
            // over-describing the rolled-back asset is refused by G3 (a SAFE
            // failure, per the doctrine behind this fix).
            Directory.Delete(journal.TempFilePath);
            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_logRoot, _target);

            TestContext.WriteLine(
                $"T5 nextBoot: status={report.Status}, restored={report.RestoredCount}, abandoned={report.AbandonedCount}, " +
                $"assetHigh={first.renderScale:0.00}, assetMedium={second.renderScale:0.00}");

            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Partial),
                "T5: one entry heals, the stale one is refused");
            Assert.That(first.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T5: the surviving entry really heals the dirty asset on the next boot");
            Assert.That(second.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "T5: the stale entry is rejected, never written");
        }

        [Test]
        public void AssetWriteRefusal_WarnsOnce_AndGovernanceKeepsRunning()
        {
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            _target.RefuseWrites = true; // read-only asset: the Player-build case (design point 7)
            var journal = new RenderScaleRecoveryJournal(_logRoot);

            NewHost(journal);
            _host.SetEnabled(true);
            Assert.DoesNotThrow(() => Pump(6.2f), "a refused asset write must never escape as an exception");

            TestContext.WriteLine(
                $"write refusal: attempts={_target.Attempts}, asset={asset.renderScale:0.00}, " +
                $"writeWarnings={_warnings.Count(w => w.Contains("cannot write renderScale"))}");

            Assert.That(_target.Attempts, Is.GreaterThan(1), "precondition: the governor tried several rungs");
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f), "nothing landed on the read-only asset");
            Assert.That(_warnings.Count(w => w.Contains("cannot write renderScale")), Is.EqualTo(1),
                "one warning per session — never per frame: " + string.Join(" | ", _warnings));
        }

        // ---- journal resolution ---------------------------------------------------------

        [Test]
        public void JournalEntryForVanishedAsset_IsAbandoned_NotThrown()
        {
            _target.Add(AssetName, OriginalScale);
            _target.ActiveIndex = 0;
            var journal = new RenderScaleRecoveryJournal(_root);
            Assert.IsTrue(journal.Save(OneEntry(0, "URP_Deleted", OriginalScale, WrittenScale)));

            RenderScaleRecoveryReport report = null;
            Assert.DoesNotThrow(() => report = RenderScaleRecovery.Recover(_root, _target));

            TestContext.WriteLine(
                $"vanished: status={report.Status}, writes=[{string.Join(",", _target.Writes)}], " +
                $"journalExists={File.Exists(journal.FilePath)}");

            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Abandoned));
            Assert.That(_target.Writes, Is.Empty);
            Assert.That(File.Exists(journal.FilePath), Is.False);
            Assert.That(_warnings.Any(w => w.Contains("is gone")), Is.True);
        }

        [Test]
        public void JournalEntry_StillResolves_WhenQualityLevelOrderChanged()
        {
            // Journal says level 0, but the asset now lives at level 1: the name
            // is the stable key (quality levels can be re-ordered between builds).
            _target.Add("URP_Low", OriginalScale);
            UniversalRenderPipelineAsset asset = _target.Add(AssetName, WrittenScale);
            _target.ActiveIndex = 1;
            var journal = new RenderScaleRecoveryJournal(_root);
            Assert.IsTrue(journal.Save(OneEntry(0, AssetName, OriginalScale, WrittenScale)));

            RenderScaleRecoveryReport report = RenderScaleRecovery.Recover(_root, _target);

            TestContext.WriteLine(
                $"level reorder: journal level=0, asset now at level=1 -> status={report.Status}, asset={asset.renderScale:0.00}");

            Assert.That(report.Status, Is.EqualTo(RenderScaleRecoveryStatus.Restored));
            Assert.That(asset.renderScale, Is.EqualTo(OriginalScale).Within(1e-5f),
                "the asset must be found by name even after the level index changed");
        }

        // ---- helpers ---------------------------------------------------------------------

        private static RenderScaleJournalEntry[] OneEntry(int level, string name, float original, float lastWritten)
        {
            return new[]
            {
                new RenderScaleJournalEntry
                {
                    QualityLevel = level,
                    AssetName = name,
                    OriginalScale = original,
                    LastWrittenScale = lastWritten,
                },
            };
        }

        private void NewHost(RenderScaleRecoveryJournal journal)
        {
            var go = new GameObject("~RenderScaleRecoveryHostTest");
            _host = go.AddComponent<FrameGovernorHost>();
            _host.Initialize(_telemetry, _target, journal);
        }

        /// <summary>
        /// A journal root that can never be created: a plain FILE occupies the path
        /// the journal needs as a directory. That is the real "unwritable location"
        /// failure (no mock of the journal is involved).
        /// </summary>
        private RenderScaleRecoveryJournal BlockedJournal()
        {
            string blockedRoot = Path.Combine(_logRoot, "blocked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_logRoot);
            File.WriteAllText(blockedRoot, "not a directory");
            return new RenderScaleRecoveryJournal(blockedRoot);
        }

        private int JournalFailureWarnings => _warnings.Count(w => w.Contains(JournalFailureMarker));

        private void Pump(float seconds, float dt = 0.5f)
        {
            _telemetry.GpuFrameMs = 0f;
            _telemetry.CpuFrameMs = 30f; // > TargetMs(60fps) * 1.1
            int ticks = Mathf.CeilToInt(seconds / dt);
            for (int i = 0; i < ticks; i++)
            {
                _host.Tick(dt);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
                // temp dir: best effort
            }
            catch (UnauthorizedAccessException)
            {
                // temp dir: best effort
            }
        }

        private sealed class FakeTelemetry : IFrameTelemetry
        {
            public float GpuFrameMs { get; set; }
            public float CpuFrameMs { get; set; }
        }

        /// <summary>
        /// Stand-in for the URP assets: throwaway CreateInstance objects, never
        /// the project's <c>URP_*.asset</c> files. <see cref="WriteScale"/> is
        /// also the ordering probe — it captures the journal content at the
        /// moment of the first write.
        /// </summary>
        private sealed class FakeTarget : IRenderScaleTarget, IDisposable
        {
            private readonly List<UniversalRenderPipelineAsset> _assets = new List<UniversalRenderPipelineAsset>();

            public readonly List<float> Writes = new List<float>();

            public int ActiveIndex { get; set; }

            /// <summary>When true, <see cref="WriteScale"/> throws — the read-only-asset case in a Player build.</summary>
            public bool RefuseWrites { get; set; }

            /// <summary>Number of WriteScale calls (counted even when the write is refused).</summary>
            public int Attempts { get; private set; }

            /// <summary>Returns the journal text at write time, or null when the file is missing.</summary>
            public Func<string> JournalProbe { get; set; }

            public string JournalAtFirstWrite { get; private set; }

            public UniversalRenderPipelineAsset Add(string name, float scale)
            {
                var asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                asset.name = name;
                asset.renderScale = scale;
                _assets.Add(asset);
                return asset;
            }

            public RenderPipelineAsset ActiveAsset => _assets[ActiveIndex];

            public int QualityLevelCount => _assets.Count;

            public RenderPipelineAsset GetAssetAt(int qualityLevel)
            {
                return qualityLevel >= 0 && qualityLevel < _assets.Count ? _assets[qualityLevel] : null;
            }

            public int IndexOf(RenderPipelineAsset asset)
            {
                for (int i = 0; i < _assets.Count; i++)
                {
                    if (ReferenceEquals(_assets[i], asset))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void WriteScale(RenderPipelineAsset asset, float scale)
            {
                if (Writes.Count == 0 && JournalProbe != null)
                {
                    JournalAtFirstWrite = JournalProbe();
                }

                Attempts++;
                if (RefuseWrites)
                {
                    throw new InvalidOperationException("renderScale is read-only on this platform");
                }

                if (asset is UniversalRenderPipelineAsset urp)
                {
                    urp.renderScale = scale;
                }

                Writes.Add(scale);
            }

            public void Dispose()
            {
                foreach (UniversalRenderPipelineAsset asset in _assets)
                {
                    if (asset != null)
                    {
                        UnityEngine.Object.DestroyImmediate(asset);
                    }
                }

                _assets.Clear();
            }
        }
    }
}
