using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Seam reserved for list-item / widget pooling (TASK-A-M3-01 leaves the
    /// interface, not the implementation): scrolling lists that spawn many
    /// row prefabs should rent/return instances through this abstraction so a
    /// pool-backed implementation can be added in a later milestone without
    /// touching panel code. Panel instances themselves are NOT pooled through
    /// it — they use <see cref="PanelConfig.CachePolicy"/> instead.
    /// </summary>
    public interface IUIWidgetPool
    {
        /// <summary>Rents (or instantiates) a widget of <paramref name="prefabKey"/> under <paramref name="parent"/>.</summary>
        T Rent<T>(string prefabKey, Transform parent) where T : Component;

        /// <summary>Returns a rented widget to the pool (or destroys it when it never came from one).</summary>
        void Return<T>(T instance) where T : Component;
    }
}
