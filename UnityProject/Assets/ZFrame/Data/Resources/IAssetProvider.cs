using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.Data
{
    /// <summary>
    /// Abstraction over the raw asset backend (Resources, Addressables, custom
    /// bundles, ...). <see cref="ResourceService"/> is constructed with one
    /// implementation and never touches a backend directly, so switching the
    /// project from Resources to Addressables later means writing one new
    /// <see cref="IAssetProvider"/> implementation and changing a single
    /// registration in <see cref="ZFrameData.Init"/> — no call-site changes.
    /// </summary>
    public interface IAssetProvider
    {
        /// <summary>
        /// Loads the asset registered under <paramref name="key"/>. The key
        /// format is provider-specific (Resources: path relative to any
        /// Resources folder without extension; Addressables: address).
        /// Must return a faulted/failed result (e.g. throw) when the key is
        /// unknown or the asset cannot be cast to
        /// <typeparamref name="T"/> — never silently return null.
        /// </summary>
        Task<T> LoadAsync<T>(string key) where T : Object;

        /// <summary>
        /// Drops the provider-side handle for <paramref name="key"/> (no-op
        /// for providers that do not track handles). Called by
        /// <see cref="ResourceService"/> only when the reference count of the
        /// key has reached zero.
        /// </summary>
        void Release(string key);

        /// <summary>Returns whether an asset exists under <paramref name="key"/>.</summary>
        bool Exists(string key);
    }
}
