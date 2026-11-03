using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// Header metadata of a save file, stored in its own isolated "meta"
    /// section so <see cref="SaveSystem.GetSlotInfo"/> can answer slot
    /// queries without touching module data.
    /// </summary>
    public sealed class SaveMeta
    {
        /// <summary>Field name of the UTC save timestamp inside the meta section.</summary>
        public const string SavedAtField = "savedAtUtc";

        /// <summary>Field name of the playtime (seconds) inside the meta section.</summary>
        public const string PlaytimeField = "playtimeSeconds";

        /// <summary>Field name of the display name inside the meta section.</summary>
        public const string DisplayNameField = "displayName";

        /// <summary>UTC timestamp of when the save was written.</summary>
        public DateTime SavedAtUtc { get; set; }

        /// <summary>Playtime in seconds at the moment of saving.</summary>
        public double PlaytimeSeconds { get; set; }

        /// <summary>Human readable slot label (character/level name); never null.</summary>
        public string DisplayName { get; set; }

        /// <summary>Creates a meta with UtcNow timestamp, zero playtime and an empty display name.</summary>
        public SaveMeta()
        {
            SavedAtUtc = DateTime.UtcNow;
            PlaytimeSeconds = 0d;
            DisplayName = string.Empty;
        }

        /// <summary>Serializes to the "meta" J section (null-safe on all fields).</summary>
        public JObject ToJToken()
        {
            return new JObject
            {
                [SavedAtField] = SavedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                [PlaytimeField] = PlaytimeSeconds,
                [DisplayNameField] = DisplayName ?? string.Empty,
            };
        }

        /// <summary>
        /// Parses the "meta" section. Null-safe: any missing/invalid field
        /// falls back to its default (UtcNow / 0 / empty string).
        /// </summary>
        public static SaveMeta FromJToken(JToken token)
        {
            var meta = new SaveMeta();
            if (!(token is JObject obj))
            {
                return meta;
            }

            JToken savedAt = obj[SavedAtField];
            if (savedAt != null && savedAt.Type == JTokenType.String)
            {
                if (DateTime.TryParse((string)savedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime parsed))
                {
                    meta.SavedAtUtc = parsed;
                }
            }

            JToken playtime = obj[PlaytimeField];
            if (playtime != null && (playtime.Type == JTokenType.Float || playtime.Type == JTokenType.Integer))
            {
                meta.PlaytimeSeconds = Convert.ToDouble(((JValue)playtime).Value, CultureInfo.InvariantCulture);
            }

            JToken displayName = obj[DisplayNameField];
            if (displayName != null && displayName.Type == JTokenType.String)
            {
                meta.DisplayName = (string)displayName;
            }

            return meta;
        }
    }
}
