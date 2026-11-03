using System;

namespace ZFrame.View
{
    /// <summary>
    /// The seam between the settings UI and the input layer (TASK-A-M3-03).
    /// <see cref="RebindService"/> is the real implementation;
    /// <see cref="SettingsRowFactory"/> talks to this interface only, so the
    /// "操作" tab is unit-testable with a fake (no Input System device, no
    /// play mode) and stays free of <c>ZFrameControls</c> knowledge.
    ///
    /// Keys are fully qualified action keys ("Player/Interact" — see
    /// <see cref="BindingSnapshot.ActionKey"/>). The index-aware members
    /// (TASK-A-M3-04) address ONE binding of an action: <c>-1</c> means "the
    /// action's primary binding" (the A-M3-03 behaviour), a concrete index
    /// addresses a composite part — the four 移动 rows use the W/A/S/D parts
    /// of <c>Player/Move</c>.
    /// </summary>
    public interface IBindingSettings
    {
        /// <summary>False when the input asset could not be loaded (rows render disabled controls).</summary>
        bool IsAvailable { get; }

        /// <summary>Human-readable key of the action's primary binding ("E", "W"), or a placeholder when unbound.</summary>
        string GetBindingDisplay(string actionKey);

        /// <summary>
        /// Human-readable key of ONE binding ("W" for the up part of WASD);
        /// <paramref name="bindingIndex"/> -1 = primary binding, out-of-range
        /// indices fall back to the unbound placeholder.
        /// </summary>
        string GetBindingDisplay(string actionKey, int bindingIndex);

        /// <summary>
        /// Starts an interactive rebind of the action's primary binding.
        /// The callback fires exactly once with the terminal
        /// <see cref="RebindOutcome"/> (a <see cref="BindingSnapshot"/>
        /// accompanies <see cref="RebindOutcome.Conflict"/> and
        /// <see cref="RebindOutcome.Done"/>; null otherwise).
        /// </summary>
        void Rebind(string actionKey, Action<RebindOutcome, BindingSnapshot> onComplete);

        /// <summary>
        /// Starts an interactive rebind of ONE binding (<paramref name="bindingIndex"/> -1 =
        /// primary). Same single-shot callback contract as the overload above.
        /// </summary>
        void Rebind(string actionKey, int bindingIndex, Action<RebindOutcome, BindingSnapshot> onComplete);

        /// <summary>Drops every override of ONE action (restores its asset defaults) and persists.</summary>
        void ResetBinding(string actionKey);

        /// <summary>
        /// Drops the override of ONE binding only (<paramref name="bindingIndex"/> -1 =
        /// primary) and persists — the per-row 恢复默认 of a composite part must
        /// not wipe its siblings (移动·上 must not reset 左/下/右).
        /// </summary>
        void ResetBinding(string actionKey, int bindingIndex);

        /// <summary>Drops every override of the whole asset (恢复默认) and persists.</summary>
        void ResetAll();
    }
}
