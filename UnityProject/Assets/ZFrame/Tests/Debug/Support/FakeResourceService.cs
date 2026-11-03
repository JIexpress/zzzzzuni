using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using ZFrame.Data;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>
    /// IResourceService fake over a key → prefab dictionary: counts LoadAsync
    /// calls and records Release calls so tests can verify that UIManager
    /// loads panel prefabs by key and drops the reference on destroy.
    /// </summary>
    public sealed class FakeResourceService : IResourceService
    {
        public readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();

        public int LoadCount { get; private set; }

        public readonly List<string> ReleasedKeys = new List<string>();

        public Task<T> LoadAsync<T>(string key) where T : Object
        {
            if (!Prefabs.TryGetValue(key, out GameObject prefab) || !(prefab is T typed))
            {
                throw new KeyNotFoundException($"[FakeResourceService] no prefab registered for key '{key}'.");
            }

            LoadCount++;
            return Task.FromResult(typed);
        }

        public void Release(string key)
        {
            ReleasedKeys.Add(key);
        }

        public Task<GameObject> InstantiateAsync(string key, Transform parent = null)
        {
            throw new System.NotSupportedException("[FakeResourceService] InstantiateAsync is not used by UIManager.");
        }

        public void ReleaseInstance(GameObject instance)
        {
            throw new System.NotSupportedException("[FakeResourceService] ReleaseInstance is not used by UIManager.");
        }

        public void UnloadAllUnused()
        {
        }

        public int PendingLoadCount => 0;

        public int CachedAssetCount => Prefabs.Count;

        public int LiveInstanceCount => 0;

        public int GetRefCount(string key)
        {
            return 0;
        }

        public void Clear()
        {
        }
    }
}
