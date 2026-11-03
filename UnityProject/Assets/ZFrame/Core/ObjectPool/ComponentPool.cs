using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFrame.Core
{
    /// <summary>Runtime statistics snapshot of a <see cref="ComponentPool{T}"/>.</summary>
    public readonly struct PoolStats
    {
        /// <summary>Instances currently checked out of the pool.</summary>
        public int ActiveCount { get; }

        /// <summary>Instances currently stored in the pool.</summary>
        public int PooledCount { get; }

        /// <summary>High-water mark of simultaneously active instances.</summary>
        public int PeakActive { get; }

        /// <summary>Total number of Instantiate calls performed by this pool.</summary>
        public int InstantiateCount { get; }

        public PoolStats(int activeCount, int pooledCount, int peakActive, int instantiateCount)
        {
            ActiveCount = activeCount;
            PooledCount = pooledCount;
            PeakActive = peakActive;
            InstantiateCount = instantiateCount;
        }
    }

    /// <summary>Internal view of a pool for <see cref="PoolRegistry.GetAllStats"/> without generics.</summary>
    internal interface IPoolStatsSource
    {
        /// <summary>Human-readable pool identity (prefab name + component type).</summary>
        string PoolName { get; }

        /// <summary>Snapshot of pool statistics.</summary>
        PoolStats Stats();
    }

    /// <summary>
    /// Pool for Unity components cloned from a prefab. Spawns activate the
    /// instance (and call <see cref="IPoolable.OnSpawn"/>); releases
    /// deactivate it, reset common residue via <see cref="PoolResetter"/>,
    /// call <see cref="IPoolable.OnDespawn"/> and reparent to a shared
    /// "~Pooled" holder so idle instances never linger in live hierarchies.
    /// </summary>
    public sealed class ComponentPool<T> : IDisposable, IPoolStatsSource where T : Component
    {
        /// <summary>Identity shown by debug panels: prefab name + component type.</summary>
        public string PoolName => $"{prefab.name}({typeof(T).Name})";
        private const string PoolRootName = "~Pooled";

        private readonly T prefab;
        private readonly int maxCapacity;
        private readonly Stack<T> pooled = new Stack<T>();
        private readonly HashSet<int> active = new HashSet<int>();
        private readonly GameObject poolRoot;
        private readonly PoolResetter resetterPrototype;
        private bool capacityWarned;

        /// <summary>Instances currently checked out.</summary>
        public int ActiveCount => active.Count;

        /// <summary>Instances currently stored in the pool.</summary>
        public int PooledCount => pooled.Count;

        /// <summary>High-water mark of simultaneously active instances.</summary>
        public int PeakActive { get; private set; }

        /// <summary>Total Instantiate calls performed by this pool.</summary>
        public int InstantiateCount { get; private set; }

        /// <summary>The shared parent that stores idle instances.</summary>
        public GameObject PoolRoot => poolRoot;

        /// <summary>Creates a pool for <paramref name="prefab"/> and optionally warms it up.</summary>
        /// <param name="prefab">Prefab to clone when the pool runs dry.</param>
        /// <param name="warmup">Number of instances to pre-create (they are stored inactive).</param>
        /// <param name="maxCapacity">Upper bound on pooled instances kept; extra releases are destroyed.</param>
        public ComponentPool(T prefab, int warmup = 0, int maxCapacity = int.MaxValue)
        {
            this.prefab = prefab;
            this.maxCapacity = maxCapacity;
            poolRoot = new GameObject($"{PoolRootName}({typeof(T).Name})#{GetHashCode():X8}");
            resetterPrototype = poolRoot.AddComponent<PoolResetter>();
            resetterPrototype.enabled = false;

            for (int i = 0; i < warmup; i++)
            {
                var instance = CreateInstance();
                Store(instance);
            }
        }

        /// <summary>Takes an instance from the pool, instantiating one when dry.</summary>
        /// <param name="parent">Optional parent for the spawned instance.</param>
        public T Get(Transform parent = null)
        {
            T instance;
            lock (pooled)
            {
                instance = pooled.Count > 0 ? pooled.Pop() : null;
            }

            if (instance == null)
            {
                instance = CreateInstance();
            }
            else
            {
                // Reactivate renderers hidden by the resetter on release.
                resetterPrototype.RestoreVisibility(instance.gameObject);
            }

            if (parent != null)
            {
                instance.transform.SetParent(parent, false);
            }

            instance.gameObject.SetActive(true);
            active.Add(instance.GetInstanceID());
            PeakActive = Math.Max(PeakActive, active.Count);
            (instance as IPoolable)?.OnSpawn();
            return instance;
        }

        /// <summary>Returns an instance to the pool, resetting residue and reparenting to the pool root.</summary>
        public void Release(T instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            var id = instance.GetInstanceID();
            if (!active.Remove(id))
            {
                UnityEngine.Debug.LogWarning(
                    $"ComponentPool: Release called on an instance that is not active/tracked ({instance.name}). Ignoring.");
                return;
            }

            (instance as IPoolable)?.OnDespawn();
            resetterPrototype.ResetForReuse(instance.gameObject);

            if (pooled.Count >= maxCapacity)
            {
                if (!capacityWarned)
                {
                    UnityEngine.Debug.LogWarning(
                        $"ComponentPool<{typeof(T).Name}>: capacity {maxCapacity} reached; destroying surplus instances. " +
                        "This warning logs only once per pool.");
                    capacityWarned = true;
                }

                DestroyObject(instance.gameObject);
                return;
            }

            instance.gameObject.SetActive(false);
            Store(instance);
        }

        /// <summary>Shrinks the pool down to (at most) <paramref name="keep"/> stored instances, destroying the rest.</summary>
        public void Shrink(int keep)
        {
            lock (pooled)
            {
                while (pooled.Count > Math.Max(0, keep))
                {
                    var instance = pooled.Pop();
                    DestroyObject(instance.gameObject);
                }
            }
        }

        /// <summary>Whether <paramref name="instance"/> is currently tracked as active in this pool.</summary>
        public bool Contains(T instance)
        {
            return instance != null && active.Contains(instance.GetInstanceID());
        }

        /// <summary>Returns a snapshot of pool statistics.</summary>
        public PoolStats Stats()
        {
            lock (pooled)
            {
                return new PoolStats(active.Count, pooled.Count, PeakActive, InstantiateCount);
            }
        }

        /// <summary>Destroys the pool root and all pooled instances. Active instances are left alone.</summary>
        public void Dispose()
        {
            Shrink(0);
            if (poolRoot != null)
            {
                DestroyObject(poolRoot);
            }
        }

        private static void DestroyObject(UnityEngine.Object target)
        {
            // Destroy is deferred to end of frame, which never arrives inside
            // EditMode tests — use DestroyImmediate there so destruction is
            // observable immediately.
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private T CreateInstance()
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            InstantiateCount++;
            return instance;
        }

        private void Store(T instance)
        {
            instance.transform.SetParent(poolRoot.transform, false);
            lock (pooled)
            {
                pooled.Push(instance);
            }
        }
    }
}
