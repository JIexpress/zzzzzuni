using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Real-prefab smoke of the settings panel (TASK-A-M3-02), same discipline
    /// as <see cref="UIPrefabSmokeTests"/>: the Setup-generated
    /// <c>ui_settings_panel</c> prefab is pushed through the REAL UIManager
    /// (MVP assembled via <see cref="UIBinding.EnsureDefaults"/>), the tab bar
    /// and every row are generated from the registry at open time, and the
    /// three-button semantics (应用并关闭=save+close / 撤销更改=revert /
    /// 恢复默认=reset) plus the Esc-cancel path run against a temp-root model.
    /// </summary>
    public sealed class SettingsPanelSmokeTests
    {
        private UIRoot _root;
        private UIManager _ui;
        private ResourceService _resources;
        private SettingsModel _model;
        private string _tempRoot;
        private bool _closed;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
            _resources = new ResourceService(new ResourcesProvider());
            _ui = new UIManager(_resources, _root);

            _tempRoot = Path.Combine(Path.GetTempPath(), "zframe_settings_panel_tests_" + Guid.NewGuid().ToString("N"));
            _model = new SettingsModel(SettingsRegistry.BuildDefaults(), _tempRoot);
            _closed = false;
        }

        [TearDown]
        public void TearDown()
        {
            UITestEnv.TearDown();
            _resources.Clear();
            InputBlocker.Reset(); // the presenter acquires/releases in pairs, but a failed test must not poison others
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        private SettingsPanelView Push(Action close = null)
        {
            Assert.IsTrue(_ui.Push<SettingsPanelView>(
                SettingsPanelView.DefaultConfig,
                SettingsPanelArgs.Create(_model, close ?? (() => _closed = true))).GetAwaiter().GetResult());
            Assert.IsTrue(_ui.TryGetView(out SettingsPanelView view));
            return view;
        }

        // ---------- binding + prefab ----------

        [Test]
        public void SettingsPanelPrefab_IsCommitted_AndResolvesByKey()
        {
            GameObject prefab = Resources.Load<GameObject>("ui_settings_panel");
            Assert.IsNotNull(prefab, "the SettingsUiSetup-generated prefab must live under Assets/UI/Resources with the exact key");
            Assert.IsNotNull(prefab.GetComponent<SettingsPanelView>());
        }

        [Test]
        public void UIBinding_Defaults_IncludeSettingsPanel()
        {
            Assert.IsTrue(UIBinding.TryGetFactory(typeof(SettingsPanelView), out Func<IUIView, UIPresenter> factory));
            var view = new GameObject("settings_binding_probe", typeof(RectTransform)).AddComponent<SettingsPanelView>();
            try
            {
                Assert.IsInstanceOf<SettingsPanelPresenter>(factory(view));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(view.gameObject);
            }
        }

        // ---------- data-driven generation at open time ----------

        [Test]
        public void Push_GeneratesTabBarAndVideoRows_FromRegistry()
        {
            SettingsPanelView view = Push();

            Assert.AreEqual("设置", view.TitleText);
            Assert.AreEqual(5, view.TabCount, "one generated tab button per registry page");
            Assert.AreEqual("video", view.CurrentPageId, "first page selected automatically");
            Assert.AreEqual(9, view.RowCount, "video page row count comes straight from the registry definitions");
            Assert.AreSame(_root.GetLayerCanvas(UIConsts.UILayer.Panel).transform, view.transform.parent);
            Assert.IsTrue(InputBlocker.Active, "the presenter freezes player input while the panel is open");

            // 两行新设置（A-R9-01）必须是真的控件，不是只存在于注册表里：
            // 行对象名 = Row_<setting id>（SettingsRowFactory.RowNamePrefix），
            // 控件类型 = Toggle / Dropdown，初值与定义默认值一致。
            Transform container = view.RowHost.GetChild(0);
            Toggle drs = container.Find("Row_video.dynamicResolution/Toggle").GetComponent<Toggle>();
            Assert.IsNotNull(drs, "动态分辨率 must render as a real Toggle");
            Assert.IsFalse(drs.isOn, "默认关（零破坏：与 governor 默认 Enabled=false 一致）");

            Dropdown target = container.Find("Row_video.frameRateTarget/Dropdown").GetComponent<Dropdown>();
            Assert.IsNotNull(target, "帧率目标 must render as a real Dropdown");
            CollectionAssert.AreEqual(new[] { "30", "60", "120" }, target.options.Select(o => o.text).ToArray());
            Assert.AreEqual(FrameGovernorSettingOptions.DefaultTargetFpsOptionIndex, target.value, "默认 60fps = governor 的 DefaultTargetFps");
            Assert.AreEqual(
                FrameGovernorSettingOptions.BuildTargetFpsLabel(),
                container.Find("Row_video.frameRateTarget/Label").GetComponent<Text>().text,
                "标签必须真的把「DRS·不锁帧」这段生效条件渲染出来（不是只写在常量里）");

            // 插帧行（B-R9-06）：同样是真控件，且 EditMode（= Editor 环境）下必须
            // 禁用 + 标签写「仅打包后生效」——不许让人以为点了有用。
            Toggle frameGeneration = container
                .Find("Row_video.frameGeneration/Toggle").GetComponent<Toggle>();
            Assert.IsNotNull(frameGeneration, "插帧 must render as a real Toggle");
            Assert.IsFalse(frameGeneration.isOn, "默认关（零破坏：纯透传）");
            Assert.IsFalse(frameGeneration.interactable,
                "Editor 内必须禁用该行（dxgi 代理不生效）");
            StringAssert.Contains("仅打包后生效",
                container.Find("Row_video.frameGeneration/Label").GetComponent<Text>().text,
                "禁用必须伴随诚实的标签，否则玩家只看到灰掉的行");
        }

        [Test]
        public void SelectPage_RebuildsRows_PerRegistry()
        {
            SettingsPanelView view = Push();

            view.SelectPage("audio");
            Assert.AreEqual(4, view.RowCount);

            view.SelectPage("controls");
            Assert.AreEqual(12, view.RowCount, "操作页：移动四向 + 交互 + 暂停 = 6 个键位行，A-AND-10 触屏四行（死区/拖拽半径/灵敏度/固定摇杆），手柄说明行，全部恢复默认");

            view.SelectPage("gameplay");
            Assert.AreEqual(4, view.RowCount);

            view.SelectPage("accessibility");
            Assert.AreEqual(3, view.RowCount);

            view.SelectPage("video");
            Assert.AreEqual(9, view.RowCount);

            // 加选项不改布局: pop the current panel, then open over a registry
            // that carries ONE extra definition — a new row appears, zero layout code.
            Assert.IsTrue(_ui.Pop());
            var extended = new SettingsRegistry(new[]
            {
                new SettingsPage("video", "视频", new List<SettingDefinition>(
                    SettingsRegistry.BuildDefaults().Pages[0].Settings)
                {
                    SettingDefinition.Toggle("video.extra", "新加的选项", true),
                }),
                new SettingsPage("audio", "音频", new[] { SettingDefinition.Slider("audio.master", "主音量", 1f, 0f, 1f) }),
            });
            var extendedModel = new SettingsModel(extended, _tempRoot);
            Assert.IsTrue(_ui.Push<SettingsPanelView>(
                SettingsPanelView.DefaultConfig,
                SettingsPanelArgs.Create(extendedModel, () => { })).GetAwaiter().GetResult());
            Assert.IsTrue(_ui.TryGetView(out SettingsPanelView extendedView));
            Assert.AreEqual(10, extendedView.RowCount, "one added definition line → one generated row, zero layout changes");
        }

        [Test]
        public void RowControls_WriteThroughToTheModel()
        {
            SettingsPanelView view = Push();
            view.SelectPage("gameplay");

            Transform container = view.RowHost.GetChild(0); // generated page container
            Toggle subtitlesToggle = container.Find("Row_gameplay.subtitles/Toggle").GetComponent<Toggle>();
            subtitlesToggle.isOn = false;

            Assert.IsFalse(_model.Get<bool>("gameplay.subtitles"), "row control change must reach the model immediately (即时应用)");
        }

        // ---------- three-button semantics ----------

        [Test]
        public void ApplyButton_SavesToInjectedRoot_AndCloses()
        {
            SettingsPanelView view = Push(() => _ui.Pop(SettingsPanelView.DefaultConfig));
            view.SelectPage("gameplay");
            Transform container = view.RowHost.GetChild(0);
            container.Find("Row_gameplay.subtitles/Toggle").GetComponent<Toggle>().isOn = false;

            view.ApplyButton.onClick.Invoke();

            Assert.AreEqual(0, _ui.StackCount, "应用并关闭 closes the panel");
            Assert.IsTrue(File.Exists(_model.FilePath), "应用 = commit the live-applied values to settings.json");
            Assert.IsFalse(InputBlocker.Active, "detach must release the input block");
            Assert.IsFalse(JObject.Parse(File.ReadAllText(_model.FilePath))["settings"]["gameplay.subtitles"].Value<bool>(),
                "the value changed before applying must be the persisted one");
        }

        [Test]
        public void RevertButton_RestoresSnapshot_AndStaysOpen()
        {
            SettingsPanelView view = Push();
            view.SelectPage("gameplay");
            Transform container = view.RowHost.GetChild(0);
            Toggle subtitles = container.Find("Row_gameplay.subtitles/Toggle").GetComponent<Toggle>();
            subtitles.isOn = false;
            Assert.IsTrue(_model.IsDirty);

            view.RevertButton.onClick.Invoke();

            Assert.IsTrue(_model.Get<bool>("gameplay.subtitles"), "撤销更改 rolls back to the open-time snapshot");
            Assert.IsFalse(_model.IsDirty);
            Assert.AreEqual(1, _ui.StackCount, "撤销更改 keeps the panel open");
            Assert.AreEqual(4, view.RowCount, "rows are refreshed against the restored values (gameplay 页含 A-AND-10 震动反馈)");
        }

        [Test]
        public void ResetButton_RestoresDefaults_AndStaysOpen()
        {
            _model.Set("audio.master", 0.25f);
            _model.TakeSnapshot();
            SettingsPanelView view = Push();

            view.ResetButton.onClick.Invoke();

            Assert.AreEqual(1f, _model.Get<float>("audio.master"), "恢复默认 applies every definition default");
            Assert.IsTrue(_model.IsDirty, "reset changes live state — persisting still requires 应用并关闭");
            Assert.AreEqual(1, _ui.StackCount);
        }

        // ---------- Esc = cancel ----------

        [Test]
        public void RequestClose_RevertsAndCloses_WithoutSaving()
        {
            SettingsPanelView view = Push(() =>
            {
                _closed = true;
                _ui.Pop(SettingsPanelView.DefaultConfig);
            });
            view.SelectPage("gameplay");
            Transform container = view.RowHost.GetChild(0);
            container.Find("Row_gameplay.subtitles/Toggle").GetComponent<Toggle>().isOn = false;
            Assert.IsTrue(_model.IsDirty);

            view.RequestClose(); // the Esc path (Input polling itself is play-mode only)

            Assert.IsTrue(_closed);
            Assert.AreEqual(0, _ui.StackCount);
            Assert.IsTrue(_model.Get<bool>("gameplay.subtitles"), "Esc cancels: unsaved changes roll back");
            Assert.IsFalse(File.Exists(_model.FilePath), "Esc must NEVER write settings.json");
            Assert.IsFalse(InputBlocker.Active);
        }

        [Test]
        public void Pop_ReleasesEverything()
        {
            SettingsPanelView view = Push();
            Assert.IsTrue(_ui.Pop());
            Assert.IsTrue(view == null, "cachePolicy None → destroyed on pop (edit mode: immediately)");
            Assert.IsFalse(InputBlocker.Active);
        }
    }
}
