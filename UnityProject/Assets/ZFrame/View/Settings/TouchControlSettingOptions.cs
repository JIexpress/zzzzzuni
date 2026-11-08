using System;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Single source of the five TASK-A-AND-10 touch-polish settings rows and
    /// their live values (same pattern as <see cref="FrameGovernorSettingOptions"/>):
    /// the registry only maps id → apply, this class owns clamping, defaults and
    /// the <see cref="Changed"/> notification live overlays listen to.
    ///
    /// 操作页 rows (multipliers, Slider 0.5~2.0):
    /// - <see cref="DeadZoneSettingId"/> — scales <see cref="VirtualJoystick.DefaultDeadZone"/>;
    /// - <see cref="DragRadiusSettingId"/> — scales the stick base (visual + drag limit);
    /// - <see cref="SensitivitySettingId"/> — multiplies the produced stick vector (capped at 1);
    /// - <see cref="FixedStickSettingId"/> — false (default) = 浮动摇杆, true = A-AND-04 固定摇杆.
    /// 游戏性页 row:
    /// - <see cref="HapticsSettingId"/> — writes <see cref="TouchHaptics.Enabled"/>.
    ///
    /// Persistence needs no special handling: the values are ordinary
    /// Toggle/Slider rows, so <see cref="SettingsModel.Load"/> (boot applier
    /// included) replays the apply callbacks exactly like every other row.
    /// </summary>
    public static class TouchControlSettingOptions
    {
        /// <summary>Persisted key of the 死区半径 multiplier row (Slider 0.5~2.0).</summary>
        public const string DeadZoneSettingId = "controls.touch.deadZone";

        /// <summary>Persisted key of the 拖拽半径 multiplier row (Slider 0.5~2.0).</summary>
        public const string DragRadiusSettingId = "controls.touch.dragRadius";

        /// <summary>Persisted key of the 灵敏度 multiplier row (Slider 0.5~2.0).</summary>
        public const string SensitivitySettingId = "controls.touch.sensitivity";

        /// <summary>Persisted key of the 固定摇杆 row (Toggle; false = 浮动 — TASK-A-AND-10 default).</summary>
        public const string FixedStickSettingId = "controls.touch.fixedStick";

        /// <summary>Persisted key of the 震动反馈 row (Toggle, 游戏性页).</summary>
        public const string HapticsSettingId = "gameplay.haptics";

        /// <summary>Slider lower bound of the three multiplier rows.</summary>
        public const float MinMultiplier = 0.5f;

        /// <summary>Slider upper bound of the three multiplier rows.</summary>
        public const float MaxMultiplier = 2.0f;

        /// <summary>Factory default of every multiplier row (1.0 = A-AND-04 geometry untouched).</summary>
        public const float DefaultMultiplier = 1.0f;

        /// <summary>Factory default of the 固定摇杆 row: floating stick (TASK-A-AND-10 point 1).</summary>
        public const bool DefaultFixedStick = false;

        /// <summary>Factory default of the 震动反馈 row: haptics on.</summary>
        public const bool DefaultHaptics = true;

        /// <summary>死区半径 multiplier applied on top of <see cref="VirtualJoystick.DefaultDeadZone"/>.</summary>
        public static float DeadZoneMultiplier { get; private set; } = DefaultMultiplier;

        /// <summary>拖拽半径 multiplier — scales the stick base rect (visual and drag limit together).</summary>
        public static float DragRadiusMultiplier { get; private set; } = DefaultMultiplier;

        /// <summary>灵敏度 multiplier — multiplies the stick output vector (capped at unit length).</summary>
        public static float SensitivityMultiplier { get; private set; } = DefaultMultiplier;

        /// <summary>True = A-AND-04 fixed bottom-left stick; false (default) = floating origin stick.</summary>
        public static bool FixedStick { get; private set; } = DefaultFixedStick;

        /// <summary>True = action-button presses pulse <see cref="TouchHaptics"/> (the actual gate).</summary>
        public static bool HapticsEnabled { get; private set; } = DefaultHaptics;

        /// <summary>
        /// Raised after any value changed — live <see cref="TouchControlsView"/>
        /// overlays re-apply tuning/mode from these properties on the spot.
        /// </summary>
        public static event Action Changed;

        /// <summary>Applies the 死区半径 multiplier (clamped to [<see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>]).</summary>
        public static void ApplyDeadZone(float multiplier)
        {
            DeadZoneMultiplier = ClampMultiplier(multiplier);
            FireChanged();
        }

        /// <summary>Applies the 拖拽半径 multiplier (clamped likewise).</summary>
        public static void ApplyDragRadius(float multiplier)
        {
            DragRadiusMultiplier = ClampMultiplier(multiplier);
            FireChanged();
        }

        /// <summary>Applies the 灵敏度 multiplier (clamped likewise).</summary>
        public static void ApplySensitivity(float multiplier)
        {
            SensitivityMultiplier = ClampMultiplier(multiplier);
            FireChanged();
        }

        /// <summary>Applies the 固定摇杆 toggle (true = fixed A-AND-04 stick).</summary>
        public static void ApplyFixedStick(bool fixedStick)
        {
            FixedStick = fixedStick;
            FireChanged();
        }

        /// <summary>Applies the 震动反馈 toggle — the single write path into <see cref="TouchHaptics.Enabled"/>.</summary>
        public static void ApplyHaptics(bool enabled)
        {
            HapticsEnabled = enabled;
            TouchHaptics.Enabled = enabled;
            FireChanged();
        }

        /// <summary>
        /// Restores every touch-polish value to its factory default. The
        /// production path is <c>SettingsModel.ResetAll</c> (replays the row
        /// defaults); this exists for tests so static state never leaks
        /// between fixtures.
        /// </summary>
        public static void ResetToDefaults()
        {
            DeadZoneMultiplier = DefaultMultiplier;
            DragRadiusMultiplier = DefaultMultiplier;
            SensitivityMultiplier = DefaultMultiplier;
            FixedStick = DefaultFixedStick;
            HapticsEnabled = DefaultHaptics;
            TouchHaptics.Enabled = DefaultHaptics;
            FireChanged();
        }

        private static float ClampMultiplier(float multiplier)
        {
            return Mathf.Clamp(multiplier, MinMultiplier, MaxMultiplier);
        }

        private static void FireChanged()
        {
            Changed?.Invoke();
        }
    }
}
