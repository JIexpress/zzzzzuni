using System;
using System.Collections.Generic;
using Game.Domain.Interaction;
using Game.View.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Data.Config;
using ZFrame.Data.Save;
using ZFrame.Gameplay;
using ZFrame.View;
using ZFrame.View.Diagnostics;

namespace Game.Bootstrap
{
    /// <summary>
    /// Boot chain + game flow of the M4 vertical slice (TASK-A-M4-02). Lives
    /// on the boot scene (SCN_Boot carries a dedicated "GameBootstrap"
    /// GameObject, assembled by Game.Bootstrap.Editor.BootSceneSetup) and
    /// drives, in order:
    ///
    ///   GameRoot.Bootstrap (loop/clock/services) → ZFrameData.Init
    ///   (resource/config/save services) → services readiness check →
    ///   input/UI stack up → main menu panel (UIManager.Push) →
    ///   开始/继续 → SceneLoader.LoadSceneAsync("SCN_Game", payload).
    ///
    /// Gameplay-state duties while SCN_Game is active:
    ///  - Esc (PauseInputEvent) opens the pause panel and freezes the Logic
    ///    tick (GameTime.IsPaused, the M0-04 freeze semantics) plus the engine
    ///    clock (Time.timeScale = 0, because the slice's PlayerMotor/EnemyView
    ///    consume scaled Time.deltaTime directly);
    ///  - the HUD (GameHud) is spawned per game-scene load;
    ///  - save wiring: the two ISaveable modules (inventory + player
    ///    position) register on scene activation; the SceneLoader
    ///    OnBeforeSceneUnload hook writes slot 0 when leaving SCN_Game
    ///    (回主菜单自动存档) and resets the shared interaction world;
    ///  - 主菜单"继续" reads slot 0 after SCN_Game activates.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameBootstrap : MonoBehaviour
    {
        /// <summary>Name of the gameplay scene (build settings index 2).</summary>
        public const string GameSceneName = "SCN_Game";

        /// <summary>Name of the boot scene (build settings index 0).</summary>
        public const string BootSceneName = "SCN_Boot";

        /// <summary>Payload key that asks the game scene to load slot 0 on activation.</summary>
        public const string LoadPayloadKey = "load";

        private static GameBootstrap _instance;

        private UIManager _ui;
        private SettingsModel _settingsModel;
        private IDisposable _pauseBlocker;
        private GameHud _hud;
        private readonly List<IDisposable> _tokens = new List<IDisposable>();

        /// <summary>Live bootstrap instance (null before boot / after teardown).</summary>
        public static GameBootstrap Instance => _instance;

        /// <summary>True while the slice is paused (GameTime.IsPaused mirror for tests/debug).</summary>
        public bool IsPaused => Services.TryGet(out GameTime time) && time.IsPaused;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.Log("[GameBootstrap] duplicate boot object destroyed (existing bootstrap stays).", this);
                DeviceFileLogger.Step("GameBootstrap.Awake: duplicate boot object destroyed");
                // Only THIS GameObject dies; the DontDestroyOnLoad owner keeps
                // its subscriptions. NOTE: never co-locate GameBootstrap with a
                // GameRoot component — the framework guarantees a GameRoot via
                // AudioService (BeforeSceneLoad EnsureInstance), and GameRoot's
                // duplicate handling destroys the whole GameObject.
                Destroy(gameObject);
                BootCamera.Ensure(); // 回主菜单：the reloaded SCN_Boot needs its presenter again
                _instance.EnsureMainMenu(); // menu must exist after any SCN_Boot (re)load
                return;
            }

            _instance = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }

            Debug.Log("[GameBootstrap] Awake: full boot starts.", this);
            DeviceFileLogger.Step("GameBootstrap.Awake: full boot starts");

            // The device file log is the ONLY diagnostic channel when a ROM
            // suppresses Unity logcat — if it failed to arm, say so ON SCREEN
            // (screencap-visible) instead of silently going blind again.
            // TASK-A-AND-03 V2-F1: dev-gated (same #if as DeviceFileLogger) —
            // in a non-development build the IsArmed stub is constantly false
            // (DeviceFileLogger.cs #else branch), so an ungated call here
            // would show a permanent "BOOT ERROR" on every release start.
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (!DeviceFileLogger.IsArmed)
            {
                BootErrorOverlay.Show(
                    "device file log NOT armed (zframe_boot.log missing):\n"
                    + DeviceFileLogger.LastError);
            }
#endif

            // Idempotent boot: covers the case where this Awake runs before
            // AudioService's BeforeSceneLoad EnsureInstance had a chance to
            // create the GameRoot instance (services only; the driver comes
            // with the GameRoot instance itself).
            //
            // TASK-A-AND-03 diagnosis semantics: every step is independent.
            // A step that throws is logged (device file + engine log), shown
            // on screen (BootErrorOverlay) and the chain CONTINUES with the
            // next step — one broken service must not blind the rest of the
            // boot (its dependent steps fail with their own visible errors).
            RunStep("GameRoot.Bootstrap", () => GameRoot.Bootstrap());

            // 1. Data layer: resource / config / save services.
            RunStep("ZFrameData.Init", () => ZFrameData.Init());

            // 2. Services readiness check (fail fast on a broken boot).
            RunStep("services readiness check", () =>
            {
                Services.Get<IGameTime>();
                Services.Get<IResourceService>();
                Services.Get<IConfigService>();
                Services.Get<ISaveSystem>();
                ZLog.Info("Boot", "services ready: " + string.Join(", ", Services.GetAllRegistered()));
            });

            // 3. Input + UI stacks, then the main menu panel.
            RunStep("InputRuntime.EnsureCreated", () => InputRuntime.EnsureCreated());
            RunStep("UIRoot.EnsureCreated", () => UIRoot.EnsureCreated());
            RunStep("UIManager + bindings", () =>
            {
                _ui = new UIManager(Services.Get<IResourceService>(), UIRoot.Instance);
                RegisterBindings();
            });

            RunStep("event subscriptions", () =>
            {
                _tokens.Add(EventBus.Subscribe<SceneLoadedEvent>(OnSceneLoaded));
                _tokens.Add(EventBus.Subscribe<PauseInputEvent>(OnPauseInput));
                SceneLoader.OnBeforeSceneUnload += OnBeforeSceneUnload;
            });

            // TASK-A-AND-03 root cause: SCN_Boot has no camera, so a URP player
            // presents no frame at all — menu (overlay UI) included — and the
            // device shows the stale startup gray forever. Provide the
            // presentation anchor before pushing the menu.
            RunStep("BootCamera.Ensure", () => BootCamera.Ensure());

            _ = PushMainMenuAsync();
            DeviceFileLogger.Step("GameBootstrap.Awake end (menu push is async)");

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // On-screen boot status (screen is the only trustworthy display
            // channel when the ROM suppresses logcat). Dev builds only.
            int canvasCount = UIRoot.Instance != null ? UIRoot.Instance.LayerCanvasCount : -1;
            BootErrorOverlay.Info(
                $"boot: steps={DeviceFileLogger.StepCount} devlog={DeviceFileLogger.IsArmed}"
                + $" uiCanvases={canvasCount}");
            DumpMenuStateToOverlay();
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        /// <summary>
        /// TASK-A-AND-03 instrument: one-line live state of the pushed main
        /// menu (active/canvas/alpha/rect/scale) onto the on-screen overlay —
        /// distinguishes "menu not instantiated" from "instantiated but not
        /// presented" without needing logcat.
        /// </summary>
        private void DumpMenuStateToOverlay()
        {
            Game.View.UI.MainMenuView menu = FindObjectOfType<Game.View.UI.MainMenuView>();
            if (menu == null)
            {
                BootErrorOverlay.Info("menu: INSTANCE NOT FOUND after push");
                return;
            }

            RectTransform rect = menu.transform as RectTransform;
            CanvasGroup group = menu.GetComponent<CanvasGroup>();
            Canvas canvas = menu.GetComponentInParent<Canvas>();
            BootErrorOverlay.Info(string.Format(
                "menu: active={0} canvas={1}(on={2} order={3} mode={4}) alpha={5} size={6} pos={7} scale={8}",
                menu.gameObject.activeInHierarchy,
                canvas != null ? canvas.name : "null",
                canvas != null && canvas.enabled,
                canvas != null ? canvas.sortingOrder : -999,
                canvas != null ? canvas.renderMode : UnityEngine.RenderMode.ScreenSpaceOverlay,
                group != null ? group.alpha : -1f,
                rect != null ? rect.rect.size : Vector2.zero,
                rect != null ? rect.position : Vector3.zero,
                rect != null ? rect.lossyScale : Vector3.zero));
        }
#endif

        /// <summary>
        /// TASK-A-AND-03 boot-step wrapper: bracket <paramref name="action"/>
        /// with device-file step markers; on throw, record the exception, show
        /// it on screen and swallow — the next boot step still runs (per-step
        /// independence semantics, documented in the task report).
        /// </summary>
        private void RunStep(string name, Action action)
        {
            DeviceFileLogger.Step(name + " ...");
            try
            {
                action();
                DeviceFileLogger.Step(name + " done");
            }
            catch (Exception error)
            {
                DeviceFileLogger.Step(name + " FAILED");
                DeviceFileLogger.Exception("boot step " + name, error);
                ZLog.Error("Boot", $"boot step '{name}' failed: {error.Message}");
                BootErrorOverlay.Show($"boot step '{name}' failed:\n{error}");
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            SceneLoader.OnBeforeSceneUnload -= OnBeforeSceneUnload;
            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            RestorePlayback();
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private float _nextTouchLogTime;
        private float _nextMoveLogTime;
        private Vector3 _lastPlayerPosition;
        private bool _hasPlayerSample;

        /// <summary>
        /// TASK-A-AND-03 touch probe (dev builds only): proves whether touch
        /// input reaches the engine at all — independent of UGUI hit-testing.
        /// Throttled to one line per second so a held finger cannot flood the
        /// device log.
        ///
        /// TASK-A-AND-04 adds a player-position probe: the on-screen stick is
        /// only "working" if the pawn actually travels, and the device file log
        /// is the one channel that survives a ROM that mutes logcat.
        /// </summary>
        private void Update()
        {
            if (Input.touchCount > 0 && Time.unscaledTime >= _nextTouchLogTime)
            {
                _nextTouchLogTime = Time.unscaledTime + 1f;
                Touch touch = Input.GetTouch(0);
                DeviceFileLogger.Step(
                    $"engine sees touch: count={Input.touchCount} phase={touch.phase} pos={touch.position}");
            }

            if (Time.unscaledTime < _nextMoveLogTime)
            {
                return;
            }

            Transform player = FindPlayerTransform();
            if (player == null)
            {
                return;
            }

            Vector3 position = player.position;
            if (!_hasPlayerSample)
            {
                _hasPlayerSample = true;
                _lastPlayerPosition = position;
                DeviceFileLogger.Step($"player sample start: pos={position}");
                return;
            }

            if ((position - _lastPlayerPosition).sqrMagnitude > 1e-4f)
            {
                _nextMoveLogTime = Time.unscaledTime + 0.5f;
                DeviceFileLogger.Step($"player moved: {_lastPlayerPosition} -> {position}");
                _lastPlayerPosition = position;
            }
        }
#endif

        // ---- main menu flow --------------------------------------------------

        /// <summary>Config of the main menu panel: blocking Panel-layer panel, no animation (batch/test friendly).</summary>
        public static PanelConfig MainMenuConfig { get; } = PanelConfig.Create(
            prefabKey: "ui_main_menu",
            layer: UIConsts.UILayer.Panel,
            blockMask: true);

        /// <summary>Config of the pause panel: blocking Panel-layer panel over the HUD.</summary>
        public static PanelConfig PauseConfig { get; } = PanelConfig.Create(
            prefabKey: "ui_pause_panel",
            layer: UIConsts.UILayer.Panel,
            blockMask: true);

        private void RegisterBindings()
        {
            UIBinding.Register<MainMenuView>(view => new MainMenuPresenter((MainMenuView)view));
            UIBinding.Register<PausePanelView>(view => new PausePanelPresenter((PausePanelView)view));
        }

        /// <summary>
        /// Guarantees the main menu is on the stack (no-op when it already is).
        /// Called after boot and after every SCN_Boot (re)load, however it was
        /// loaded (SceneLoader or a direct SceneManager load).
        /// </summary>
        public void EnsureMainMenu()
        {
            if (_ui != null && !_ui.IsOnStack<MainMenuView>())
            {
                _ = PushMainMenuAsync();
            }
        }

        private async System.Threading.Tasks.Task PushMainMenuAsync()
        {
            DeviceFileLogger.Step("PushMainMenuAsync begin (slot check -> UIManager.Push)");
            try
            {
                bool hasSave = Services.Get<ISaveSystem>().GetSlotInfo(0) != null;
                DeviceFileLogger.Step($"PushMainMenuAsync: slot check done (hasSave={hasSave})");

                if (_ui == null)
                {
                    DeviceFileLogger.Step("PushMainMenuAsync: _ui is null (an earlier boot step failed) — menu cannot be pushed");
                    BootErrorOverlay.Show("main menu not pushed: UIManager was never created (see earlier boot errors)");
                    return;
                }

                await _ui.Push<MainMenuView>(
                    MainMenuConfig,
                    MainMenuArgs.Create(StartNewGame, StartContinueGame, OpenSettings, QuitGame, hasSave));
                DeviceFileLogger.Step("PushMainMenuAsync COMPLETE — main menu on stack");
            }
            catch (Exception error)
            {
                DeviceFileLogger.Step("PushMainMenuAsync FAILED");
                DeviceFileLogger.Exception("PushMainMenuAsync", error);
                ZLog.Error("Boot", $"main menu push failed: {error.Message}");
                BootErrorOverlay.Show($"main menu push failed:\n{error}");
            }
        }

        /// <summary>开始：进入 SCN_Game（不读档）。PlayMode smoke calls this path through the menu presenter.</summary>
        public void StartNewGame()
        {
            EnterGame(loadSave: false);
        }

        /// <summary>继续：进入 SCN_Game 并在场景激活后读 slot 0。</summary>
        public void StartContinueGame()
        {
            EnterGame(loadSave: true);
        }

        /// <summary>打开设置面板（主菜单与暂停面板共用同一 UIManager / SettingsModel）。</summary>
        public void OpenSettings()
        {
            if (_ui == null || _ui.IsOnStack<SettingsPanelView>())
            {
                return;
            }

            InputRuntime.EnsureCreated(); // 操作 tab needs the rebind service (tolerant when missing)
            _settingsModel ??= new SettingsModel(SettingsRegistry.BuildDefaults());
            _settingsModel.Load();

            _ = _ui.Push<SettingsPanelView>(
                SettingsPanelView.DefaultConfig,
                SettingsPanelArgs.Create(
                    _settingsModel,
                    () => _ui.Pop(SettingsPanelView.DefaultConfig),
                    InputRuntime.Bindings));
        }

        /// <summary>退出游戏。编辑器 Play 模式下 Application.Quit 不结束编辑器（Demo.md 限制清单已注明）。</summary>
        public void QuitGame()
        {
            ZLog.Info("Boot", "Quit requested.");
            Application.Quit();
        }

        private void EnterGame(bool loadSave)
        {
            if (SceneLoader.IsLoading)
            {
                return;
            }

            DeviceFileLogger.Step($"EnterGame(loadSave={loadSave}) begin");
            RestorePlayback();
            _ui.Pop(MainMenuConfig); // loading overlay (SceneLoader OnGUI) covers the transition

            var payload = new Dictionary<string, string>();
            if (loadSave)
            {
                payload[LoadPayloadKey] = "1";
            }

            SceneLoader.LoadSceneAsync(GameSceneName, payload);
        }

        /// <summary>回主菜单：解除暂停（panel pop + clock restore），SceneLoader 钩子负责存档。</summary>
        public void QuitToMenu()
        {
            if (SceneLoader.IsLoading)
            {
                return;
            }

            ClosePause();
            SceneLoader.LoadSceneAsync(BootSceneName);
        }

        // ---- pause flow ------------------------------------------------------

        private void OnPauseInput(PauseInputEvent evt)
        {
            if (SceneLoader.IsLoading || IsPaused ||
                !string.Equals(SceneManager.GetActiveScene().name, GameSceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            OpenPause();
        }

        /// <summary>Opens the pause panel and freezes gameplay (framework + engine clock).</summary>
        public void OpenPause()
        {
            if (_ui == null || _ui.IsOnStack<PausePanelView>() || SceneLoader.IsLoading)
            {
                return;
            }

            if (Services.TryGet(out GameTime time))
            {
                time.IsPaused = true; // framework semantics: TickQueue Logic stage + Scheduler frozen
            }

            Time.timeScale = 0f; // engine semantics: PlayerMotor/EnemyView consume scaled Time.deltaTime
            _pauseBlocker = InputBlocker.Acquire(); // Player map off — Esc now arrives via PausePanelView polling

            _ = _ui.Push<PausePanelView>(
                PauseConfig,
                PausePanelArgs.Create(ClosePause, OpenSettings, QuitToMenu));
        }

        /// <summary>Closes the pause panel and restores playback. Safe to call when not paused.</summary>
        public void ClosePause()
        {
            if (_ui != null && _ui.IsOnStack<PausePanelView>())
            {
                _ui.Pop(PauseConfig);
            }

            _pauseBlocker?.Dispose();
            _pauseBlocker = null;
            RestorePlayback();
        }

        private static void RestorePlayback()
        {
            if (Services.TryGet(out GameTime time))
            {
                time.IsPaused = false;
            }

            Time.timeScale = 1f;
        }

        // ---- scene lifecycle ---------------------------------------------------

        private void OnBeforeSceneUnload()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, GameSceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                Services.Get<ISaveSystem>().SaveToSlot(0);
                ZLog.Info("Boot", "Leaving SCN_Game — slot 0 saved.");
            }
            catch (System.Exception error)
            {
                ZLog.Error("Boot", $"Slot 0 save failed: {error.Message}");
            }

            InteractionWorld.ResetShared(); // stale Domain entities must not leak into the next scene (M4-01 §5.4)
        }

        private void OnSceneLoaded(SceneLoadedEvent evt)
        {
            DeviceFileLogger.Step(
                $"OnSceneLoaded('{evt.SceneName}') eventSystem={(UnityEngine.EventSystems.EventSystem.current != null ? UnityEngine.EventSystems.EventSystem.current.name : "NULL")}");
            if (string.Equals(evt.SceneName, BootSceneName, System.StringComparison.Ordinal))
            {
                BootCamera.Ensure(); // 回主菜单：recreated boot scene has no camera of its own
                EnsureMainMenu(); // 回主菜单：重新推主菜单
                return;
            }

            if (!string.Equals(evt.SceneName, GameSceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            RestorePlayback();

            // Save modules: (re)bind to THIS session's inventory + player transform.
            InteractionWorld world = InteractionWorld.Shared;
            ISaveSystem save = Services.Get<ISaveSystem>();
            save.Unregister(InventorySaveable.Key);
            save.Unregister(PlayerTransformSaveable.Key);
            save.Register(new InventorySaveable(world.Inventory));
            save.Register(new PlayerTransformSaveable(FindPlayerTransform()));

            // HUD per game-scene load (dies with the scene).
            var hudHost = new GameObject("GameHud");
            _hud = hudHost.AddComponent<GameHud>();
            _hud.Build(UIRoot.EnsureCreated());

            // 读档（主菜单"继续"）：payload 驱动，恢复到刚注册的模块里。
            if (SceneLoader.TryPeekPayload(LoadPayloadKey, out string loadFlag) && loadFlag == "1")
            {
                SaveData data = save.LoadFromSlot(0);
                ZLog.Info("Boot", data != null
                    ? "Continue: slot 0 loaded."
                    : "Continue requested but slot 0 is empty — starting fresh.");
            }

            _hud.SetPickupTotal(world.Inventory.TotalCount);
        }

        private static Transform FindPlayerTransform()
        {
            GameObject player = GameObject.Find("Player");
            return player != null ? player.transform : null;
        }
    }
}
