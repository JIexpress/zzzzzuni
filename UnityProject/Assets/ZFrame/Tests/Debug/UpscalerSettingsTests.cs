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
        public void Labels_WithoutService_AllVendorOptionsMarkedUnsupported()
        {
            // 能力未知（服务未注册 / 原生不可用）时厂商项全部标注——
            // 不许让用户选一个必然失败的项还不提示。
            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1（本机不支持）", "FSR 4.1（本机不支持）", "DLSS 5（本机不支持）" },
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
                },
            };

            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1", "FSR 4.1（本机不支持）", "DLSS 5（本机不支持）" },
                UpscalerSettingOptions.BuildOptionLabels(stub));
        }

        [Test]
        public void Labels_FullCapability_NoAnnotations()
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

            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1", "FSR 4.1", "DLSS 5" },
                UpscalerSettingOptions.BuildOptionLabels(stub));
        }

        [Test]
        public void Labels_ServicePresentButNativeUnavailable_AllVendorOptionsMarked()
        {
            var stub = new StubUpscalerSettings
            {
                Available = false,
                Caps = default, // Available=false
            };

            CollectionAssert.AreEqual(
                new[] { "自动", "关", "FSR 3.1（本机不支持）", "FSR 4.1（本机不支持）", "DLSS 5（本机不支持）" },
                UpscalerSettingOptions.BuildOptionLabels(stub));
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
            // 无服务注册（EditMode 默认态）：厂商项全部标注，确定性可断言。
            SettingsRegistry registry = SettingsRegistry.BuildDefaults();

            Assert.IsTrue(registry.TryGetDefinition("video.upscaler", out SettingDefinition upscaler));
            Assert.AreEqual("超分", upscaler.DisplayName);
            Assert.AreEqual(SettingType.Enum, upscaler.Type);
            Assert.IsNotNull(upscaler.Apply, "超分必须真调 IUpscalerSettings.TrySetPreferred（B-R6-03 实接）");
            Assert.AreEqual(0, (int)upscaler.DefaultValue, "默认 = 自动");
            Assert.AreEqual(5, upscaler.EnumOptions.Length);
            StringAssert.EndsWith("（本机不支持）", upscaler.EnumOptions[3], "能力未知时 FSR 4.1 应标注");
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
