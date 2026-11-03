using System;
using ZFrame.Core;
using ZFrame.View;

namespace Game.View.UI
{
    /// <summary>
    /// Push args of <see cref="MainMenuView"/> (TASK-A-M4-02). The presenter
    /// is pure C# and cannot reach the flow owner, so the opener (GameBootstrap)
    /// injects the four intents as delegates plus whether a save slot exists.
    /// </summary>
    public sealed class MainMenuArgs
    {
        /// <summary>开始新游戏.</summary>
        public Action Start;

        /// <summary>继续（读最近存档）.</summary>
        public Action ContinueGame;

        /// <summary>打开设置面板.</summary>
        public Action OpenSettings;

        /// <summary>退出游戏.</summary>
        public Action Quit;

        /// <summary>True when slot 0 has a readable save — drives the "继续" button visibility.</summary>
        public bool HasSave;

        /// <summary>Convenience builder.</summary>
        public static MainMenuArgs Create(Action start, Action continueGame, Action openSettings, Action quit, bool hasSave)
        {
            return new MainMenuArgs
            {
                Start = start,
                ContinueGame = continueGame,
                OpenSettings = openSettings,
                Quit = quit,
                HasSave = hasSave
            };
        }
    }

    /// <summary>
    /// Main menu presenter (TASK-A-M4-02, MVP): pure C# — subscribes the view's
    /// intent events inside the <see cref="UIPresenter"/> event scope and
    /// forwards them to the injected delegates. No UnityEngine types, so it is
    /// unit-testable with fakes alone.
    /// </summary>
    public sealed class MainMenuPresenter : UIPresenter<MainMenuView>
    {
        private readonly MainMenuView _view;
        private MainMenuArgs _args;

        /// <summary>Creates the presenter for the injected view (registered by GameBootstrap via UIBinding).</summary>
        public MainMenuPresenter(MainMenuView view)
            : base(view)
        {
            _view = view;
        }

        /// <inheritdoc />
        protected override void OnAttach(object args)
        {
            _args = args as MainMenuArgs;

            _view.SetTitle("ZFrame 垂直切片");
            _view.SetContinueVisible(_args != null && _args.HasSave);
            _view.StartRequested += OnStart;
            _view.ContinueRequested += OnContinue;
            _view.SettingsRequested += OnSettings;
            _view.QuitRequested += OnQuit;
        }

        /// <inheritdoc />
        protected override void OnDetach()
        {
            _view.StartRequested -= OnStart;
            _view.ContinueRequested -= OnContinue;
            _view.SettingsRequested -= OnSettings;
            _view.QuitRequested -= OnQuit;
            _args = null;
        }

        private void OnStart()
        {
            _args?.Start?.Invoke();
        }

        private void OnContinue()
        {
            _args?.ContinueGame?.Invoke();
        }

        private void OnSettings()
        {
            _args?.OpenSettings?.Invoke();
        }

        private void OnQuit()
        {
            _args?.Quit?.Invoke();
        }
    }
}
