using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.Data
{
    /// <summary>
    /// <see cref="IAssetProvider"/> over Unity's built-in Resources folders.
    /// This is the swap-out seam of the resource layer: the rest of the
    /// framework only depends on <see cref="IAssetProvider"/>, so migrating
    /// to Addressables later is a new provider class + one registration
    /// change in <see cref="ZFrameData.Init"/> (see 设计方案 数据层).
    /// </summary>
    public sealed class ResourcesProvider : IAssetProvider
    {
        /// <summary>
        /// Synchronous <see cref="Resources.Load"/> wrapped in a completed
        /// task. Resources has NO true async loading — large assets still
        /// stall the main thread for the duration of the read. This is the
        /// known limitation this provider hides behind the async signature;
        /// the Addressables implementation will provide real asynchronous
        /// loading without any call-site changes.
        /// </summary>
        public Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("ResourcesProvider: key must not be null or empty.", nameof(key));
            }

            var asset = Resources.Load<T>(key);
            if (asset == null)
            {
                throw new InvalidOperationException(
                    $"ResourcesProvider: no asset of type '{typeof(T).Name}' found at Resources path '{key}'.");
            }

            return Task.FromResult(asset);
        }

        /// <summary>
        /// Intentionally does NOT call <see cref="Resources.UnloadUnusedAssets"/>
        /// — that operation unloads globally and is far too expensive to run
        /// per key. Batch release is owned by
        /// <see cref="IResourceService.UnloadAllUnused"/> (scene-switch hook);
        /// individually handed-out assets are freed by Unity's GC once all
        /// references (including our cache entry) are gone.
        /// </summary>
        public void Release(string key)
        {
            // No per-key handle to drop for Resources.
        }

        /// <summary>Probes with a non-generic load; cheap enough for guards and editor tooling.</summary>
        public bool Exists(string key)
        {
            return !string.IsNullOrEmpty(key) && Resources.Load(key) != null;
        }
    }
}
