using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// The only way <see cref="FrameGovernorHost"/> and the recovery touch a
    /// renderScale (TASK-A-R9-02 hard constraint: tests must never modify the
    /// real <c>URP_*.asset</c> files). Production code uses
    /// <see cref="QualitySettingsRenderScaleTarget"/>; EditMode tests inject a
    /// fake whose assets are <c>CreateInstance&lt;UniversalRenderPipelineAsset&gt;()</c>
    /// throwaways and whose <see cref="WriteScale"/> can record the call order.
    /// </summary>
    public interface IRenderScaleTarget
    {
        /// <summary>The asset behind the active pipeline; null when the active pipeline is not URP.</summary>
        RenderPipelineAsset ActiveAsset { get; }

        /// <summary>Number of QualitySettings levels (assets are enumerated through these, never AssetDatabase).</summary>
        int QualityLevelCount { get; }

        /// <summary>Asset bound to a QualitySettings level, or null.</summary>
        RenderPipelineAsset GetAssetAt(int qualityLevel);

        /// <summary>QualitySettings level the asset is bound to, or -1 when unbound.</summary>
        int IndexOf(RenderPipelineAsset asset);

        /// <summary>Lands the value on the asset. May throw in a Player build (read-only asset) — callers guard.</summary>
        void WriteScale(RenderPipelineAsset asset, float scale);
    }

    /// <summary>
    /// Production <see cref="IRenderScaleTarget"/>: the active pipeline asset
    /// plus the per-quality-level bindings, read through
    /// <see cref="QualitySettings.GetRenderPipelineAssetAt"/> — deliberately no
    /// <c>AssetDatabase</c>, so the journal can be resolved in a Player build.
    /// </summary>
    public sealed class QualitySettingsRenderScaleTarget : IRenderScaleTarget
    {
        /// <summary>Shared stateless instance (the type holds no state).</summary>
        public static readonly QualitySettingsRenderScaleTarget Default = new QualitySettingsRenderScaleTarget();

        /// <inheritdoc />
        public RenderPipelineAsset ActiveAsset => GraphicsSettings.currentRenderPipeline;

        /// <inheritdoc />
        public int QualityLevelCount => QualitySettings.names != null ? QualitySettings.names.Length : 0;

        /// <inheritdoc />
        public RenderPipelineAsset GetAssetAt(int qualityLevel)
        {
            return qualityLevel >= 0 && qualityLevel < QualityLevelCount
                ? QualitySettings.GetRenderPipelineAssetAt(qualityLevel)
                : null;
        }

        /// <inheritdoc />
        public int IndexOf(RenderPipelineAsset asset)
        {
            if (asset == null)
            {
                return -1;
            }

            int count = QualityLevelCount;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(GetAssetAt(i), asset))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <inheritdoc />
        public void WriteScale(RenderPipelineAsset asset, float scale)
        {
            if (asset is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = scale;
            }
        }
    }

    /// <summary>How a <see cref="RenderScaleRecovery.Recover"/> call ended.</summary>
    public enum RenderScaleRecoveryStatus
    {
        /// <summary>No journal — nothing to heal (normal shutdown).</summary>
        NoJournal = 0,

        /// <summary>Every journal entry was restored to its original value.</summary>
        Restored = 1,

        /// <summary>Nothing was written: the disk value no longer matched the last written value (G3), or the asset vanished.</summary>
        Abandoned = 2,

        /// <summary>The journal could not be parsed; it was quarantined and the boot continued.</summary>
        Corrupt = 3,

        /// <summary>Some entries restored, others abandoned.</summary>
        Partial = 4,
    }

    /// <summary>Diagnostics of one recovery pass (log line + assertions in tests).</summary>
    public sealed class RenderScaleRecoveryReport
    {
        /// <summary>Overall outcome.</summary>
        public RenderScaleRecoveryStatus Status { get; internal set; }

        /// <summary>Entries whose original value was written back.</summary>
        public int RestoredCount { get; internal set; }

        /// <summary>Entries deliberately skipped (value changed by someone else / asset gone).</summary>
        public int AbandonedCount { get; internal set; }

        /// <summary>Entries the platform refused to write (e.g. read-only asset in a Player build).</summary>
        public int FailedCount { get; internal set; }

        /// <summary>Journal file that was quarantined, when <see cref="Status"/> is Corrupt.</summary>
        public string QuarantinedPath { get; internal set; }

        /// <summary>Human-readable summary (also used as the log message).</summary>
        public string Detail { get; internal set; }

        /// <summary>True when at least one value was written back.</summary>
        public bool AnyRestored => RestoredCount > 0;
    }

    /// <summary>
    /// Crash self-healing for the DRS renderScale (TASK-A-R9-02).
    ///
    /// Sequence, and why the order is what it is:
    /// <code>
    ///  governor wants renderScale 0.75
    ///    → RenderScaleRecoveryJournal.Save({original 1.0, lastWritten 0.75})   [disk first]
    ///    → asset.renderScale = 0.75                                            [disk second]
    ///  ... process killed here ...
    ///  next boot, BEFORE any setting is applied:
    ///    journal exists → current 0.75 == lastWritten 0.75 → restore 1.0 → delete journal
    ///    journal exists → current 0.90 != lastWritten 0.75 → DO NOT TOUCH → delete journal + warn
    /// </code>
    ///
    /// The "write the note before the value" order is the whole point: the other
    /// order would leave a dirty asset with no record of what it used to be.
    ///
    /// Boot ordering (briefing design point 4): this hook is
    /// <see cref="RuntimeInitializeLoadType.BeforeSceneLoad"/>, while
    /// <c>SettingsBootApplier</c> — the only caller of
    /// <c>SettingsModel.Load</c> at startup — is <c>AfterSceneLoad</c>. Unity
    /// runs EVERY BeforeSceneLoad callback before the first AfterSceneLoad one,
    /// so the asset is healed before the governor captures its baseline. Fixing
    /// the value afterwards would be pointless: the ladder would already have
    /// been built from the dirty scale.
    ///
    /// Ordering against <see cref="FrameGovernorHost"/>'s own BeforeSceneLoad
    /// bootstrap is irrelevant: that path only builds a disabled placeholder
    /// core and never writes an asset; the real baseline is captured in
    /// <c>SetEnabled</c>, which happens in the AfterSceneLoad applier.
    /// </summary>
    public static class RenderScaleRecovery
    {
        /// <summary>Log channel shared with the rest of the pipeline layer.</summary>
        public const string LogChannel = "Pipeline";

        /// <summary>Comparison tolerance for "the disk value is still the one we wrote".</summary>
        private const float ScaleEpsilon = 0.0001f;

        /// <summary>
        /// Startup self-heal. Runs on every launch; a clean previous shutdown
        /// leaves no journal, so the normal path is a single File.Exists check.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            Recover();
        }

        /// <summary>
        /// Heals the renderScale of every journalled asset when — and only when
        /// — the asset still holds the value the governor last wrote. Always
        /// consumes the journal, so a given crash can never be replayed twice.
        /// <paramref name="rootDirectory"/> null/empty = real persistentDataPath;
        /// tests MUST inject a temp directory. Never throws.
        /// </summary>
        public static RenderScaleRecoveryReport Recover(string rootDirectory = null, IRenderScaleTarget target = null)
        {
            var journal = new RenderScaleRecoveryJournal(rootDirectory);
            var report = new RenderScaleRecoveryReport();
            IRenderScaleTarget lookup = target ?? QualitySettingsRenderScaleTarget.Default;

            JournalReadStatus read = journal.TryRead(out List<RenderScaleJournalEntry> entries);
            if (read == JournalReadStatus.Missing)
            {
                report.Status = RenderScaleRecoveryStatus.NoJournal;
                report.Detail = "no recovery journal — previous session ended cleanly";
                return report;
            }

            if (read == JournalReadStatus.Corrupt)
            {
                // Briefing design point 6: a broken journal must not break the
                // boot. Quarantine it (kept for diagnosis) and carry on with the
                // dirty-but-unknown asset — nothing can be safely restored.
                string quarantined = journal.Quarantine();
                report.Status = RenderScaleRecoveryStatus.Corrupt;
                report.QuarantinedPath = quarantined;
                report.Detail =
                    $"recovery journal '{RenderScaleRecoveryJournal.FileName}' is unreadable; quarantined to '{quarantined}', renderScale left untouched";
                ZLog.Warn(LogChannel, "governor recovery: " + report.Detail);
                return report;
            }

            int restored = 0;
            int abandoned = 0;
            int failed = 0;
            var details = new List<string>();

            foreach (RenderScaleJournalEntry entry in entries)
            {
                RenderPipelineAsset asset = Resolve(lookup, entry);
                if (!(asset is UniversalRenderPipelineAsset urp))
                {
                    abandoned++;
                    details.Add($"'{entry.AssetName}' (level {entry.QualityLevel}) not found");
                    ZLog.Warn(LogChannel,
                        $"governor recovery: asset '{entry.AssetName}' from quality level {entry.QualityLevel} is gone — " +
                        "nothing restored for it (was renderScale " + Format(entry.LastWrittenScale) + ").");
                    continue;
                }

                float current = urp.renderScale;
                if (Mathf.Abs(current - entry.LastWrittenScale) > ScaleEpsilon)
                {
                    // G3: somebody else changed the value (hand edit, asset
                    // re-import) — restoring would clobber a legitimate change,
                    // so only warn and drop the journal entry.
                    abandoned++;
                    details.Add($"'{entry.AssetName}' kept at {Format(current)} (journal expected {Format(entry.LastWrittenScale)})");
                    ZLog.Warn(LogChannel,
                        $"governor recovery: '{urp.name}' is {Format(current)} but the journal says the governor left " +
                        $"{Format(entry.LastWrittenScale)} — value was changed by something else, NOT restoring " +
                        $"{Format(entry.OriginalScale)}.");
                    continue;
                }

                try
                {
                    lookup.WriteScale(urp, entry.OriginalScale);
                    restored++;
                    details.Add($"'{entry.AssetName}' {Format(current)} -> {Format(entry.OriginalScale)}");
                    ZLog.Warn(LogChannel,
                        $"governor recovery: previous session did not shut down cleanly — renderScale of '{urp.name}' " +
                        $"restored {Format(current)} -> {Format(entry.OriginalScale)} (quality level {entry.QualityLevel}).");
                }
                catch (Exception e)
                {
                    // Player builds may hand out a read-only asset: one warning,
                    // no retry loop, boot continues (design point 7).
                    failed++;
                    details.Add($"'{entry.AssetName}' write refused: {e.Message}");
                    ZLog.Warn(LogChannel,
                        $"governor recovery: cannot write renderScale of '{urp.name}' ({e.GetType().Name}: {e.Message}) — " +
                        "the asset is probably read-only on this platform; value left as-is.");
                }
            }

            // All three paths (restored / abandoned / unreadable) consume the
            // journal: recovery must never run twice for the same crash.
            journal.Delete();

            if (restored > 0 && abandoned == 0 && failed == 0)
            {
                report.Status = RenderScaleRecoveryStatus.Restored;
            }
            else if (restored > 0)
            {
                report.Status = RenderScaleRecoveryStatus.Partial;
            }
            else
            {
                report.Status = RenderScaleRecoveryStatus.Abandoned;
            }

            report.RestoredCount = restored;
            report.AbandonedCount = abandoned;
            report.FailedCount = failed;
            report.Detail = details.Count == 0 ? "journal was empty" : string.Join("; ", details);
            return report;
        }

        /// <summary>
        /// Finds the asset for a journal entry: the recorded level first (exact
        /// name match required), then a name scan over all levels — quality
        /// levels can be re-ordered between builds without invalidating the note.
        /// </summary>
        private static RenderPipelineAsset Resolve(IRenderScaleTarget lookup, RenderScaleJournalEntry entry)
        {
            RenderPipelineAsset atLevel = lookup.GetAssetAt(entry.QualityLevel);
            if (atLevel != null && string.Equals(atLevel.name, entry.AssetName, StringComparison.Ordinal))
            {
                return atLevel;
            }

            int count = lookup.QualityLevelCount;
            for (int i = 0; i < count; i++)
            {
                RenderPipelineAsset candidate = lookup.GetAssetAt(i);
                if (candidate != null && string.Equals(candidate.name, entry.AssetName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string Format(float scale)
        {
            return scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
