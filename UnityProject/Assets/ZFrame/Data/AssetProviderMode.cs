namespace ZFrame.Data
{
    /// <summary>
    /// Which <see cref="IAssetProvider"/> backend <see cref="ZFrameData.Init"/>
    /// registers when no explicit provider is passed. Default
    /// <see cref="Resources"/> keeps the pre-Addressables behavior untouched.
    /// </summary>
    public enum AssetProviderMode
    {
        /// <summary>Unity Resources folders (the original default backend).</summary>
        Resources = 0,

        /// <summary>
        /// Unity Addressables (built by the factory installed from the
        /// ZFrame.Data.Addressables assembly — see AddressablesBootstrap).
        /// </summary>
        Addressables = 1,
    }
}
