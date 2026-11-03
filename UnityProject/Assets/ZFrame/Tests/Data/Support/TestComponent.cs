using UnityEngine;

namespace ZFrame.Data.Tests
{
    /// <summary>Identity marker on TestCube.prefab so pool-reuse tests can verify the right
    /// prefab was spawned. Runtime support assembly (see TestConfigData remarks).</summary>
    public sealed class TestComponent : MonoBehaviour
    {
        /// <summary>Constant marker; tests assert it after instantiation.</summary>
        public const int Marker = 1337;

        /// <summary>Marker value of the test prefab.</summary>
        public int marker = Marker;
    }
}
