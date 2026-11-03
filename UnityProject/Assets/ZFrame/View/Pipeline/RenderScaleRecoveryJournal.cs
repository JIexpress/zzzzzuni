using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// One record of the crash-recovery journal (TASK-A-R9-02 design point 2):
    /// which QualitySettings level the asset is bound to, the asset name, the
    /// user's original renderScale and the value the governor last wrote.
    /// The pair (level, name) is what <see cref="RenderScaleRecovery"/> uses to
    /// find the asset again on the next boot WITHOUT AssetDatabase — a Player
    /// build has no asset database.
    /// </summary>
    public sealed class RenderScaleJournalEntry
    {
        /// <summary>QualitySettings level index the asset was bound to; -1 when it was not bound to any level.</summary>
        public int QualityLevel { get; set; } = -1;

        /// <summary>Asset name as seen at write time (secondary match key, survives level re-ordering).</summary>
        public string AssetName { get; set; } = string.Empty;

        /// <summary>The user's original renderScale — the value a crash recovery must restore.</summary>
        public float OriginalScale { get; set; }

        /// <summary>The value the governor last wrote to disk; recovery only overwrites when the asset still holds exactly this.</summary>
        public float LastWrittenScale { get; set; }
    }

    /// <summary>Result of reading the journal file.</summary>
    public enum JournalReadStatus
    {
        /// <summary>No journal on disk — the previous session ended cleanly (or never governed).</summary>
        Missing = 0,

        /// <summary>Journal parsed and validated.</summary>
        Ok = 1,

        /// <summary>Journal exists but is unreadable/invalid — caller must quarantine and continue.</summary>
        Corrupt = 2,
    }

    /// <summary>
    /// Persistence of the "the governor is about to dirty a URP asset" note
    /// (TASK-A-R9-02).
    ///
    /// Why a file at all: <c>FrameGovernorHost</c> keeps the original
    /// renderScale of every asset it touches in memory only. If the process is
    /// killed between "renderScale := 0.75" and <c>Shutdown()</c>, the original
    /// value dies with the process and the NEXT session adopts 0.75 as the user
    /// baseline — the ladder then compounds downward on every crash. This
    /// journal is the durable copy of that one fact.
    ///
    /// Layout: <c>{ "version": 1, "entries": [ { qualityLevel, assetName,
    /// originalScale, lastWrittenScale } ] }</c> at a FIXED file name under
    /// <see cref="Application.persistentDataPath"/> (or an injected root — tests
    /// must inject a temp directory).
    ///
    /// Writes are atomic in the same way as <c>Data/Save/SaveSystem</c>: the
    /// payload goes to <c>.tmp</c>, is flushed to the physical disk, then
    /// renamed onto the fixed path. A power cut therefore leaves either the
    /// previous journal or the new one — never a half-written file (which the
    /// recovery would have to treat as corrupt and refuse to heal from).
    /// </summary>
    public sealed class RenderScaleRecoveryJournal
    {
        /// <summary>Fixed file name under the root — one journal per install, not per asset.</summary>
        public const string FileName = "governor_recovery.json";

        private const string LogChannel = "Pipeline";
        private const string TempSuffix = ".tmp";
        private const string CorruptSuffix = ".corrupt_";
        private const string VersionField = "version";
        private const string EntriesField = "entries";
        private const int CurrentVersion = 1;

        private readonly string _rootDirectory;

        /// <summary>Creates the journal; <paramref name="rootDirectory"/> null/empty = real persistentDataPath.</summary>
        public RenderScaleRecoveryJournal(string rootDirectory = null)
        {
            _rootDirectory = string.IsNullOrEmpty(rootDirectory)
                ? Application.persistentDataPath
                : rootDirectory;
        }

        /// <summary>Directory the journal lives in.</summary>
        public string RootDirectory => _rootDirectory;

        /// <summary>Full path of the journal file.</summary>
        public string FilePath => Path.Combine(_rootDirectory, FileName);

        /// <summary>Transient file the atomic write goes through first (also the seam tests use to simulate a full disk).</summary>
        public string TempFilePath => FilePath + TempSuffix;

        /// <summary>True while a journal is on disk (i.e. a session did not close cleanly).</summary>
        public bool Exists => File.Exists(FilePath);

        /// <summary>
        /// Atomically replaces the journal with <paramref name="entries"/>.
        /// Returns false when the file system refuses the write. That return
        /// value is a verdict, not a note: the caller must NOT dirty an asset
        /// whose original value could not be recorded (TASK-A-R9-02 round 2).
        /// </summary>
        public bool Save(IList<RenderScaleJournalEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            string json = BuildJson(entries);
            string tmp = TempFilePath;

            try
            {
                Directory.CreateDirectory(_rootDirectory);

                // .tmp fully written and flushed, then renamed onto the fixed
                // path: a crash can only leave either journal behind.
                WriteDurable(tmp, json);
                if (File.Exists(FilePath))
                {
                    File.Replace(tmp, FilePath, null);
                }
                else
                {
                    File.Move(tmp, FilePath);
                }

                return true;
            }
            // No warning here: the caller owns the "once per session" message —
            // a governor can save this file several times per session and must
            // never spam. See FrameGovernorHost.PersistJournalEntry.
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                TryDelete(tmp);
                return false;
            }
        }

        /// <summary>Reads and validates the journal. Never throws — a broken file reports <see cref="JournalReadStatus.Corrupt"/>.</summary>
        public JournalReadStatus TryRead(out List<RenderScaleJournalEntry> entries)
        {
            entries = null;
            string path = FilePath;
            if (!File.Exists(path))
            {
                return JournalReadStatus.Missing;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ZLog.Warn(LogChannel, $"governor recovery journal: cannot read '{path}': {e.Message}");
                return JournalReadStatus.Corrupt;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return JournalReadStatus.Corrupt;
            }

            JToken versionToken = root[VersionField];
            if (versionToken == null || versionToken.Type != JTokenType.Integer)
            {
                return JournalReadStatus.Corrupt;
            }

            int version = versionToken.Value<int>();
            if (version != CurrentVersion)
            {
                // A journal from a future/unknown format cannot be interpreted;
                // treating it as "nothing to heal" is safer than guessing.
                return JournalReadStatus.Corrupt;
            }

            if (!(root[EntriesField] is JArray array))
            {
                return JournalReadStatus.Corrupt;
            }

            var parsed = new List<RenderScaleJournalEntry>(array.Count);
            foreach (JToken item in array)
            {
                if (!(item is JObject entry))
                {
                    return JournalReadStatus.Corrupt;
                }

                JToken name = entry["assetName"];
                JToken original = entry["originalScale"];
                JToken lastWritten = entry["lastWrittenScale"];
                JToken level = entry["qualityLevel"];
                if (name == null || name.Type != JTokenType.String || string.IsNullOrEmpty(name.Value<string>()) ||
                    original == null || original.Type != JTokenType.Float && original.Type != JTokenType.Integer ||
                    lastWritten == null || lastWritten.Type != JTokenType.Float && lastWritten.Type != JTokenType.Integer ||
                    level == null || level.Type != JTokenType.Integer)
                {
                    return JournalReadStatus.Corrupt;
                }

                float originalScale = original.Value<float>();
                float lastWrittenScale = lastWritten.Value<float>();
                if (originalScale <= 0f || lastWrittenScale <= 0f)
                {
                    return JournalReadStatus.Corrupt;
                }

                parsed.Add(new RenderScaleJournalEntry
                {
                    QualityLevel = level.Value<int>(),
                    AssetName = name.Value<string>(),
                    OriginalScale = originalScale,
                    LastWrittenScale = lastWrittenScale,
                });
            }

            entries = parsed;
            return JournalReadStatus.Ok;
        }

        /// <summary>
        /// Moves an uninterpretable journal to <c>&lt;path&gt;.corrupt_&lt;stamp&gt;</c>
        /// so it is kept for diagnosis but never re-read (briefing design point 6).
        /// Returns the new path, or the original path when the move failed.
        /// </summary>
        public string Quarantine()
        {
            string path = FilePath;
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string target = path + CorruptSuffix + stamp;
            int attempt = 0;
            while (File.Exists(target))
            {
                attempt++;
                target = $"{path}{CorruptSuffix}{stamp}_{attempt.ToString(CultureInfo.InvariantCulture)}";
            }

            try
            {
                File.Move(path, target);
                return target;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ZLog.Warn(LogChannel, $"governor recovery journal: cannot quarantine '{path}': {e.Message}");
                return path;
            }
        }

        /// <summary>Removes the journal (clean shutdown / after a completed recovery). Best effort, never throws.</summary>
        public void Delete()
        {
            TryDelete(FilePath);
            TryDelete(FilePath + TempSuffix);
        }

        private static string BuildJson(IList<RenderScaleJournalEntry> entries)
        {
            var array = new JArray();
            foreach (RenderScaleJournalEntry entry in entries)
            {
                array.Add(new JObject
                {
                    ["qualityLevel"] = entry.QualityLevel,
                    ["assetName"] = entry.AssetName ?? string.Empty,
                    ["originalScale"] = entry.OriginalScale,
                    ["lastWrittenScale"] = entry.LastWrittenScale,
                });
            }

            var root = new JObject
            {
                [VersionField] = CurrentVersion,
                [EntriesField] = array,
            };

            return root.ToString(Formatting.Indented);
        }

        private static void WriteDurable(string path, string content)
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            byte[] bytes = encoding.GetBytes(content);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ZLog.Warn(LogChannel, $"governor recovery journal: cannot delete '{path}': {e.Message}");
            }
        }
    }
}
