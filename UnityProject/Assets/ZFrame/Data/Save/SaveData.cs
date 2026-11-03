using Newtonsoft.Json.Linq;

namespace ZFrame.Data.Save
{
    /// <summary>
    /// In-memory representation of one save file:
    /// <c>{ "version": n, "meta": {…}, "modules": { saveKey: {…} } }</c>.
    /// The "meta" section is an isolated header block read by
    /// <see cref="SaveSystem.GetSlotInfo"/>; "modules" holds one JObject
    /// section per <see cref="ISaveable.SaveKey"/>.
    /// </summary>
    public sealed class SaveData
    {
        /// <summary>Field name of the format version in the file root.</summary>
        public const string VersionField = "version";

        /// <summary>Field name of the header metadata section in the file root.</summary>
        public const string MetaField = "meta";

        /// <summary>Field name of the module sections container in the file root.</summary>
        public const string ModulesField = "modules";

        /// <summary>Format version of this save (see <see cref="SaveSystem.CurrentVersion"/>).</summary>
        public int Version { get; set; }

        /// <summary>Header metadata; never null after construction.</summary>
        public SaveMeta Meta { get; set; }

        /// <summary>Module sections keyed by <see cref="ISaveable.SaveKey"/>; never null after construction.</summary>
        public JObject Modules { get; }

        /// <summary>Creates an empty save at <paramref name="version"/> with default meta.</summary>
        public SaveData(int version)
        {
            Version = version;
            Meta = new SaveMeta();
            Modules = new JObject();
        }

        /// <summary>Creates an empty save at <see cref="SaveSystem.CurrentVersion"/> with default meta.</summary>
        public SaveData() : this(SaveSystem.CurrentVersion)
        {
        }

        /// <summary>Builds the serialized file root (null-safe on Meta/Modules).</summary>
        public JObject ToJObject()
        {
            return new JObject
            {
                [VersionField] = Version,
                [MetaField] = Meta != null ? Meta.ToJToken() : new JObject(),
                [ModulesField] = Modules ?? new JObject(),
            };
        }

        /// <summary>
        /// Parses a file root. Returns false when the root is not structured
        /// as expected (missing/non-integer version) — the caller treats this
        /// like a corrupt save.
        /// </summary>
        public static bool TryParse(JObject root, out SaveData data)
        {
            data = null;
            if (root == null || !TryReadVersion(root, out int version))
            {
                return false;
            }

            data = new SaveData(version)
            {
                Meta = SaveMeta.FromJToken(root[MetaField]),
            };

            if (root[ModulesField] is JObject modules)
            {
                data.Modules.RemoveAll();
                foreach (var pair in modules)
                {
                    data.Modules[pair.Key] = pair.Value;
                }
            }

            return true;
        }

        /// <summary>Reads only the version field of a root. False when missing or not an integer.</summary>
        public static bool TryReadVersion(JObject root, out int version)
        {
            version = 0;
            if (root == null)
            {
                return false;
            }

            JToken token = root[VersionField];
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            version = (int)token;
            return true;
        }
    }
}
