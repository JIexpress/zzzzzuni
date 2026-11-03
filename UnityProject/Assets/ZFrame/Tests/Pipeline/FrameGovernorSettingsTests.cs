using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    /// <summary>
    /// TASK-A-R9-01 的接线证据：视频页「动态分辨率」「帧率目标」两行必须
    /// 1) 真存在于注册表（数据驱动的唯一事实源）且默认值零破坏；
    /// 2) apply 真的抵达 <see cref="FrameGovernorHost"/>（使能状态 / TargetMs），
    ///    而不是"值存下来了但没人用"；
    /// 3) 冷启动（不经设置面板）由 <see cref="SettingsBootApplier"/> 从
    ///    settings.json 送达 —— 这是本任务要修的根因；
    /// 4) 没有 settings.json 的全新用户，宿主一个字段都不动。
    ///
    /// 宿主复用 A-R8-01 的公开测试缝 <see cref="FrameGovernorHost.Initialize"/>
    /// （注入假遥测，不注册 player loop 也能建实例），并在 TearDown 里 Shutdown
    /// 走真实恢复路径，避免污染其它 fixture 的管线状态。
    /// </summary>
    public sealed class FrameGovernorSettingsTests
    {
        private FrameGovernorHost _host;
        private SilentTelemetry _telemetry;
        private string _tempRoot;

        private sealed class SilentTelemetry : IFrameTelemetry
        {
            public float GpuFrameMs => 0f;

            public float CpuFrameMs { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "zframe_gov_settings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                _host.Shutdown();
                UnityEngine.Object.DestroyImmediate(_host.gameObject);
                _host = null;
            }

            if (PipelineManager.Instance != null)
            {
                UnityEngine.Object.DestroyImmediate(PipelineManager.Instance.gameObject);
            }

            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        private void NewHost()
        {
            _telemetry = new SilentTelemetry();
            var go = new GameObject("~FrameGovernorSettingsTestHost");
            _host = go.AddComponent<FrameGovernorHost>();
            // TASK-A-R9-02: keep the recovery journal out of the real persistentDataPath.
            _host.Initialize(_telemetry, null, new RenderScaleRecoveryJournal(
                Path.Combine(_tempRoot, "governor_journal")));
        }

        private SettingsModel NewModel()
        {
            return new SettingsModel(SettingsRegistry.BuildDefaults(), _tempRoot);
        }

        private static UniversalRenderPipelineAsset ActiveUrp()
        {
            return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        }

        // ---- 1. 行定义（数据源） -----------------------------------------------

        [Test]
        public void VideoPage_HasDynamicResolutionToggle_DefaultsOff_WiredLive()
        {
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition(FrameGovernorSettingOptions.DrsSettingId, out SettingDefinition drs),
                "动态分辨率行必须由 SettingsRegistry 定义（数据驱动，面板只负责渲染）");
            Assert.AreEqual(SettingType.Toggle, drs.Type);
            Assert.AreEqual(false, drs.DefaultValue, "零破坏：默认关 = governor 默认 Enabled=false");
            Assert.IsNotNull(drs.Apply, "该行必须真接 FrameGovernorHost（否则就是能点不生效的假开关）");
            StringAssert.StartsWith(FrameGovernorSettingOptions.DrsDisplayName, drs.DisplayName);
        }

        [Test]
        public void VideoPage_HasTargetFpsEnum_MapsTo30_60_120()
        {
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition(FrameGovernorSettingOptions.TargetFpsSettingId, out SettingDefinition target));
            Assert.AreEqual(SettingType.Enum, target.Type);
            CollectionAssert.AreEqual(new[] { "30", "60", "120" }, target.EnumOptions);
            Assert.AreEqual(1, (int)target.DefaultValue, "默认 60fps = FrameGovernorHost.DefaultTargetFps");
            Assert.IsNotNull(target.Apply);
            Assert.AreEqual(FrameGovernorSettingOptions.BuildTargetFpsLabel(), target.DisplayName);

            Assert.AreEqual(30f, FrameGovernorSettingOptions.TargetFpsFromOption(0));
            Assert.AreEqual(60f, FrameGovernorSettingOptions.TargetFpsFromOption(1));
            Assert.AreEqual(120f, FrameGovernorSettingOptions.TargetFpsFromOption(2));
            Assert.AreEqual(FrameGovernorHost.DefaultTargetFps, FrameGovernorSettingOptions.TargetFpsFromOption(99),
                "越界索引回退默认 60fps（绝不返回非正值）");
        }

        // ---- 1b. 与既有"帧率上限"的语义互斥（主控裁决 2） -----------------------

        [Test]
        public void FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct()
        {
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition("video.frameRateCap", out SettingDefinition cap));
            Assert.IsTrue(registry.TryGetDefinition(FrameGovernorSettingOptions.TargetFpsSettingId, out SettingDefinition target));

            Assert.AreEqual("帧率上限", cap.DisplayName, "锁帧那一行的既有文案不变（本任务不动它的行为）");
            StringAssert.Contains("不锁帧", target.DisplayName,
                "目标行必须明说不锁帧，否则玩家会把它当成第二个帧率上限");
            StringAssert.Contains("DRS", target.DisplayName,
                "目标行必须说明它只在开启动态分辨率后生效");
            StringAssert.DoesNotContain("上限", target.DisplayName, "两行的名称必须互斥（上限 vs 不锁帧）");
            Assert.AreNotEqual(cap.DisplayName, target.DisplayName);
        }

        [Test]
        public void GovernorRowLabels_FitTheLabelColumn_WithoutTruncation()
        {
            // 两个可能变长的标签都必须单行放进标签列：否则 Unity Text 会折行/截断，
            // 真正要传达的"生效条件/不支持"反而看不见（诚实的标注必须看得见）。
            AssertLabelFits(FrameGovernorSettingOptions.BuildTargetFpsLabel());
            AssertLabelFits(FrameGovernorSettingOptions.BuildDrsLabel(renderScaleSupported: false));
        }

        private static void AssertLabelFits(string label)
        {
            float width = MeasureTextWidth(label);
            TestContext.WriteLine($"label '{label}' → {width:0.0}px (column {SettingsRowFactory.LabelWidth:0}px)");
            Assume.That(width, Is.GreaterThan(0f), "字体度量不可用（preferredWidth=0）时本用例不成立");

            Assert.LessOrEqual(width, SettingsRowFactory.LabelWidth,
                $"行标签 '{label}' 超出标签列会被折行/截断，诚实的后缀反而看不见");
        }

        private static float MeasureTextWidth(string content)
        {
            var go = new GameObject("label_measure", typeof(RectTransform));
            try
            {
                Text text = go.AddComponent<Text>();
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = 30; // SettingsRowFactory 的行标签字号
                text.text = content;
                return text.preferredWidth;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---- 2. apply 必须抵达 governor ----------------------------------------

        [Test]
        public void ApplyDrs_ReachesGovernorHost_EnableState()
        {
            NewHost();
            Assert.IsFalse(_host.IsGovernorEnabled, "precondition: 宿主默认不治理");

            SettingsModel model = NewModel();
            model.Set(FrameGovernorSettingOptions.DrsSettingId, true);

            Assert.IsTrue(_host.IsGovernorEnabled, "动态分辨率=开 必须真的让 governor 开始治理");
            Assert.IsTrue(_host.Governor.Enabled);

            model.Set(FrameGovernorSettingOptions.DrsSettingId, false);

            Assert.IsFalse(_host.IsGovernorEnabled, "动态分辨率=关 必须真的让 governor 停止治理");
            Assert.IsFalse(_host.Governor.Enabled);
        }

        [Test]
        public void ApplyTargetFps_UpdatesGovernorTargetMs_LiveWhileEnabled()
        {
            NewHost();
            _host.SetEnabled(true);
            Assert.AreEqual(1000f / FrameGovernorHost.DefaultTargetFps, _host.Governor.TargetMs, 1e-3f);

            SettingsModel model = NewModel();
            model.Set(FrameGovernorSettingOptions.TargetFpsSettingId, FrameGovernorSettingOptions.TargetFpsOption30);

            Assert.AreEqual(30f, _host.TargetFps);
            Assert.AreEqual(1000f / 30f, _host.Governor.TargetMs, 1e-3f,
                "目标帧时必须落到 governor 的 TargetMs（迟滞判定的预算就是它）");

            model.Set(FrameGovernorSettingOptions.TargetFpsSettingId, FrameGovernorSettingOptions.TargetFpsOption120);

            Assert.AreEqual(120f, _host.TargetFps);
            Assert.AreEqual(1000f / 120f, _host.Governor.TargetMs, 1e-3f, "治理中改目标也必须即时生效");
        }

        [Test]
        public void TargetFps_WhileDrsOff_StoresValueOnly_WithNoRuntimeEffect()
        {
            // 主控裁决 2-③：DRS 关时改帧率目标只能"存值"，不得产生任何运行行为。
            NewHost();
            _telemetry.CpuFrameMs = 60f; // 16.7fps —— 对任何预算都算过载

            SettingsModel model = NewModel();
            model.Set(FrameGovernorSettingOptions.TargetFpsSettingId, FrameGovernorSettingOptions.TargetFpsOption30);

            Assert.AreEqual(FrameGovernorSettingOptions.TargetFpsOption30,
                model.Get<int>(FrameGovernorSettingOptions.TargetFpsSettingId), "值必须落库");
            Assert.AreEqual(30f, _host.TargetFps, "值必须抵达宿主（只是不驱动措施）");

            Assume.That(ActiveUrp(), Is.Not.Null, "本用例的物理落点断言需要 URP 管线");
            float scaleBefore = ActiveUrp().renderScale;

            for (int i = 0; i < 10; i++)
            {
                _host.Tick(0.5f); // 5 秒持续过载
            }

            Assert.IsFalse(_host.IsGovernorEnabled, "DRS 关 → 治理器不得被目标帧率顺手打开");
            Assert.IsFalse(_host.Governor.Enabled);
            Assert.AreEqual(0, _host.Governor.Level, "DRS 关 → 阶梯一步都不许走");
            Assert.AreEqual(scaleBefore, ActiveUrp().renderScale, 1e-5f, "DRS 关 → 不得改 renderScale");

            // 反证：同一个目标值在 DRS 打开后立刻开始驱动措施（说明它此前只是"没被消费"）。
            _host.SetEnabled(true);
            for (int i = 0; i < 6; i++)
            {
                _host.Tick(0.5f);
            }

            Assert.Greater(_host.Governor.Level, 0, "DRS 打开后，30fps 预算 + 60ms 帧耗必须触发降级");
            Assert.Less(ActiveUrp().renderScale, scaleBefore, "降级必须真的落到 renderScale 上");
        }

        [Test]
        public void Apply_WithoutBootstrapHost_StillStoresTheValue()
        {
            Assert.IsNull(FrameGovernorHost.Instance, "precondition: 未自举（EditMode / boot 前 / 已 Shutdown）");

            SettingsModel model = NewModel();
            Assert.DoesNotThrow(() => model.Set(FrameGovernorSettingOptions.DrsSettingId, true));
            Assert.DoesNotThrow(() =>
                model.Set(FrameGovernorSettingOptions.TargetFpsSettingId, FrameGovernorSettingOptions.TargetFpsOption120));

            Assert.IsTrue(model.Get<bool>(FrameGovernorSettingOptions.DrsSettingId), "宿主缺席时值仍必须落库，等开机应用点重放");
            Assert.AreEqual(FrameGovernorSettingOptions.TargetFpsOption120,
                model.Get<int>(FrameGovernorSettingOptions.TargetFpsSettingId));
        }

        // ---- 3. 冷启动生效（本任务核心） ---------------------------------------

        [Test]
        public void ColdStart_AppliesPersistedGovernorSettings_WithoutOpeningThePanel()
        {
            // 模拟"上次退出前存过 DRS=开 + 30fps"的 settings.json（Save() 的同一格式）。
            File.WriteAllText(
                Path.Combine(_tempRoot, SettingsModel.FileName),
                "{\n" +
                "  \"version\": 1,\n" +
                "  \"settings\": {\n" +
                "    \"video.dynamicResolution\": true,\n" +
                "    \"video.frameRateTarget\": 0\n" +
                "  }\n" +
                "}\n");

            NewHost();
            Assert.IsFalse(_host.IsGovernorEnabled, "precondition: 开机时宿主未治理");

            int applied = SettingsBootApplier.ApplyFromDisk(_tempRoot);
            TestContext.WriteLine(
                $"cold start: applied={applied}, governorEnabled={_host.IsGovernorEnabled}, " +
                $"targetFps={_host.TargetFps:0}, targetMs={_host.Governor.TargetMs:0.00}");

            Assert.GreaterOrEqual(applied, 2, "两条 governor 设置都必须被识别并重放");
            Assert.IsTrue(_host.IsGovernorEnabled,
                "冷启动（不经设置面板）后 governor 必须已经开着 —— 这是 SettingsModel.Load 只在打开面板时调用的根因修复");
            Assert.AreEqual(30f, _host.TargetFps, "目标帧率必须来自 settings.json，而不是默认 60");
            Assert.AreEqual(1000f / 30f, _host.Governor.TargetMs, 1e-3f,
                "值必须抵达 governor 内部预算（TargetMs），不是只把 model 读了一遍");
            Assert.IsTrue(_host.Governor.Enabled);
        }

        [Test]
        public void ColdStart_WithoutSettingsFile_LeavesGovernorDefaultsUntouched()
        {
            NewHost();
            float targetMsBefore = _host.Governor.TargetMs;
            float fpsBefore = _host.TargetFps;

            int applied = SettingsBootApplier.ApplyFromDisk(_tempRoot); // 空目录 = 全新用户

            Assert.AreEqual(0, applied, "没有 settings.json 时不得应用任何设置");
            Assert.IsFalse(_host.IsGovernorEnabled, "零破坏：DRS 仍关");
            Assert.AreEqual(fpsBefore, _host.TargetFps, "零破坏：目标帧率仍为宿主默认 60");
            Assert.AreEqual(targetMsBefore, _host.Governor.TargetMs, "零破坏：占位核心的预算未被改动（未开治理 = 不限帧）");
            Assert.IsFalse(_host.Governor.Enabled);
        }

        // ---- 4. 持久化 roundtrip ------------------------------------------------

        [Test]
        public void SaveThenLoad_RoundTripsGovernorRows()
        {
            SettingsModel model = NewModel();
            model.Set(FrameGovernorSettingOptions.DrsSettingId, true);
            model.Set(FrameGovernorSettingOptions.TargetFpsSettingId, FrameGovernorSettingOptions.TargetFpsOption120);
            model.Save();

            string json = File.ReadAllText(model.FilePath);
            TestContext.WriteLine("settings.json →" + Environment.NewLine + json);
            StringAssert.Contains("\"video.dynamicResolution\": true", json);
            StringAssert.Contains("\"video.frameRateTarget\": 2", json);

            var reloaded = new SettingsModel(SettingsRegistry.BuildDefaults(), _tempRoot);
            int applied = reloaded.Load();

            Assert.GreaterOrEqual(applied, 2);
            Assert.IsTrue(reloaded.Get<bool>(FrameGovernorSettingOptions.DrsSettingId), "roundtrip: DRS 值必须保持");
            Assert.AreEqual(FrameGovernorSettingOptions.TargetFpsOption120,
                reloaded.Get<int>(FrameGovernorSettingOptions.TargetFpsSettingId), "roundtrip: 目标帧率索引必须保持");
        }

        // ---- 5. 诚实标注 --------------------------------------------------------

        [Test]
        public void DrsLabel_IsHonest_WhenRenderScaleCannotLand()
        {
            Assert.AreEqual(FrameGovernorSettingOptions.DrsDisplayName,
                FrameGovernorSettingOptions.BuildDrsLabel(renderScaleSupported: true),
                "URP 上降分辨率真的生效 → 不加后缀");

            string unsupported = FrameGovernorSettingOptions.BuildDrsLabel(renderScaleSupported: false);
            StringAssert.EndsWith(FrameGovernorSettingOptions.DrsNonUrpSuffix, unsupported,
                "非 URP 管线必须在行标签上如实说明只剩降档/后处理（照超分行的后缀风格）");
            StringAssert.StartsWith(FrameGovernorSettingOptions.DrsDisplayName, unsupported);
        }
    }
}
