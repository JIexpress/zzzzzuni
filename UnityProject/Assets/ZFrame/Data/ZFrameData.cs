using System;
using System.Collections.Generic;
using ZFrame.Core;
using ZFrame.Data.Config;
using ZFrame.Data.Save;

namespace ZFrame.Data
{
    /// <summary>
    /// Boot entry for the ZFrame.Data layer: registers the resource service,
    /// the config service and the save system into <see cref="Services"/>.
    /// Idempotent — safe to call from Bootstrap every launch and from tests.
    /// </summary>
    public static class ZFrameData
    {
        /// <summary>Semantic version of the Data layer assembly.</summary>
        public const string Version = "0.1.2";

        /// <summary>Whether <see cref="Init"/> currently has registrations in place.</summary>
        public static bool IsInitialized
        {
            get
            {
                return Services.TryGet(out IResourceService _)
                    && Services.TryGet(out IConfigService _)
                    && Services.TryGet(out ISaveSystem _);
            }
        }

        /// <summary>
        /// Provider factories per <see cref="AssetProviderMode"/>. ZFrame.Data
        /// itself only knows <see cref="ResourcesProvider"/>; the Addressables
        /// backend lives one assembly up (ZFrame.Data.Addressables) and
        /// installs its factory through <see cref="RegisterProviderFactory"/> —
        /// inverse dependency, no circular asmdef reference. Registrations
        /// survive <see cref="Shutdown"/> (module installs are session-wide).
        /// </summary>
        private static readonly Dictionary<AssetProviderMode, Func<IAssetProvider>> s_providerFactories =
            new Dictionary<AssetProviderMode, Func<IAssetProvider>>();

        /// <summary>
        /// Installs the provider factory for <paramref name="mode"/> (e.g. the
        /// Addressables assembly installing
        /// <c>AddressablesBootstrap.UseAddressables</c>). Passing
        /// <paramref name="factory"/> is required and idempotent per mode —
        /// the latest registration wins.
        /// </summary>
        public static void RegisterProviderFactory(AssetProviderMode mode, Func<IAssetProvider> factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory),
                    $"ZFrameData: a provider factory for {mode} must not be null.");
            }

            s_providerFactories[mode] = factory;
        }

        /// <summary>
        /// Resolves the provider for <paramref name="mode"/> through the
        /// registered factories.
        /// </summary>
        private static IAssetProvider CreateProvider(AssetProviderMode mode)
        {
            switch (mode)
            {
                case AssetProviderMode.Resources:
                    return new ResourcesProvider();
                case AssetProviderMode.Addressables:
                    if (s_providerFactories.TryGetValue(mode, out var addressablesFactory))
                    {
                        return addressablesFactory();
                    }

                    throw new InvalidOperationException(
                        "ZFrameData: no Addressables provider factory installed. " +
                        "Reference ZFrame.Data.Addressables (it self-installs on play mode entry) " +
                        "or call ZFrame.Data.Addressables.AddressablesBootstrap.UseAddressables() first.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown asset provider mode.");
            }
        }

        /// <summary>
        /// Registers <see cref="IResourceService"/> with the default
        /// Resources-backed provider, the <see cref="IConfigService"/> and the
        /// <see cref="ISaveSystem"/>. Pass a custom <paramref name="assetProvider"/>
        /// to swap backends (the Addressables migration point) or in tests;
        /// with <paramref name="assetProvider"/> null the backend is picked by
        /// <paramref name="providerMode"/> (default <see cref="AssetProviderMode.Resources"/>
        /// keeps the original behavior — switching to Addressables is the
        /// TASK-A-R8-02 switch, business code stays unchanged).
        /// Each registration is a no-op when a service of that type is
        /// already registered.
        /// </summary>
        public static void Init(IAssetProvider assetProvider = null,
            AssetProviderMode providerMode = AssetProviderMode.Resources)
        {
            if (!Services.TryGet(out IResourceService _))
            {
                Services.Register<IResourceService>(
                    new ResourceService(assetProvider ?? CreateProvider(providerMode)));
            }

            if (!Services.TryGet(out IConfigService _))
            {
                Services.Register<IConfigService>(new ConfigService());
            }

            if (!Services.TryGet(out ISaveSystem _))
            {
                Services.Register<ISaveSystem>(new SaveSystem());
            }
        }

        /// <summary>
        /// Clears all services' bookkeeping and unregisters them. Does not
        /// touch <see cref="PoolRegistry"/> (owned by Core teardown). Safe
        /// to call repeatedly and when Init never ran.
        /// </summary>
        public static void Shutdown()
        {
            if (Services.TryGet(out IResourceService service))
            {
                service.Clear();
            }

            Services.Unregister<IResourceService>();

            if (Services.TryGet(out IConfigService configService))
            {
                configService.Clear();
            }

            Services.Unregister<IConfigService>();

            if (Services.TryGet(out ISaveSystem saveSystem))
            {
                saveSystem.Clear();
            }

            Services.Unregister<ISaveSystem>();
        }
    }
}
