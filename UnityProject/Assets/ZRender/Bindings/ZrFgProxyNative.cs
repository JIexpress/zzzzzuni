// ZrFgProxyNative.cs —— dxgi 代理（`dxgi.dll`）私有 ZFG_* 导出的 P/Invoke 声明
// 与 POD 结构镜像（TASK-B-R9-06）。
//
// 为什么是 `dxgi.dll` 而不是 `zfg_hook.dll`：Unity 只按 exe 同目录的 dxgi.dll 走代理
// （ZFG_HOOK 由代理读），而代理把三个入口按名转发给 zfg_hook。因此托管侧只有一条
// 绑定路径，且"代理没装"与"hook 没装"在托管侧是同一个失败面。
//
// 失败语义（照 ZrPlugin/ ZrUpscalerSettings 的既有降级纪律）：
//   - dll 不在           → DllNotFoundException
//   - 符号不在（Editor 里绑到 System32\dxgi.dll） → EntryPointNotFoundException
//   - 位数不符           → BadImageFormatException
// 三类都在本层捕获成 false + 记录失败类型，**一次之后不再重试**（不刷屏、不抛异常）。
// 调用方（ZFrame.Render 的 ZrFrameGenerationSettings）负责 ZLog 告警与能力门控。

using System;
using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 与 <c>Native/zfgprobe/zfg_fg_input.h</c> 的 <c>ZfgFgFrameInputs</c> 逐字段对应
    /// （x64 = 128 字节：2×u32 + 2×void* + 8×u32/float 段 + 4×vec3 + 2×i32）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZfgFgFrameInputsNative
    {
        /// <summary>= sizeof(ZfgFgFrameInputs)（原生侧强校验）。</summary>
        public uint StructSize;

        /// <summary>= <see cref="ZrFgProxyNative.InputsVersion"/>。</summary>
        public uint Version;

        /// <summary>ID3D12Resource*（深度，Unity 侧 Texture.GetNativeTexturePtr()）。</summary>
        public IntPtr Depth;

        /// <summary>ID3D12Resource*（运动矢量）。</summary>
        public IntPtr MotionVectors;

        /// <summary>渲染分辨率宽（= 交换链宽）。</summary>
        public uint Width;

        /// <summary>渲染分辨率高。</summary>
        public uint Height;

        /// <summary>FFX 资源状态位；0 = 原生取默认（PIXEL|COMPUTE 可读）。</summary>
        public uint DepthState;

        /// <summary>同上（MV）。</summary>
        public uint MvState;

        /// <summary>亚像素抖动；本集成恒 0（URP MV 用非 jitter 矩阵）。</summary>
        public float JitterX;

        /// <summary>同上。</summary>
        public float JitterY;

        /// <summary>MV 像素换算 X（本集成 = width）。</summary>
        public float MvScaleX;

        /// <summary>MV 像素换算 Y（本集成 = -height，y 向下）。</summary>
        public float MvScaleY;

        /// <summary>上一帧耗时（毫秒）。</summary>
        public float FrameTimeMs;

        /// <summary>垂直 FOV（弧度）。</summary>
        public float FovY;

        /// <summary>近裁剪面。</summary>
        public float NearZ;

        /// <summary>远裁剪面。</summary>
        public float FarZ;

        /// <summary>相机世界位置。</summary>
        public float CamPosX;

        /// <summary>同上。</summary>
        public float CamPosY;

        /// <summary>同上。</summary>
        public float CamPosZ;

        /// <summary>相机 up。</summary>
        public float CamUpX;

        /// <summary>同上。</summary>
        public float CamUpY;

        /// <summary>同上。</summary>
        public float CamUpZ;

        /// <summary>相机 right。</summary>
        public float CamRightX;

        /// <summary>同上。</summary>
        public float CamRightY;

        /// <summary>同上。</summary>
        public float CamRightZ;

        /// <summary>相机 forward。</summary>
        public float CamForwardX;

        /// <summary>同上。</summary>
        public float CamForwardY;

        /// <summary>同上。</summary>
        public float CamForwardZ;

        /// <summary>1 = 丢历史（首帧/尺寸变化/输入断档）。</summary>
        public int Reset;

        /// <summary>保留（0）。</summary>
        public int Reserved;
    }

    /// <summary>
    /// 与 <c>ZfgFgStats</c> 逐字段对应（48 字节：4×u64 + 4×u32）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZfgFgStatsNative
    {
        /// <summary>真帧计数（宿主自己呈现的次数）。</summary>
        public ulong RealPresents;

        /// <summary>插帧计数（FFX 合成出来的帧）。</summary>
        public ulong GeneratedPresents;

        /// <summary>成功派发 PrepareV2 的次数。</summary>
        public ulong PrepareCount;

        /// <summary>Configure/PrepareV2 失败次数。</summary>
        public ulong PrepareFailed;

        /// <summary>上一帧是否真的在插帧。</summary>
        public uint Enabled;

        /// <summary>最后一次 ffx 返回码（0 = OK）。</summary>
        public uint LastReturnCode;

        /// <summary>1 = 至少收到过一次合法 depth/MV 提交。</summary>
        public uint HaveInputs;

        /// <summary>保留（0）。</summary>
        public uint Reserved;
    }

    /// <summary>
    /// dxgi 代理三个 FG 控制入口的托管门面（异常语义已折算，永不抛）。
    /// </summary>
    public static class ZrFgProxyNative
    {
        /// <summary>ZfgFgFrameInputs.version 的当前值（与 zfg_fg_input.h 同步）。</summary>
        public const uint InputsVersion = 1u;

        private const string Dll = "dxgi.dll";

        [DllImport(Dll, EntryPoint = "ZFG_FgSubmitInputs", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SubmitInputsNative(ref ZfgFgFrameInputsNative inputs);

        [DllImport(Dll, EntryPoint = "ZFG_FgGetStats", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetStatsNative(out ZfgFgStatsNative stats);

        [DllImport(Dll, EntryPoint = "ZFG_FgSetEnabled", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetEnabledNative(int enabled);

        /// <summary>一次失败后永久降级（同进程内不再尝试 P/Invoke）。</summary>
        private static bool s_bindingFailed;

        private static string s_bindingFailure = string.Empty;

        /// <summary>ZfgFgFrameInputs 的托管字节数（调用方填 StructSize 必须用这个）。</summary>
        public static int InputsSize => Marshal.SizeOf<ZfgFgFrameInputsNative>();

        /// <summary>ZfgFgStats 的托管字节数（与原生 48 字节对照用）。</summary>
        public static int StatsSize => Marshal.SizeOf<ZfgFgStatsNative>();

        /// <summary>绑定是否已经不可用（dll/符号缺失过一次）。</summary>
        public static bool BindingFailed => s_bindingFailed;

        /// <summary>失败类型名（空 = 从未失败）；用于能力门控的"原因"。</summary>
        public static string BindingFailure => s_bindingFailure;

        /// <summary>提交一帧输入；true = 代理已受理（不等于本帧被插帧）。</summary>
        public static bool TrySubmit(ref ZfgFgFrameInputsNative inputs)
        {
            if (s_bindingFailed)
            {
                return false;
            }

            try
            {
                return SubmitInputsNative(ref inputs) == 1;
            }
            catch (Exception e) when (IsBindingError(e))
            {
                MarkFailed(e);
                return false;
            }
        }

        /// <summary>读统计；true = 代理链已建立（= 交换链被 wrap 过，g_fg != nullptr）。</summary>
        public static bool TryGetStats(out ZfgFgStatsNative stats)
        {
            if (s_bindingFailed)
            {
                stats = default;
                return false;
            }

            try
            {
                return GetStatsNative(out stats) == 1;
            }
            catch (Exception e) when (IsBindingError(e))
            {
                MarkFailed(e);
                stats = default;
                return false;
            }
        }

        /// <summary>运行期开关；true = 代理受理（原生无条件返回 1，真生效要看 stats.Enabled）。</summary>
        public static bool TrySetEnabled(bool enabled)
        {
            if (s_bindingFailed)
            {
                return false;
            }

            try
            {
                return SetEnabledNative(enabled ? 1 : 0) == 1;
            }
            catch (Exception e) when (IsBindingError(e))
            {
                MarkFailed(e);
                return false;
            }
        }

        private static bool IsBindingError(Exception e)
        {
            return e is DllNotFoundException || e is EntryPointNotFoundException
                || e is BadImageFormatException;
        }

        private static void MarkFailed(Exception e)
        {
            s_bindingFailed = true;
            s_bindingFailure = e.GetType().Name;
        }
    }
}
