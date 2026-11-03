using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.View.Diagnostics;

namespace ZFrame.View
{
    /// <summary>
    /// Asynchronous scene loader with eased progress, input blocking, EventBus
    /// events, cross-scene payload handoff and before-unload hooks
    /// (TASK-A-M2-01).
    ///
    /// MOUNTING (Core zero-awareness): <see cref="GameRoot"/> lives in
    /// ZFrame.Core which must never reference ZFrame.View. Exactly like the
    /// DebugPanelRoot precedent, this component self-bootstraps via
    /// <see cref="RuntimeInitializeOnLoadMethod"/> (BeforeSceneLoad) by
    /// creating its own DontDestroyOnLoad host GameObject — the Core layer
    /// has no knowledge of this class; the dependency edge is View → Core
    /// only. <see cref="SceneLoaderFacade"/> is the stable static entry other
    /// layers call.
    ///
    /// Load sequence per <see cref="LoadSceneAsync"/>:
    ///  1. <see cref="NotifyBeforeSceneUnload"/> — fires
    ///     <see cref="OnBeforeSceneUnload"/> (save-system hook point), then
    ///     releases unused assets via
    ///     <see cref="IResourceService.UnloadAllUnused"/> and shrinks all
    ///     object pools via <see cref="PoolRegistry.ClearAll"/> (方案 §6.5).
    ///  2. Fires <see cref="SceneLoadStartedEvent"/> on the EventBus.
    ///  3. Acquires <see cref="InputBlocker"/> and starts
    ///     <c>SceneManager.LoadSceneAsync</c> with
    ///     <c>allowSceneActivation = false</c>.
    ///  4. Pumps eased progress (<see cref="SceneProgressMath"/>) as
    ///     <see cref="SceneLoadProgressEvent"/>; after raw progress reaches
    ///     the 0.9 gate the bar eases to 1 over a short minimum window, then
    ///     activation is allowed.
    ///  5. On activation: fires <see cref="SceneLoadedEvent"/>, releases the
    ///     InputBlocker and publishes the payload for
    ///     <see cref="ConsumePayload"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneLoader : MonoBehaviour
    {
        private const string LogChannel = "Scene";

        /// <summary>Minimum seconds the bar spends easing from 0.9 to 1 after the gate — keeps short loads readable.</summary>
        private const float MinTailSeconds = 0.4f;

        private static SceneLoader _instance;

        /// <summary>
        /// Before-unload hook: raised at the start of every load, BEFORE the
        /// old scene unloads. Subscribe here for per-scene teardown (save
        /// flush, transient state). The built-in resource/pool cleanup runs
        /// right after this event returns.
        /// </summary>
        public static event Action OnBeforeSceneUnload;

        /// <summary>The self-bootstrapped instance (null only before boot / after explicit destruction).</summary>
        public static SceneLoader Instance => _instance;

        /// <summary>True between LoadSceneAsync and full activation of the target scene.</summary>
        public static bool IsLoading { get; private set; }

        /// <summary>Displayed (eased, non-regressing) progress 0..1 of the active load.</summary>
        public static float DisplayedProgress { get; private set; }

        /// <summary>Scene currently being loaded (null when idle).</summary>
        public static string LoadingSceneName { get; private set; }

        private AsyncOperation _operation;
        private bool _gateReached;
        private bool _activationRequested;
        private float _tailTime;
        private float _nextProgressMark; // device-log progress marks (TASK-A-AND-03)

        // Payload handoff state is static: the loader is a process-wide
        // singleton (self-bootstrapped host) and the static Consume/TryPeek
        // entry points must reach it even before callers grab the instance.
        private static ScenePayload _pendingPayload;
        private static ScenePayload _currentPayload;

        /// <summary>Self-bootstrap: creates the DontDestroyOnLoad host before the first scene loads. Core is never involved.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            if (_instance != null)
            {
                return;
            }

            var host = new GameObject("~SceneLoaderRoot");
            _instance = host.AddComponent<SceneLoader>();
            DontDestroyOnLoad(host);
        }

        /// <summary>
        /// Starts an asynchronous load of the build-settings scene named
        /// <paramref name="sceneName"/>. The optional <paramref name="payload"/>
        /// is copied and becomes readable via <see cref="ConsumePayload"/>
        /// once the scene is active. Re-entrant calls while a load is already
        /// running are rejected with an error log (fail-safe, not silent).
        /// </summary>
        public static void LoadSceneAsync(string sceneName, IReadOnlyDictionary<string, string> payload = null)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                throw new ArgumentException("SceneLoader: scene name must not be null/empty.", nameof(sceneName));
            }

            if (_instance == null)
            {
                AutoBoot();
            }

            _instance.BeginLoad(sceneName, payload);
        }

        /// <summary>
        /// Consumes one payload value of the ACTIVE scene (read-once: the
        /// entry is removed after this call). Null-safe: returns null when no
        /// payload exists, the key is absent, or no scene was loaded yet.
        /// </summary>
        public static string ConsumePayload(string key)
        {
            return _currentPayload == null ? null : _currentPayload.ConsumePayload(key);
        }

        /// <summary>Reads an active-scene payload value without consuming it.</summary>
        public static bool TryPeekPayload(string key, out string value)
        {
            if (_currentPayload == null)
            {
                value = null;
                return false;
            }

            return _currentPayload.TryPeek(key, out value);
        }

        /// <summary>
        /// Public seam for the before-unload phase (also invoked as step 1 of
        /// every load): raises <see cref="OnBeforeSceneUnload"/>, then runs
        /// the built-in cleanup — ResourceService.UnloadAllUnused (only when
        /// a resource service is registered) and PoolRegistry.ClearAll
        /// (pool shrink, per PoolRegistry's own "call on scene switches"
        /// contract). Unit-testable in EditMode; the full async sequence is
        /// PlayMode smoke scope.
        /// </summary>
        public static void NotifyBeforeSceneUnload()
        {
            OnBeforeSceneUnload?.Invoke();

            if (Services.TryGet(out IResourceService resourceService))
            {
                resourceService.UnloadAllUnused();
            }

            PoolRegistry.ClearAll();
        }

        private void BeginLoad(string sceneName, IReadOnlyDictionary<string, string> payload)
        {
            if (IsLoading)
            {
                ZLog.Error(LogChannel,
                    $"LoadSceneAsync('{sceneName}') rejected: a load of '{LoadingSceneName}' is already running.");
                return;
            }

            ZLog.Info(LogChannel, $"Loading scene '{sceneName}' (async, gated).");
            DeviceFileLogger.Step($"SceneLoader.BeginLoad('{sceneName}') begin");
            NotifyBeforeSceneUnload();
            DeviceFileLogger.Step($"SceneLoader.BeginLoad('{sceneName}') before-unload phase done");

            EventBus.Fire(new SceneLoadStartedEvent(sceneName));

            _operation = SceneManager.LoadSceneAsync(sceneName);
            if (_operation == null)
            {
                ZLog.Error(LogChannel, $"LoadSceneAsync('{sceneName}') failed: scene not in build settings?");
                DeviceFileLogger.Step($"SceneLoader.BeginLoad('{sceneName}') FAILED: operation is null (scene not in build settings?)");
                return;
            }

            _operation.allowSceneActivation = false;

            _pendingPayload = new ScenePayload(payload);
            LoadingSceneName = sceneName;
            DisplayedProgress = 0f;
            _gateReached = false;
            _activationRequested = false;
            _tailTime = 0f;
            _nextProgressMark = 0.2f;
            IsLoading = true;

            DeviceFileLogger.Step($"SceneLoader.BeginLoad('{sceneName}') armed (async op acquired, activation gated, input blocked)");
            InputBlocker.Acquire();
        }

        private void Update()
        {
            if (!IsLoading || _operation == null)
            {
                return;
            }

            float target = SceneProgressMath.Remap(_operation.progress);

            // Transition-only device-log marks: every 0.2 raw progress, the
            // 0.9 gate and the activation request (never per-frame output).
            if (_operation.progress >= _nextProgressMark && _nextProgressMark < SceneProgressMath.ActivationGate)
            {
                DeviceFileLogger.Step($"SceneLoader '{LoadingSceneName}' raw progress {_operation.progress:0.00}");
                _nextProgressMark += 0.2f;
            }

            if (!_gateReached && _operation.progress >= SceneProgressMath.ActivationGate)
            {
                _gateReached = true;
                DeviceFileLogger.Step($"SceneLoader '{LoadingSceneName}' activation gate {SceneProgressMath.ActivationGate:0.0} reached");
            }

            if (_gateReached)
            {
                _tailTime += Time.unscaledDeltaTime / MinTailSeconds;
                float easedTail = SceneProgressMath.ActivationGate
                                  + (1f - SceneProgressMath.ActivationGate) * SceneProgressMath.EaseTail(_tailTime);
                target = SceneProgressMath.NonRegressing(target, easedTail);

                if (_tailTime >= 1f && !_activationRequested)
                {
                    _activationRequested = true;
                    _operation.allowSceneActivation = true;
                    DeviceFileLogger.Step($"SceneLoader '{LoadingSceneName}' allowSceneActivation=true (scene activates next frames)");
                }
            }

            float displayed = SceneProgressMath.NonRegressing(DisplayedProgress, target);
            if (displayed != DisplayedProgress)
            {
                DisplayedProgress = displayed;
                EventBus.Fire(new SceneLoadProgressEvent(LoadingSceneName, DisplayedProgress));
            }

            if (_operation.isDone)
            {
                FinishLoad();
            }
        }

        private void FinishLoad()
        {
            EventBus.Fire(new SceneLoadProgressEvent(LoadingSceneName, 1f));

            _currentPayload = _pendingPayload;
            _pendingPayload = null;
            _operation = null;
            _gateReached = false;
            _activationRequested = false;
            _tailTime = 0f;
            _nextProgressMark = 0f;
            IsLoading = false;
            DisplayedProgress = 1f;

            InputBlocker.Release();

            EventBus.Fire(new SceneLoadedEvent(LoadingSceneName));
            ZLog.Info(LogChannel, $"Scene '{LoadingSceneName}' loaded.");
            DeviceFileLogger.Step($"SceneLoader.FinishLoad: '{LoadingSceneName}' loaded");
            LoadingSceneName = null;
        }

        private void OnGUI()
        {
            if (IsLoading)
            {
                SceneLoadingOverlay.Draw(DisplayedProgress, LoadingSceneName);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
