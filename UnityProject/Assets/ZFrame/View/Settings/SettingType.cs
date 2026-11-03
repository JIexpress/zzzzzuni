namespace ZFrame.View
{
    /// <summary>
    /// The control type of a settings row (TASK-A-M3-02). <see cref="SettingsRowFactory"/>
    /// maps each value here to one UGUI control; <see cref="SettingsModel"/> uses it to
    /// decide storage/persistence semantics (Action/Binding rows carry no persisted value).
    /// </summary>
    public enum SettingType
    {
        /// <summary>Boolean switch — UGUI Toggle, persisted as JSON bool.</summary>
        Toggle = 0,

        /// <summary>Ranged float — UGUI Slider, persisted as JSON number (clamped to Min/Max).</summary>
        Slider = 1,

        /// <summary>Discrete choice — UGUI Dropdown, stored as the 0-based option index
        /// (JSON number); <see cref="SettingDefinition.EnumOptions"/> holds the display names.</summary>
        Enum = 2,

        /// <summary>Imperative trigger — UGUI Button. NO persisted value: clicking invokes
        /// the definition's apply callback directly.</summary>
        Action = 3,

        /// <summary>
        /// Key-binding row (TASK-A-M3-03) — label + current-key button + per-row
        /// "恢复默认" button. NO persisted value in settings.json: the override
        /// itself is persisted by <see cref="RebindService"/> into
        /// <c>input_bindings.json</c>, so the settings file stays a pure
        /// value store. <see cref="SettingDefinition.ActionKey"/> names the
        /// rebound action ("Player/Interact").
        /// </summary>
        Binding = 4,

        /// <summary>
        /// Map-wide binding reset (TASK-A-M3-03) — one wide button at the
        /// bottom of the 操作 tab (全部恢复默认). NO persisted value; clicking
        /// drops every override through <see cref="IBindingSettings.ResetAll"/>.
        /// </summary>
        BindingReset = 5,

        /// <summary>
        /// Read-only explanatory line (TASK-A-M3-04) — a plain Text row with no
        /// control, no value and no interaction. Used by the 操作 tab to state
        /// what is NOT rebindable (手柄摇杆使用默认设置) instead of pretending
        /// it is: no fake button, nothing to click.
        /// </summary>
        Note = 6,
    }

    /// <summary>
    /// Storage semantics of <see cref="SettingType"/>, in ONE place: the
    /// settings model must only track/validate/persist rows that actually own
    /// a value. Toggle/Slider/Enum do; Action (A-M3-02),
    /// Binding/BindingReset (A-M3-03) and Note (A-M3-04) do not.
    /// </summary>
    public static class SettingTypeExtensions
    {
        /// <summary>True for row types that own a persisted value (Toggle/Slider/Enum).</summary>
        public static bool CarriesValue(this SettingType type)
        {
            return type == SettingType.Toggle
                || type == SettingType.Slider
                || type == SettingType.Enum;
        }
    }
}
