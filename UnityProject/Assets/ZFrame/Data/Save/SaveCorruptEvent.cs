namespace ZFrame.Data.Save
{
    /// <summary>
    /// Raised on the <see cref="ZFrame.Core.EventBus"/> when a save slot file
    /// failed to read/parse/migrate and was quarantined as
    /// <c>.corrupt_yyyyMMddHHmmss</c> (failure scene preserved on disk).
    /// </summary>
    public struct SaveCorruptEvent
    {
        /// <summary>Slot whose file failed.</summary>
        public int Slot { get; }

        /// <summary>Human readable failure reason (parse error, version rejection, migration failure...).</summary>
        public string Reason { get; }

        /// <summary>Path the failed file was renamed to (same as the original path if the rename itself failed).</summary>
        public string QuarantinedPath { get; }

        /// <summary>Builds the event.</summary>
        public SaveCorruptEvent(int slot, string reason, string quarantinedPath)
        {
            Slot = slot;
            Reason = reason;
            QuarantinedPath = quarantinedPath;
        }
    }
}
