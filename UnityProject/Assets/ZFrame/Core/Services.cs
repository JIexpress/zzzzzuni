using System;
using System.Collections.Generic;

namespace ZFrame.Core
{
    /// <summary>
    /// Static service locator for ZFrame. Register concrete instances (or
    /// single implementations of an interface) during boot; consumers resolve
    /// them via <see cref="Get{T}"/>. Convention: register against interfaces
    /// whenever possible (<c>Services.Register&lt;ISaveService&gt;(new
    /// SaveService())</c>) so implementations can be swapped without touching
    /// call sites.
    /// </summary>
    /// <remarks>
    /// Main-thread only. Registration is expected to happen during boot;
    /// runtime code should only resolve. Duplicate registration and resolving
    /// an unregistered service both throw — fail fast in development instead
    /// of silently returning null.
    /// </remarks>
    public static class Services
    {
        private static readonly Dictionary<Type, object> Map = new Dictionary<Type, object>();

        /// <summary>Registers <paramref name="instance"/> as the service for type <typeparamref name="T"/>. Main-thread only.</summary>
        /// <exception cref="InvalidOperationException">A service of this type is already registered.</exception>
        public static void Register<T>(T instance)
        {
            var key = typeof(T);
            if (Map.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"Services: service of type '{key.FullName}' is already registered. " +
                    "Unregister it first if you intend to replace it.");
            }

            Map[key] = instance;
        }

        /// <summary>Resolves the service registered for type <typeparamref name="T"/>. Main-thread only.</summary>
        /// <exception cref="InvalidOperationException">No service of this type is registered.</exception>
        public static T Get<T>()
        {
            if (Map.TryGetValue(typeof(T), out var value))
            {
                return (T)value;
            }

            throw new InvalidOperationException(
                $"Services: no service registered for '{typeof(T).FullName}'. Registered services: " +
                (Map.Count == 0 ? "(none)" : string.Join(", ", GetAllRegistered())) + ".");
        }

        /// <summary>Attempts to resolve the service for type <typeparamref name="T"/> without throwing.</summary>
        public static bool TryGet<T>(out T service)
        {
            if (Map.TryGetValue(typeof(T), out var value))
            {
                service = (T)value;
                return true;
            }

            service = default;
            return false;
        }

        /// <summary>Unregisters the service for type <typeparamref name="T"/>. No-op when absent.</summary>
        public static void Unregister<T>()
        {
            Map.Remove(typeof(T));
        }

        /// <summary>Removes all registrations. Intended for tests and full shutdown.</summary>
        public static void Clear()
        {
            Map.Clear();
        }

        /// <summary>Returns the type names of all registered services, for debug panels.</summary>
        public static IEnumerable<string> GetAllRegistered()
        {
            foreach (var key in Map.Keys)
            {
                yield return key.FullName;
            }
        }
    }
}
