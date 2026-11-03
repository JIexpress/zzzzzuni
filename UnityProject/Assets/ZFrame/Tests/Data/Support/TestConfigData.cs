using UnityEngine;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// Payload of the committed test asset Assets/ZFrame/Tests/Resources/TestAsset.asset.
    /// Lives in the runtime support assembly (ZFrame.Data.Tests.Support)
    /// because .asset/.prefab files cannot reference scripts from
    /// Editor-only assemblies; the ".Tests." name keeps it exempt from the
    /// architecture validator matrix.
    /// </summary>
    public sealed class TestConfigData : ScriptableObject
    {
        /// <summary>Marker value written by the generator (42); tests assert it round-trips.</summary>
        public int value;
    }
}
