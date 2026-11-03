namespace ZFrame.Data.Save
{
    /// <summary>
    /// A gameplay module that participates in save games. Implementations
    /// write their state into a <see cref="SaveWriter"/> section keyed by
    /// <see cref="SaveKey"/> and restore it from a <see cref="SaveReader"/>.
    /// Sections are namespaced per module, so two modules never collide even
    /// when they use the same field names.
    /// </summary>
    public interface ISaveable
    {
        /// <summary>
        /// Unique key for this module's section inside a save file. Must be
        /// stable across game versions — it is the migration/compat anchor.
        /// </summary>
        string SaveKey { get; }

        /// <summary>Writes the current state. Called by <see cref="SaveSystem.SaveToSlot"/> during slot collection.</summary>
        void Save(SaveWriter writer);

        /// <summary>Restores the state. Called by <see cref="SaveSystem.LoadFromSlot"/> after migration succeeded.</summary>
        void Load(SaveReader reader);
    }
}
