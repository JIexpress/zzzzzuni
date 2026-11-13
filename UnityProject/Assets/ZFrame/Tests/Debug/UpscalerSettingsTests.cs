using System;
using System.Collections.Generic;
using NUnit.Framework;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// EditMode tests for the 超分 settings glue (TASK-B-R6-03):
    /// <see cref="UpscalerSettingOptions"/> mapping (index → native preferred,
    /// incl. out-of-range/unknown fallback to Auto), capability-gated option
    /// labels, the apply path through a stubbed <see cref="IUpscalerSettings"/>
    /// service and the registry integration (row present, live apply, default 自动).
    /// </summary>
    public sealed class UpscalerSettingsTests
    {
        /// <summary>可录制的 IUpscalerSettings 桩：记录 SetPreferred 调用，能力可配置。</summary>
        private sealed class StubUpscalerSettings : IUpscalerSettings
        {
            public readonly List<int> SetPreferredCalls = new List<int>();
            public bool Available;
            public UpscalerCapsInfo Caps;
            public bool SetPreferredResult = true;

            public bool IsAvailable => Available;

            public UpscalerCapsInfo QueryCaps()
            {
                return Caps;
            }

            public bool TrySetPreferred(int preferred)
            {
                SetPreferredCalls.Add(preferred);
                return SetPreferredResult;
            }

            public bool TryGetActiveBackend(out int activeBackend)
            {
                activeBackend = 0;
                return false;
            }
        }

        [SetUp]
        public void SetUp()
        {
            // 隔离全局服务表：任何泄漏的注册都会污染标签/apply 断言。
            Services.Unregister<IUpscalerSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            Services.Unregister<IUpscalerSettings>();
        }

        // ---------- Enum 索引 ↔ preferred 映射 ----------

        [Test]
        public void Map_AllFiveOptions_ProduceNativePreferredValues()
        {
            // 自动/关/FSR 3.1/FSR 4.1/DLSS 5 → Auto/Bilinear/FSR3/FSR4/DLSS。
            // 原生无 FSR2 选项（2 跳过）；关 = Bilinear 兜底。
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionAuto));
            Assert.AreEqual(1, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionOff));
            Assert.AreEqual(3, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionFsr3));
            Assert.AreEqual(4, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionFsr4));
            Assert.AreEqual(5, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionDlss));
        }

        [Test]
        public void Map_OutOfRangeOrUnknown_FallsBackToAuto()
        {
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(-1));
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(UpscalerSettingOptions.OptionCount));
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(999));
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(int.MinValue));
            Assert.AreEqual(0, UpscalerSettingOptions.OptionIndexToPreferred(int.MaxValue));
        }

        [Test]
        public void Map_Default_IsAuto()
        {
            Assert.AreEqual(0, UpscalerSettingOptions.DefaultOptionIndex);
            Assert.AreEqual(5, UpscalerSettingOptions.OptionCount);
        }

        // ---------- 能力门控标注 ----------

        [Test]
        public void Labels_WithoutService_AllVendorOptionsMarkedUnprobed()
        {
            // 服务未注册 = 根本没测（B-FSR-02）：厂商项一律标"未检测（原生内核未启用）"，
            // 不许无来源地宣称"本机不支持"。
            CollectionAssert.AreEqual(
                new[]
                {
                    "自动", "关",
                    "FSR 3.1：未检测（原生内核未启用）",
                    "FSR 4.1：未检测（原生内核未启用）",
                    "DLSS 5：未检测（原生内核未启用）",
                },
                UpscalerSettingOptions.BuildOptionLabels((IUpscalerSettings)null));
        }

        [Test]
        public void Labels_WithCaps_AnnotateOnlyUnsupportedVendorOptions()
        {
            var stub = new StubUpscalerSettings
            {
                Available = true,
                Caps = new UpscalerCapsInfo
                {
                    Available = true,
                    FsrRuntimePresent = true,
                    Fsr4Available = false,
                    DlssAvailable = false,
                    Fsr4ProbeMode = 0u, // 真设备探测口径——"本机不支持"的唯一合法来源
                },
            };

            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1", "FSR 4.1（本机不支持）", "DLSS 5（本机不支持）" },
                UpscalerSettingOptions.BuildOptionLabels(stub));
        }

        [Test]
        public void Labels_FullCapability_Fsr4MarkedAvailable()
        {
            var stub = new StubUpscalerSettings
            {
                Available = true,
                Caps = new UpscalerCapsInfo
                {
                    Available = true,
                    FsrRuntimePresent = true,
                    Fsr4Available = true,
                    DlssAvailable = true,
                },
            };

            // FSR4 可用 → "（可用）"后缀（B-FSR-02：provider 版本串的 C ABI 读口
            // 暂缺，如实标"可用"，不编造版本号）。
            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1", "FSR 4.1（可用）", "DLSS 5" },
                UpscalerSettingOptions.BuildOptionLabels(stub));
        }

        [Test]
        public void Labels_ServicePresentButNativeUnavailable_AllVendorOptionsMarkedUnprobed()
        {
            var stub = new StubUpscalerSettings
            {
                Available = false,
                Caps = default, // Available=false → 根本没测，不许宣称"不支持"
            };

            CollectionAssert.AreEqual(
                new[]
                {
                    "自动", "关",
                    "FSR 3.1：未检测（原生内核未启用）",
                    "FSR 4.1：未检测（原生内核未启用）",
                    "DLSS 5：未检测（原生内核未启用）",
                },
                UpscalerSettingOptions.BuildOptionLabels(stub));
        }

        // ---------- FSR4 三态文案映射（B-FSR-02，表驱动） ----------

        /// <summary>FSR4 三态映射表的一行：输入 caps 关键位 + 期望状态文本 + 期望完整标签。</summary>
        private sealed class Fsr4Case
        {
            public readonly bool Available;
            public readonly bool FsrRuntimePresent;
            public readonly bool Fsr4Available;
            public readonly uint ProbeMode;
            public readonly string ExpectedStatus;
            public readonly string ExpectedLabel;

            public Fsr4Case(bool available, bool fsrRuntimePresent, bool fsr4Available,
                uint probeMode, string expectedStatus, string expectedLabel)
            {
                Available = available;
                FsrRuntimePresent = fsrRuntimePresent;
                Fsr4Available = fsr4Available;
                ProbeMode = probeMode;
                ExpectedStatus = expectedStatus;
                ExpectedLabel = expectedLabel;
            }
        }

        /// <summary>
        /// 三态 + 兜底映射表（TASK-B-FSR-02 简报第 2 点）：
        /// fsrRuntimePresent=0 → 运行时缺失；probeMode!=0 且 fsr4=0 → 未检测（探测回退）；
        /// probeMode=0 且 fsr4=0 → 本机不支持；fsr4=1 → 可用（版本读口暂缺）；
        /// 未知 probeMode 兜底 → 未检测；服务缺席 → 未检测（原生内核未启用）。
        /// </summary>
        private static readonly Fsr4Case[] Fsr4Table =
        {
            // 服务缺席（根本没测）：优先于一切，不许宣称"不支持"
            new Fsr4Case(false, false, false, 0u,
                UpscalerSettingOptions.UnprobedNoNativeText,
                "FSR 4.1：未检测（原生内核未启用）"),
            // fsr4Available=1 → 可用（真实命中；探测口径不影响阳性陈述）
            new Fsr4Case(true, true, true, 0u,
                UpscalerSettingOptions.Fsr4AvailableText, "FSR 4.1（可用）"),
            // 运行时缺失：比探测口径更具体的真实原因，优先于"未检测/不支持"
            new Fsr4Case(true, false, false, 0u,
                UpscalerSettingOptions.Fsr4RuntimeMissingText,
                "FSR 4.1：运行时缺失（FFX DLL 未找到）"),
            new Fsr4Case(true, false, false, 1u,
                UpscalerSettingOptions.Fsr4RuntimeMissingText,
                "FSR 4.1：运行时缺失（FFX DLL 未找到）"),
            // probeMode=1（null 回退）且 fsr4=0 → 未检测（结果可能失真）
            new Fsr4Case(true, true, false, 1u,
                UpscalerSettingOptions.Fsr4UnprobedText,
                "FSR 4.1：未检测（探测回退，结果不可信）"),
            // probeMode=2（未探测）且 fsr4=0 → 未检测
            new Fsr4Case(true, true, false, 2u,
                UpscalerSettingOptions.Fsr4UnprobedText,
                "FSR 4.1：未检测（探测回退，结果不可信）"),
            // probeMode=0（真设备探测可信）且 fsr4=0 → 本机不支持（唯一合法"不支持"）
            new Fsr4Case(true, true, false, 0u,
                UpscalerSettingOptions.Fsr4UnsupportedText, "FSR 4.1（本机不支持）"),
            // 未知 probeMode 兜底：!=0 一律按"未检测"处理，不得当作 0
            new Fsr4Case(true, true, false, 99u,
                UpscalerSettingOptions.Fsr4UnprobedText,
                "FSR 4.1：未检测（探测回退，结果不可信）"),
            new Fsr4Case(true, true, false, uint.MaxValue,
                UpscalerSettingOptions.Fsr4UnprobedText,
                "FSR 4.1：未检测（探测回退，结果不可信）"),
        };

        [Test]
        public void Fsr4Status_TableDriven_ThreeStatesAndFallbacks()
        {
            foreach (Fsr4Case entry in Fsr4Table)
            {
                var caps = new UpscalerCapsInfo
                {
                    Available = entry.Available,
                    FsrRuntimePresent = entry.FsrRuntimePresent,
                    Fsr4Available = entry.Fsr4Available,
                    Fsr4ProbeMode = entry.ProbeMode,
                };

                Assert.AreEqual(entry.ExpectedStatus,
                    UpscalerSettingOptions.BuildFsr4StatusText(caps),
                    $"状态文本映射错误（available={entry.Available} runtime={entry.FsrRuntimePresent} "
                    + $"fsr4={entry.Fsr4Available} probeMode={entry.ProbeMode}）");

                // 经 BuildOptionLabels 全链路核对 FSR4 行标签（下标 3）
                var stub = new StubUpscalerSettings { Available = entry.Available, Caps = caps };
                string[] labels = UpscalerSettingOptions.BuildOptionLabels(stub);
                Assert.AreEqual(entry.ExpectedLabel, labels[UpscalerSettingOptions.OptionFsr4],
                    $"FSR4 标签映射错误（probeMode={entry.ProbeMode}）");
            }
        }

        // ---------- apply 链路（Enum → 映射 → IUpscalerSettings） ----------

        [Test]
        public void Apply_WithService_PassesMappedPreferredValue()
        {
            var stub = new StubUpscalerSettings();
            Services.Register<IUpscalerSettings>(stub);

            UpscalerSettingOptions.ApplyOptionIndex(UpscalerSettingOptions.OptionFsr3);

            CollectionAssert.AreEqual(new[] { 3 }, stub.SetPreferredCalls, "FSR 3.1 应映射为原生 FSR3(3)");
        }

        [Test]
        public void Apply_AutoOption_PassesZero()
        {
            var stub = new StubUpscalerSettings();
            Services.Register<IUpscalerSettings>(stub);

            UpscalerSettingOptions.ApplyOptionIndex(UpscalerSettingOptions.OptionAuto);

            CollectionAssert.AreEqual(new[] { 0 }, stub.SetPreferredCalls, "自动必须原样传 Auto(0)，选优交给原生 Chain");
        }

        [Test]
        public void Apply_UnknownIndex_FallsBackToAuto_AndStillCalls()
        {
            var stub = new StubUpscalerSettings();
            Services.Register<IUpscalerSettings>(stub);

            UpscalerSettingOptions.ApplyOptionIndex(999);

            CollectionAssert.AreEqual(new[] { 0 }, stub.SetPreferredCalls, "越界索引应回退 Auto 后仍下发");
        }

        [Test]
        public void Apply_WithoutService_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => UpscalerSettingOptions.ApplyOptionIndex(UpscalerSettingOptions.OptionFsr4));
        }

        [Test]
        public void Apply_ServiceRejects_DoesNotThrow()
        {
            var stub = new StubUpscalerSettings { SetPreferredResult = false };
            Services.Register<IUpscalerSettings>(stub);

            Assert.DoesNotThrow(() => UpscalerSettingOptions.ApplyOptionIndex(UpscalerSettingOptions.OptionDlss));
            CollectionAssert.AreEqual(new[] { 5 }, stub.SetPreferredCalls);
        }

        // ---------- registry 集成 ----------

        [Test]
        public void Registry_UpscalerRow_IsLiveEnum_DefaultAuto_WithGatedLabels()
        {
            // 无服务注册（EditMode 默认态）= 根本没测：厂商项标"未检测"，确定性可断言。
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();

            Assert.IsTrue(registry.TryGetDefinition("video.upscaler", out SettingDefinition upscaler));
            Assert.AreEqual("超分", upscaler.DisplayName);
            Assert.AreEqual(SettingType.Enum, upscaler.Type);
            Assert.IsNotNull(upscaler.Apply, "超分必须真调 IUpscalerSettings.TrySetPreferred（B-R6-03 实接）");
            Assert.AreEqual(0, (int)upscaler.DefaultValue, "默认 = 自动");
            Assert.AreEqual(5, upscaler.EnumOptions.Length);
            StringAssert.EndsWith("未检测（原生内核未启用）", upscaler.EnumOptions[3],
                "无服务 = 没测过，FSR 4.1 必须标未检测而非不支持（B-FSR-02）");
        }

        [Test]
        public void Registry_UpscalerRow_LabelsFollowRegisteredCaps()
        {
            var stub = new StubUpscalerSettings
            {
                Available = true,
                Caps = new UpscalerCapsInfo
                {
                    Available = true,
                    FsrRuntimePresent = true,
                    Fsr4Available = false,
                    DlssAvailable = false,
                    Fsr4ProbeMode = 0u, // 真设备探测口径 → 允许"本机不支持"
                },
            };
            Services.Register<IUpscalerSettings>(stub);

            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition("video.upscaler", out SettingDefinition upscaler));

            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1", "FSR 4.1（本机不支持）", "DLSS 5（本机不支持）" },
                upscaler.EnumOptions);
        }

        [Test]
        public void Registry_UpscalerApply_FiresThroughModel_AndCallsService()
        {
            var stub = new StubUpscalerSettings();
            Services.Register<IUpscalerSettings>(stub);

            SettingsRegistry registry = SettingsRegistry.BuildDefaults();
            Assert.IsTrue(registry.TryGetDefinition("video.upscaler", out SettingDefinition upscaler));

            // 直接走定义的 apply（SettingsModel.Set 的最终落点），验证整条链路。
            upscaler.Apply.Invoke(2); // FSR 3.1

            CollectionAssert.AreEqual(new[] { 3 }, stub.SetPreferredCalls);
        }
    }
}
