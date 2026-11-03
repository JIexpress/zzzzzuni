using UnityEngine;

namespace ZFrame.Data.Addressables
{
    /// <summary>
    /// Installs the Addressables backend into <see cref="ZFrameData"/> so the
    /// static switch <c>ZFrameData.Init(providerMode: AssetProviderMode.Addressables)</c>
    /// can build an <see cref="AddressablesProvider"/>. The assembly layering
    /// runs ZFrame.Data ← ZFrame.Data.Addressables, so the switch is wired by
    /// registration (factory injection), never by a direct reference back.
    ///
    /// Default stays off: <see cref="ZFrameData.Init"/> without arguments keeps
    /// registering the <see cref="ResourcesProvider"/> (zero breakage).
    /// </summary>
    public static class AddressablesBootstrap
    {
        /// <summary>
        /// Registers the Addressables provider factory (idempotent). Called
        /// automatically when entering play mode via
        /// <see cref="RuntimeInitializeOnLoadMethod"/>, so game code switches
        /// backends with a single <c>ZFrameData.Init</c> argument and zero
        /// additional references. In EditMode tests, call this explicitly.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void UseAddressables()
        {
            ZFrameData.RegisterProviderFactory(AssetProviderMode.Addressables,
                () => new AddressablesProvider());
        }
    }
}
