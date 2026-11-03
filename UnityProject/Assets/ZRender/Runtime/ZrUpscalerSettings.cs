// ZrUpscalerSettings.cs —— 超分设置服务：IUpscalerSettings 的原生实现（TASK-B-R6-03）。
// 经 ZFrame.Render.Bindings 的 C ABI 三导出对接 zrender.dll；dll 缺失 / 符号缺失 /
// ABI 不符时优雅降级（返回不可用 + ZLog 警告一次），绝不抛异常炸设置 UI。
// 首选后端编号见 Native/zplugin/zplugin.h：0=Auto、1=Bilinear、3=FSR3、4=FSR4、5=DLSS；
// Auto 语义由原生 UpscalerChain 按能力矩阵选优，本层只透传，不做二次决策。
// UI 侧的 Enum 索引↔preferred 映射与能力门控标注在 ZFrame.View 的 UpscalerSettingOptions。

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>
    /// 低层 API 缝隙（供单测注入"原生不可用"桩）：一个方法对应一个 C ABI 导出，
    /// 返回值/错误码语义与 zplugin.h 一致。真实现即裸调 P/Invoke —— dll/符号缺失时
    /// 由运行时抛 DllNotFoundException / EntryPointNotFoundException，由上层
    /// <see cref="ZrUpscalerSettings"/> 捕获降级。
    /// </summary>
    public interface IZrUpscalerApi
    {
        /// <summary>对应 ZR_Upscaler_SetPreferred：0=OK，-2=preferred 越界。</summary>
        int SetPreferred(int preferred);

        /// <summary>对应 ZR_Upscaler_GetActive：返回生效后端 1..5（按 Auto 解析时含兜底）。</summary>
        int GetActive();

        /// <summary>对应 ZR_Upscaler_QueryCaps：0=OK，-3=structSize 与当前 ABI 不符。</summary>
        int QueryCaps(out ZrUpscalerCapsNative caps);
    }

    /// <summary>真实现：直连 ZrNative 的 P/Invoke 声明（zrender.dll 经 Assets/Plugins/ZRender 入库，ADR-004）。</summary>
    public sealed class ZrUpscalerRealApi : IZrUpscalerApi
    {
        /// <summary>进程内共享实例（无状态）。</summary>
        public static readonly ZrUpscalerRealApi Instance = new ZrUpscalerRealApi();

        public int SetPreferred(int preferred)
        {
            return ZrUpscalerNative.SetPreferred(preferred);
        }

        public int GetActive()
        {
            return ZrUpscalerNative.GetActive();
        }

        public int QueryCaps(out ZrUpscalerCapsNative caps)
        {
            return ZrUpscalerNative.QueryCaps(out caps);
        }
    }

    /// <summary>
    /// 超分设置服务（注册为 <see cref="IUpscalerSettings"/>）：对 C ABI 三导出做
    /// 优雅降级封装。所有异常路径（DllNotFound / EntryPointNotFound / 原生错误码）
    /// 都折算成"不可用"返回值 + ZLog 警告（每实例每类失败只告警一次，防止下拉
    /// 反复选择刷屏），设置界面永不因原生问题抛异常。
    /// </summary>
    public sealed class ZrUpscalerSettings : IUpscalerSettings
    {
        // 原生错误码（zplugin.h ZR_UPSCALER_*）。
        private const int ZrOk = 0;

        private readonly IZrUpscalerApi _api;
        private bool _warnedUnavailable;

        /// <summary>使用真实 P/Invoke API（生产路径）。</summary>
        public ZrUpscalerSettings()
            : this(null)
        {
        }

        /// <summary>注入低层 API（单测桩；null = 真实 P/Invoke）。</summary>
        public ZrUpscalerSettings(IZrUpscalerApi api)
        {
            _api = api ?? ZrUpscalerRealApi.Instance;
        }

        /// <inheritdoc />
        public bool IsAvailable => QueryCaps().Available;

        /// <inheritdoc />
        public UpscalerCapsInfo QueryCaps()
        {
            try
            {
                var native = new ZrUpscalerCapsNative
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(ZrUpscalerCapsNative)),
                };

                int code = _api.QueryCaps(out native);
                if (code != ZrOk)
                {
                    WarnOnce($"能力查询失败（ZR_Upscaler_QueryCaps 返回 {code}；-3 表示 structSize 与当前 ABI 不符，需重新构建 zrender.dll）");
                    return default;
                }

                return new UpscalerCapsInfo
                {
                    Available = true,
                    VendorId = native.VendorId,
                    DeviceId = native.DeviceId,
                    FsrRuntimePresent = native.FsrRuntimePresent != 0u,
                    Fsr4Available = native.Fsr4Available != 0u,
                    DlssAvailable = native.DlssAvailable != 0u,
                    PreferredBackend = unchecked((int)native.PreferredBackend),
                    ActiveBackend = unchecked((int)native.ActiveBackend),
                };
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                WarnOnce($"原生库不可用（{e.GetType().Name}）—— 超分设置降级：选择仅存储不生效");
                return default;
            }
        }

        /// <inheritdoc />
        public bool TrySetPreferred(int preferred)
        {
            try
            {
                int code = _api.SetPreferred(preferred);
                if (code != ZrOk)
                {
                    WarnOnce($"设置偏好失败（ZR_Upscaler_SetPreferred({preferred}) 返回 {code}；-2 表示越界）");
                    return false;
                }

                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                WarnOnce($"原生库不可用（{e.GetType().Name}）—— 偏好 {preferred} 未下发");
                return false;
            }
        }

        /// <inheritdoc />
        public bool TryGetActiveBackend(out int activeBackend)
        {
            try
            {
                int active = _api.GetActive();
                if (active >= 1 && active <= 5)
                {
                    activeBackend = active;
                    return true;
                }

                WarnOnce($"生效后端查询异常（ZR_Upscaler_GetActive 返回 {active}，应在 1..5）");
                activeBackend = 0;
                return false;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                WarnOnce($"原生库不可用（{e.GetType().Name}）—— 生效后端未知");
                activeBackend = 0;
                return false;
            }
        }

        /// <summary>不可用告警只发一次（同一实例），避免设置界面反复操作时刷屏。</summary>
        private void WarnOnce(string detail)
        {
            if (_warnedUnavailable)
            {
                return;
            }

            _warnedUnavailable = true;
            ZLog.Warn("Settings", $"[超分] {detail}");
        }
    }

    /// <summary>
    /// 运行时自注册：进入 Play Mode 时把 <see cref="ZrUpscalerSettings"/> 挂到
    /// Services（幂等；EditMode 下不触发，测试可自行注册桩）。注册本身不触碰
    /// 原生库 —— dll 是否可用由服务在首次查询时惰性判定并降级。
    /// </summary>
    internal static class ZrUpscalerBootstrapper
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (!Services.TryGet<IUpscalerSettings>(out _))
            {
                Services.Register<IUpscalerSettings>(new ZrUpscalerSettings());
            }
        }
    }
}
