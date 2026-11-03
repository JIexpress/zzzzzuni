using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ZFrame.Data.Addressables
{
    // Our own namespace is literally named "Addressables", which would shadow
    // the UnityEngine.AddressableAssets.Addressables class on every use — an
    // explicit alias declared inside the namespace wins the lookup.
    using UnityAddressables = UnityEngine.AddressableAssets.Addressables;
    /// <summary>
    /// The one real <see cref="IAddressablesAdapter"/>: every static
    /// UnityEngine.Addressables call of the Addressables backend lives here.
    ///
    /// Editor playback: the adapter points <c>UnityAddressables.InitializeAsync</c>
    /// at the project's AddressableAssetSettings asset via the same
    /// <c>GUID:</c> mechanism the package's own BuildScriptFastMode uses —
    /// which yields the "Use Asset Database (fastest)" play mode (an
    /// <c>AddressableAssetSettingsLocator</c> over the committed settings,
    /// loading through AssetDatabaseProvider) WITHOUT any content build and
    /// WITHOUT the package's EditorBuildSettings default-object hook. This
    /// lets the settings live at a non-default folder. In real player builds
    /// the whole block is compiled out and initialization falls back to the
    /// standard catalog path (<c>RuntimePath + "/settings.json"</c>).
    /// </summary>
    public sealed class AddressablesRuntimeAdapter : IAddressablesAdapter
    {
        /// <summary>Process-wide singleton (the Addressables API itself is static).</summary>
        public static AddressablesRuntimeAdapter Instance { get; } = new AddressablesRuntimeAdapter();

        private AddressablesRuntimeAdapter()
        {
        }

        /// <inheritdoc />
        public async Task<AddressablesLoadResult> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("AddressablesRuntimeAdapter: key must not be null or empty.", nameof(key));
            }

            EnsureEditorPlaybackDataPath();

            // Idempotent — a completed/no-op handle comes back once initialized.
            var init = UnityAddressables.InitializeAsync();
            if (!init.IsDone)
            {
                await init.Task;
            }

            var handle = UnityAddressables.LoadAssetAsync<T>(key);
            var completion = new TaskCompletionSource<AddressablesLoadResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            handle.Completed += op =>
            {
                if (op.Status == AsyncOperationStatus.Failed)
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"AddressablesRuntimeAdapter: load failed for '{key}': " +
                        $"{op.OperationException?.Message ?? "unknown error"}", op.OperationException));
                    return;
                }

                if (op.Result == null)
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"AddressablesRuntimeAdapter: load returned null for '{key}' ({typeof(T).Name})."));
                    return;
                }

                completion.TrySetResult(new AddressablesLoadResult(op.Result, new NativeHandle(op)));
            };

            return await completion.Task;
        }

        /// <inheritdoc />
        public void Release(IAddressablesHandle handle)
        {
            if (handle is NativeHandle native)
            {
                // IsValid goes false once released; guards a stray double release.
                if (native.Value.IsValid())
                {
                    UnityAddressables.Release(native.Value);
                }
            }
        }

        /// <inheritdoc />
        public bool Exists(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var found = false;
            foreach (var locator in UnityAddressables.ResourceLocators)
            {
                if (locator.Locate(key, typeof(UnityEngine.Object), out var locations) && locations != null && locations.Count > 0)
                {
                    found = true;
                    break;
                }
            }

            return found;
        }

        /// <summary>
        /// Editor-only: mirrors BuildScriptFastMode — sets the runtime-data
        /// PlayerPrefs key to <c>GUID:&lt;settings asset guid&gt;</c> so
        /// <see cref="UnityAddressables.InitializeAsync"/> builds the asset
        /// database locator from the committed settings asset. Runs before
        /// every init attempt, so a stale GUID from an earlier session
        /// self-heals. No-op outside the editor (real builds use the catalog).
        /// </summary>
        private static void EnsureEditorPlaybackDataPath()
        {
#if UNITY_EDITOR
            var guid = FindSettingsAssetGuid();
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError(
                    "AddressablesRuntimeAdapter: no AddressableAssetSettings asset in the project. " +
                    "Run ZFrame/Tests/Addressables/Generate Test Setup (or create Addressables settings) first.");
                return;
            }

            var desired = $"GUID:{guid}";
            if (!string.Equals(PlayerPrefs.GetString(UnityAddressables.kAddressablesRuntimeDataPath, string.Empty), desired,
                    StringComparison.Ordinal))
            {
                PlayerPrefs.SetString(UnityAddressables.kAddressablesRuntimeDataPath, desired);
            }
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Finds the project's AddressableAssetSettings asset by type search
        /// (no dependency on the editor assembly). Deterministic: an asset
        /// under Assets/ZFrame/Tests/AddressablesData wins over any other.
        /// </summary>
        private static string FindSettingsAssetGuid()
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:AddressableAssetSettings");
            if (guids == null || guids.Length == 0)
            {
                return null;
            }

            foreach (var guid in guids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (path.Replace('\\', '/').Contains("ZFrame/Tests/AddressablesData"))
                {
                    return guid;
                }
            }

            if (guids.Length > 1)
            {
                Debug.LogWarning(
                    $"AddressablesRuntimeAdapter: {guids.Length} AddressableAssetSettings assets found; " +
                    $"using '{UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0])}'.");
            }

            return guids[0];
        }
#endif

        /// <summary>Real handle: wraps the native operation until release.</summary>
        private sealed class NativeHandle : IAddressablesHandle
        {
            /// <summary>Non-generic wrap (any typed handle converts implicitly).</summary>
            internal AsyncOperationHandle Value { get; }

            internal NativeHandle(AsyncOperationHandle value)
            {
                Value = value;
            }
        }
    }
}
