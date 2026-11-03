using System;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.View;

namespace Game.View.UI
{
    /// <summary>
    /// Pause panel view (TASK-A-M4-02). Prefab-generated UGUI frame
    /// (ui_pause_panel.prefab, built by Game.View.Editor.GameUiSetup): dim
    /// blocker + three buttons (继续/设置/回主菜单).
    ///
    /// Esc semantics: the Player action map is DISABLED while this panel is
    /// open (the flow owner acquires an <see cref="InputBlocker"/>), so the
    /// Pause action cannot deliver a second Esc. Same precedent as
    /// SettingsPanelView, the view polls legacy Input in play mode and raises
    /// <see cref="ResumeRequested"/> on Escape — the presenter treats it as
    /// "close and resume".
    /// </summary>
    public sealed class PausePanelView : UIViewBase
    {
        [SerializeField] private Text _titleText;
        [SerializeField] private Button _resumeButton;
        [SerializeField] private Button _settingsButton;
        [SerializeField] private Button _quitToMenuButton;

        /// <summary>继续 pressed (button or Esc).</summary>
        public event Action ResumeRequested;

        /// <summary>设置 pressed.</summary>
        public event Action SettingsRequested;

        /// <summary>回主菜单 pressed.</summary>
        public event Action QuitToMenuRequested;

        /// <summary>Serialized references (diagnostics/tests/prefab validation).</summary>
        public Button ResumeButton => _resumeButton;
        public Button SettingsButton => _settingsButton;
        public Button QuitToMenuButton => _quitToMenuButton;

        private bool _escPollingEnabled = true;

        /// <inheritdoc />
        public override void OnPause()
        {
            // A panel ABOVE us (e.g. settings) owns Esc while we are blocked;
            // our own polling must not double-handle it.
            _escPollingEnabled = false;
        }

        /// <inheritdoc />
        public override void OnResume()
        {
            _escPollingEnabled = true;
        }

        /// <inheritdoc />
        public override void OnInit(object args)
        {
            if (_resumeButton != null)
            {
                _resumeButton.onClick.AddListener(OnResumeClicked);
            }

            if (_settingsButton != null)
            {
                _settingsButton.onClick.AddListener(OnSettingsClicked);
            }

            if (_quitToMenuButton != null)
            {
                _quitToMenuButton.onClick.AddListener(OnQuitToMenuClicked);
            }
        }

        /// <inheritdoc />
        protected override void OnViewDestroy()
        {
            if (_resumeButton != null)
            {
                _resumeButton.onClick.RemoveListener(OnResumeClicked);
            }

            if (_settingsButton != null)
            {
                _settingsButton.onClick.RemoveListener(OnSettingsClicked);
            }

            if (_quitToMenuButton != null)
            {
                _quitToMenuButton.onClick.RemoveListener(OnQuitToMenuClicked);
            }
        }

        /// <summary>Sets the title caption.</summary>
        public void SetTitle(string title)
        {
            if (_titleText != null)
            {
                _titleText.text = title;
            }
        }

        /// <summary>
        /// Test seam: re-fires the resume intent without touching real input devices.
        /// TASK-B-R10-01: 测试专用，收为 internal（见 Game/View/AssemblyInfo.cs）。
        /// </summary>
        internal void InvokeResumeForTests()
        {
            OnResumeClicked();
        }

        private void Update()
        {
            // Play-mode only: Esc resumes (Player map is blocked while paused,
            // so the Esc that got us here cannot be repeated through the map).
            // Suppressed while a panel above blocks us (see OnPause/OnResume).
            if (_escPollingEnabled && Input.GetKeyDown(KeyCode.Escape))
            {
                ResumeRequested?.Invoke();
            }
        }

        private void OnResumeClicked()
        {
            ResumeRequested?.Invoke();
        }

        private void OnSettingsClicked()
        {
            SettingsRequested?.Invoke();
        }

        private void OnQuitToMenuClicked()
        {
            QuitToMenuRequested?.Invoke();
        }
    }
}
