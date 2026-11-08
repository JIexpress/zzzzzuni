using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode tests of the TASK-A-AND-10 touch-polish options
    /// (<see cref="TouchControlSettingOptions"/>) and the haptics gate
    /// (<see cref="TouchHaptics"/>): clamping, change notification, the
    /// gameplay.haptics row driving TouchHaptics.Enabled, the mock-pulse
    /// counting contract (可证伪用例⑤ 的 EditMode 几何步) and the settings.json
    /// round-trip through the ordinary persistence path.
    /// </summary>
    public sealed class TouchControlSettingOptionsTests
    {
        private string _root;
        private readonly List<IDisposable> _tokens = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            TouchControlSettingOptions.ResetToDefaults();
            TouchHaptics.ResetCounters();
            TouchHaptics.Pulse = TouchHaptics.DefaultPulse;
            _root = Path.Combine(Path.GetTempPath(), "zframe-touch-options-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();
            TouchControlSettingOptions.ResetToDefaults();
            TouchHaptics.ResetCounters();
            TouchHaptics.Pulse = TouchHaptics.DefaultPulse;

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ---------- clamping ----------

        [Test]
        public void MultiplierRows_ClampToTheSliderRange()
        {
            TouchControlSettingOptions.ApplyDeadZone(0.1f);
            Assert.AreEqual(0.5f, TouchControlSettingOptions.DeadZoneMultiplier, "低于下限必须夹到 0.5");

            TouchControlSettingOptions.ApplyDeadZone(9f);
            Assert.AreEqual(2.0f, TouchControlSettingOptions.DeadZoneMultiplier, "高于上限必须夹到 2.0");

            TouchControlSettingOptions.ApplyDragRadius(1.37f);
            Assert.AreEqual(1.37f, TouchControlSettingOptions.DragRadiusMultiplier);

            TouchControlSettingOptions.ApplySensitivity(0.62f);
            Assert.AreEqual(0.62f, TouchControlSettingOptions.SensitivityMultiplier);
        }

        [Test]
        public void Defaults_AreFloating_AtUnitMultiplier_WithHapticsOn()
        {
            TouchControlSettingOptions.ApplyFixedStick(true);
            TouchControlSettingOptions.ApplySensitivity(2f);
            TouchControlSettingOptions.ApplyHaptics(false);
            TouchControlSettingOptions.ResetToDefaults();

            Assert.IsFalse(TouchControlSettingOptions.FixedStick, "默认浮动");
            Assert.AreEqual(1f, TouchControlSettingOptions.DeadZoneMultiplier);
            Assert.AreEqual(1f, TouchControlSettingOptions.DragRadiusMultiplier);
            Assert.AreEqual(1f, TouchControlSettingOptions.SensitivityMultiplier);
            Assert.IsTrue(TouchControlSettingOptions.HapticsEnabled);
            Assert.IsTrue(TouchHaptics.Enabled, "ResetToDefaults 必须同步触觉闸门");
        }

        [Test]
        public void EveryApply_FiresChanged()
        {
            int changes = 0;
            Action handler = () => changes++;
            TouchControlSettingOptions.Changed += handler;
            try
            {
                TouchControlSettingOptions.ApplyDeadZone(1.5f);
                TouchControlSettingOptions.ApplyDragRadius(1.5f);
                TouchControlSettingOptions.ApplySensitivity(1.5f);
                TouchControlSettingOptions.ApplyFixedStick(true);
                TouchControlSettingOptions.ApplyHaptics(false);
                TouchControlSettingOptions.ResetToDefaults();
            }
            finally
            {
                TouchControlSettingOptions.Changed -= handler;
            }

            Assert.AreEqual(6, changes, "每个 apply 入口（含 ResetToDefaults）都必须通知活动 overlay");
        }

        // ---------- haptics gate ----------

        [Test]
        public void ApplyHaptics_GatesTouchHaptics()
        {
            TouchControlSettingOptions.ApplyHaptics(false);
            Assert.IsFalse(TouchHaptics.Enabled, "震动反馈行关闭必须落到触觉闸门");

            TouchControlSettingOptions.ApplyHaptics(true);
            Assert.IsTrue(TouchHaptics.Enabled);
        }

        [Test]
        public void TouchHaptics_TriggerCountsOnlyWhenEnabled()
        {
            int pulses = 0;
            TouchHaptics.Pulse = () => pulses++;

            TouchHaptics.Trigger();
            Assert.AreEqual(1, pulses, "开启触觉时必须调用一次脉冲");
            Assert.AreEqual(1, TouchHaptics.PulseCount);

            TouchHaptics.Enabled = false;
            TouchHaptics.Trigger();
            Assert.AreEqual(1, pulses, "触觉关闭时脉冲必须零调用（可证伪用例⑤的 mock 计数契约）");
            Assert.AreEqual(1, TouchHaptics.PulseCount, "关闭时的 Trigger 也不得计数");

            TouchHaptics.Enabled = true;
            TouchHaptics.Pulse = null;
            Assert.DoesNotThrow(TouchHaptics.Trigger, "脉冲接缝为 null 时 Trigger 必须安全");
            Assert.AreEqual(2, TouchHaptics.PulseCount);
        }

        // ---------- persistence round-trip ----------

        [Test]
        public void SettingsRows_PersistAndReplayThroughTheOrdinaryPath()
        {
            var registry = SettingsRegistry.BuildDefaults();
            var writer = new SettingsModel(registry, _root);
            writer.Set(TouchControlSettingOptions.SensitivitySettingId, 2f);
            writer.Set(TouchControlSettingOptions.FixedStickSettingId, true);
            writer.Set(TouchControlSettingOptions.HapticsSettingId, false);
            Assert.AreEqual(2f, TouchControlSettingOptions.SensitivityMultiplier, "Set 必须即时应用");
            Assert.IsTrue(TouchControlSettingOptions.FixedStick);
            Assert.IsFalse(TouchHaptics.Enabled);
            writer.Save();

            // 全新的 model（模拟下次启动）：Load 按 apply 回调重放，静态值被重新置位。
            TouchControlSettingOptions.ResetToDefaults();
            var reader = new SettingsModel(registry, _root);
            int applied = reader.Load();

            Assert.AreEqual(2f, TouchControlSettingOptions.SensitivityMultiplier, "开机应用点必须重放触屏行");
            Assert.IsTrue(TouchControlSettingOptions.FixedStick, "开机应用点必须重放固定摇杆开关");
            Assert.IsFalse(TouchHaptics.Enabled, "开机应用点必须重放震动反馈开关");

            // Save 写出全部有值行（不止本任务的三行），Load 按定义逐行重放。
            int valueRows = 0;
            foreach (SettingDefinition definition in registry.AllSettings)
            {
                if (definition.Type.CarriesValue())
                {
                    valueRows++;
                }
            }

            Assert.AreEqual(valueRows, applied, "全部有值行（含三行触屏设置）都必须被识别并应用");
        }
    }
}
