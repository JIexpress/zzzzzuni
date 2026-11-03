using System;
using System.Threading.Tasks;
using UnityEngine;
using ZFrame.Core;

namespace ZFrame.Data
{
    /// <summary>
    /// Static facade over the registered <see cref="IResourceService"/>
    /// (shorthand for <c>Services.Get&lt;IResourceService&gt;()</c>). Requires
    /// <see cref="ZFrameData.Init"/> to have run — resolving before that
    /// throws with a pointed message instead of a null service.
    /// </summary>
    public static class Res
    {
        /// <summary>The registered resource service; throws when ZFrameData.Init has not run.</summary>
        public static IResourceService Service => Get();

        /// <summary>Shorthand for <see cref="IResourceService.LoadAsync{T}(string)"/>.</summary>
        public static Task<T> LoadAsync<T>(string key) where T : UnityEngine.Object
        {
            return Get().LoadAsync<T>(key);
        }

        /// <summary>Shorthand for <see cref="IResourceService.Release"/>.</summary>
        public static void Release(string key)
        {
            Get().Release(key);
        }

        /// <summary>Shorthand for <see cref="IResourceService.InstantiateAsync"/>.</summary>
        public static Task<GameObject> InstantiateAsync(string key, Transform parent = null)
        {
            return Get().InstantiateAsync(key, parent);
        }

        /// <summary>Shorthand for <see cref="IResourceService.ReleaseInstance"/>.</summary>
        public static void ReleaseInstance(GameObject instance)
        {
            Get().ReleaseInstance(instance);
        }

        /// <summary>Shorthand for <see cref="IResourceService.UnloadAllUnused"/>.</summary>
        public static void UnloadAllUnused()
        {
            Get().UnloadAllUnused();
        }

        private static IResourceService Get()
        {
            if (Services.TryGet(out IResourceService service))
            {
                return service;
            }

            throw new InvalidOperationException(
                "Res: no IResourceService registered. Call ZFrameData.Init() (e.g. from Bootstrap) " +
                "before using the Res facade.");
        }
    }
}
