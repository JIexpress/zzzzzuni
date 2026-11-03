using System.Collections.Generic;
using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>
    /// Global facade over per-prefab <see cref="ComponentPool{T}"/> instances.
    /// Spawn/Despawn look up the pool registered for the prefab and lazily
    /// create one when missing. Intended as the single spawn entry point for
    /// gameplay code.
    /// </summary>
    public static class PoolRegistry
    {
        private static readonly Dictionary<Object, object> Pools = new Dictionary<Object, object>();

        /// <summary>Registers an existing <paramref name="pool"/> for <paramref name="prefab"/>.</summary>
        public static void Register<T>(T prefab, ComponentPool<T> pool) where T : Component
        {
            Pools[prefab] = pool;
        }

        /// <summary>Spawns an instance of <paramref name="prefab"/>, lazily creating its pool. Main-thread only.</summary>
        public static T Spawn<T>(T prefab, Transform parent = null) where T : Component
        {
            return GetPool(prefab).Get(parent);
        }

        /// <summary>Returns <paramref name="instance"/> to the pool of its source prefab. Main-thread only.</summary>
        public static void Despawn<T>(T instance) where T : Component
        {
            if (instance == null)
            {
                return;
            }

            foreach (var pair in Pools)
            {
                if (pair.Value is ComponentPool<T> pool && pool.Contains(instance))
                {
                    pool.Release(instance);
                    return;
                }
            }

            // Not tracked by any pool: destroy to avoid leaking ghost objects.
            if (Application.isPlaying)
            {
                Object.Destroy(instance.gameObject);
            }
            else
            {
                Object.DestroyImmediate(instance.gameObject);
            }
        }

        /// <summary>
        /// Snapshot statistics of every registered pool, for the debug panel.
        /// </summary>
        public static IEnumerable<(string PoolName, PoolStats Stats)> GetAllStats()
        {
            foreach (var pair in Pools)
            {
                if (pair.Value is IPoolStatsSource source)
                {
                    yield return (source.PoolName, source.Stats());
                }
            }
        }

        /// <summary>Disposes and forgets all pools. Call on scene switches.</summary>
        public static void ClearAll()
        {
            foreach (var pair in Pools)
            {
                if (pair.Value is System.IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            Pools.Clear();
        }

        private static ComponentPool<T> GetPool<T>(T prefab) where T : Component
        {
            if (Pools.TryGetValue(prefab, out var boxed) && boxed is ComponentPool<T> pool)
            {
                return pool;
            }

            pool = new ComponentPool<T>(prefab);
            Pools[prefab] = pool;
            return pool;
        }
    }
}
