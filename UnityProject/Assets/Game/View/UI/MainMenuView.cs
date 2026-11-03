using System;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.View;

namespace Game.View.UI
{
    /// <summary>
    /// Main menu panel view (TASK-A-M4-02). Prefab-generated UGUI frame
    /// (ui_main_menu.prefab, built by Game.View.Editor.GameUiSetup): title +
    /// four buttons. The view only raises intents as C# events; the
    /// <see cref="MainMenuPresenter"/> decides what they mean (start /
    /// continue / settings / quit) — MVP split, same discipline as the
    /// framework sample panel.
    ///
    /// "继续" (continue from save) is hidden unless the presenter says a save
    /// slot exists (<see cref="SetContinueVisible"/>). A single click replays
    /// the last intent through <see cref="ReplayPrimaryForTests"/> so PlayMode
    /// smoke tests can drive the chain without synthesizing input.
    /// </summary>
    public sealed class MainMenuView : UIViewBase
    {
        [SerializeField] private Text _titleText;
        [SerializeField] private Button _startButton;
        [SerializeField] private Button _continueButton;
        [SerializeField] private Button _settingsButton;
        [SerializeField] private Button _quitButton;

        /// <summary>开始 pressed.</summary>
        public event Action StartRequested;

        /// <summary>继续（读档） pressed.</summary>
        public event Action ContinueRequested;

        /// <summary>设置 pressed.</summary>
        public event Action SettingsRequested;

        /// <summary>退出 pressed.</summary>
        public event Action QuitRequested;

        /// <summary>Serialized references (diagnostics/tests/prefab validation).</summary>
        public Button StartButton => _startButton;
        public Button ContinueButton => _continueButton;
        public Button SettingsButton => _settingsButton;
        public Button QuitButton => _quitButton;

        /// <inheritdoc />
        public override void OnInit(object args)
        {
            if (_startButton != null)
            {
                _startButton.onClick.AddListener(OnStartClicked);
            }

            if (_continueButton != null)
            {
                _continueButton.onClick.AddListener(OnContinueClicked);
            }

            if (_settingsButton != null)
            {
                _settingsButton.onClick.AddListener(OnSettingsClicked);
            }

            if (_quitButton != null)
            {
                _quitButton.onClick.AddListener(OnQuitClicked);
            }
        }

        /// <inheritdoc />
        protected override void OnViewDestroy()
        {
            if (_startButton != null)
            {
                _startButton.onClick.RemoveListener(OnStartClicked);
            }

            if (_continueButton != null)
            {
                _continueButton.onClick.RemoveListener(OnContinueClicked);
            }

            if (_settingsButton != null)
            {
                _settingsButton.onClick.RemoveListener(OnSettingsClicked);
            }

            if (_quitButton != null)
            {
                _quitButton.onClick.RemoveListener(OnQuitClicked);
            }
        }

        /// <summary>Shows/hides the "继续" entry (presenter decides from the save system state).</summary>
        public void SetContinueVisible(bool visible)
        {
            if (_continueButton != null)
            {
                _continueButton.gameObject.SetActive(visible);
            }
        }

        /// <summary>Sets the title caption (prefab default is fine; kept for symmetry/tests).</summary>
        public void SetTitle(string title)
        {
            if (_titleText != null)
            {
                _titleText.text = title;
            }
        }

        /// <summary>
        /// Test seam: re-fires the most recent primary intent without touching
        /// real input devices. EditMode never invokes this (no play session);
        /// PlayMode smoke uses it to "click 开始".
        /// TASK-B-R10-01: 测试专用，收为 internal（见 Game/View/AssemblyInfo.cs）。
        /// </summary>
        internal void ReplayPrimaryForTests()
        {
            OnStartClicked();
        }

        private void OnStartClicked()
        {
            StartRequested?.Invoke();
        }

        private void OnContinueClicked()
        {
            ContinueRequested?.Invoke();
        }

        private void OnSettingsClicked()
        {
            SettingsRequested?.Invoke();
        }

        private void OnQuitClicked()
        {
            QuitRequested?.Invoke();
        }
    }
}
