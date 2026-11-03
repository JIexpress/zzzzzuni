using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// Multi-slot save system (task brief A-M1-03):
    /// <list type="bullet">
    /// <item>Atomic writes — the payload goes to <c>.tmp</c> first, then the
    /// old main file rolls into <c>.bak1</c> (previous <c>.bak1</c> rolls into
    /// <c>.bak2</c>), and finally <c>.tmp</c> is renamed onto the main path.
    /// Two generations of automatic backups are kept.</item>
    /// <item>Corruption containment — an unreadable/invalid/outdated-major
    /// file is renamed to <c>.corrupt_yyyyMMddHHmmss</c> (scene preserved) and
    /// a <see cref="SaveCorruptEvent"/> fires on the <see cref="EventBus"/>.
    /// The failing candidate is NOT returned as the result: the very same
    /// call keeps going through the remaining candidates
    /// (main → <c>.bak1</c> → <c>.bak2</c>) and returns the first one that
    /// reads, migrates and restores — null only when all three are
    /// unavailable. Recovery deliberately does not depend on the caller
    /// retrying.</item>
    /// <item>Version migration — a contiguous chain of <see cref="IMigration"/>
    /// steps upgrades old saves to <see cref="CurrentVersion"/>; unknown newer
    /// versions and migration failures go through the corrupt-save path.</item>
    /// <item>Injectable root — the constructor takes the save directory;
    /// tests must inject a temp directory and never touch the real
    /// persistentDataPath.</item>
    /// </list>
    /// File layout: <c>{ "version": n, "meta": {…}, "modules": { key: {…} } }</c>.
    /// </summary>
    public sealed class SaveSystem : ISaveSystem
    {
        /// <summary>Number of player-facing save slots.</summary>
        public const int DefaultSlotCount = 3;

        /// <summary>
        /// Current save format version. Bump this and register a matching
        /// <see cref="IMigration"/> (FromVersion = old value) whenever the
        /// save layout changes incompatibly.
        /// </summary>
        public const int CurrentVersion = 2;

        private const string LogChannel = "Save";
        private const string SlotFilePrefix = "slot_";
        private const string SlotFileExtension = ".json";
        private const string TempSuffix = ".tmp";
        private const string Backup1Suffix = ".bak1";
        private const string Backup2Suffix = ".bak2";
        private const string CorruptSuffix = ".corrupt_";

        private readonly string _rootDirectory;
        private readonly Dictionary<string, ISaveable> _saveables = new Dictionary<string, ISaveable>();
        private readonly Dictionary<int, IMigration> _migrations = new Dictionary<int, IMigration>();

        /// <summary>
        /// Serialises writes: two concurrent <see cref="SaveToSlot"/> calls for the
        /// same slot would otherwise interleave on the fixed <c>.tmp</c> path and
        /// throw mid-roll (leaving the slot without a main file).
        /// </summary>
        private readonly object _writeLock = new object();

        /// <summary>Playtime (seconds) written into the meta header on the next save.</summary>
        public double CurrentPlaytimeSeconds { get; set; }

        /// <summary>Display name written into the meta header on the next save (never stored as null).</summary>
        public string CurrentDisplayName { get; set; } = string.Empty;

        /// <summary>Slot index <see cref="AutoSave"/> writes to. Out-of-range disables auto-save.</summary>
        public int AutoSaveSlot { get; set; } = 0;

        /// <inheritdoc />
        public int SlotCount
        {
            get { return DefaultSlotCount; }
        }

        /// <summary>
        /// Creates the save system. <paramref name="rootDirectory"/> is the
        /// directory holding <c>slot_n.json</c> files; when null/empty the
        /// default <c>Application.persistentDataPath/saves</c> is used.
        /// Tests MUST inject their own temp directory.
        /// </summary>
        public SaveSystem(string rootDirectory = null)
        {
            _rootDirectory = string.IsNullOrEmpty(rootDirectory)
                ? Path.Combine(Application.persistentDataPath, "saves")
                : rootDirectory;
        }

        /// <summary>Full path of a slot's main save file.</summary>
        public string SlotPath(int slot)
        {
            return Path.Combine(_rootDirectory, SlotFilePrefix + slot.ToString(CultureInfo.InvariantCulture) + SlotFileExtension);
        }

        /// <inheritdoc />
        public void Register(ISaveable saveable)
        {
            if (saveable == null)
            {
                throw new ArgumentNullException(nameof(saveable));
            }

            if (string.IsNullOrEmpty(saveable.SaveKey))
            {
                throw new ArgumentException("SaveSystem: ISaveable.SaveKey must not be null or empty.", nameof(saveable));
            }

            if (_saveables.ContainsKey(saveable.SaveKey))
            {
                throw new InvalidOperationException(
                    $"SaveSystem: a module with SaveKey '{saveable.SaveKey}' is already registered.");
            }

            _saveables.Add(saveable.SaveKey, saveable);
        }

        /// <inheritdoc />
        public bool Unregister(string saveKey)
        {
            return _saveables.Remove(saveKey);
        }

        /// <inheritdoc />
        public void RegisterMigration(IMigration migration)
        {
            if (migration == null)
            {
                throw new ArgumentNullException(nameof(migration));
            }

            if (_migrations.ContainsKey(migration.FromVersion))
            {
                throw new InvalidOperationException(
                    $"SaveSystem: a migration for FromVersion {migration.FromVersion} is already registered.");
            }

            _migrations[migration.FromVersion] = migration;
        }

        /// <inheritdoc />
        public void SaveToSlot(int slot)
        {
            ValidateSlot(slot);

            var meta = new SaveMeta
            {
                SavedAtUtc = DateTime.UtcNow,
                PlaytimeSeconds = CurrentPlaytimeSeconds,
                DisplayName = CurrentDisplayName ?? string.Empty,
            };

            var data = new SaveData(CurrentVersion) { Meta = meta };
            foreach (var pair in _saveables)
            {
                var section = new JObject();
                pair.Value.Save(new SaveWriter(section));
                data.Modules[pair.Key] = section;
            }

            string json = data.ToJObject().ToString(Formatting.Indented);
            string main = SlotPath(slot);
            string tmp = main + TempSuffix;
            string bak1 = main + Backup1Suffix;
            string bak2 = main + Backup2Suffix;

            // Whole roll-and-rename sequence is serialised: the .tmp path is fixed, so
            // overlapping saves on one slot would corrupt each other's half-written file.
            lock (_writeLock)
            {
                Directory.CreateDirectory(_rootDirectory);

                try
                {
                    // TASK-A-AUDIT-03 (B-1): payload FIRST — this is the slow, failure-prone
                    // step (Flush(flushToDisk:true) below). The previous order moved main
                    // into .bak1 before writing, so a disk-full / permission / AV-lock
                    // failure left the slot without a main file at all. With the payload
                    // durably on disk first, a failure here leaves main and .bak1 untouched.
                    WriteDurable(tmp, json);

                    // Only now that the new generation is durable do we roll the old ones:
                    // old main -> bak1, old bak1 -> bak2 (keep last 2), then tmp -> main.
                    if (File.Exists(main))
                    {
                        if (File.Exists(bak1))
                        {
                            if (File.Exists(bak2))
                            {
                                File.Delete(bak2);
                            }

                            File.Move(bak1, bak2);
                        }

                        File.Move(main, bak1);
                    }

                    File.Move(tmp, main);
                }
                catch (Exception)
                {
                    // Never swallow: the caller (GameBootstrap) reports the failure. Only the
                    // cleanup is best-effort, and it must leave at least one readable
                    // generation behind — main, or .bak1 which LoadFromSlot falls back to.
                    RecoverAfterFailedWrite(main, bak1, tmp);
                    throw;
                }
            }
        }

        /// <summary>
        /// Best-effort cleanup after a failed <see cref="SaveToSlot"/>: if the roll had
        /// already moved main into <c>.bak1</c> (failure at the final rename), copy the
        /// previous generation back onto the main path so "current save" still exists;
        /// remove a half-written <c>.tmp</c>. The original exception is rethrown by the
        /// caller — nothing here is allowed to mask it.
        /// </summary>
        private static void RecoverAfterFailedWrite(string main, string bak1, string tmp)
        {
            try
            {
                if (!File.Exists(main) && File.Exists(bak1))
                {
                    File.Copy(bak1, main, overwrite: false);
                }
            }
            catch (IOException)
            {
                // .bak1 remains readable through LoadFromSlot's fallback chain.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: the fallback chain keeps the slot loadable.
            }

            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
                // A leftover .tmp is overwritten by the next save (FileMode.Create).
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 (no BOM)
        /// and flushes the OS/file-system cache to disk before returning.
        /// </summary>
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

        /// <inheritdoc />
        public SaveData LoadFromSlot(int slot)
        {
            ValidateSlot(slot);

            // Main file first; when it is missing, unreadable or quarantined as corrupt,
            // fall back to .bak1 then .bak2 *within this call*. Recovery must not depend on
            // the caller retrying: gameplay calls LoadFromSlot once and treats null as
            // "empty slot", so a retry-based contract silently turns a recoverable save into
            // a fresh game that overwrites the backups on the next auto-save.
            string main = SlotPath(slot);
            string[] candidates =
            {
                main,
                main + Backup1Suffix,
                main + Backup2Suffix,
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                SaveData data = ReadSlotFile(slot, path);
                if (data != null)
                {
                    return data;
                }

                // Failed candidate was quarantined (or unreadable) - try the next generation.
            }

            return null;
        }

        /// <inheritdoc />
        public SlotInfo? GetSlotInfo(int slot)
        {
            ValidateSlot(slot);

            // Same candidate order as LoadFromSlot: a corrupt main file must not hide a
            // perfectly good .bak1 from the main-menu "Continue" button. Read-only - no
            // quarantine side effects here.
            string main = SlotPath(slot);
            string[] candidates =
            {
                main,
                main + Backup1Suffix,
                main + Backup2Suffix,
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                SlotInfo? info = TryReadSlotInfo(slot, path);
                if (info.HasValue)
                {
                    return info;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the meta header of a single file. Read-only: returns null for a
        /// broken/unreadable file without renaming it (quarantine belongs to a real load).
        /// The meta block is an isolated header section, so extracting it never touches
        /// module data.
        /// </summary>
        private static SlotInfo? TryReadSlotInfo(int slot, string path)
        {
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                if (!SaveData.TryReadVersion(root, out int version))
                {
                    return null;
                }

                SaveMeta meta = SaveMeta.FromJToken(root[SaveData.MetaField]);
                return new SlotInfo(slot, version, meta.SavedAtUtc, meta.PlaytimeSeconds, meta.DisplayName);
            }
            catch (JsonReaderException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <inheritdoc />
        public void DeleteSlot(int slot)
        {
            ValidateSlot(slot);

            string main = SlotPath(slot);
            DeleteIfExists(main);
            DeleteIfExists(main + Backup1Suffix);
            DeleteIfExists(main + Backup2Suffix);
            DeleteIfExists(main + TempSuffix);
        }

        /// <inheritdoc />
        public void AutoSave()
        {
            if (AutoSaveSlot < 0 || AutoSaveSlot >= SlotCount)
            {
                ZLog.Warn(LogChannel, $"AutoSaveSlot {AutoSaveSlot} out of range [0,{SlotCount}) — auto-save skipped.");
                return;
            }

            SaveToSlot(AutoSaveSlot);
        }

        /// <inheritdoc />
        public void Clear()
        {
            _saveables.Clear();
            _migrations.Clear();
        }

        private SaveData ReadSlotFile(int slot, string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException e)
            {
                ZLog.Error(LogChannel, $"slot {slot}: cannot read '{Path.GetFileName(path)}': {e.Message}");
                EventBus.Fire(new SaveCorruptEvent(slot, $"read failed: {e.Message}", path));
                return null;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonReaderException e)
            {
                return Quarantine(slot, path, $"invalid JSON: {e.Message}");
            }

            if (!SaveData.TryReadVersion(root, out int version))
            {
                return Quarantine(slot, path, "missing or non-integer version field");
            }

            if (version > CurrentVersion)
            {
                return Quarantine(slot, path,
                    $"save version {version} is newer than supported {CurrentVersion} (downgrade not allowed)");
            }

            while (version < CurrentVersion)
            {
                if (!_migrations.TryGetValue(version, out IMigration migration))
                {
                    return Quarantine(slot, path, $"no migration registered from version {version}");
                }

                JObject migrated;
                try
                {
                    migrated = migration.Migrate(root);
                }
                catch (Exception e)
                {
                    return Quarantine(slot, path, $"migration from version {version} failed: {e.Message}");
                }

                if (migrated == null)
                {
                    return Quarantine(slot, path, $"migration from version {version} returned null");
                }

                // SaveSystem owns the version counter: stamp FromVersion + 1
                // so the chain advances deterministically to CurrentVersion.
                migrated[SaveData.VersionField] = version + 1;
                root = migrated;
                version++;
            }

            if (!SaveData.TryParse(root, out SaveData data))
            {
                return Quarantine(slot, path, "post-migration structure invalid");
            }

            // A single module that cannot handle its section must not fail the whole load:
            // the JSON already validated, so isolate the module, log it, and let the rest
            // restore. Throwing here would leave earlier modules applied and later ones not
            // (half-loaded state) while the caller only sees an exception.
            foreach (var pair in _saveables)
            {
                try
                {
                    JToken section = data.Modules[pair.Key];
                    pair.Value.Load(new SaveReader(section));
                }
                catch (Exception e)
                {
                    ZLog.Error(LogChannel, $"slot {slot}: module '{pair.Key}' failed to load: {e.Message}");
                }
            }

            return data;
        }

        /// <summary>Renames the broken file to <c>.corrupt_&lt;timestamp&gt;</c>, logs, fires <see cref="SaveCorruptEvent"/> and returns null.</summary>
        private SaveData Quarantine(int slot, string path, string reason)
        {
            string renamedTo = QuarantineFile(path);
            ZLog.Warn(LogChannel, $"slot {slot}: {reason}; quarantined '{Path.GetFileName(path)}' -> '{Path.GetFileName(renamedTo)}'");
            EventBus.Fire(new SaveCorruptEvent(slot, reason, renamedTo));
            return null;
        }

        private static string QuarantineFile(string path)
        {
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
            catch (IOException)
            {
                // Cannot move (locked/permission) — leave the scene in place;
                // the event still reports the failure reason.
                return path;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private void ValidateSlot(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), slot, $"SaveSystem: slot must be in [0,{SlotCount}).");
            }
        }
    }
}
