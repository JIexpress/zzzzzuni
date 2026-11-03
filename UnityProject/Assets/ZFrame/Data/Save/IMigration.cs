using Newtonsoft.Json.Linq;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// One step of the save version migration chain. Registered steps run in
    /// ascending <see cref="FromVersion"/> order until the data reaches
    /// <see cref="SaveSystem.CurrentVersion"/>. The chain must be contiguous:
    /// a save with version V needs a migration with <see cref="FromVersion"/>
    /// equal to V, otherwise the load is treated like a corrupt save.
    /// </summary>
    public interface IMigration
    {
        /// <summary>Version this migration consumes; the save's version field must equal it to apply.</summary>
        int FromVersion { get; }

        /// <summary>
        /// Upgrades the save one version. Receives the full file root
        /// (<c>{version, meta, modules}</c>) and returns the upgraded root;
        /// the version field itself is stamped by <see cref="SaveSystem"/>
        /// (<see cref="FromVersion"/> + 1). Throwing or returning null fails
        /// the load (same handling as a corrupt save).
        /// </summary>
        JObject Migrate(JObject data);
    }
}
