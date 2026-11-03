// ZrFrameGenerationSettingsTests.cs —— TASK-B-R9-06 的原生接线侧证据（EditMode）：
// 1) POD 布局：托管镜像必须与 zfg_fg_input.h 的 128/48 字节逐字段对齐
//    （布局漂移 = 原生侧 structSize/版本校验拒收，或更糟：静默读错字段）；
// 2) 能力门控矩阵：四档 + 一行原因，纯函数可直测（不受本机 GPU 影响）；
// 3) 服务失败语义：不可用时 TrySetEnabled(true) 返回 false、不抛；
//    "关"在任何情况下都返回 true（关是最安全的动作）；
// 4) 统计映射：原生的 haveInputs/enabled/generated 逐字段落到托管结构。
//
// 5) TASK-B-R9-08 N2：还原行为本身必须有牙 —— 通过可注入桩断言"关插帧后
//    msaaSampleCount / supportsCameraDepthTexture / requiresDepthOption 回到开之前的原值"。
//    删掉 RestoreQualityOverrides() 的调用，这两条必须变红（B-R9-07 验收遗留 3）。
//
// 真机（Player + D3D12 + dxgi 代理）的数字在 Docs/Logs/TASK-B-R9-06.md 阶段 3。

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;
using ZFrame.Render;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    public sealed class ZrFrameGenerationSettingsTests
    {
        /// <summary>可配置的 API 桩：记录调用，天然覆盖"代理在/不在"两种世界。</summary>
        private sealed class StubApi : IZrFrameGenApi
        {
            public bool SubmitResult = true;

            public bool StatsResult = true;

            public bool SetEnabledResult = true;

            public string Failure = string.Empty;

            public int SubmitCalls;

            public int EnableCalls;

            public bool? LastEnabled;

            public ZfgFgStatsNative Stats = new ZfgFgStatsNative();

            public bool Submit(ref ZfgFgFrameInputsNative inputs)
            {
                SubmitCalls++;
                return SubmitResult;
            }

            public bool TryGetStats(out ZfgFgStatsNative stats)
            {
                stats = Stats;
                return StatsResult;
            }

            public bool SetEnabled(bool enabled)
            {
                EnableCalls++;
                LastEnabled = enabled;
                return SetEnabledResult;
            }

            public string BindingFailure => Failure;
        }

        /// <summary>管线副本的桩：只存两个被覆盖的字段（真实现是 URP 资产）。</summary>
        private sealed class StubPipelineTarget : IZrFrameGenerationPipelineTarget
        {
            public int MsaaSampleCount { get; set; }

            public bool SupportsCameraDepthTexture { get; set; }
        }

        /// <summary>相机深度开关的桩：**枚举**，不是 bool（N1 的语义所在）。</summary>
        private sealed class StubCameraDepthTarget : IZrFrameGenerationCameraDepthTarget
        {
            public CameraOverrideOption RequiresDepthOption { get; set; } = CameraOverrideOption.UsePipelineSettings;
        }

        [TearDown]
        public void TearDown()
        {
            // 静态运行期状态在 EditMode 里跨用例存活：还回"无目标 + 关闭"，避免互相污染。
            ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
            ZrFrameGenerationRuntime.BindQualityTargetsForTests(null, null);
        }

        // ---- 1. POD 布局 -------------------------------------------------------

        [Test]
        public void FrameInputsLayout_MatchesTheNativeContract()
        {
            Assert.AreEqual(128, Marshal.SizeOf<ZfgFgFrameInputsNative>(),
                "ZfgFgFrameInputs 在 x64 上是 128 字节（zfg_fg_input.h）");
            Assert.AreEqual(8, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.Depth)));
            Assert.AreEqual(16, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.MotionVectors)));
            Assert.AreEqual(24, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.Width)));
            Assert.AreEqual(32, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.DepthState)));
            Assert.AreEqual(48, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.MvScaleX)));
            Assert.AreEqual(112, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.CamForwardY)));
            Assert.AreEqual(120, (int)Marshal.OffsetOf<ZfgFgFrameInputsNative>(nameof(ZfgFgFrameInputsNative.Reset)));
            Assert.AreEqual(127, Marshal.SizeOf<ZfgFgFrameInputsNative>() - 1, "最后一个字段是 reserved(i32)");
        }

        [Test]
        public void StatsLayout_MatchesTheNativeContract_IncludingHaveInputs()
        {
            Assert.AreEqual(48, Marshal.SizeOf<ZfgFgStatsNative>(),
                "ZfgFgStats 在 TASK-B-R9-06 之后是 48 字节（4×u64 + 4×u32）");
            Assert.AreEqual(32, (int)Marshal.OffsetOf<ZfgFgStatsNative>(nameof(ZfgFgStatsNative.Enabled)));
            Assert.AreEqual(36, (int)Marshal.OffsetOf<ZfgFgStatsNative>(nameof(ZfgFgStatsNative.LastReturnCode)));
            Assert.AreEqual(40, (int)Marshal.OffsetOf<ZfgFgStatsNative>(nameof(ZfgFgStatsNative.HaveInputs)),
                "haveInputs 是阶段 1 门禁的读点，必须紧跟 lastReturnCode");
            Assert.AreEqual(48, ZrFgProxyNative.StatsSize);
            Assert.AreEqual(128, ZrFgProxyNative.InputsSize);
        }

        // ---- 2. 能力门控矩阵 ---------------------------------------------------

        [Test]
        public void CapabilityPolicy_CoversTheWholeMatrix()
        {
            Assert.AreEqual(FrameGenerationCapability.EditorOnly, Decide(true, GraphicsDeviceType.Direct3D12, string.Empty, true, true));
            Assert.AreEqual(FrameGenerationCapability.NotD3D12, Decide(false, GraphicsDeviceType.Direct3D11, string.Empty, true, true));
            Assert.AreEqual(FrameGenerationCapability.ProxyUnavailable, Decide(false, GraphicsDeviceType.Direct3D12, "EntryPointNotFoundException", true, true));
            Assert.AreEqual(FrameGenerationCapability.ProxyUnavailable, Decide(false, GraphicsDeviceType.Direct3D12, string.Empty, false, true));
            Assert.AreEqual(FrameGenerationCapability.PipelineUnsupported, Decide(false, GraphicsDeviceType.Direct3D12, string.Empty, true, false));
            Assert.AreEqual(FrameGenerationCapability.Available, Decide(false, GraphicsDeviceType.Direct3D12, string.Empty, true, true));
        }

        [Test]
        public void CapabilityPolicy_AlwaysExplainsItself_WhenUnavailable()
        {
            foreach (FrameGenerationCapability capability in Enum.GetValues(typeof(FrameGenerationCapability)))
            {
                if (capability == FrameGenerationCapability.Available)
                {
                    continue;
                }

                string detail;
                switch (capability)
                {
                    case FrameGenerationCapability.EditorOnly:
                        FrameGenerationCapabilityPolicy.Decide(true, GraphicsDeviceType.Direct3D12, string.Empty, true, true, out detail);
                        break;
                    case FrameGenerationCapability.NotD3D12:
                        FrameGenerationCapabilityPolicy.Decide(false, GraphicsDeviceType.Direct3D11, string.Empty, true, true, out detail);
                        break;
                    case FrameGenerationCapability.ProxyUnavailable:
                        FrameGenerationCapabilityPolicy.Decide(false, GraphicsDeviceType.Direct3D12, string.Empty, false, true, out detail);
                        break;
                    default:
                        FrameGenerationCapabilityPolicy.Decide(false, GraphicsDeviceType.Direct3D12, string.Empty, true, false, out detail);
                        break;
                }

                Assert.IsNotEmpty(detail, "不可用必须给出可写进日志的原因：" + capability);
            }
        }

        private static FrameGenerationCapability Decide(
            bool isEditor, GraphicsDeviceType device, string failure, bool linkReady, bool urp)
        {
            return FrameGenerationCapabilityPolicy.Decide(isEditor, device, failure, linkReady, urp, out _);
        }

        // ---- 3. 服务失败语义 ---------------------------------------------------

        [Test]
        public void TrySetEnabled_True_IsRefused_WhenCapabilityIsMissing()
        {
            var api = new StubApi();
            var service = new ZrFrameGenerationSettings(api);

            // EditMode 里 Application.isEditor = true → 必然 EditorOnly。
            Assert.AreEqual(FrameGenerationCapability.EditorOnly, service.QueryCapability());
            Assert.IsFalse(service.IsAvailable);
            Assert.IsFalse(service.TrySetEnabled(true), "不可用时开启必须返回 false（不抛、不谎报）");
            Assert.AreEqual(0, api.EnableCalls, "不可用时不应把 1 下发给原生（避免半开状态）");
            Assert.IsNotEmpty(service.CapabilityDetail);
        }

        [Test]
        public void TrySetEnabled_False_IsAlwaysAccepted()
        {
            var api = new StubApi { SetEnabledResult = false, Failure = "DllNotFoundException" };
            var service = new ZrFrameGenerationSettings(api);

            Assert.IsTrue(service.TrySetEnabled(false), "关闭是最安全的动作：代理不在也当作已关闭");
            Assert.AreEqual(1, api.EnableCalls);
            Assert.AreEqual(false, api.LastEnabled);
            Assert.IsFalse(ZrFrameGenerationRuntime.SubmissionEnabled, "关闭必须同时停掉每帧提交（纯透传）");
        }

        // ---- 4. 统计映射 -------------------------------------------------------

        [Test]
        public void TryGetStats_MapsEveryField()
        {
            var api = new StubApi
            {
                Stats = new ZfgFgStatsNative
                {
                    RealPresents = 3600,
                    GeneratedPresents = 3402,
                    PrepareCount = 3500,
                    PrepareFailed = 0,
                    Enabled = 1,
                    LastReturnCode = 0,
                    HaveInputs = 1,
                    Reserved = 0,
                },
            };
            var service = new ZrFrameGenerationSettings(api);

            Assert.IsTrue(service.TryGetStats(out FrameGenerationStatsInfo stats));
            Assert.IsTrue(stats.LinkReady);
            Assert.IsTrue(stats.HaveInputs, "阶段 1 门禁：haveInputs 必须能从托管侧读到");
            Assert.IsTrue(stats.Enabled);
            Assert.AreEqual(3600UL, stats.RealPresents);
            Assert.AreEqual(3402UL, stats.GeneratedPresents);
            Assert.AreEqual(0UL, stats.PrepareFailed);
            Assert.AreEqual(0u, stats.LastReturnCode);
            Assert.AreEqual(0.945f, (float)stats.GeneratedPresents / stats.RealPresents, 0.01f);
        }

        [Test]
        public void TryGetStats_WhenLinkIsDown_ReturnsFalseWithoutThrowing()
        {
            var api = new StubApi { StatsResult = false };
            var service = new ZrFrameGenerationSettings(api);

            Assert.IsFalse(service.TryGetStats(out FrameGenerationStatsInfo stats));
            Assert.IsFalse(stats.LinkReady);
            Assert.AreEqual(0UL, stats.GeneratedPresents);
        }

        // ---- 5. 关闭时的收尾（TASK-B-R9-07 M2/M3 的守卫） -----------------------

        [Test]
        public void Disable_WithoutInstall_RestoresNothing_AndNeverThrows()
        {
            // 关是本任务新加的"还原画质 + 停采集"入口：没装过通道时它必须是无害空操作
            // （生产路径里 PrimeDisabled() 在开机就会走一次，EditMode 里也会被单测调到）。
            Assert.DoesNotThrow(() => ZrFrameGenerationRuntime.SetSubmissionEnabled(false));
            Assert.IsFalse(ZrFrameGenerationRuntime.SubmissionEnabled, "关必须落到'不再提交'");

            // 开机自举路径同样不能因为"还没装通道"而抛。
            var api = new StubApi();
            var service = new ZrFrameGenerationSettings(api);
            Assert.DoesNotThrow(() => service.PrimeDisabled());
            Assert.IsFalse(ZrFrameGenerationRuntime.SubmissionEnabled);
        }

        [Test]
        public void Disable_StopsSubmission_SoThePassStopsBeingEnqueued()
        {
            // M3 的开关侧前提：SubmissionEnabled 是 pass 入队的唯一判据
            // （ZrFrameGenerationFeature.AddRenderPasses）。这里钉死"关 → 该位为 false"，
            // 真机侧的 enq/submit 计数证据在 Docs/Logs/TASK-B-R9-07.md。
            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            Assert.IsTrue(ZrFrameGenerationRuntime.SubmissionEnabled);

            var service = new ZrFrameGenerationSettings(new StubApi());
            Assert.IsTrue(service.TrySetEnabled(false));
            Assert.IsFalse(ZrFrameGenerationRuntime.SubmissionEnabled, "关必须让 pass 停止入队（不再取 depth/MV）");
        }

        // ---- 6. 还原行为本身（TASK-B-R9-08 N2） --------------------------------
        // 为什么这两条必须存在：B-R9-07 验收实测"把 RestoreQualityOverrides() 整段删掉，
        // 上面两条用例照样全绿"——它们只断言标志位，不断言字段真的回去了。这里通过
        // 生产入口（SetSubmissionEnabled）对可注入的桩断言**字段值**，删掉还原调用必红。

        [Test]
        public void Disable_RestoresMsaaSampleCountAndCameraDepthOption_ToTheValuesCapturedBeforeEnable()
        {
            var pipeline = new StubPipelineTarget { MsaaSampleCount = 4, SupportsCameraDepthTexture = false };
            var camera = new StubCameraDepthTarget { RequiresDepthOption = CameraOverrideOption.UsePipelineSettings };
            ZrFrameGenerationRuntime.BindQualityTargetsForTests(pipeline, camera);

            // 绑定时会顺带覆盖一次；这里把桩复位成"开之前"，让覆盖动作必须由生产入口触发。
            pipeline.MsaaSampleCount = 4;
            pipeline.SupportsCameraDepthTexture = false;
            camera.RequiresDepthOption = CameraOverrideOption.UsePipelineSettings;

            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            Assert.AreEqual(1, pipeline.MsaaSampleCount, "开插帧必须把 MSAA 压到 1×（FFX 的 depth 必须单采样）");
            Assert.IsTrue(pipeline.SupportsCameraDepthTexture, "开插帧必须让管线产出 _CameraDepthTexture");
            Assert.AreEqual(CameraOverrideOption.On, camera.RequiresDepthOption, "开插帧必须把相机深度开关置 On");

            ZrFrameGenerationRuntime.SetSubmissionEnabled(false);

            Assert.AreEqual(4, pipeline.MsaaSampleCount, "关插帧必须把 MSAA 还原成开之前的原值（不是下次启动才生效）");
            Assert.IsFalse(pipeline.SupportsCameraDepthTexture, "关插帧必须还原 supportsCameraDepthTexture");
            Assert.AreEqual(CameraOverrideOption.UsePipelineSettings, camera.RequiresDepthOption,
                "关插帧必须还原 requiresDepthOption 的枚举原值；存 bool 投影会把它压成 On，关态白渲一遍 depth");
        }

        [Test]
        public void Restore_KeepsTheDepthOptionEnum_ThatTheBoolProjectionWouldLose()
        {
            // 三档逐条验证。旧实现存的是 requiresDepthTexture（bool 投影）：
            //   Off → false → 写回 false → Off（碰巧对）
            //   On → true → 写回 true → On（碰巧对）
            //   UsePipelineSettings → 资产 supportsDepth 已被置 true → true → 写回 On（错，用户设置被永久改掉）
            // 所以只测 Off/On 会漏，必须把 UsePipelineSettings 也钉进来。
            CameraOverrideOption[] originals =
            {
                CameraOverrideOption.Off,
                CameraOverrideOption.On,
                CameraOverrideOption.UsePipelineSettings,
            };

            foreach (CameraOverrideOption original in originals)
            {
                var pipeline = new StubPipelineTarget { MsaaSampleCount = 2, SupportsCameraDepthTexture = false };
                var camera = new StubCameraDepthTarget { RequiresDepthOption = original };
                ZrFrameGenerationRuntime.BindQualityTargetsForTests(pipeline, camera);
                camera.RequiresDepthOption = original;

                ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
                Assert.AreEqual(CameraOverrideOption.On, camera.RequiresDepthOption,
                    "开插帧期间相机必须强制要深度：" + original);

                ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
                Assert.AreEqual(original, camera.RequiresDepthOption,
                    "关插帧后必须回到枚举原值（bool 投影会丢掉 UsePipelineSettings）：" + original);
            }
        }
    }
}
