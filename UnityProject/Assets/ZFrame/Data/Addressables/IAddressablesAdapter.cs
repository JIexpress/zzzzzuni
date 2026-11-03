using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.Data.Addressables
{
    /// <summary>
    /// Thin seam over the static UnityEngine.Addressables API. All static
    /// calls live behind this interface so EditMode tests can inject a fake
    /// (counting adapter) instead of an initialized Addressables runtime —
    /// the real Addressables path is covered by one PlayMode smoke test.
    /// One implementation per process (<see cref="AddressablesRuntimeAdapter"/>).
    /// </summary>
    public interface IAddressablesAdapter
    {
        /// <summary>
        /// Begins loading the asset registered under <paramref name="key"/>
        /// as <typeparamref name="T"/>. The task completes with the loaded
        /// asset and its release handle; it faults (instead of completing
        /// with null) when the key is unknown or the load fails.
        /// </summary>
        Task<AddressablesLoadResult> LoadAsync<T>(string key) where T : Object;

        /// <summary>
        /// Releases one previously returned load handle. Must be called at
        /// most once per handle (the provider's reference counting
        /// guarantees this); adapters defensively ignore stale handles.
        /// </summary>
        void Release(IAddressablesHandle handle);

        /// <summary>
        /// Synchronous existence probe: does any registered resource locator
        /// resolve <paramref name="key"/>? Returns false when Addressables
        /// has not been initialized yet (no locators registered).
        /// </summary>
        bool Exists(string key);
    }
}
