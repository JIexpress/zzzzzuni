// FrameGenerationSettingsTests.cs —— TASK-B-R9-06 的设置页侧证据：
// 1) video.frameGeneration 真存在于注册表（数据驱动的唯一事实源），Toggle、默认关；
// 2) 三档（+ 两个细分）门控在**标签上**如实表达、并在不可用环境禁用该行；
// 3) apply 真的抵达 IFrameGenerationSettings.TrySetEnabled（不是"值存下来没人用"）；
// 4) 冷启动由 SettingsBootApplier 从 settings.json 重放（开机生效，不用打开面板）；
// 5) 标签长度受 340px 标签列约束（诚实的后缀不能被截断）；
// 6) TASK-B-R9-07 M1：可用档的行标签必须写上"抗锯齿降至 1×"的代价，不可用档不许提
//    （那些行根本拨不动，混进代价标注反而误导）。
//
// 注意：这里注册的是**桩服务**——真 Path 的开关落到 dxgi 代理的 ZFG_FgSetEnabled，
// 只能在打包后的 Player 里验（见 Docs/Logs/TASK-B-R9-06.md 阶段 3 的真机数字）。

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Pipeline.Tests
{
    public sealed class FrameGenerationSettingsTests
    {
        private string _tempRoot;

        /// <summary>可录制的 IFrameGenerationSettings 桩：记录请求，能力可配置。</summary>
        private sealed class StubFrameGeneration : IFrameGenerationSettings
        {
            public FrameGenerationCapability Capability = FrameGenerationCapability.Available;

            public bool AcceptEnable = true;

            public int Calls;

            public bool? LastRequested;

            public bool IsAvailable => Capability == FrameGenerationCapability.Available;

            public string CapabilityDetail => Capability == FrameGenerationCapability.Available
                ? string.Empty
                : "stub:" + Capability;

            public FrameGenerationCapability QueryCapability()
            {
                return Capability;
            }

            public bool TrySetEnabled(bool enabled)
            {
                Calls++;
                LastRequested = enabled;
                return AcceptEnable;
            }

            public bool TryGetStats(out FrameGenerationStatsInfo stats)
            {
                stats = default;
                return false;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "zframe_fg_settings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            Services.Unregister<IFrameGenerationSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            Services.Unregister<IFrameGenerationSettings>();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        private SettingsModel NewModel()
        {
            return new SettingsModel(SettingsRegistry.BuildDefaults(), _tempRoot);
        }

        // ---- 1. 行定义 ---------------------------------------------------------

        [Test]
        public void VideoPage_HasFrameGenerationToggle_DefaultsOff_WiredLive()
        {
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition(FrameGenerationSettingOptions.SettingId, out SettingDefinition row),
                "插帧行必须由 SettingsRegistry 定义（数据驱动，面板只负责渲染）");
            Assert.AreEqual(SettingType.Toggle, row.Type);
            Assert.AreEqual(false, row.DefaultValue, "零破坏：默认关 = 纯透传");
            Assert.IsNotNull(row.Apply, "该行必须真接 IFrameGenerationSettings（否则就是能点不生效的假开关）");
            StringAssert.StartsWith(FrameGenerationSettingOptions.DisplayName, row.DisplayName);
        }

        [Test]
        public void FrameGenerationRow_IsAppendedLast_SoExistingRowIndicesStayStable()
        {
            Assert.IsTrue(SettingsRegistry.BuildDefaults().TryGetPage("video", out SettingsPage video));
            Assert.AreEqual(FrameGenerationSettingOptions.SettingId, video.Settings[video.Settings.Count - 1].Id,
                "新行排在末尾：既有行的下标/顺序（DRS、帧率目标）不受影响");
        }

        // ---- 2. 三档门控的诚实表达（标签 + 禁用） -------------------------------

        [Test]
        public void CapabilitySuffixes_AreDistinct_AndHonest()
        {
            // TASK-B-R9-07 M1：可用档不再是空后缀 —— 必须挂上"抗锯齿降至 1×"的代价标注。
            Assert.AreEqual(FrameGenerationSettingOptions.DisplayName + "（抗锯齿降至 1×）",
                FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.Available),
                "可用时必须在行标签上说明开启的代价（MSAA 会被压到 1×）");
            Assert.AreEqual(FrameGenerationSettingOptions.DisplayName + "（仅打包后生效）",
                FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.EditorOnly),
                "Editor 内必须明说只在打包后生效（dxgi 代理不接管）");
            Assert.AreEqual(FrameGenerationSettingOptions.DisplayName + "（需 D3D12）",
                FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.NotD3D12),
                "非 D3D12 必须明说需要 D3D12");
            StringAssert.Contains("代理",
                FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.ProxyUnavailable),
                "代理缺失/hook 未装必须点名原因");
            StringAssert.Contains("URP",
                FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.PipelineUnsupported),
                "非 URP 必须明说需要 URP（MV 的唯一来源）");

            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (FrameGenerationCapability capability in Enum.GetValues(typeof(FrameGenerationCapability)))
            {
                Assert.IsTrue(seen.Add(FrameGenerationSettingOptions.SuffixFor(capability)),
                    "每一档的门控文案必须互不相同：" + capability);
            }
        }

        [Test]
        public void MsaaCostIsOnlyAdvertisedWhereTheSwitchCanActuallyBeFlipped()
        {
            string available = FrameGenerationSettingOptions.BuildLabel(FrameGenerationCapability.Available);
            StringAssert.Contains("抗锯齿", available, "M1：可用档必须提到抗锯齿代价");
            StringAssert.Contains("1×", available, "M1：必须写明降到 1×");

            // 不可用的四档：开关被禁用、画质不会被改，所以标签只讲"为什么不能用"。
            // 混进代价标注会把"不能开"和"开了会怎样"搅成一句，反而误导。
            foreach (FrameGenerationCapability capability in Enum.GetValues(typeof(FrameGenerationCapability)))
            {
                if (capability == FrameGenerationCapability.Available)
                {
                    continue;
                }

                string label = FrameGenerationSettingOptions.BuildLabel(capability);
                StringAssert.DoesNotContain("抗锯齿", label,
                    "不可用档不许承诺/吓唬画质后果（该行根本拨不动）：" + capability);
                StringAssert.StartsWith(FrameGenerationSettingOptions.DisplayName, label);
            }
        }

        [Test]
        public void RowLabels_FitTheLabelColumn_WithoutTruncation()
        {
            foreach (FrameGenerationCapability capability in Enum.GetValues(typeof(FrameGenerationCapability)))
            {
                AssertLabelFits(FrameGenerationSettingOptions.BuildLabel(capability));
            }
        }

        [Test]
        public void RowIsInteractable_OnlyWhenCapabilityIsAvailable()
        {
            var stub = new StubFrameGeneration { Capability = FrameGenerationCapability.Available };
            Assert.IsFalse(FrameGenerationSettingOptions.IsRowInteractable(),
                "无服务（EditMode）时不可交互 —— 否则就是让人拨一个不生效的开关");

            Services.Register<IFrameGenerationSettings>(stub);
            Assert.IsTrue(FrameGenerationSettingOptions.IsRowInteractable(), "能力可用时可交互");

            stub.Capability = FrameGenerationCapability.NotD3D12;
            Assert.IsFalse(FrameGenerationSettingOptions.IsRowInteractable(), "非 D3D12 必须禁用该行");
            Assert.AreEqual(FrameGenerationSettingOptions.DisplayName + "（需 D3D12）",
                FrameGenerationSettingOptions.BuildLabel());
        }

        // ---- 3. apply 链路 -----------------------------------------------------

        [Test]
        public void Apply_ReachesTheService_WithTheExactRequest()
        {
            var stub = new StubFrameGeneration();
            Services.Register<IFrameGenerationSettings>(stub);

            FrameGenerationSettingOptions.ApplyEnabled(true);
            Assert.AreEqual(1, stub.Calls, "开启必须真的调用 TrySetEnabled");
            Assert.AreEqual(true, stub.LastRequested);

            FrameGenerationSettingOptions.ApplyEnabled(false);
            Assert.AreEqual(2, stub.Calls, "关闭必须真的调用 TrySetEnabled（回纯透传）");
            Assert.AreEqual(false, stub.LastRequested);
        }

        [Test]
        public void Apply_WithoutService_OnlyWarns_AndValueStillPersists()
        {
            Assert.DoesNotThrow(() => FrameGenerationSettingOptions.ApplyEnabled(true));

            SettingsModel model = NewModel();
            Assert.DoesNotThrow(() => model.Set(FrameGenerationSettingOptions.SettingId, true));
            Assert.IsTrue(model.Get<bool>(FrameGenerationSettingOptions.SettingId),
                "服务缺席时值仍必须落库，等开机应用点重放");
        }

        [Test]
        public void ApplyRowFromRegistry_GoesThroughTheSameEntry()
        {
            var stub = new StubFrameGeneration();
            Services.Register<IFrameGenerationSettings>(stub);

            Assert.IsTrue(SettingsRegistry.BuildDefaults()
                .TryGetDefinition(FrameGenerationSettingOptions.SettingId, out SettingDefinition row));
            row.Apply(true);
            Assert.AreEqual(1, stub.Calls, "注册表里的 apply 必须与服务入口同一条路径（单一事实源）");
        }

        // ---- 4. 冷启动 + 持久化 -------------------------------------------------

        [Test]
        public void ColdStart_AppliesPersistedFrameGeneration_WithoutOpeningThePanel()
        {
            File.WriteAllText(
                Path.Combine(_tempRoot, SettingsModel.FileName),
                "{\n" +
                "  \"version\": 1,\n" +
                "  \"settings\": {\n" +
                "    \"video.frameGeneration\": true\n" +
                "  }\n" +
                "}\n");

            var stub = new StubFrameGeneration();
            Services.Register<IFrameGenerationSettings>(stub);

            int applied = SettingsBootApplier.ApplyFromDisk(_tempRoot);
            TestContext.WriteLine($"cold start: applied={applied} fgCalls={stub.Calls} requested={stub.LastRequested}");

            Assert.GreaterOrEqual(applied, 1, "插帧设置必须被识别并重放");
            Assert.AreEqual(1, stub.Calls, "开机重放必须真的把请求送达服务（不是只读了文件）");
            Assert.AreEqual(true, stub.LastRequested);
        }

        [Test]
        public void SaveThenLoad_RoundTripsFrameGeneration()
        {
            SettingsModel model = NewModel();
            model.Set(FrameGenerationSettingOptions.SettingId, true);
            model.Save();

            string json = File.ReadAllText(model.FilePath);
            TestContext.WriteLine("settings.json →" + Environment.NewLine + json);
            StringAssert.Contains("\"video.frameGeneration\": true", json);

            var reloaded = new SettingsModel(SettingsRegistry.BuildDefaults(), _tempRoot);
            Assert.GreaterOrEqual(reloaded.Load(), 1);
            Assert.IsTrue(reloaded.Get<bool>(FrameGenerationSettingOptions.SettingId), "roundtrip: 插帧值必须保持");
        }

        // ---- 5. 面板上真的是控件（不是只存在于注册表里） -------------------------

        [Test]
        public void FrameGenerationRow_RendersAsAReal_DisabledToggle_InEditor()
        {
            var registry = new SettingsRegistry(new[]
            {
                new SettingsPage("video", "视频", new[]
                {
                    SettingDefinition.Toggle(
                        FrameGenerationSettingOptions.SettingId,
                        FrameGenerationSettingOptions.BuildLabel(),
                        false,
                        apply: value => FrameGenerationSettingOptions.ApplyEnabled((bool)value),
                        interactable: FrameGenerationSettingOptions.IsRowInteractable),
                }),
            });
            var model = new SettingsModel(registry, _tempRoot);
            var root = new GameObject("~fg_row_canvas", typeof(RectTransform));
            try
            {
                GameObject row = SettingsRowFactory.CreateRow(
                    registry.Pages[0].Settings[0],
                    root.transform,
                    onValueChanged: (definition, value) => model.Set(definition.Id, value));
                Toggle toggle = row.GetComponentInChildren<Toggle>(includeInactive: true);
                Assert.IsNotNull(toggle, "插帧行必须渲染成真 Toggle 控件");
                Assert.IsFalse(toggle.interactable,
                    "EditMode（Editor）下必须禁用：dxgi 代理不生效，拨了也不会插帧");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
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
    }
}
