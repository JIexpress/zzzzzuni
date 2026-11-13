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

        // ---- TASK-B-UNITY-02：共享渲染目标（ZRender 画自己的纹理，Unity 采样贴屏）----
        // 错误码 0=OK；-1=NULL、-2=未接入设备、-3=设备/资源创建失败、-5=状态错误、-6=尺寸非法。
        // 与 B-UNITY-01 那组的分工：那组要宿主交出 backbuffer（有状态/跨队列硬阻断），
        // 这组由 ZRender 自己建纹理、自己等完成，Unity 只负责采样。

        [DllImport("zrender")]
        internal static extern int ZR_Unity_AttachDeviceFromTexture(IntPtr d3d12Texture);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_CreateSharedTarget(uint width, uint height);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetSharedTargetHandle(out ulong outHandle);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetSharedTargetResource(out IntPtr outResource);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_RenderFrame();

        [DllImport("zrender")]
        internal static extern void ZR_Unity_ReleaseSharedTarget();

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetTargetStats(out ZrUnityTargetStatsNative stats);

        // ---- TASK-B-UNITY-04：帧同步（fence 双缓冲）+ 双槽目标 ----
        // 语义变化：目标颜色/深度是双槽（0/1 轮流），RenderFrame 不再排空队列，
        // 宿主每帧采样 GetReadableTargetIndex() 指出的那一槽（= 上一帧那份，已画完）。

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetTargetResourceAt(uint index, out IntPtr outResource);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetReadableTargetIndex(out uint outIndex);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetSyncStats(out ZrUnitySyncStatsNative stats);

        // ---- TASK-B-UNITY-03：场景提交（相机 + 网格 + 绘制）----
        // 矩阵一律**行主序存储 + 列向量**（平移量在下标 3/7/11），托管侧须按元素
        // 转置后传入（见 ZRenderSceneBridge.ToNativeRowMajor）。错误码 0=OK；
        // -1=NULL、-2=未接入设备、-3=资源创建失败、-5=绘制项溢出、-6=参数非法、
        // -7=句柄无效（ZR_UNITY_ERR_HANDLE）。

        [DllImport("zrender")]
        internal static extern int ZR_Unity_SetCamera([In] float[] view16, [In] float[] proj16);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_CreateMesh([In] float[] vertices, uint vertexCount,
            uint strideFloats, [In] uint[] indices, uint indexCount, out ulong outHandle);

        [DllImport("zrender")]
        internal static extern void ZR_Unity_DestroyMesh(ulong handle);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_DrawMesh(ulong handle, [In] float[] world16,
            [In] float[] rgba);

        [DllImport("zrender")]
        internal static extern int ZR_Unity_BeginScene();

        [DllImport("zrender")]
        internal static extern int ZR_Unity_EndScene();

        [DllImport("zrender")]
        internal static extern int ZR_Unity_GetSceneStats(out ZrUnitySceneStatsNative stats);

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
    /// 原生 ZrUnityTargetStats（48B @x64；布局须与 zplugin.h 一致）。
    /// sharedHandle 放在末尾以避免结构体内 padding；CenterRGBA 是**纹理回读**结果，
    /// 不是屏幕抓取——它证明交出去的那张纹理里到底有什么颜色。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrUnityTargetStatsNative
    {
        public uint StructSize;
        public uint RenderFrameCount;
        public uint Width;
        public uint Height;
        public uint ReadbackCount;
        public uint LastError;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] CenterRGBA;
        public ulong SharedHandle;
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
    /// 原生 ZrUpscalerCaps（36B @ABI v2，全定宽；布局须与 zplugin.h 一致）。
    /// 调用方须先把 <see cref="ZrUpscalerCapsNative.StructSize"/> 填为 sizeof（版本化约定），再调 QueryCaps。
    /// ABI 历史：v1 = 前 8 字段（32B）；v2（B-FSR-02）= 末尾追加 Fsr4ProbeMode。
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
        /// <summary>
        /// Fsr4Available 的探测口径三态（B-FSR-02）：0=真实设备探测（可信）；
        /// 1=保守回退（建备失败走 device=null 枚举，结果可能失真——消费方按
        /// "未检测"处理）；2=未探测（provider 枚举未执行，典型：FFX 运行时缺席，
        /// 此时 Fsr4Available 恒 0）。未知值一律按"未检测"处理，不得当作 0。
        /// </summary>
        public uint Fsr4ProbeMode;
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

    /// <summary>
    /// 纹理格式白名单（与 zplugin.h 的 ZrFgTexFormat 同值）。
    /// </summary>
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

    /// <summary>
    /// 共享渲染目标的托管门面（TASK-B-UNITY-02）：ZRender 渲染到自己的共享纹理，
    /// 托管侧只做「开句柄 → CreateExternalTexture → 采样贴屏」。
    /// 与 B-UNITY-01 的 <c>ZR_Unity_BeginFrame/EndFrame</c> 不同，这条路径不需要
    /// backbuffer 的状态控制，也不需要跨队列 fence——原生内部自己转态、自己等完成。
    /// </summary>
    public static class ZrUnityTargetNative
    {
        /// <summary>0 = 成功。</summary>
        public const int Ok = 0;

        /// <summary>空指针参数。</summary>
        public const int ErrNull = -1;

        /// <summary>尚未接入宿主设备。</summary>
        public const int ErrNoDevice = -2;

        /// <summary>设备包装 / D3D12 资源创建失败。</summary>
        public const int ErrDevice = -3;

        /// <summary>状态错误（未建目标就渲染 / 未释放就换尺寸）。</summary>
        public const int ErrState = -5;

        /// <summary>尺寸非法（0 或超过 16384）。</summary>
        public const int ErrArgs = -6;

        /// <summary>由宿主纹理反查设备并包装（幂等）。返回 0 或错误码。</summary>
        public static int AttachDeviceFromTexture(IntPtr d3d12Texture)
        {
            return ZrNative.ZR_Unity_AttachDeviceFromTexture(d3d12Texture);
        }

        /// <summary>创建共享颜色目标（BGRA8）；同尺寸重复调用幂等返回 0。</summary>
        public static int CreateSharedTarget(uint width, uint height)
        {
            return ZrNative.ZR_Unity_CreateSharedTarget(width, height);
        }

        /// <summary>取 NT 共享句柄（跨进程/跨 API 用）；失败写 0。</summary>
        public static int GetSharedTargetHandle(out ulong handle)
        {
            return ZrNative.ZR_Unity_GetSharedTargetHandle(out handle);
        }

        /// <summary>
        /// 取目标纹理的 ID3D12Resource*：Unity 2022.3/D3D12 的 CreateExternalTexture
        /// 需要的是资源指针（传 NT 句柄实测首帧崩在 UnityPlayer，0xc0000005）。
        /// </summary>
        public static int GetSharedTargetResource(out IntPtr resource)
        {
            return ZrNative.ZR_Unity_GetSharedTargetResource(out resource);
        }

        /// <summary>
        /// 渲染一帧（TASK-B-UNITY-04：提交后只 Signal fence，不再排空队列）。
        /// 返回后可安全采样的是 <see cref="GetReadableTargetIndex"/> 指出的槽位。
        /// </summary>
        public static int RenderFrame()
        {
            return ZrNative.ZR_Unity_RenderFrame();
        }

        /// <summary>释放目标与渲染链（设备包装保留，便于尺寸变化后重建）。</summary>
        public static void ReleaseSharedTarget()
        {
            ZrNative.ZR_Unity_ReleaseSharedTarget();
        }

        /// <summary>取统计（真调用计数 + 中心像素回读）。</summary>
        public static int GetTargetStats(out ZrUnityTargetStatsNative stats)
        {
            return ZrNative.ZR_Unity_GetTargetStats(out stats);
        }

        /// <summary>取第 index 个（0/1）颜色目标的资源指针（每个槽位一张外部纹理）。</summary>
        public static int GetTargetResourceAt(uint index, out IntPtr resource)
        {
            return ZrNative.ZR_Unity_GetTargetResourceAt(index, out resource);
        }

        /// <summary>取可安全采样的槽位号（= 上一帧渲染的那一份）。</summary>
        public static int GetReadableTargetIndex(out uint index)
        {
            return ZrNative.ZR_Unity_GetReadableTargetIndex(out index);
        }

        /// <summary>取帧同步统计（等待耗时 / 在途帧数）。</summary>
        public static int GetSyncStats(out ZrUnitySyncStatsNative stats)
        {
            return ZrNative.ZR_Unity_GetSyncStats(out stats);
        }
    }

    /// <summary>
    /// 原生 ZrUnitySyncStats（32B @x64；布局须与 zplugin.h 一致）。
    /// avgWaitMs 是"CPU 等 GPU"的直接数字：WaitIdle 版每帧等整帧 GPU 时间，
    /// fence 双缓冲后等的是上一帧（几乎总是已完成）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrUnitySyncStatsNative
    {
        public uint StructSize;
        public uint Frames;
        public uint BlockedFrames;
        public uint FramesInFlight;
        public double LastWaitMs;
        public double AvgWaitMs;
    }

    /// <summary>
    /// 场景提交的托管门面（TASK-B-UNITY-03）：把 Unity 的相机与物件交给 ZRender。
    /// 每帧顺序：<c>SetCamera → BeginScene → DrawMesh×N → RenderFrame → EndScene</c>。
    /// 矩阵是行主序存储 + 列向量约定（平移量在下标 3/7/11），必须按元素转置后传入，
    /// 直接 memcpy 会得到转置画面。
    /// </summary>
    public static class ZrUnitySceneNative
    {
        /// <summary>0 = 成功。</summary>
        public const int Ok = 0;

        /// <summary>空指针参数。</summary>
        public const int ErrNull = -1;

        /// <summary>尚未接入宿主设备。</summary>
        public const int ErrNoDevice = -2;

        /// <summary>设备 / 缓冲创建失败。</summary>
        public const int ErrDevice = -3;

        /// <summary>绘制项溢出（超过原生 1024 上限）。</summary>
        public const int ErrState = -5;

        /// <summary>参数非法（顶点布局/数量）。</summary>
        public const int ErrArgs = -6;

        /// <summary>mesh 句柄无效（过期或从未创建）。</summary>
        public const int ErrHandle = -7;

        /// <summary>设置本帧相机（view/proj 各 16 float，行主序）。</summary>
        public static int SetCamera(float[] view16, float[] proj16)
        {
            return ZrNative.ZR_Unity_SetCamera(view16, proj16);
        }

        /// <summary>上传网格（8 float/顶点 = pos+normal+uv，32B）；每个 mesh 只该传一次。</summary>
        public static int CreateMesh(float[] vertices, uint vertexCount, uint strideFloats,
            uint[] indices, uint indexCount, out ulong handle)
        {
            return ZrNative.ZR_Unity_CreateMesh(vertices, vertexCount, strideFloats, indices,
                indexCount, out handle);
        }

        /// <summary>释放网格句柄（幂等；无效句柄静默忽略）。</summary>
        public static void DestroyMesh(ulong handle)
        {
            ZrNative.ZR_Unity_DestroyMesh(handle);
        }

        /// <summary>提交一次绘制（world16 行主序；rgba 为空 = 白色）。</summary>
        public static int DrawMesh(ulong handle, float[] world16, float[] rgba)
        {
            return ZrNative.ZR_Unity_DrawMesh(handle, world16, rgba);
        }

        /// <summary>开始新一帧（清空绘制表）。</summary>
        public static int BeginScene()
        {
            return ZrNative.ZR_Unity_BeginScene();
        }

        /// <summary>结束本帧（累计统计）。</summary>
        public static int EndScene()
        {
            return ZrNative.ZR_Unity_EndScene();
        }

        /// <summary>取场景统计（drawCount / meshCount / uploadCount 等）。</summary>
        public static int GetSceneStats(out ZrUnitySceneStatsNative stats)
        {
            return ZrNative.ZR_Unity_GetSceneStats(out stats);
        }
    }

    /// <summary>
    /// 原生 ZrUnitySceneStats（40B @x64；布局须与 zplugin.h 一致）。
    /// uploadCount 是"mesh 只上传一次"的硬证据：它应恒等于 mesh 种类数，
    /// 而不是随帧数增长。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ZrUnitySceneStatsNative
    {
        public uint StructSize;
        public uint DrawCount;
        public uint MeshCount;
        public uint Reserved;
        public ulong UploadCount;
        public ulong SceneFrameCount;
        public ulong DrawTotal;
    }
}
