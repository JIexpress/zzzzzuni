using System;

namespace ZFrame.View
{
    /// <summary>
    /// Push args of <see cref="SettingsPanelView"/> (TASK-A-M3-02): the
    /// <see cref="SettingsModel"/> to display/edit and the close delegate (the
    /// presenter is pure C# and cannot reach the <see cref="UIManager"/> — the
    /// opener injects "how to close me", typically
    /// <c>() => ui.Pop(SettingsPanelView.DefaultConfig)</c>).
    /// </summary>
    public sealed class SettingsPanelArgs
    {
        /// <summary>The model bound to the panel (registry drives tabs/rows; values drive controls).</summary>
        public SettingsModel Model { get; set; }

        /// <summary>Close action invoked on 应用并关闭 and on the Esc cancel path. Null = stay open (tests).</summary>
        public Action Close { get; set; }

        /// <summary>
        /// Binding backend of the 操作 tab (TASK-A-M3-03) — normally
        /// <see cref="RebindService"/> owned by <see cref="InputRuntime"/>.
        /// Null = binding rows render disabled (tests/headless), every other
        /// tab is unaffected.
        /// </summary>
        public IBindingSettings Bindings { get; set; }

        /// <summary>Convenience builder.</summary>
        public static SettingsPanelArgs Create(SettingsModel model, Action close, IBindingSettings bindings = null)
        {
            return new SettingsPanelArgs { Model = model, Close = close, Bindings = bindings };
        }
    }
}
