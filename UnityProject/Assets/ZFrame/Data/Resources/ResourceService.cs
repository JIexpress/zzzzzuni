using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.Data
{
    /// <summary>
    /// Default <see cref="IResourceService"/>: reference-counted cache over
    /// an injected <see cref="IAssetProvider"/> and instantiation routed
    /// through <see cref="PoolRegistry"/>. Main-thread only (Resources and
    /// instantiation are main-thread APIs).
    /// </summary>
    public sealed class ResourceService : IResourceService
    {
        private sealed class Entry
        {
            public Object Asset;
            public int RefCount;
        }

        private readonly IAssetProvider _provider;
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();
        private readonly Dictionary<GameObject, string> _instances = new Dictionary<GameObject, string>();
        private int _pending;

        /// <summary>
        /// The backend this service was built on (exposed for tests and debug
        /// panels — e.g. asserting the active backend after switching
        /// provider modes; runtime code must go through the load/release API).
        /// </summary>
        public IAssetProvider Provider => _provider;

        /// <summary>Current reference count of <paramref name="key"/> (0 when not cached).</summary>
        public int GetRefCount(string key)
        {
            return _cache.TryGetValue(key, out var entry) ? entry.RefCount : 0;
        }

        /// <summary>Number of loads currently in flight (async providers).</summary>
        public int PendingLoadCount => _pending;

        /// <summary>Number of assets currently held in the reference-counted cache.</summary>
        public int CachedAssetCount => _cache.Count;

        /// <summary>Number of live instances handed out and not yet released.</summary>
        public int LiveInstanceCount => _instances.Count;

        /// <param name="provider">Backend to load from (e.g. <see cref="ResourcesProvider"/>).</param>
        public ResourceService(IAssetProvider provider)
        {
            _provider = provider ?? throw new System.ArgumentNullException(nameof(provider));
        }

        /// <summary>Loads with reference counting; a cached key returns the same object and bumps its count.</summary>
        public async Task<T> LoadAsync<T>(string key) where T : Object
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new System.ArgumentException("ResourceService: key must not be null or empty.", nameof(key));
            }

            if (_cache.TryGetValue(key, out var entry) && entry.Asset is T cached)
            {
                entry.RefCount++;
                return cached;
            }

            _pending++;
            try
            {
                var asset = await _provider.LoadAsync<T>(key);
                if (asset == null)
                {
                    throw new System.InvalidOperationException(
                        $"ResourceService: provider returned null for key '{key}' ({typeof(T).Name}).");
                }

                // A concurrent load of the same key may have populated the
                // cache while we awaited; reuse that entry instead of storing a second object.
                if (_cache.TryGetValue(key, out entry))
                {
                    entry.RefCount++;
                    return (T)entry.Asset;
                }

                _cache[key] = new Entry { Asset = asset, RefCount = 1 };
                return asset;
            }
            finally
            {
                _pending--;
            }
        }

        /// <summary>Drops one reference; the provider is only notified when the count reaches zero.</summary>
        public void Release(string key)
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                Debug.LogWarning($"ResourceService: Release called for '{key}' which is not cached. Ignoring.");
                return;
            }

            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                return;
            }

            _cache.Remove(key);
            _provider.Release(key);
        }

        /// <summary>Loads the prefab and spawns it through <see cref="PoolRegistry"/>, holding one asset reference per live instance.</summary>
        public async Task<GameObject> InstantiateAsync(string key, Transform parent = null)
        {
            var prefab = await LoadAsync<GameObject>(key);

            // Pool on the Transform component (every GameObject has one) so
            // prefabs without any custom script still pool correctly.
            var instance = PoolRegistry.Spawn(prefab.transform, parent).gameObject;
            _instances[instance] = key;
            return instance;
        }

        /// <summary>Returns an instance to its pool and releases its asset reference; untracked (non-pooled) objects are destroyed.</summary>
        public void ReleaseInstance(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (_instances.Remove(instance, out var key))
            {
                Release(key);
            }

            PoolRegistry.Despawn(instance.transform);
        }

        /// <summary>
        /// Releases any zero-reference leftovers (defensive — normally none
        /// exist) and triggers a global unused-asset unload. Scene-switch hook.
        /// </summary>
        public void UnloadAllUnused()
        {
            var deadKeys = new List<string>();
            foreach (var pair in _cache)
            {
                if (pair.Value.RefCount <= 0)
                {
                    deadKeys.Add(pair.Key);
                }
            }

            foreach (var key in deadKeys)
            {
                _cache.Remove(key);
                _provider.Release(key);
            }

            // Global unload: valid on the main thread in and out of play mode.
            // NOTE: when an Addressables provider lands, this should be
            // routed through it instead of the Resources API directly.
            _ = Resources.UnloadUnusedAssets();
        }

        /// <summary>Drops the cache and instance bookkeeping; used by <see cref="ZFrameData.Shutdown"/> and tests.</summary>
        public void Clear()
        {
            _cache.Clear();
            _instances.Clear();
        }
    }
}
