// ZrFrameGenerationSettings.cs —— 插帧设置服务：IFrameGenerationSettings 的原生实现
// （TASK-B-R9-06）。
//
// 三条落点：
//   1) 能力门控：Editor / 非 D3D12 / 代理缺失 / 非 URP 四档，全部折算成"不可用 + 一行
//      原因"，永不抛异常（设置页据此禁用该行并标注，见 FrameGenerationSettingOptions）；
//   2) 运行期开关：直连 dxgi 代理的 ZFG_FgSetEnabled（0/1）。关闭 = 纯透传——
//      不提交输入、不 Prepare、**不销毁 FFX 上下文**（销毁会连带毁掉 Unity 持有的
//      交换链对象，fg_ffx.cpp:760-765，不可逆；见 TASK-B-R9-06-recon §3.3）；
//   3) 每帧输入：真正提交 depth/MV 的是 ZrFrameGenerationRuntime（同目录的
//      ZrFrameGenerationFeature.cs），本类只负责"要不要开"。
//
// 为什么"开"要顺带装一条 URP 渲染通道：URP 14 没有资产级 motion vector 开关，
// MV 只由 pass 的 ScriptableRenderPassInput.Motion 驱动（TASK-B-R9-02 实证）。
// 通道是**运行期克隆**管线资产装上去的，磁盘上的 URP_*.asset 一个字节都不碰。

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>
    /// 能力判定的纯函数部分（不碰引擎/原生，EditMode 可直测）。
    /// 顺序即优先级：Editor → 非 D3D12 → 代理链 → 管线。
    /// </summary>
    public static class FrameGenerationCapabilityPolicy
    {
        /// <summary>按环境要素判定能力档位，并给出一行原因（可用时为空串）。</summary>
        public static FrameGenerationCapability Decide(
            bool isEditor,
            GraphicsDeviceType deviceType,
            string bindingFailure,
            bool linkReady,
            bool pipelineIsUrp,
            out string detail)
        {
            if (isEditor)
            {
                detail = "Editor 内 dxgi 代理不生效（DllImport 落到系统 dxgi.dll）";
                return FrameGenerationCapability.EditorOnly;
            }

            if (deviceType != GraphicsDeviceType.Direct3D12)
            {
                detail = "当前图形 API 为 " + deviceType + "，插帧需要 D3D12";
                return FrameGenerationCapability.NotD3D12;
            }

            if (!string.IsNullOrEmpty(bindingFailure))
            {
                detail = "dxgi 代理的 ZFG_* 入口不可用（" + bindingFailure + "）";
                return FrameGenerationCapability.ProxyUnavailable;
            }

            if (!linkReady)
            {
                detail = "代理链未建立（ZFG_FgGetStats 返回 0：ZFG_HOOK 未设 / hook 未装 / 交换链尚未 wrap）";
                return FrameGenerationCapability.ProxyUnavailable;
            }

            if (!pipelineIsUrp)
            {
                detail = "当前渲染管线不是 URP：拿不到运动矢量（URP 是 MV 的唯一来源）";
                return FrameGenerationCapability.PipelineUnsupported;
            }

            detail = string.Empty;
            return FrameGenerationCapability.Available;
        }
    }

    /// <summary>
    /// 低层 API 缝隙（单测注入桩用）：一个方法对应一个 dxgi 代理导出。
    /// 返回值语义 = 代理语义（true = 已受理/链已建），异常由真实现自己吞掉。
    /// </summary>
    public interface IZrFrameGenApi
    {
        /// <summary>对应 ZFG_FgSubmitInputs：true = 已受理。</summary>
        bool Submit(ref ZfgFgFrameInputsNative inputs);

        /// <summary>对应 ZFG_FgGetStats：true = 代理链已建立。</summary>
        bool TryGetStats(out ZfgFgStatsNative stats);

        /// <summary>对应 ZFG_FgSetEnabled：true = 已受理（原生无条件返回 1）。</summary>
        bool SetEnabled(bool enabled);

        /// <summary>绑定失败类型名（空 = 从未失败）。</summary>
        string BindingFailure { get; }
    }

    /// <summary>真实现：直连 <see cref="ZrFgProxyNative"/>（dxgi.dll 代理）。</summary>
    public sealed class ZrFrameGenRealApi : IZrFrameGenApi
    {
        /// <summary>进程内共享实例（无状态，失败标志在绑定层）。</summary>
        public static readonly ZrFrameGenRealApi Instance = new ZrFrameGenRealApi();

        public bool Submit(ref ZfgFgFrameInputsNative inputs)
        {
            return ZrFgProxyNative.TrySubmit(ref inputs);
        }

        public bool TryGetStats(out ZfgFgStatsNative stats)
        {
            return ZrFgProxyNative.TryGetStats(out stats);
        }

        public bool SetEnabled(bool enabled)
        {
            return ZrFgProxyNative.TrySetEnabled(enabled);
        }

        public string BindingFailure => ZrFgProxyNative.BindingFailure;
    }

    /// <summary>
    /// 插帧设置服务（注册为 <see cref="IFrameGenerationSettings"/>）。
    /// 所有异常路径（dll/符号缺失、代理链未建、管线不支持）都折算成"不可用"
    /// 返回值 + 一次 ZLog 警告；设置界面永不因原生问题抛异常或刷屏。
    /// </summary>
    public sealed class ZrFrameGenerationSettings : IFrameGenerationSettings
    {
        private readonly IZrFrameGenApi _api;
        private bool _warnedUnavailable;
        private bool _warnedEnableRefused;

        /// <summary>使用真实 P/Invoke API（生产路径）。</summary>
        public ZrFrameGenerationSettings()
            : this(null)
        {
        }

        /// <summary>注入低层 API（单测桩；null = 真实 P/Invoke）。</summary>
        public ZrFrameGenerationSettings(IZrFrameGenApi api)
        {
            _api = api ?? ZrFrameGenRealApi.Instance;
        }

        /// <inheritdoc />
        public FrameGenerationCapability Capability => QueryCapability();

        /// <summary>最近一次 <see cref="QueryCapability"/> 的原因文本（可用时为空串）。</summary>
        public string CapabilityDetail { get; private set; } = string.Empty;

        /// <inheritdoc />
        public bool IsAvailable => QueryCapability() == FrameGenerationCapability.Available;

        /// <inheritdoc />
        public FrameGenerationCapability QueryCapability()
        {
            bool linkReady = _api.TryGetStats(out ZfgFgStatsNative _);
            FrameGenerationCapability capability = FrameGenerationCapabilityPolicy.Decide(
                Application.isEditor,
                SystemInfo.graphicsDeviceType,
                _api.BindingFailure,
                linkReady,
                ZrFrameGenerationRuntime.PipelineIsUrp,
                out string detail);
            CapabilityDetail = detail;
            return capability;
        }

        /// <inheritdoc />
        public bool TryGetStats(out FrameGenerationStatsInfo stats)
        {
            if (!_api.TryGetStats(out ZfgFgStatsNative native))
            {
                stats = default;
                return false;
            }

            stats = new FrameGenerationStatsInfo
            {
                LinkReady = true,
                HaveInputs = native.HaveInputs != 0u,
                Enabled = native.Enabled != 0u,
                RealPresents = native.RealPresents,
                GeneratedPresents = native.GeneratedPresents,
                PrepareCount = native.PrepareCount,
                PrepareFailed = native.PrepareFailed,
                LastReturnCode = native.LastReturnCode,
            };
            return true;
        }

        /// <inheritdoc />
        public bool TrySetEnabled(bool enabled)
        {
            if (!enabled)
            {
                // 关是最安全的动作：即使代理不在，也要把"本模块不再提交输入"落地，
                // 保证纯透传。绝不销毁/重建 FFX 上下文（recon §3.3 方案 A）。
                // TASK-B-R9-07：SetSubmissionEnabled(false) 同时负责两件收尾 ——
                // 画质字段配对还原（MSAA/depth，无需重启）与 pass 停止入队（不再采 MV/depth）。
                ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
                _api.SetEnabled(false);
                return true;
            }

            FrameGenerationCapability capability = QueryCapability();
            if (capability != FrameGenerationCapability.Available)
            {
                WarnUnavailableOnce(CapabilityDetail);
                return false;
            }

            if (!_api.SetEnabled(true))
            {
                WarnEnableRefusedOnce("ZFG_FgSetEnabled(1) 未被受理（代理链不在）");
                return false;
            }

            if (!ZrFrameGenerationRuntime.TryInstallPipeline(out string error))
            {
                // 回滚到纯透传：授权位与提交都收回，绝不留下"半开"状态。
                _api.SetEnabled(false);
                ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
                WarnEnableRefusedOnce("插帧渲染通道安装失败（" + error + "）");
                return false;
            }

            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            return true;
        }

        /// <summary>
        /// 开机自举用：把授权位与提交都置于"关"，且不产生任何告警。
        /// 出厂默认必须是纯透传（原生 g_enabledOverride 的 -1 = 跟随模式，
        /// 在 ZFG_HOOK=fg 下等同"开"，所以产品侧必须显式压一次 0）。
        /// </summary>
        public void PrimeDisabled()
        {
            ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
            _api.SetEnabled(false);
        }

        private void WarnUnavailableOnce(string detail)
        {
            if (_warnedUnavailable)
            {
                return;
            }

            _warnedUnavailable = true;
            ZLog.Warn("Settings", "[插帧] 不可用：" + detail + " —— 开关仅存储不生效");
        }

        private void WarnEnableRefusedOnce(string detail)
        {
            if (_warnedEnableRefused)
            {
                return;
            }

            _warnedEnableRefused = true;
            ZLog.Warn("Settings", "[插帧] 开启失败：" + detail + " —— 已回退纯透传");
        }
    }

    /// <summary>
    /// 运行时自注册（幂等；EditMode 下不触发，测试自行注册桩）。
    /// 时机 BeforeSceneLoad 早于 <c>SettingsBootApplier</c> 的 AfterSceneLoad，
    /// 因此开机重放 <c>video.frameGeneration</c> 时服务一定已经就位。
    /// </summary>
    internal static class ZrFrameGenerationBootstrapper
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (Services.TryGet<IFrameGenerationSettings>(out _))
            {
                return;
            }

            var settings = new ZrFrameGenerationSettings();
            Services.Register<IFrameGenerationSettings>(settings);
            // 零破坏默认：不设该项的老存档也必须是纯透传。
            settings.PrimeDisabled();
        }
    }
}
