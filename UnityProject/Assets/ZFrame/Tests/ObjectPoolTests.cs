using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.Core.Tests
{
    /// <summary>EditMode tests for the object pool (ComponentPool/IPoolable/PoolResetter/PoolRegistry).</summary>
    public class ObjectPoolTests
    {
        private sealed class PoolSpy : MonoBehaviour, IPoolable
        {
            public int SpawnCalls;
            public int DespawnCalls;
            public readonly List<string> Log = new List<string>();

            public void OnSpawn()
            {
                SpawnCalls++;
                Log.Add("spawn");
            }

            public void OnDespawn()
            {
                DespawnCalls++;
                Log.Add("despawn");
            }
        }

        private GameObject prefabHolder;

        [SetUp]
        public void SetUp()
        {
            prefabHolder = new GameObject("PrefabHolder");
            prefabHolder.SetActive(false);
        }

        [TearDown]
        public void TearDown()
        {
            PoolRegistry.ClearAll();
            if (prefabHolder != null)
            {
                Object.DestroyImmediate(prefabHolder);
            }
        }

        private ComponentPool<PoolSpy> MakePool(int warmup = 0, int maxCapacity = int.MaxValue)
        {
            var prefab = prefabHolder.AddComponent<PoolSpy>();
            return new ComponentPool<PoolSpy>(prefab, warmup, maxCapacity);
        }

        [Test]
        public void Get_EmptyPool_InstantiatesFromPrefab()
        {
            var pool = MakePool();

            var instance = pool.Get();

            Assert.IsNotNull(instance);
            Assert.AreEqual(1, pool.InstantiateCount, "first Get must instantiate the prefab exactly once");
            Assert.IsTrue(instance.gameObject.activeSelf, "spawned instance must be active");
            Object.DestroyImmediate(instance.gameObject);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void Release_ThenGet_ReusesWithoutInstantiate()
        {
            var pool = MakePool();

            var first = pool.Get();
            pool.Release(first);
            var second = pool.Get();

            Assert.AreSame(first, second, "second Get must reuse the released instance");
            Assert.AreEqual(1, pool.InstantiateCount, "reuse must not instantiate again");
            Object.DestroyImmediate(second.gameObject);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void IPoolable_OnSpawnOnDespawn_CalledInOrder()
        {
            var pool = MakePool();

            var instance = pool.Get();
            pool.Release(instance);
            pool.Get();

            Assert.AreEqual(2, instance.SpawnCalls, "OnSpawn must run once per Get");
            Assert.AreEqual(1, instance.DespawnCalls, "OnDespawn must run once per Release");
            CollectionAssert.AreEqual(
                new[] { "spawn", "despawn", "spawn" },
                instance.Log,
                "lifecycle calls must interleave spawn/despawn correctly");
            Object.DestroyImmediate(instance.gameObject);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void Release_ReparentsToPoolRoot_AndDeactivates()
        {
            var pool = MakePool();

            var instance = pool.Get();
            var liveParent = new GameObject("LiveParent");
            instance.transform.SetParent(liveParent.transform, false);
            pool.Release(instance);

            Assert.AreSame(pool.PoolRoot.transform, instance.transform.parent,
                "released instances must live under the pool root (no ghost objects in live hierarchies)");
            Assert.IsFalse(instance.gameObject.activeSelf, "released instances must be inactive");
            Object.DestroyImmediate(liveParent);
            pool.Shrink(0);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void Warmup_PrecreatesInactiveInstances()
        {
            var pool = MakePool(warmup: 3);

            var stats = pool.Stats();
            Assert.AreEqual(3, stats.PooledCount, "warmup must pre-create 3 pooled instances");
            Assert.AreEqual(3, pool.InstantiateCount, "warmup instantiates exactly 3");
            Assert.AreEqual(0, stats.ActiveCount);
            Assert.IsFalse(pool.PoolRoot.transform.GetChild(0).gameObject.activeSelf,
                "warmed-up instances must be stored inactive");
            pool.Shrink(0);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void Stats_TrackCountsAndPeak()
        {
            var pool = MakePool();

            var a = pool.Get();
            var b = pool.Get();
            pool.Release(a);

            var stats = pool.Stats();
            Assert.AreEqual(1, stats.ActiveCount, "one instance still checked out");
            Assert.AreEqual(1, stats.PooledCount, "one instance returned");
            Assert.AreEqual(2, stats.PeakActive, "peak must remember the two simultaneous actives");
            Assert.AreEqual(2, stats.InstantiateCount);
            Object.DestroyImmediate(b.gameObject);
            pool.Shrink(0);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void Shrink_DestroysExcessPooledInstances()
        {
            var pool = MakePool();

            var instances = new List<PoolSpy>();
            for (int i = 0; i < 4; i++)
            {
                instances.Add(pool.Get());
            }

            foreach (var instance in instances)
            {
                pool.Release(instance);
            }

            Assert.AreEqual(4, pool.PooledCount);
            pool.Shrink(1);
            Assert.AreEqual(1, pool.PooledCount, "Shrink must keep only the requested count");

            int destroyed = 0;
            foreach (var instance in instances)
            {
                if (instance == null)
                {
                    destroyed++;
                }
            }

            Assert.AreEqual(3, destroyed, "shrink must destroy the 3 surplus instances");
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void MaxCapacity_ReleasesBeyondLimit_AreDestroyed()
        {
            var pool = MakePool(maxCapacity: 1); // Debug.LogWarning is expected once; warnings do not fail EditMode tests

            var a = pool.Get();
            var b = pool.Get();
            pool.Release(a);
            pool.Release(b);

            Assert.AreEqual(1, pool.PooledCount, "only one instance may stay pooled");
            Assert.IsTrue(b == null, "instance released beyond capacity must be destroyed (Unity fake-null)");
            Assert.IsTrue(a != null);
            pool.Shrink(0);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void PoolResetter_ClearsRigidbodyVelocity()
        {
            var go = new GameObject("RigidbodyTarget", typeof(Rigidbody));
            var rigidbody = go.GetComponent<Rigidbody>();
            var resetter = go.AddComponent<PoolResetter>();
            rigidbody.velocity = new Vector3(3f, 4f, 5f);
            rigidbody.angularVelocity = new Vector3(1f, 2f, 3f);

            resetter.ResetForReuse(go);

            Assert.AreEqual(Vector3.zero, rigidbody.velocity, "velocity must be zeroed before reuse");
            Assert.AreEqual(Vector3.zero, rigidbody.angularVelocity, "angularVelocity must be zeroed before reuse");
            Object.DestroyImmediate(go);
        }

        [Test]
        public void PoolResetter_DisablesAndRestoresTrailRenderer()
        {
            var go = new GameObject("TrailTarget", typeof(TrailRenderer));
            var trail = go.GetComponent<TrailRenderer>();
            var resetter = go.AddComponent<PoolResetter>();
            trail.enabled = true;

            resetter.ResetForReuse(go);
            Assert.IsFalse(trail.enabled, "trail renderer must be hidden while pooled");

            resetter.RestoreVisibility(go);
            Assert.IsTrue(trail.enabled, "spawn must restore trail renderer visibility");
            Object.DestroyImmediate(go);
        }

        [Test]
        public void PoolResetter_StopsAndClearsParticleSystem()
        {
            var go = new GameObject("ParticleTarget", typeof(ParticleSystem));
            var particles = go.GetComponent<ParticleSystem>();
            var resetter = go.AddComponent<PoolResetter>();
            particles.Play();
            Assert.IsTrue(particles.isPlaying);

            resetter.ResetForReuse(go);

            Assert.IsFalse(particles.isPlaying, "particles must be stopped on release");
            Object.DestroyImmediate(go);
        }

        [Test]
        public void PoolRegistry_SpawnAndDespawn_ReuseInstance()
        {
            var prefab = prefabHolder.AddComponent<PoolSpy>();

            var first = PoolRegistry.Spawn(prefab);
            PoolRegistry.Despawn(first);
            var second = PoolRegistry.Spawn(prefab);

            Assert.AreSame(first, second, "registry facade must reuse pooled instances");
            PoolRegistry.ClearAll();
        }

        [Test]
        public void PoolRegistry_RegisterExplicitPool_IsUsedForSpawn()
        {
            var prefab = prefabHolder.AddComponent<PoolSpy>();
            var pool = new ComponentPool<PoolSpy>(prefab, warmup: 2);
            PoolRegistry.Register(prefab, pool);

            var instance = PoolRegistry.Spawn(prefab);

            Assert.AreEqual(2, pool.InstantiateCount, "spawn must come from the explicitly registered warm pool, not a lazy one");
            Assert.IsTrue(pool.Contains(instance));
            Object.DestroyImmediate(instance.gameObject);
            pool.Shrink(0);
            Object.DestroyImmediate(pool.PoolRoot);
        }

        [Test]
        public void PoolRegistry_ClearAll_EmptiesRegistry()
        {
            var prefab = prefabHolder.AddComponent<PoolSpy>();
            var a = PoolRegistry.Spawn(prefab);
            PoolRegistry.Despawn(a);

            PoolRegistry.ClearAll();
            var b = PoolRegistry.Spawn(prefab);

            Assert.AreNotSame(a, b, "after ClearAll a new lazy pool must be built (a was disposed)");
            Object.DestroyImmediate(b.gameObject);
            PoolRegistry.ClearAll();
        }
    }
}
