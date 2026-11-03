using System;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Data;

namespace ZFrame.View
{
    /// <summary>
    /// HUD entry button of the settings panel (TASK-A-M3-02, sample-panel
    /// bootstrap discipline). Self-mounts after scene load (same pattern as
    /// <see cref="PipelineManager"/>/<see cref="UIRoot"/>: Core never involved):
    /// builds one small "设置" Button on the HUD layer canvas of
    /// <see cref="UIRoot"/>; clicking pushes <see cref="SettingsPanelView"/>
    /// over a shared <see cref="SettingsModel"/> (loaded from
    /// <c>settings.json</c> once per session).
    ///
    /// This is the deliberate PLACEHOLDER entry of the brief — a HUD button
    /// until real menus (A-M4 交互切片) replace it. The shared model lifetime
    /// note: it lives for the whole session so reverted-but-unsaved changes do
    /// not leak between opens; each open still takes a fresh snapshot.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsEntry : MonoBehaviour
    {
        /// <summary>GameObject name of the self-mounted host.</summary>
        public const string HostName = "~SettingsEntry";

        /// <summary>Caption on the entry button.</summary>
        public const string ButtonCaption = "设置";

        private static SettingsEntry _instance;
        private static SettingsModel _sharedModel;
        private static UIManager _sharedUi;
        private static ResourceService _sharedResources;

        private Button _button;

        /// <summary>Runtime bootstrap: after scene load the HUD canvas exists (UIRoot boots at the same stage).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            EnsureCreated();
        }

        /// <summary>Mounts the entry (idempotent). Safe in play mode, EditMode tests and batch validation.</summary>
        public static SettingsEntry EnsureCreated()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject(HostName);
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            _instance = host.AddComponent<SettingsEntry>();
            return _instance;
        }

        /// <summary>
        /// Forgets all static references (tests destroy scene objects themselves).
        /// TASK-B-R10-01: 测试专用，收为 internal（见 View/AssemblyInfo.cs）。
        /// </summary>
        internal static void ResetForTests()
        {
            _instance = null;
            _sharedModel = null;
            _sharedUi = null;
            _sharedResources = null;
        }

        private void OnEnable()
        {
            BuildButton();
        }

        private void OnDisable()
        {
            if (_button != null)
            {
                _button.onClick.RemoveListener(OpenSettings);
                _button = null;
            }
        }

        /// <summary>Opens (or focuses) the settings panel over the shared model. Public so test/code entry points can reuse the path.</summary>
        public static void OpenSettings()
        {
            UIRoot root = UIRoot.EnsureCreated();
            if (_sharedUi == null)
            {
                _sharedResources = new ResourceService(new ResourcesProvider());
                _sharedUi = new UIManager(_sharedResources, root);
            }

            _sharedModel ??= new SettingsModel(SettingsRegistry.BuildDefaults());
            _sharedModel.Load(); // tolerant: applies persisted values (no-op when the file is absent/corrupt)

            // 操作 tab needs the shared rebind service (TASK-A-M3-03). Tolerant:
            // when the input asset is missing the panel still opens and the
            // binding rows render disabled.
            InputRuntime.EnsureCreated();

            if (_sharedUi.IsOnStack<SettingsPanelView>())
            {
                return; // already open (e.g. double click)
            }

            _ = _sharedUi.Push<SettingsPanelView>(
                SettingsPanelView.DefaultConfig,
                SettingsPanelArgs.Create(
                    _sharedModel,
                    () => _sharedUi.Pop(SettingsPanelView.DefaultConfig),
                    InputRuntime.Bindings));
        }

        /// <summary>The shared model (diagnostics/tests).</summary>
        public static SettingsModel SharedModel => _sharedModel;

        private void BuildButton()
        {
            Canvas hudCanvas = UIRoot.EnsureCreated().GetLayerCanvas(UIConsts.UILayer.HUD);
            if (hudCanvas == null)
            {
                ZLog.Warn("UI", "SettingsEntry: HUD canvas missing — entry button not built.");
                return;
            }

            var buttonGo = new GameObject("SettingsEntryButton", typeof(RectTransform));
            RectTransform rect = buttonGo.GetComponent<RectTransform>();
            rect.SetParent(hudCanvas.transform, false);
            // Entry anchor is hand-placed ON PURPOSE — the "布局零手摆" discipline
            // applies to the settings ROWS (data-driven); a single entry button is
            // static chrome, not generated from setting definitions.
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -24f);
            rect.sizeDelta = new Vector2(160f, 64f);

            Image image = buttonGo.AddComponent<Image>();
            image.color = new Color(0.2f, 0.22f, 0.3f, 0.9f);
            _button = buttonGo.AddComponent<Button>();

            var labelGo = new GameObject("Text", typeof(RectTransform));
            RectTransform labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(buttonGo.transform, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            Text label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 36;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = ButtonCaption;

            _button.onClick.AddListener(OpenSettings);
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
