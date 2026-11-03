using System;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// Read-only header snapshot of one save slot, returned by
    /// <see cref="SaveSystem.GetSlotInfo"/>. Taken from the isolated "meta"
    /// section of the save file; module data is never touched.
    /// </summary>
    public readonly struct SlotInfo
    {
        /// <summary>Slot index this info was read from.</summary>
        public int Slot { get; }

        /// <summary>Format version stored in the save file.</summary>
        public int Version { get; }

        /// <summary>UTC timestamp of when the save was written.</summary>
        public DateTime SavedAtUtc { get; }

        /// <summary>Playtime in seconds recorded at save time.</summary>
        public double PlaytimeSeconds { get; }

        /// <summary>Human readable label recorded at save time (never null).</summary>
        public string DisplayName { get; }

        /// <summary>Builds a slot info snapshot.</summary>
        public SlotInfo(int slot, int version, DateTime savedAtUtc, double playtimeSeconds, string displayName)
        {
            Slot = slot;
            Version = version;
            SavedAtUtc = savedAtUtc;
            PlaytimeSeconds = playtimeSeconds;
            DisplayName = displayName ?? string.Empty;
        }
    }
}
