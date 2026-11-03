using System;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.UI
{
    /// <summary>
    /// Push args of <see cref="PausePanelView"/> (TASK-A-M4-02): the three
    /// intents as delegates, injected by the flow owner (GameBootstrap).
    /// </summary>
    public sealed class PausePanelArgs
    {
        /// <summary>继续（解除暂停）.</summary>
        public Action Resume;

        /// <summary>打开设置面板.</summary>
        public Action OpenSettings;

        /// <summary>存档并回主菜单.</summary>
        public Action QuitToMenu;

        /// <summary>Convenience builder.</summary>
        public static PausePanelArgs Create(Action resume, Action openSettings, Action quitToMenu)
        {
            return new PausePanelArgs { Resume = resume, OpenSettings = openSettings, QuitToMenu = quitToMenu };
        }
    }

    /// <summary>
    /// Pause panel presenter (TASK-A-M4-02, MVP): pure C# — forwards the
    /// view's intent events to the injected delegates. The pause/unpause
    /// engine side effects (GameTime.IsPaused, Time.timeScale, InputBlocker)
    /// are owned by the flow owner through the delegates; this class has no
    /// opinion.
    /// </summary>
    public sealed class PausePanelPresenter : UIPresenter<PausePanelView>
    {
        private readonly PausePanelView _view;
        private PausePanelArgs _args;

        /// <summary>Creates the presenter for the injected view (registered by GameBootstrap via UIBinding).</summary>
        public PausePanelPresenter(PausePanelView view)
            : base(view)
        {
            _view = view;
        }

        /// <inheritdoc />
        protected override void OnAttach(object args)
        {
            _args = args as PausePanelArgs;

            _view.SetTitle("已暂停");
            _view.ResumeRequested += OnResume;
            _view.SettingsRequested += OnSettings;
            _view.QuitToMenuRequested += OnQuitToMenu;
        }

        /// <inheritdoc />
        protected override void OnDetach()
        {
            _view.ResumeRequested -= OnResume;
            _view.SettingsRequested -= OnSettings;
            _view.QuitToMenuRequested -= OnQuitToMenu;
            _args = null;
        }

        private void OnResume()
        {
            _args?.Resume?.Invoke();
        }

        private void OnSettings()
        {
            _args?.OpenSettings?.Invoke();
        }

        private void OnQuitToMenu()
        {
            _args?.QuitToMenu?.Invoke();
        }
    }
}
