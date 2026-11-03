using System;
using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// Stable static entry point for the scene system. Thin forwarder over
    /// <see cref="SceneLoader"/> (which owns the actual MonoBehaviour,
    /// self-bootstrapped via <see cref="RuntimeInitializeOnLoadMethod"/> —
    /// see SceneLoader remarks for the Core zero-awareness mounting notes).
    /// Other layers (Gameplay/Game) should call this facade, not the
    /// MonoBehaviour, so the implementation can move behind the same surface.
    /// </summary>
    public static class SceneLoaderFacade
    {
        /// <summary>True while a scene load is running.</summary>
        public static bool IsLoading => SceneLoader.IsLoading;

        /// <summary>Displayed (eased, non-regressing) progress 0..1 of the active load.</summary>
        public static float DisplayedProgress => SceneLoader.DisplayedProgress;

        /// <summary>Scene currently being loaded (null when idle).</summary>
        public static string LoadingSceneName => SceneLoader.LoadingSceneName;

        /// <summary>
        /// Before-unload hook (forwards to <see cref="SceneLoader.OnBeforeSceneUnload"/>):
        /// raised at the start of every load, before the old scene unloads.
        /// </summary>
        public static event Action OnBeforeSceneUnload
        {
            add => SceneLoader.OnBeforeSceneUnload += value;
            remove => SceneLoader.OnBeforeSceneUnload -= value;
        }

        /// <summary>Starts an asynchronous, input-blocked, progress-eased scene load with an optional payload.</summary>
        public static void LoadSceneAsync(string sceneName, IReadOnlyDictionary<string, string> payload = null)
        {
            SceneLoader.LoadSceneAsync(sceneName, payload);
        }

        /// <summary>Consumes one payload value of the active scene (read-once, null-safe).</summary>
        public static string ConsumePayload(string key)
        {
            return SceneLoader.ConsumePayload(key);
        }

        /// <summary>Reads an active-scene payload value without consuming it.</summary>
        public static bool TryPeekPayload(string key, out string value)
        {
            return SceneLoader.TryPeekPayload(key, out value);
        }
    }
}
