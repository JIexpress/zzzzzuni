using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode tests for <see cref="SettingsRegistry"/> (TASK-A-M3-02): the
    /// five shipped tabs must exist with the exact definition/type distribution
    /// the task brief mandates — these assertions double as the "数据驱动"
    /// acceptance evidence (every shipped row is one definition line in
    /// BuildDefaults, nothing is laid out by hand anywhere). Placeholder
    /// definitions are verified functionally (null Apply = stored only); the
    /// XML doc notes on those placeholders are cited by line number in the
    /// task log's construction report.
    /// </summary>
    public sealed class SettingsRegistryTests
    {
        private SettingsRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = SettingsRegistry.BuildDefaults();
        }

        // ---------- overall shape ----------

        [Test]
        public void BuildDefaults_HasExactlyFivePages_InTabOrder()
        {
            CollectionAssert.AreEqual(
                new[] { "video", "audio", "controls", "gameplay", "accessibility" },
                _registry.Pages.Select(p => p.Id).ToArray());

            CollectionAssert.AreEqual(
                new[] { "视频", "音频", "操作", "游戏性", "无障碍" },
                _registry.Pages.Select(p => p.DisplayName).ToArray());
        }

        [Test]
        public void BuildDefaults_SettingIds_AreGloballyUnique()
        {
            List<string> ids = _registry.AllSettings.Select(s => s.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "duplicate setting ids would silently collide in persistence");
            Assert.AreEqual(_registry.TotalSettingCount, ids.Count);
        }

        [Test]
        public void TryGetPage_And_TryGetDefinition_ResolveById()
        {
            Assert.IsTrue(_registry.TryGetPage("video", out SettingsPage video));
            Assert.AreEqual("视频", video.DisplayName);

            Assert.IsTrue(_registry.TryGetDefinition("video.vsync", out SettingDefinition vsync));
            Assert.AreEqual("垂直同步", vsync.DisplayName);

            Assert.IsFalse(_registry.TryGetPage("nope", out _));
            Assert.IsFalse(_registry.TryGetDefinition("nope.nope", out _));
        }

        [Test]
        public void Registry_DuplicateSettingId_Throws()
        {
            ArgumentException duplicate = Assert.Throws<ArgumentException>(() => new SettingsRegistry(new[]
            {
                new SettingsPage("p1", "P1", new[] { SettingDefinition.Toggle("same.id", "A", true) }),
                new SettingsPage("p2", "P2", new[] { SettingDefinition.Toggle("same.id", "B", false) }),
            }));
            StringAssert.Contains("duplicate setting id", duplicate.Message);
        }

        // ---------- 视频: all pipeline-live (upscaler wired since B-R6-03) ----------

        [Test]
        public void VideoPage_HasNineDefinitionsWithExpectedTypes()
        {
            Assert.IsTrue(_registry.TryGetPage("video", out SettingsPage video));

            CollectionAssert.AreEqual(
                new[]
                {
                    SettingType.Enum,   // video.fullscreenMode
                    SettingType.Toggle, // video.vsync
                    SettingType.Enum,   // video.frameRateCap
                    SettingType.Enum,   // video.qualityTier
                    SettingType.Enum,   // video.upscaler (live via IUpscalerSettings, B-R6-03)
                    SettingType.Toggle, // video.dynamicResolution (live via FrameGovernorHost, A-R9-01)
                    SettingType.Enum,   // video.frameRateTarget (live via FrameGovernorHost, A-R9-01)
                    SettingType.Enum,   // video.shadowDistance
                    SettingType.Toggle, // video.frameGeneration (live via IFrameGenerationSettings, B-R9-06)
                },
                video.Settings.Select(s => s.Type).ToArray());

            CollectionAssert.AreEqual(
                new[]
                {
                    "video.fullscreenMode", "video.vsync", "video.frameRateCap", "video.qualityTier",
                    "video.upscaler", "video.dynamicResolution", "video.frameRateTarget", "video.shadowDistance",
                    "video.frameGeneration",
                },
                video.Settings.Select(s => s.Id).ToArray());

            // 两行的文案（A-R9-01）：动态分辨率可能带管线后缀（StartsWith）；
            // 帧率目标必须带"DRS·不锁帧"后缀，与"帧率上限"互斥。
            StringAssert.StartsWith(FrameGovernorSettingOptions.DrsDisplayName, video.Settings[5].DisplayName);
            Assert.AreEqual(FrameGovernorSettingOptions.BuildTargetFpsLabel(), video.Settings[6].DisplayName);
            CollectionAssert.AreEqual(new[] { "30", "60", "120" }, video.Settings[6].EnumOptions);
        }

        [Test]
        public void VideoPage_FrameRateCap_OptionsAre30_60_120_Unlimited()
        {
            Assert.IsTrue(_registry.TryGetDefinition("video.frameRateCap", out SettingDefinition cap));
            CollectionAssert.AreEqual(new[] { "30", "60", "120", "无限制" }, cap.EnumOptions);
        }

        [Test]
        public void VideoPage_QualityTier_IsWiredLive()
        {
            Assert.IsTrue(_registry.TryGetDefinition("video.qualityTier", out SettingDefinition tier));
            Assert.IsNotNull(tier.Apply, "画质档位 must apply live (PipelineManager.SetTier — wiring cited in the task log)");
            CollectionAssert.AreEqual(new[] { "Low", "Medium", "High" }, tier.EnumOptions);
            Assert.AreEqual(1, (int)tier.DefaultValue, "default tier = Medium");
        }

        [Test]
        public void VideoPage_Upscaler_IsWiredLive_WithCapabilityGatedLabels()
        {
            Assert.IsTrue(_registry.TryGetDefinition("video.upscaler", out SettingDefinition upscaler));
            Assert.IsNotNull(upscaler.Apply, "upscaler applies live through IUpscalerSettings.TrySetPreferred (TASK-B-R6-03)");
            Assert.AreEqual("超分", upscaler.DisplayName);
            Assert.AreEqual(0, (int)upscaler.DefaultValue, "default = 自动");
            Assert.AreEqual(5, upscaler.EnumOptions.Length, "自动/关/FSR 3.1/FSR 4.1/DLSS 5");
            // 无服务注册的 EditMode 态 = 能力未知 → 厂商项全部标注（详见 UpscalerSettingsTests）。
            StringAssert.EndsWith("（本机不支持）", upscaler.EnumOptions[2]);
        }

        [Test]
        public void VideoPage_ShadowDistance_IsGearEnum_WithLiveApply()
        {
            Assert.IsTrue(_registry.TryGetDefinition("video.shadowDistance", out SettingDefinition shadow));
            CollectionAssert.AreEqual(new[] { "关", "20", "40", "60" }, shadow.EnumOptions);
            Assert.IsNotNull(shadow.Apply, "阴影距离 applies live through QualitySettings.shadowDistance");
        }

        [Test]
        public void VideoPage_FullscreenVsyncFramerate_AreWiredLive()
        {
            Assert.IsTrue(_registry.TryGetDefinition("video.fullscreenMode", out SettingDefinition fullscreen));
            Assert.IsNotNull(fullscreen.Apply);

            Assert.IsTrue(_registry.TryGetDefinition("video.vsync", out SettingDefinition vsync));
            Assert.IsNotNull(vsync.Apply);
            Assert.AreEqual(SettingType.Toggle, vsync.Type);
            Assert.AreEqual(true, (bool)vsync.DefaultValue);

            Assert.IsTrue(_registry.TryGetDefinition("video.frameRateCap", out SettingDefinition cap));
            Assert.IsNotNull(cap.Apply);
        }

        // ---------- 音频: four real bus sliders ----------

        [Test]
        public void AudioPage_HasFourBusSliders_WiredLive()
        {
            Assert.IsTrue(_registry.TryGetPage("audio", out SettingsPage audio));
            Assert.AreEqual(4, audio.SettingCount);

            CollectionAssert.AreEqual(
                new[] { "audio.master", "audio.bgm", "audio.sfx", "audio.ui" },
                audio.Settings.Select(s => s.Id).ToArray());

            foreach (SettingDefinition slider in audio.Settings)
            {
                Assert.AreEqual(SettingType.Slider, slider.Type);
                Assert.AreEqual(0f, slider.Min);
                Assert.AreEqual(1f, slider.Max);
                Assert.AreEqual(1f, (float)slider.DefaultValue);
                Assert.IsNotNull(slider.Apply, "every bus slider must apply live through IAudioService.SetBusVolume");
            }
        }

        // ---------- 操作: real key binding list (A-M3-03 / A-M3-04 四向) ----------

        [Test]
        public void ControlsPage_IsTheRealBindingList()
        {
            Assert.IsTrue(_registry.TryGetPage("controls", out SettingsPage controls));
            Assert.AreEqual(12, controls.SettingCount,
                "移动四向 + 交互 + 暂停 = 6 个键位行，TASK-A-AND-10 触屏四行（死区/拖拽半径/灵敏度/固定摇杆），另有手柄说明行 + 底部「全部恢复默认」");

            CollectionAssert.AreEqual(
                new[]
                {
                    SettingType.Binding, SettingType.Binding, SettingType.Binding,
                    SettingType.Binding, SettingType.Binding, SettingType.Binding,
                    SettingType.Slider, SettingType.Slider, SettingType.Slider, SettingType.Toggle,
                    SettingType.Note, SettingType.BindingReset,
                },
                controls.Settings.Select(s => s.Type).ToArray());

            CollectionAssert.AreEqual(
                new[] { "移动·上", "移动·左", "移动·下", "移动·右", "交互", "暂停" },
                controls.Settings.Take(6).Select(s => s.DisplayName).ToArray());

            CollectionAssert.AreEqual(
                new[]
                {
                    "Player/Move", "Player/Move", "Player/Move", "Player/Move",
                    "Player/Interact", "Player/Pause",
                },
                controls.Settings.Take(6).Select(s => s.ActionKey).ToArray());

            foreach (SettingDefinition bindingRow in controls.Settings.Take(6))
            {
                Assert.IsNull(bindingRow.Apply, "binding rows carry no apply callback — the row owns the interaction through IBindingSettings");
                Assert.IsNull(bindingRow.DefaultValue, "binding rows carry no persisted value (input_bindings.json owns overrides)");
            }

            // 四个方向各自指向一个具体的复合轴部件（索引由 path 解析，见 MoveRebindTests）。
            foreach (SettingDefinition direction in controls.Settings.Take(4))
            {
                Assert.GreaterOrEqual(direction.BindingIndex, 0,
                    "asset 存在时四向行必须解析出具体 bindingIndex（不是 -1 主绑定）");
            }

            // 单绑定动作仍沿用「主绑定」语义（-1）。
            Assert.AreEqual(-1, controls.Settings[4].BindingIndex);
            Assert.AreEqual(-1, controls.Settings[5].BindingIndex);

            // TASK-A-AND-10 触屏四行：0.5~2.0 倍率 Slider（默认 1.0）+ 固定摇杆开关
            // （默认 false = 浮动），全部接 TouchControlSettingOptions（即时生效）。
            SettingDefinition deadZone = controls.Settings[6];
            Assert.AreEqual("controls.touch.deadZone", deadZone.Id);
            Assert.AreEqual("触屏·死区半径", deadZone.DisplayName);
            Assert.AreEqual(0.5f, deadZone.Min);
            Assert.AreEqual(2.0f, deadZone.Max);
            Assert.AreEqual(1f, (float)deadZone.DefaultValue);
            Assert.IsNotNull(deadZone.Apply, "触屏行必须即时生效（TouchControlSettingOptions.ApplyDeadZone）");

            Assert.AreEqual("controls.touch.dragRadius", controls.Settings[7].Id);
            Assert.AreEqual("controls.touch.sensitivity", controls.Settings[8].Id);
            foreach (SettingDefinition sliderRow in new[] { controls.Settings[7], controls.Settings[8] })
            {
                Assert.AreEqual(SettingType.Slider, sliderRow.Type);
                Assert.AreEqual(0.5f, sliderRow.Min);
                Assert.AreEqual(2.0f, sliderRow.Max);
                Assert.IsNotNull(sliderRow.Apply);
            }

            SettingDefinition fixedStick = controls.Settings[9];
            Assert.AreEqual("controls.touch.fixedStick", fixedStick.Id);
            Assert.AreEqual(SettingType.Toggle, fixedStick.Type);
            Assert.AreEqual(false, (bool)fixedStick.DefaultValue, "默认浮动（TASK-A-AND-10 设计要点 1）");
            Assert.IsNotNull(fixedStick.Apply);

            Assert.AreEqual("手柄摇杆使用默认设置，暂不支持重绑定", controls.Settings[10].DisplayName);
            Assert.AreEqual("全部恢复默认", controls.Settings[11].DisplayName);
        }

        // ---------- 游戏性: 字幕/教程 stored, 震动反馈 live (A-AND-10) ----------

        [Test]
        public void GameplayPage_StoresValues_WithoutConsumersYet()
        {
            Assert.IsTrue(_registry.TryGetPage("gameplay", out SettingsPage gameplay));
            CollectionAssert.AreEqual(
                new[] { SettingType.Toggle, SettingType.Enum, SettingType.Enum, SettingType.Toggle },
                gameplay.Settings.Select(s => s.Type).ToArray());
            CollectionAssert.AreEqual(
                new[] { "gameplay.subtitles", "gameplay.subtitleSize", "gameplay.tutorialFrequency", "gameplay.haptics" },
                gameplay.Settings.Select(s => s.Id).ToArray());

            foreach (SettingDefinition definition in gameplay.Settings.Take(3))
            {
                Assert.IsNull(definition.Apply, "字幕/教程 settings are store-only placeholders this milestone (consumers documented in XML doc)");
            }

            // TASK-A-AND-10：震动反馈是本页唯一 LIVE 行（TouchHaptics.Enabled 开关）。
            SettingDefinition haptics = gameplay.Settings[3];
            Assert.AreEqual("震动反馈", haptics.DisplayName);
            Assert.AreEqual(true, (bool)haptics.DefaultValue);
            Assert.IsNotNull(haptics.Apply, "震动反馈必须接 TouchControlSettingOptions.ApplyHaptics（即时生效）");
        }

        // ---------- 无障碍: UI 缩放 real, others stored ----------

        [Test]
        public void AccessibilityPage_UiScaleSlider_IsReal_OthersStored()
        {
            Assert.IsTrue(_registry.TryGetPage("accessibility", out SettingsPage accessibility));
            Assert.AreEqual(3, accessibility.SettingCount);
            CollectionAssert.AreEqual(
                new[] { SettingType.Enum, SettingType.Slider, SettingType.Slider },
                accessibility.Settings.Select(s => s.Type).ToArray());

            SettingDefinition colorBlind = accessibility.Settings[0];
            CollectionAssert.AreEqual(new[] { "关", "红", "绿", "蓝" }, colorBlind.EnumOptions);
            Assert.IsNull(colorBlind.Apply, "色盲模式 stored until the R6 post-processing pass (XML doc at the definition)");

            SettingDefinition uiScale = accessibility.Settings[1];
            Assert.AreEqual(0.8f, uiScale.Min);
            Assert.AreEqual(1.2f, uiScale.Max);
            Assert.AreEqual(1f, (float)uiScale.DefaultValue);
            Assert.IsNotNull(uiScale.Apply, "UI 缩放 must apply live to the UIRoot CanvasScalers");

            SettingDefinition screenShake = accessibility.Settings[2];
            Assert.IsNull(screenShake.Apply, "屏幕震动强度 stored until the M4 gameplay slice (XML doc at the definition)");
        }
    }
}
