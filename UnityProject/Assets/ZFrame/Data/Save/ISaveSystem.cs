namespace ZFrame.Data.Save
{
    /// <summary>
    /// Save/load façade for the player progression. Registered in
    /// <see cref="ZFrame.Data.ZFrameData.Init"/>; resolves via
    /// <c>Services.Get&lt;ISaveSystem&gt;()</c>.
    /// </summary>
    public interface ISaveSystem
    {
        /// <summary>Number of player-facing save slots.</summary>
        int SlotCount { get; }

        /// <summary>
        /// Registers a module that participates in every save/load.
        /// Duplicate <see cref="ISaveable.SaveKey"/> throws.
        /// </summary>
        void Register(ISaveable saveable);

        /// <summary>Removes a previously registered module by key. Returns false when the key was not registered.</summary>
        bool Unregister(string saveKey);

        /// <summary>Registers a version migration step. A duplicate <see cref="IMigration.FromVersion"/> throws.</summary>
        void RegisterMigration(IMigration migration);

        /// <summary>Collects all registered <see cref="ISaveable"/> modules and atomically writes the slot (with rolling backups).</summary>
        void SaveToSlot(int slot);

        /// <summary>
        /// Loads a slot, degrading <c>main → .bak1 → .bak2</c> WITHIN THE SAME
        /// CALL: the first candidate that reads, migrates and restores wins
        /// (a missing file is skipped; a corrupt/invalid one is quarantined,
        /// reported through <c>SaveCorruptEvent</c> and skipped). Returns null
        /// only when none of the three candidates yields a readable save —
        /// i.e. for an empty slot or when every generation is unusable.
        /// </summary>
        SaveData LoadFromSlot(int slot);

        /// <summary>
        /// Reads only the slot's header metadata, with the same candidate
        /// order as <see cref="LoadFromSlot"/> (main → .bak1 → .bak2, no
        /// quarantine side effects). Null only when no candidate file exists
        /// or every existing one is unreadable.
        /// </summary>
        SlotInfo? GetSlotInfo(int slot);

        /// <summary>Deletes a slot's main file and both backups. Idempotent.</summary>
        void DeleteSlot(int slot);

        /// <summary>Slot index <see cref="AutoSave"/> writes to (default 0; out-of-range disables auto-save).</summary>
        int AutoSaveSlot { get; set; }

        /// <summary>
        /// Auto-save hook. Writes to <see cref="AutoSaveSlot"/>; scheduling
        /// (timer/event driven) is the caller's business in this milestone.
        /// </summary>
        void AutoSave();

        /// <summary>Removes all registered modules and migrations (teardown/tests).</summary>
        void Clear();
    }
}
