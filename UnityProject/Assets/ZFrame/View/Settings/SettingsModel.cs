using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Pure-C# state + persistence owner of the settings system (TASK-A-M3-02).
    /// Holds the current value of every registered VALUE-BEARING definition
    /// (<see cref="SettingTypeExtensions.CarriesValue"/>: Toggle/Slider/Enum;
    /// Action/Binding/BindingReset rows are skipped by construction) and
    /// provides: <see cref="Get{T}"/>/<see cref="Set{T}"/> (Set applies the
    /// definition's callback IMMEDIATELY — 即时应用), <see cref="TakeSnapshot"/>/
    /// <see cref="RevertToSnapshot"/> (撤销更改 semantics) and
    /// <see cref="ResetAll"/> (恢复默认). Persistence is a single device-level
    /// <c>settings.json</c> (NOT part of the save-slot system), written as
    /// Newtonsoft JSON with a version field; unknown persisted ids and wrong
    /// typed values are ignored on load (forward compatibility).
    ///
    /// The root directory is INJECTABLE (same discipline as the SaveSystem
    /// A-M1-03 precedent): tests pass a temp directory and never touch the
    /// real <see cref="Application.persistentDataPath"/>.
    /// </summary>
    public sealed class SettingsModel
    {
        /// <summary>Current schema version written to settings.json; higher versions are ignored on load.</summary>
        public const int CurrentVersion = 1;

        /// <summary>File name inside the root directory.</summary>
        public const string FileName = "settings.json";

        /// <summary>In-flight file suffix: the payload is written here first, then renamed onto the main path.</summary>
        private const string TempSuffix = ".tmp";

        private readonly SettingsRegistry _registry;
        private readonly string _rootDirectory;
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);
        private Dictionary<string, object> _snapshot;
        private bool _dirty;

        /// <summary>Creates the model over <paramref name="registry"/>. When <paramref name="rootDirectory"/> is null/empty the real persistent data path is used (runtime); tests MUST inject a temp root.</summary>
        public SettingsModel(SettingsRegistry registry, string rootDirectory = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _rootDirectory = string.IsNullOrEmpty(rootDirectory)
                ? Application.persistentDataPath
                : rootDirectory;

            foreach (SettingDefinition definition in _registry.AllSettings)
            {
                if (definition.Type.CarriesValue())
                {
                    _values[definition.Id] = definition.DefaultValue;
                }
            }
        }

        /// <summary>The registry this model is bound to (the view builds tabs/rows from it).</summary>
        public SettingsRegistry Registry => _registry;

        /// <summary>True when at least one Set/ResetAll happened since the last Save/Load/RevertToSnapshot.</summary>
        public bool IsDirty => _dirty;

        /// <summary>Full path of the persisted JSON file.</summary>
        public string FilePath => Path.Combine(_rootDirectory, FileName);

        /// <summary>Reads the current value of a setting (typed). Unknown id → ArgumentException; value-less rows (Action/Binding/BindingReset) → InvalidOperationException.</summary>
        public T Get<T>(string settingId)
        {
            SettingDefinition definition = RequireSetting(settingId);
            if (!definition.Type.CarriesValue())
            {
                throw new InvalidOperationException(
                    $"setting '{settingId}' is a {definition.Type} row and carries no persisted value.");
            }

            return (T)_values[settingId];
        }

        /// <summary>
        /// Commits a new value: validates/clamps it against the definition,
        /// marks the model dirty, mirrors onto the definition and invokes the
        /// apply callback IMMEDIATELY (即时应用). Unknown id → ArgumentException.
        /// A value equal to the current one is a no-op — no dirty flag, no apply
        /// re-fire (TASK-A-R9-11). UGUI's editor-only <c>Toggle.Rebuild</c>
        /// re-announces the live control value during canvas rebuilds, and
        /// <c>DefaultControls</c>' Undo-based reparenting forces such rebuilds
        /// while a page is being built; the echo of an unchanged value must not
        /// flag the panel dirty, or 撤销更改 + the row rebuild it triggers would
        /// immediately re-dirty the model (user-facing: 撤销 stops being "clean").
        /// </summary>
        public void Set<T>(string settingId, T value)
        {
            SettingDefinition definition = RequireSetting(settingId);
            object committed = ValidateAndNormalize(definition, value);

            if (_values.TryGetValue(definition.Id, out object current) && Equals(current, committed))
            {
                return; // unchanged value = no state change (TASK-A-R9-11, see doc above)
            }

            ApplyValue(definition, committed, markDirty: true);
        }

        /// <summary>Stores the current values as the revert target (the panel takes one when it opens — "撤销更改" rolls back to here).</summary>
        public void TakeSnapshot()
        {
            _snapshot = new Dictionary<string, object>(_values, StringComparer.Ordinal);
        }

        /// <summary>
        /// Restores every value from the last snapshot (no snapshot taken → no-op)
        /// and re-applies each through the definitions' callbacks, so the live
        /// engine state rolls back too. Clears the dirty flag.
        /// </summary>
        public void RevertToSnapshot()
        {
            if (_snapshot == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> entry in _snapshot)
            {
                if (_registry.TryGetDefinition(entry.Key, out SettingDefinition definition))
                {
                    ApplyValue(definition, entry.Value, markDirty: false);
                }
            }

            _dirty = false;
        }

        /// <summary>Sets every setting back to its definition default and applies each (恢复默认). Marks the model dirty — persisting still requires an explicit Save.</summary>
        public void ResetAll()
        {
            foreach (SettingDefinition definition in _registry.AllSettings)
            {
                if (definition.Type.CarriesValue())
                {
                    ApplyValue(definition, definition.DefaultValue, markDirty: true);
                }
            }
        }

        /// <summary>
        /// Writes settings.json (version field + flat id→value map). The directory is
        /// created on demand and the payload is written UTF-8 without BOM to a
        /// <c>.tmp</c> file that is flushed to disk and only then renamed onto the
        /// main path (see <see cref="WriteAtomically"/>). Clears the dirty flag.
        /// </summary>
        public void Save()
        {
            var root = new JObject
            {
                ["version"] = CurrentVersion,
            };

            var settings = new JObject();
            foreach (SettingDefinition definition in _registry.AllSettings)
            {
                if (!definition.Type.CarriesValue())
                {
                    continue;
                }

                settings[definition.Id] = _values[definition.Id] switch
                {
                    bool b => new JValue(b),
                    float f => new JValue(f),
                    int i => new JValue(i),
                    _ => JValue.CreateNull(),
                };
            }

            root["settings"] = settings;

            Directory.CreateDirectory(_rootDirectory);
            WriteAtomically(FilePath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            _dirty = false;
        }

        /// <summary>
        /// Replaces <paramref name="path"/> atomically: the payload goes to
        /// <c>path.tmp</c> (flushed to the physical disk) and only the complete temp file
        /// is renamed onto the main path. A power cut, a crash or an IO error therefore
        /// leaves either the previous settings.json or the new one — never a truncated
        /// main file, which <see cref="Load"/> would silently degrade to defaults (the
        /// player's settings would vanish without any message).
        ///
        /// Same discipline as <c>ZFrame.Data.SaveSystem.WriteDurable</c>, deliberately
        /// re-implemented here instead of shared: settings and saves live in different
        /// assemblies and the save system's internals must not become public API.
        /// </summary>
        private static void WriteAtomically(string path, string content)
        {
            string tmp = path + TempSuffix;
            WriteDurable(tmp, content);

            if (File.Exists(path))
            {
                // Swap in one step (ReplaceFile on Windows / rename on POSIX); a brand-new
                // profile has no destination yet, so the first save is a plain rename.
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>Writes <paramref name="content"/> as UTF-8 (no BOM) and flushes the OS cache to disk before returning.</summary>
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

        /// <summary>
        /// Loads settings.json and re-applies every known, correctly typed value
        /// through the definitions' callbacks (so persisted settings take effect
        /// at boot). Tolerant by design: missing file, corrupt JSON, unknown
        /// ids, wrong typed values and newer versions are all ignored without
        /// throwing. Returns how many known values were applied.
        /// </summary>
        public int Load()
        {
            if (!File.Exists(FilePath))
            {
                return 0;
            }

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(FilePath));
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is IOException)
            {
                Debug.LogWarning($"[SettingsModel] settings.json unreadable ({e.GetType().Name}) — defaults kept.");
                return 0;
            }

            int version = root.Value<int?>("version") ?? CurrentVersion;
            if (version > CurrentVersion)
            {
                Debug.LogWarning($"[SettingsModel] settings.json version {version} newer than supported {CurrentVersion} — ignored.");
                return 0;
            }

            if (root["settings"] is not JObject settings)
            {
                return 0;
            }

            int applied = 0;
            foreach (KeyValuePair<string, JToken> entry in settings)
            {
                if (!_registry.TryGetDefinition(entry.Key, out SettingDefinition definition) ||
                    !definition.Type.CarriesValue())
                {
                    continue; // unknown/legacy key — tolerated and skipped
                }

                if (TryConvert(definition, entry.Value, out object value))
                {
                    ApplyValue(definition, value, markDirty: false);
                    applied++;
                }
            }

            _dirty = false;
            return applied;
        }

        // ---- internals ------------------------------------------------------

        private SettingDefinition RequireSetting(string settingId)
        {
            if (!_registry.TryGetDefinition(settingId, out SettingDefinition definition))
            {
                throw new ArgumentException($"unknown setting id '{settingId}'.", nameof(settingId));
            }

            return definition;
        }

        /// <summary>Single write path: authoritative map + display mirror + dirty flag + immediate apply.</summary>
        private void ApplyValue(SettingDefinition definition, object value, bool markDirty)
        {
            _values[definition.Id] = value;
            definition.CurrentValue = value;
            if (markDirty)
            {
                _dirty = true;
            }

            definition.Apply?.Invoke(value);
        }

        private object ValidateAndNormalize<T>(SettingDefinition definition, T value)
        {
            if (!definition.Type.CarriesValue())
            {
                throw new InvalidOperationException(
                    $"setting '{definition.Id}' is a {definition.Type} row and cannot hold a value.");
            }

            if (value == null)
            {
                throw new ArgumentException($"setting '{definition.Id}': value must not be null.");
            }

            switch (definition.Type)
            {
                case SettingType.Toggle:
                    if (value is not bool)
                    {
                        throw new ArgumentException($"setting '{definition.Id}': expected bool, got {value.GetType().Name}.");
                    }

                    return value;

                case SettingType.Slider:
                    float f = Convert.ToSingle(value);
                    return Mathf.Clamp(f, definition.Min, definition.Max);

                case SettingType.Enum:
                    int i = Convert.ToInt32(value);
                    if (i < 0 || i >= definition.EnumOptions.Length)
                    {
                        throw new ArgumentException($"setting '{definition.Id}': enum index {i} outside 0..{definition.EnumOptions.Length - 1}.");
                    }

                    return i;

                default:
                    throw new ArgumentOutOfRangeException(definition.Id);
            }
        }

        private static bool TryConvert(SettingDefinition definition, JToken token, out object value)
        {
            value = null;
            try
            {
                switch (definition.Type)
                {
                    case SettingType.Toggle:
                        if (token.Type != JTokenType.Boolean)
                        {
                            return false;
                        }

                        value = token.Value<bool>();
                        return true;

                    case SettingType.Slider:
                        if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                        {
                            return false;
                        }

                        value = Mathf.Clamp(Convert.ToSingle(((JValue)token).Value), definition.Min, definition.Max);
                        return true;

                    case SettingType.Enum:
                        if (token.Type != JTokenType.Integer)
                        {
                            return false;
                        }

                        int index = token.Value<int>();
                        if (index < 0 || index >= definition.EnumOptions.Length)
                        {
                            return false;
                        }

                        value = index;
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                return false; // wrong-typed persisted value — tolerated
            }
        }
    }
}
