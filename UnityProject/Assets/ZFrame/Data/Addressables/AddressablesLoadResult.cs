using UnityEngine;

namespace ZFrame.Data.Addressables
{
    /// <summary>
    /// One adapter-level load outcome: the loaded asset plus the opaque
    /// handle that must eventually be handed back to
    /// <see cref="IAddressablesAdapter.Release"/>. Created by
    /// <see cref="IAddressablesAdapter"/> implementations and kept alive by
    /// <see cref="AddressablesProvider"/> for as long as the key is referenced.
    /// </summary>
    public sealed class AddressablesLoadResult
    {
        /// <summary>The loaded asset (never null on a successful load).</summary>
        public Object Asset { get; }

        /// <summary>Opaque release token owned by the adapter that produced this result.</summary>
        public IAddressablesHandle Handle { get; }

        /// <summary>Creates one load outcome. Both arguments are required.</summary>
        public AddressablesLoadResult(Object asset, IAddressablesHandle handle)
        {
            Asset = asset;
            Handle = handle;
        }
    }
}
