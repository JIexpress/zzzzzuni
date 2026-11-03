using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.Data.Addressables
{
    /// <summary>
    /// <see cref="IAssetProvider"/> over Unity Addressables — the second
    /// backend behind <see cref="ResourceService"/> (方案 §4.1：Resources 切
    /// Addressables 业务零改动). Loading is real asynchronous work (unlike
    /// <see cref="ResourcesProvider"/>, whose async signature hides a
    /// synchronous load).
    ///
    /// Reference counting: one underlying adapter load per key, reused for
    /// every <see cref="LoadAsync{T}"/> while any lease is alive (including
    /// concurrent in-flight loads of the same key). <see cref="Release"/>
    /// drops one lease; at zero the underlying handle is released and the
    /// entry is dropped. Instance pooling is NOT provider business —
    /// <see cref="ResourceService.InstantiateAsync"/> routes instantiation
    /// through <see cref="ZFrame.Core.PoolRegistry"/> exactly as it does for
    /// Resources, so the two backends are behaviorally identical there.
    ///
    /// Main-thread only (Addressables operations and Unity objects).
    /// Keys must be loaded with a consistent <typeparamref name="T"/> — a
    /// reuse with a mismatching type throws, mirroring the
    /// <see cref="ResourceService"/> cast semantics.
    /// </summary>
    public sealed class AddressablesProvider : IAssetProvider
    {
        /// <summary>Provider-side record for one key: one underlying handle + lease count.</summary>
        private sealed class Entry
        {
            /// <summary>Live leases (LoadAsync successes not yet Release()d).</summary>
            public int RefCount;

            /// <summary>The loaded asset (null while the first load is in flight).</summary>
            public UnityEngine.Object Asset;

            /// <summary>The single underlying load; non-null while shared, nulled after completion.</summary>
            public Task<AddressablesLoadResult> InFlight;

            /// <summary>The completed load outcome (handle used at release time).</summary>
            public AddressablesLoadResult Load;
        }

        private readonly IAddressablesAdapter _adapter;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private int _pending;

        /// <summary>Creates the provider on top of the real Addressables runtime.</summary>
        public AddressablesProvider()
            : this(AddressablesRuntimeAdapter.Instance)
        {
        }

        /// <summary>Creates the provider on top of an injected adapter (EditMode test seam).</summary>
        public AddressablesProvider(IAddressablesAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        /// <summary>Number of loads currently in flight.</summary>
        public int PendingLoadCount => _pending;

        /// <summary>Number of keys currently tracked (leased or in flight).</summary>
        public int TrackedKeyCount => _entries.Count;

        /// <summary>Current lease count of <paramref name="key"/> (0 when untracked). For tests and diagnostics.</summary>
        public int GetRefCount(string key)
        {
            return _entries.TryGetValue(key, out var entry) ? entry.RefCount : 0;
        }

        /// <inheritdoc />
        public async Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AddressablesProvider: key must not be null or empty.", nameof(key));
            }

            // Completed entry: reuse — the second load makes NO adapter call.
            if (_entries.TryGetValue(key, out var entry) && entry.Asset != null)
            {
                entry.RefCount++;
                return (T)entry.Asset;
            }

            if (entry == null)
            {
                // First load for the key: exactly one underlying adapter call.
                entry = new Entry();
                _entries[key] = entry;
                entry.InFlight = _adapter.LoadAsync<T>(key);
            }
            // else: a load is already in flight for the key — share it
            // (still exactly one underlying call; covers concurrent
            // ResourceService awaits of the same key).

            entry.RefCount++;
            _pending++;
            try
            {
                var result = await entry.InFlight;

                // Idempotent for concurrent sharers completing together.
                entry.Asset = result.Asset;
                entry.Load = result;
                entry.InFlight = null;

                if (entry.RefCount <= 0)
                {
                    // Defensive: leases cannot hit zero while an awaiter holds
                    // one (main-thread only), but if it ever happens, release
                    // instead of leaking the handle.
                    _adapter.Release(result.Handle);
                    if (_entries.TryGetValue(key, out var current) && current == entry)
                    {
                        _entries.Remove(key);
                    }
                }

                return (T)result.Asset;
            }
            catch
            {
                entry.RefCount--;
                if (entry.RefCount <= 0 && _entries.TryGetValue(key, out var current) && current == entry &&
                    entry.Asset == null)
                {
                    _entries.Remove(key); // failed load leaves no dangling entry
                }

                throw;
            }
            finally
            {
                _pending--;
            }
        }

        /// <inheritdoc />
        public void Release(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AddressablesProvider: key must not be null or empty.", nameof(key));
            }

            if (!_entries.TryGetValue(key, out var entry))
            {
                Debug.LogWarning($"AddressablesProvider: Release called for '{key}' which is not tracked. Ignoring.");
                return;
            }

            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                return;
            }

            // Zero leases: drop the underlying handle and the entry.
            if (entry.Load != null)
            {
                _adapter.Release(entry.Load.Handle);
            }

            _entries.Remove(key);
        }

        /// <inheritdoc />
        public bool Exists(string key)
        {
            return _adapter.Exists(key);
        }
    }
}
