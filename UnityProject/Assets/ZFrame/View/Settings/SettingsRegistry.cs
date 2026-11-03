using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Registry of all settings pages (TASK-A-M3-02). <see cref="BuildDefaults"/>
    /// is the single static factory that defines the five shipped tabs
    /// (视频/音频/操作/游戏性/无障碍) — ADDING AN OPTION TO THE UI MEANS ADDING
    /// ONE <see cref="SettingDefinition"/> LINE HERE; the row and its control
    /// are generated at open time by <see cref="SettingsRowFactory"/> and no
    /// layout code changes.
    ///
    /// Apply-callback wiring notes (what is REAL this milestone vs stored):
    /// - 视频: 全屏/垂直同步/帧率上限/画质档位(<see cref="PipelineManager.SetTier"/>)/
    ///   阴影距离 all apply live; 超分 applies live through IUpscalerSettings →
    ///   zrender.dll C ABI (TASK-B-R6-03, mapping + capability gating in
    ///   <see cref="UpscalerSettingOptions"/>); 动态分辨率 + 帧率目标 apply live
    ///   through <see cref="FrameGovernorHost"/>.SetEnabled/SetTargetFps
    ///   (TASK-A-R9-01, mapping in <see cref="FrameGovernorSettingOptions"/>);
    ///   渲染比例 is a STORED placeholder.
    /// - 音频: four bus sliders apply through <c>Services.Get&lt;IAudioService&gt;()</c>.
    /// - 操作: 移动四向（上/左/下/右，各绑 WASD 复合轴的一个部件）+ 交互 + 暂停的真实键位
    ///   重绑定（TASK-A-M3-03 机制 / A-M3-04 四向拆分），末尾一行只读说明（手柄摇杆保持默认）
    ///   + 全部恢复默认；值不落 settings.json — override 由 RebindService 写入 input_bindings.json。
    /// - 游戏性: stored only — consumers (subtitle/tutorial systems) come later.
    /// - 无障碍: UI 缩放 applies to the UIRoot layer CanvasScalers live;
    ///   色盲模式 stored only (R6 post-processing); 屏幕震动强度 stored only.
    /// </summary>
    public sealed class SettingsRegistry
    {
        /// <summary>Reference resolution <see cref="UIRoot"/> builds its layer CanvasScalers with (UI 缩放 divides it by the scale factor).</summary>
        public static readonly Vector2 UiReferenceResolution = new Vector2(1920f, 1080f);

        private readonly List<SettingsPage> _pages;
        private readonly Dictionary<string, SettingDefinition> _definitionsById =
            new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);

        /// <summary>Creates a registry over <paramref name="pages"/>; setting ids must be globally unique.</summary>
        public SettingsRegistry(IEnumerable<SettingsPage> pages)
        {
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }

            _pages = pages.ToList();
            if (_pages.Count == 0)
            {
                throw new ArgumentException("registry needs at least one page.", nameof(pages));
            }

            foreach (SettingsPage page in _pages)
            {
                foreach (SettingDefinition definition in page.Settings)
                {
                    if (_definitionsById.ContainsKey(definition.Id))
                    {
                        throw new ArgumentException($"duplicate setting id '{definition.Id}' in registry.");
                    }

                    _definitionsById.Add(definition.Id, definition);
                }
            }
        }

        /// <summary>All pages in tab order (read-only).</summary>
        public IReadOnlyList<SettingsPage> Pages => _pages;

        /// <summary>Total number of definitions across all pages (diagnostics/tests).</summary>
        public int TotalSettingCount => _definitionsById.Count;

        /// <summary>Flattens every definition across all pages (registry order).</summary>
        public IEnumerable<SettingDefinition> AllSettings => _definitionsById.Values;

        /// <summary>Looks a page up by id; false when unknown.</summary>
        public bool TryGetPage(string pageId, out SettingsPage page)
        {
            page = _pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
            return page != null;
        }

        /// <summary>Looks a definition up by setting id across all pages; false when unknown (e.g. legacy persisted keys).</summary>
        public bool TryGetDefinition(string settingId, out SettingDefinition definition)
        {
            return _definitionsById.TryGetValue(settingId, out definition);
        }

        /// <summary>
        /// Builds the shipped five-tab registry (视频/音频/操作/游戏性/无障碍).
        /// Everything the UI shows is defined HERE — data-driven by design.
        /// </summary>
        public static SettingsRegistry BuildDefaults()
        {
            return new SettingsRegistry(new[]
            {
                BuildVideoPage(),
                BuildAudioPage(),
                BuildControlsPage(),
                BuildGameplayPage(),
                BuildAccessibilityPage(),
            });
        }

        // ---- 视频 -----------------------------------------------------------

        /// <summary>视频 tab: full pipeline-live applies incl. the upscaler (B-R6-03).</summary>
        private static SettingsPage BuildVideoPage()
        {
            return new SettingsPage("video", "视频", new[]
            {
                SettingDefinition.EnumSetting(
                    "video.fullscreenMode", "全屏模式", 1,
                    new[] { "独占全屏", "无边框", "窗口" },
                    apply: value =>
                    {
                        if (!Application.isPlaying)
                        {
                            return;
                        }

                        Screen.fullScreenMode = ((int)value) switch
                        {
                            0 => FullScreenMode.ExclusiveFullScreen,
                            2 => FullScreenMode.Windowed,
                            _ => FullScreenMode.FullScreenWindow,
                        };
                        Screen.fullScreen = (int)value != 2;
                    }),
                SettingDefinition.Toggle(
                    "video.vsync", "垂直同步", true,
                    apply: value =>
                    {
                        if (Application.isPlaying)
                        {
                            QualitySettings.vSyncCount = (bool)value ? 1 : 0;
                        }
                    }),
                SettingDefinition.EnumSetting(
                    "video.frameRateCap", "帧率上限", 1,
                    new[] { "30", "60", "120", "无限制" },
                    apply: value =>
                    {
                        if (Application.isPlaying)
                        {
                            int index = (int)value;
                            Application.targetFrameRate = index >= 3 ? -1 : (index + 1) * 30;
                        }
                    }),
                SettingDefinition.EnumSetting(
                    "video.qualityTier", "画质档位", 1,
                    new[] { "Low", "Medium", "High" },
                    apply: value => PipelineManager.SetTier((QualityTier)(int)value)),
                // 超分 — LIVE (TASK-B-R6-03): Enum 索引经 UpscalerSettingOptions 映射为
                // 原生 ZrUpscalerPreferred 真调 C ABI（Auto 语义由原生 Chain 选优）；
                // 选项文案按 QueryCaps 能力门控标注（不支持的厂商项追加"（本机不支持）"）。
                SettingDefinition.EnumSetting(
                    "video.upscaler", "超分", UpscalerSettingOptions.DefaultOptionIndex,
                    UpscalerSettingOptions.BuildOptionLabels(),
                    apply: value => UpscalerSettingOptions.ApplyOptionIndex((int)value),
                    requiresRestart: false),
                // 动态分辨率 — LIVE (TASK-A-R9-01): Toggle → FrameGovernorHost.SetEnabled
                // （DRS 使能）。默认 false = 与 governor 默认关一致（零破坏）。
                // 两个新增行都排在 qualityTier 之后：Load() 按定义顺序重放，
                // 档位必须先落定，governor 才能捕获正确的用户基线（侦察 R18）。
                SettingDefinition.Toggle(
                    FrameGovernorSettingOptions.DrsSettingId,
                    FrameGovernorSettingOptions.BuildDrsLabel(),
                    false,
                    apply: value => FrameGovernorSettingOptions.ApplyDrs((bool)value)),
                // 帧率目标 — LIVE (TASK-A-R9-01): Enum 索引 → FrameGovernorHost.SetTargetFps
                // （TargetMs = 1000/fps）。标签后缀如实写明"DRS 开时生效，不锁帧"：
                // 它只给治理器当预算，真正的锁帧是上面的 video.frameRateCap（帧率上限）。
                SettingDefinition.EnumSetting(
                    FrameGovernorSettingOptions.TargetFpsSettingId,
                    FrameGovernorSettingOptions.BuildTargetFpsLabel(),
                    FrameGovernorSettingOptions.DefaultTargetFpsOptionIndex,
                    FrameGovernorSettingOptions.BuildTargetFpsLabels(),
                    apply: value => FrameGovernorSettingOptions.ApplyTargetFps((int)value)),
                SettingDefinition.EnumSetting(
                    "video.shadowDistance", "阴影距离", 2,
                    new[] { "关", "20", "40", "60" },
                    apply: value =>
                    {
                        int index = (int)value;
                        QualitySettings.shadowDistance = index * 20f; // "关"=0, 20, 40, 60
                    }),
                // 插帧 — LIVE (TASK-B-R9-06): Toggle → IFrameGenerationSettings.TrySetEnabled
                // → dxgi 代理的 ZFG_FgSetEnabled(0/1)。默认关 = 纯透传（不提交输入、不 Prepare、
                // 不销毁上下文）。排在末尾：不打乱既有行序/下标，也不依赖其它行先落定。
                // 三档门控（Editor / 非 D3D12 / 代理缺失或非 URP）都在标签后缀里如实写明，
                // 并用 Interactable 禁用该行——绝不做"能点但不生效"的开关。
                // TASK-B-R9-07：可用档的标签带代价标注「（抗锯齿降至 1×）」——插帧的 depth 必须
                // 单采样，开启期间 MSAA 被压到 1×（关闭即还原，见 ZrFrameGenerationRuntime）。
                SettingDefinition.Toggle(
                    FrameGenerationSettingOptions.SettingId,
                    FrameGenerationSettingOptions.BuildLabel(),
                    false,
                    apply: value => FrameGenerationSettingOptions.ApplyEnabled((bool)value),
                    interactable: FrameGenerationSettingOptions.IsRowInteractable),
            });
        }

        // ---- 音频 -----------------------------------------------------------

        /// <summary>音频 tab: four bus sliders applying live through IAudioService (no-op until the service boots).</summary>
        private static SettingsPage BuildAudioPage()
        {
            return new SettingsPage("audio", "音频", new[]
            {
                BusSlider("audio.master", "主音量", AudioBus.Master),
                BusSlider("audio.bgm", "音乐音量", AudioBus.Bgm),
                BusSlider("audio.sfx", "音效音量", AudioBus.Sfx),
                BusSlider("audio.ui", "界面音量", AudioBus.Ui),
            });
        }

        private static SettingDefinition BusSlider(string id, string displayName, AudioBus bus)
        {
            return SettingDefinition.Slider(
                id, displayName, 1f, 0f, 1f,
                apply: value =>
                {
                    if (Services.TryGet(out IAudioService audio))
                    {
                        audio.SetBusVolume(bus, (float)value);
                    }
                });
        }

        // ---- 操作 -----------------------------------------------------------

        /// <summary>Asset default key of each direction of the WASD composite (used to RESOLVE the binding index, never as an index).</summary>
        private const string MoveUpPath = "<Keyboard>/w";

        /// <summary>Left direction of the WASD composite (asset default path).</summary>
        private const string MoveLeftPath = "<Keyboard>/a";

        /// <summary>Down direction of the WASD composite (asset default path).</summary>
        private const string MoveDownPath = "<Keyboard>/s";

        /// <summary>Right direction of the WASD composite (asset default path).</summary>
        private const string MoveRightPath = "<Keyboard>/d";

        /// <summary>
        /// 操作 tab (TASK-A-M3-03, extended by TASK-A-M3-04): 移动 is a 2DVector
        /// composite, so it gets ONE ROW PER DIRECTION (上/左/下/右) — each row
        /// rebinds exactly one WASD part — plus 交互 / 暂停 (single-binding
        /// actions, primary binding), a read-only 手柄 note and the map-wide
        /// 全部恢复默认 row.
        ///
        /// The four binding indices are RESOLVED FROM THE ASSET BY PATH
        /// (<see cref="RebindService.FindBindingIndexByPath"/>): the composite's
        /// part order is data that may change in the .inputactions file, so
        /// hardcoding 1/2/3/4 here would silently rebind the wrong direction.
        /// When the asset cannot be loaded the indices stay -1 ("primary") and
        /// every binding row renders disabled anyway (no backend either).
        /// </summary>
        private static SettingsPage BuildControlsPage()
        {
            ZFrameControls controls = ZFrameControls.FromResources();
            try
            {
                return new SettingsPage("controls", "操作", new[]
                {
                    // 每方向一行：动作名 + 当前键位按钮 + 恢复默认按钮
                    // （SettingsRowFactory 的 Binding 分支；override 由 RebindService
                    // 持久化到 input_bindings.json，settings.json 不存键位）。
                    MoveDirectionRow(controls, "Up", "移动·上", MoveUpPath),
                    MoveDirectionRow(controls, "Left", "移动·左", MoveLeftPath),
                    MoveDirectionRow(controls, "Down", "移动·下", MoveDownPath),
                    MoveDirectionRow(controls, "Right", "移动·右", MoveRightPath),
                    SettingDefinition.Binding("controls.bind.Interact", "交互", "Player/Interact"),
                    SettingDefinition.Binding("controls.bind.Pause", "暂停", "Player/Pause"),
                    // 页面底部：如实披露不支持的部分（手柄 leftStick 保持资产默认）。
                    SettingDefinition.Note(
                        "controls.note.Gamepad",
                        "手柄摇杆使用默认设置，暂不支持重绑定"),
                    // 清空全部 override（恢复资产默认键位）。
                    SettingDefinition.BindingReset("controls.resetAll", "全部恢复默认"),
                });
            }
            finally
            {
                controls?.Dispose();
            }
        }

        /// <summary>One 移动 direction row; the binding index comes from the asset's own binding paths.</summary>
        private static SettingDefinition MoveDirectionRow(
            ZFrameControls controls, string idSuffix, string displayName, string assetPath)
        {
            int bindingIndex = controls != null
                ? RebindService.FindBindingIndexByPath(controls.Move, assetPath)
                : -1;

            if (bindingIndex < 0)
            {
                ZLog.Warn("Settings", $"操作页：Player/Move 中找不到 '{assetPath}' 部件 — 「{displayName}」退回主绑定。");
            }

            return SettingDefinition.Binding(
                "controls.bind.Move" + idSuffix, displayName, "Player/Move", bindingIndex);
        }

        // ---- 游戏性 ---------------------------------------------------------

        /// <summary>
        /// 游戏性 tab: values persist but NO consumer applies them yet —
        /// consumers (字幕系统 / 教程引导系统) hook in during M4/M5 slices; the
        /// apply callbacks are deliberately null until then.
        /// </summary>
        private static SettingsPage BuildGameplayPage()
        {
            return new SettingsPage("gameplay", "游戏性", new[]
            {
                // 字幕开关 — stored only; 消费方：字幕系统（后续切片接入）。
                SettingDefinition.Toggle("gameplay.subtitles", "字幕", true),
                // 字幕大小 — stored only; 消费方：字幕系统（后续切片接入）。
                SettingDefinition.EnumSetting("gameplay.subtitleSize", "字幕大小", 1, new[] { "小", "中", "大" }),
                // 教程提示频率 — stored only; 消费方：教程引导系统（后续切片接入）。
                SettingDefinition.EnumSetting("gameplay.tutorialFrequency", "教程提示频率", 0, new[] { "每次显示", "仅首次", "从不" }),
            });
        }

        // ---- 无障碍 ---------------------------------------------------------

        /// <summary>无障碍 tab: UI 缩放 applies live to the UIRoot layer scalers; the other two are stored placeholders.</summary>
        private static SettingsPage BuildAccessibilityPage()
        {
            return new SettingsPage("accessibility", "无障碍", new[]
            {
                // 色盲模式 — STORED PLACEHOLDER (TASK-A-M3-02): R6 后处理体积接入时
                // 在此补 apply 回调（Daltonize 滤镜 Volume override）。
                SettingDefinition.EnumSetting("accessibility.colorBlindMode", "色盲模式", 0, new[] { "关", "红", "绿", "蓝" }),
                SettingDefinition.Slider(
                    "accessibility.uiScale", "UI 缩放", 1f, 0.8f, 1.2f,
                    apply: ApplyUiScale),
                // 屏幕震动强度 — stored only; 消费方：Gameplay 相机震动（M4 交互切片接入）。
                SettingDefinition.Slider("accessibility.screenShake", "屏幕震动强度", 1f, 0f, 1f),
            });
        }

        /// <summary>UI 缩放 apply: divides the per-layer CanvasScaler reference resolution by the factor (ScaleWithScreenSize has no direct multiplier).</summary>
        private static void ApplyUiScale(object value)
        {
            float scale = (float)value;
            if (scale <= 0f)
            {
                return;
            }

            UIRoot root = UIRoot.Instance;
            if (root == null)
            {
                return;
            }

            Vector2 scaled = UiReferenceResolution / scale;
            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                Canvas canvas = root.GetLayerCanvas(layer);
                CanvasScaler scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
                if (scaler != null)
                {
                    scaler.referenceResolution = scaled;
                }
            }
        }
    }
}
