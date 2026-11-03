using System;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Settings panel presenter (TASK-A-M3-02). PURE C# — drives the
    /// <see cref="SettingsPanelView"/> through its events/properties only.
    ///
    /// BUTTON SEMANTICS (written down because "即时应用" makes them subtle):
    /// Individual option changes (<see cref="SettingsPanelView.SettingChanged"/>)
    /// apply IMMEDIATELY via <see cref="SettingsModel.Set{T}"/> — the engine
    /// state changes the moment the control is touched. The three buttons are:
    /// - 应用并关闭: <see cref="SettingsModel.Save"/> (persist settings.json)
    ///   then close. "应用" therefore does NOT apply option values — they were
    ///   already applied live; it commits them to disk and closes.
    /// - 撤销更改: <see cref="SettingsModel.RevertToSnapshot"/> (snapshot taken
    ///   at attach) + row refresh. The panel stays open.
    /// - 恢复默认: <see cref="SettingsModel.ResetAll"/> + row refresh. The
    ///   panel stays open; persisting still requires 应用并关闭.
    /// Esc (<see cref="SettingsPanelView.RequestClose"/>) is CANCEL: revert to
    /// the snapshot, then close — never saving. While attached the presenter
    /// holds one <see cref="InputBlocker"/> acquire (modal input freeze, same
    /// discipline as the scene loader) and releases it on detach.
    /// </summary>
    public sealed class SettingsPanelPresenter : UIPresenter<SettingsPanelView>
    {
        private readonly SettingsPanelView _view;
        private SettingsModel _model;
        private Action _close;
        private IDisposable _inputBlock;

        /// <summary>Creates the presenter for the injected view (registered in <see cref="UIBinding.EnsureDefaults"/>).</summary>
        public SettingsPanelPresenter(SettingsPanelView view)
            : base(view)
        {
            _view = view;
        }

        protected override void OnAttach(object args)
        {
            SettingsPanelArgs panelArgs = args as SettingsPanelArgs;
            _model = panelArgs?.Model;
            _close = panelArgs?.Close;

            _view.SetTitle("设置");
            _view.SettingChanged += OnSettingChanged;
            _view.ApplyRequested += OnApplyRequested;
            _view.RevertRequested += OnRevertRequested;
            _view.ResetRequested += OnResetRequested;
            _view.CloseRequested += OnCloseRequested;

            _model?.TakeSnapshot();

            // Modal discipline: freeze player input while the settings panel is open
            // (balanced by the release in OnDetach — UIManager guarantees one pair).
            _inputBlock = InputBlocker.Acquire();
        }

        protected override void OnDetach()
        {
            _view.SettingChanged -= OnSettingChanged;
            _view.ApplyRequested -= OnApplyRequested;
            _view.RevertRequested -= OnRevertRequested;
            _view.ResetRequested -= OnResetRequested;
            _view.CloseRequested -= OnCloseRequested;

            _inputBlock?.Dispose();
            _inputBlock = null;
            _close = null;
        }

        private void OnSettingChanged(SettingDefinition definition, object value)
        {
            // Immediate apply per option type: Toggle → bool, Slider → float, Enum → int index.
            switch (definition.Type)
            {
                case SettingType.Toggle:
                    _model?.Set(definition.Id, (bool)value);
                    break;
                case SettingType.Slider:
                    _model?.Set(definition.Id, (float)value);
                    break;
                case SettingType.Enum:
                    _model?.Set(definition.Id, (int)value);
                    break;
                default:
                    // Value-less rows: Action (A-M3-02) and Binding/BindingReset
                    // (A-M3-03). Binding rows own their interaction inside the row
                    // (RebindService writes input_bindings.json); BindingReset has
                    // already dropped the overrides and the view refreshed the key
                    // captions — nothing to commit to settings.json.
                    break;
            }
        }

        private void OnApplyRequested()
        {
            // "应用" = commit the already-live-applied values to settings.json + close.
            _model?.Save();
            Close();
        }

        private void OnRevertRequested()
        {
            _model?.RevertToSnapshot();
            _view.RefreshRows();
        }

        private void OnResetRequested()
        {
            _model?.ResetAll();
            _view.RefreshRows();
        }

        private void OnCloseRequested()
        {
            // Esc = cancel: roll live state back to the open-time snapshot, close, never save.
            _model?.RevertToSnapshot();
            Close();
        }

        private void Close()
        {
            Action close = _close;
            _close = null; // single-shot: a second close request (e.g. Esc racing the button) must not pop twice
            close?.Invoke();
        }
    }
}
