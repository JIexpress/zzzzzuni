using System;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Touch haptics gate (TASK-A-AND-10): one static entry point the touch
    /// widgets call when the player presses an action button. The physical
    /// pulse is seamed out through <see cref="Pulse"/> so PlayMode tests can
    /// count invocations without a device, and every pulse is gated by
    /// <see cref="Enabled"/> — the 震动反馈 settings row
    /// (<see cref="TouchControlSettingOptions.HapticsSettingId"/>) writes
    /// exactly this flag through <see cref="TouchControlSettingOptions.ApplyHaptics"/>.
    ///
    /// 默认脉冲选型（TASK-A-AND-10 报告）：Android 26+ 用
    /// <c>VibrationEffect.createOneShot(~20ms)</c> 轻震（Handheld.Vibrate 的
    /// 固定 ~500ms 长震对手感太重），旧系统退回 <see cref="Handheld.Vibrate"/>；
    /// 桌面/编辑器无振动硬件，调用是安全的 no-op。
    /// </summary>
    public static class TouchHaptics
    {
        /// <summary>Factory pulse (Android one-shot / legacy fallback). Tests replace <see cref="Pulse"/> instead of calling this.</summary>
        public static void DefaultPulse()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator"))
                {
                    if (vibrator == null)
                    {
                        return;
                    }

                    int sdk = new AndroidJavaClass("android.os.Build$VERSION").GetStatic<int>("SDK_INT");
                    if (sdk >= 26)
                    {
                        using (var effectClass = new AndroidJavaClass("android.os.VibrationEffect"))
                        using (AndroidJavaObject effect =
                                   effectClass.CallStatic<AndroidJavaObject>("createOneShot", 20L, -1))
                        {
                            vibrator.Call("vibrate", effect);
                        }
                    }
                    else
                    {
                        vibrator.Call("vibrate", 20L);
                    }
                }
            }
            catch (Exception e)
            {
                // Haptics must never take gameplay input down with it (old devices,
                // vendor quirks): degrade to a silent no-op.
                UnityEngine.Debug.LogWarning($"[TouchHaptics] vibration unavailable: {e.GetType().Name}");
            }
#else
            Handheld.Vibrate(); // editor/desktop: documented no-op without vibration hardware
#endif
        }

        /// <summary>
        /// The platform pulse. Reassignable seam (TASK-A-AND-10 用例⑤：mock 计数断言
        /// 开关关闭时零调用) — production code never reassigns it.
        /// </summary>
        public static Action Pulse { get; set; } = DefaultPulse;

        /// <summary>Mirrors the 震动反馈 settings row (single write path: ApplyHaptics).</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Accepted (enabled) pulses since domain load — diagnostics/tests.</summary>
        public static int PulseCount { get; private set; }

        /// <summary>
        /// Fires one pulse when haptics are enabled. A disabled gate is a strict
        /// no-op: no pulse, no count (the PlayMode case asserts both).
        /// </summary>
        public static void Trigger()
        {
            if (!Enabled)
            {
                return;
            }

            PulseCount++;
            Pulse?.Invoke();
        }

        /// <summary>Test seam: resets the static counter between fixtures.</summary>
        public static void ResetCounters()
        {
            PulseCount = 0;
        }
    }
}
