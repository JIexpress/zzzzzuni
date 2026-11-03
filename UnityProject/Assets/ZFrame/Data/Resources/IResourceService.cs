using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.Data
{
    /// <summary>
    /// Unified resource front door: reference-counted asset cache on top of
    /// an <see cref="IAssetProvider"/>, plus instantiation routed through
    /// <see cref="ZFrame.Core.PoolRegistry"/> so pooled prefabs are reused
    /// instead of re-instantiated. Resolve via <c>Services.Get&lt;IResourceService&gt;()</c>
    /// or the <see cref="Res"/> facade; registered by <see cref="ZFrameData.Init"/>.
    /// </summary>
    public interface IResourceService
    {
        /// <summary>
        /// Loads <paramref name="key"/> with reference counting: loading an
        /// already-cached key returns the SAME object and bumps its count.
        /// Every successful call must eventually be matched by one
        /// <see cref="Release"/> (directly, or via
        /// <see cref="ReleaseInstance"/> for instantiated objects).
        /// </summary>
        Task<T> LoadAsync<T>(string key) where T : Object;

        /// <summary>Drops one reference; the asset is only truly released when the count reaches zero.</summary>
        void Release(string key);

        /// <summary>
        /// Loads the prefab at <paramref name="key"/> and spawns it through
        /// <see cref="ZFrame.Core.PoolRegistry"/> (repeat calls reuse pooled
        /// instances). Holds one asset reference per live instance which is
        /// returned by <see cref="ReleaseInstance"/>.
        /// </summary>
        Task<GameObject> InstantiateAsync(string key, Transform parent = null);

        /// <summary>Returns an instance to its pool (or destroys it when it never came from one).</summary>
        void ReleaseInstance(GameObject instance);

        /// <summary>
        /// Releases all zero-reference leftovers and triggers a global
        /// unload of unused assets. Intended for scene switches — calling it
        /// per-release would defeat the batch strategy (see ResourcesProvider remarks).
        /// </summary>
        void UnloadAllUnused();

        /// <summary>Number of loads currently in flight (async providers).</summary>
        int PendingLoadCount { get; }

        /// <summary>Number of assets currently held in the reference-counted cache.</summary>
        int CachedAssetCount { get; }

        /// <summary>Number of live instances handed out by InstantiateAsync and not yet released.</summary>
        int LiveInstanceCount { get; }

        /// <summary>Current reference count of <paramref name="key"/> (0 when not cached). Exposed for tests and debug panels.</summary>
        int GetRefCount(string key);

        /// <summary>
        /// Drops the whole cache and instance bookkeeping WITHOUT releasing
        /// pooled instances. Intended for <see cref="ZFrameData.Shutdown"/>
        /// and test teardown; runtime code should use Release/ReleaseInstance.
        /// </summary>
        void Clear();
    }
}
