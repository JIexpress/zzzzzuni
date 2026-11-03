// ZrNative.cs —— zrender.dll C ABI 的纯 P/Invoke 声明（唯一 DllImport 集中地）。
// 见 Native/zplugin/zplugin.h：字符串返回值为静态缓冲区指针，托管侧拷贝后无需释放。

using System;
using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>zrender.dll 导出函数的 P/Invoke 声明（程序集内部使用）。</summary>
    internal static class ZrNative
    {
        [DllImport("zrender")]
        internal static extern uint ZR_ApiVersion();

        [DllImport("zrender")]
        internal static extern IntPtr ZR_GetVersion();

        [DllImport("zrender")]
        internal static extern IntPtr ZR_GetBuildInfo();

        // ---- TASK-B-R4-01：.zmesh 加载 C ABI（句柄式，见 zplugin.h） ----
        // 句柄 = (代际<<32)|槽位；0 恒无效。错误码与 zmesh.h 的 ZMESH_ERR_* 一致（负值）。

        [DllImport("zrender")]
        internal static extern int ZR_MeshLoadFromMemory(IntPtr data, uint sizeBytes, out ulong handle);

        [DllImport("zrender")]
        internal static extern int ZR_MeshStatsGet(ulong handle, out ZmeshStatsNative stats);

        [DllImport("zrender")]
        internal static extern int ZR_MeshFree(ulong handle);

        // ---- TASK-B-R6-03：超分后端设置 C ABI（见 zplugin.h 的 ZrUpscalerPreferred
        // 与 ZR_UPSCALER_* 错误码：0=OK，-1=NULL，-2=RANGE，-3=SIZE） ----

        [DllImport("zrender")]
        internal static extern int ZR_Upscaler_SetPreferred(int preferred);

        [DllImport("zrender")]
        internal static extern int ZR_Upscaler_GetActive();

        [DllImport("zrender")]
        internal static extern int ZR_Upscaler_QueryCaps(out ZrUpscalerCapsNative caps);

        // ---- TASK-B-R9-02：FG 输入（depth/MV）交接校验（见 zplugin.h 的 ZrFgInputDesc）----
        // 0=OK；-1=NULL、-2=ABI、-3=纹理句柄为空、-4=尺寸、-5=尺寸不一致、-6=格式。

        [DllImport("zrender")]
        internal static extern uint ZR_FrameGen_InputBytesPerPixel(uint format);

        [DllImport("zrender")]
        internal static extern int ZR_FrameGen_ValidateInputs(ref ZrFgInputDescNative desc, out ZrFgInputReportNative report);

        /// <summary>原生 ZmeshStats（40B，全定宽；布局须与 zmesh.h 的 ZmeshStats 一致）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ZmeshStatsNative
        {
            public uint VertexCount;
            public uint IndexCount;
            public uint HasNormals;
            public uint HasUv;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] BoundsMin;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public float[] BoundsMax;
        }
    }

    /// <summary>
    /// 超分后端设置 C ABI 的托管门面（TASK-B-R6-03；ZrNative 为程序集内部，
    /// 跨程序集消费一律经本类）。异常语义 = 裸 P/Invoke：dll 缺失抛
    /// DllNotFoundException、符号缺失抛 EntryPointNotFoundException，由上层
    /// （ZFrame.Render 的 ZrUpscalerSettings）捕获降级。
    /// </summary>
    public static class ZrUpscalerNative
    {
        /// <summary>设置首选后端（ZrUpscalerPreferred：0=Auto、1=Bilinear、3=FSR3、4=FSR4、5=DLSS）；0=OK，-2=越界。</summary>
        public static int SetPreferred(int preferred)
        {
            return ZrNative.ZR_Upscaler_SetPreferred(preferred);
        }

        /// <summary>取解析后的生效后端（1..5；从未设置时按 Auto 解析，Bilinear 兜底）。</summary>
        public static int GetActive()
        {
            return ZrNative.ZR_Upscaler_GetActive();
        }

        /// <summary>查能力快照（caps.StructSize 须先填 sizeof）；0=OK，-3=ABI 不符。</summary>
        public static int QueryCaps(out ZrUpscalerCapsNative caps)
        {
            return ZrNative.ZR_Upscaler_QueryCaps(out caps);
        }
    }

    /// <summary>
    /// 原生 ZrUpscalerCaps（32B，全定宽；布局须与 zplugin.h 一致）。
    /// 调用方须先把 <see cref="ZrUpscalerCapsNative.StructSize"/> 填为 sizeof（版本化约定），再调 QueryCaps。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrUpscalerCapsNative
    {
        public uint StructSize;
        public uint VendorId;
        public uint DeviceId;
        public uint FsrRuntimePresent;
        public uint Fsr4Available;
        public uint DlssAvailable;
        public uint PreferredBackend;
        public uint ActiveBackend;
    }

    /// <summary>
    /// FG 输入（depth/MV）交接校验 C ABI 的托管门面（TASK-B-R9-02）。只做
    /// 非空 / 尺寸 / 格式体检——不创建设备、不碰 FFX；FG 接线属后续任务。
    /// </summary>
    public static class ZrFrameGenNative
    {
        /// <summary>校验一次交接（desc.StructSize 须先填 sizeof）；0=OK，负值见 ZrFrameGenInputError。</summary>
        public static int ValidateInputs(ref ZrFgInputDescNative desc, out ZrFgInputReportNative report)
        {
            return ZrNative.ZR_FrameGen_ValidateInputs(ref desc, out report);
        }

        /// <summary>格式 → 每像素字节数；未知格式返回 0。</summary>
        public static uint InputBytesPerPixel(uint format)
        {
            return ZrNative.ZR_FrameGen_InputBytesPerPixel(format);
        }
    }

    /// <summary>
    /// 原生 ZrFgInputDesc（40B @x64；布局须与 zplugin.h 一致）。裸纹理句柄 +
    /// 尺寸 + <see cref="ZrFgTexFormatNative"/>；调用方须先填 StructSize。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrFgInputDescNative
    {
        public uint StructSize;
        public IntPtr Depth;
        public uint DepthWidth;
        public uint DepthHeight;
        public uint DepthFormat;
        public IntPtr MotionVectors;
        public uint MvWidth;
        public uint MvHeight;
        public uint MvFormat;
        public uint MaxDimension;
    }

    /// <summary>原生 ZrFgInputReport（32B，全定宽；布局须与 zplugin.h 一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrFgInputReportNative
    {
        public uint StructSize;
        public uint Accepted;
        public uint DepthOk;
        public uint MvOk;
        public uint Width;
        public uint Height;
        public uint DepthBytesPerPixel;
        public uint MvBytesPerPixel;
    }

    /// <summary>纹理格式白名单（与 zplugin.h 的 ZrFgTexFormat 同值）。</summary>
    public enum ZrFgTexFormatNative : uint
    {
        /// <summary>未知/不受支持。</summary>
        Unknown = 0,

        /// <summary>深度拷贝，单通道 32 位浮点（URP _CameraDepthTexture 实测形态）。</summary>
        R32SFloat = 1,

        /// <summary>真实深度附件（D32 口径）。</summary>
        D32SFloat = 2,

        /// <summary>运动矢量，RG 半精度浮点（URP _MotionVectorTexture 实测形态）。</summary>
        R16G16SFloat = 3
    }
}
