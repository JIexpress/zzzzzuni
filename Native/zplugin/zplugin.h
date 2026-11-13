// =============================================================================
// zplugin.h —— zrender.dll 对外 C ABI（唯一跨 DLL/托管边界，P/Invoke 入口）
//
// 约定（Docs/Standards/file-conventions.md §3）：
//   - 跨 DLL 边界只用 C ABI（extern "C" + POD），异常禁止跨越边界
//   - 托管侧声明集中在 ZFrame.Render.Bindings（后续任务）
// 导出方式：链接期由 zplugin/zrender.def 声明，头文件不使用 dllexport，
// 因此同一份声明可直接用于静态库消费者（如 zrender_test）。
// =============================================================================
#pragma once

#include <stdint.h>

#include "zrender/zmesh.h"

#ifdef __cplusplus
extern "C" {
#endif

/// C ABI 版本号；发生不兼容变更时递增（当前 3——B-PLANET-02 追加行星通道 ABI：
/// double 相机原点 + chunk 句柄 + LOD + 高度采样，见文件末尾行星通道声明块）。
uint32_t ZR_ApiVersion(void);

/// ZRender 版本字符串（与 CMake project VERSION 同源），如 "0.1.0"。
const char* ZR_GetVersion(void);

/// 构建诊断信息（编译器 / 后端 / 各模块版本 / 构建日期），静态缓冲区，无需释放。
const char* ZR_GetBuildInfo(void);

// ---- TASK-B-R4-01：.zmesh 加载 C ABI（句柄式，简单静态表 + 代际防误用） ----
// 句柄为 uint64_t：高 32 位代际、低 32 位槽位下标；0 恒为无效句柄。
// 非线程安全（编辑器/单线程加载期使用；多线程消费待后续任务）。

/// 从内存 buffer 加载 .zmesh：内部拷贝数据并解析校验。
/// 成功返回 0 并经 outHandle 返回句柄；失败返回 zmesh 负错误码（ZMESH_ERR_*）。
int ZR_MeshLoadFromMemory(const void* data, uint32_t sizeBytes, uint64_t* outHandle);

/// 取句柄网格统计（拷贝到 out；全定宽 POD，托管侧可直接按布局封送）。
/// 成功返回 0；句柄无效（含已释放/代际不符）返回 ZMESH_ERR_BAD_ARGS。
int ZR_MeshStatsGet(uint64_t handle, ZmeshStats* out);

/// 释放句柄网格（幂等防护：重复释放/无效句柄返回 ZMESH_ERR_BAD_ARGS）。
/// 成功返回 0。
int ZR_MeshFree(uint64_t handle);

// ---- TASK-B-R7-01：RHI 后端选择 C ABI（Vulkan 第二后端；两后端共存编译） ----
// 后端编号：0 = D3D12（缺省）、1 = Vulkan。选择只改变"验证程序/上层创建哪个
// RHI 设备"，本组函数不创建任何设备——真正的设备初始化仍由调用方执行。
// ZR_GetBackend 返回当前生效选择（进程内静态，单线程验证程序用）。

/// 设置 RHI 后端。越界返回 -1，成功返回 0。
int ZR_SetBackend(int backend);

/// 取当前 RHI 后端（0 = D3D12 缺省；1 = Vulkan）。
int ZR_GetBackend(void);

// ---- TASK-B-R6-02：超分后端设置 C ABI（原生侧；Unity 侧实接属 B-R6-03） ----
//
// 线程安全语义（三函数一致）：
//   - 内部以 SRWLOCK 串行化对"偏好/生效后端/能力缓存"的读写；
//   - SetPreferred / GetActive / QueryCaps 可被任意线程并发调用，不要求外部加锁；
//   - 本组函数**不**创建 D3D12 设备、**不**初始化渲染后端——它们只维护设置视图。
//     真正的后端初始化仍由渲染线程的 UpscalerChain::SelectAndInitialize 完成，
//     后者以自身日志为权威证据。
//   - 首次调用会触发一次能力探测（DXGI 适配器 + 运行时探测；FSR4 的 provider
//     枚举用探测期短命建的真设备，B-FSR-01），结果进程内缓存。
//
// 后端编号（ZrUpscalerPreferred）：0=Auto（按能力选优），1..5 与
// zr::UpscalerBackend 的枚举值 + 1 一一对应（Bilinear=1 … Dlss=5）。

#define ZR_UPSCALER_OK 0
#define ZR_UPSCALER_ERR_NULL (-1)  // outCaps 为空
#define ZR_UPSCALER_ERR_RANGE (-2) // preferred 越界
#define ZR_UPSCALER_ERR_SIZE (-3)  // outCaps->structSize 与当前 ABI 不符

enum ZrUpscalerPreferred {
    ZR_UPSCALER_PREF_AUTO = 0,
    ZR_UPSCALER_PREF_BILINEAR = 1,
    ZR_UPSCALER_PREF_FSR2 = 2,
    ZR_UPSCALER_PREF_FSR3 = 3,
    ZR_UPSCALER_PREF_FSR4 = 4,
    ZR_UPSCALER_PREF_DLSS = 5,
    ZR_UPSCALER_PREF_COUNT = 6
};

/// 能力/状态快照（全定宽 POD；托管侧按布局直接封送）
/// ABI 历史：v1 = 本结构前 8 个字段（32B）；v2 = 末尾追加 fsr4ProbeMode（36B）。
typedef struct ZrUpscalerCaps {
    uint32_t structSize;         // 调用方填 sizeof(ZrUpscalerCaps)（版本化）
    uint32_t vendorId;           // PCI vendor id（0x1002=AMD、0x10DE=NVIDIA…）
    uint32_t deviceId;           // PCI device id
    uint32_t fsrRuntimePresent;  // 1 = FFX 运行时 dll 可加载且导出齐全
    uint32_t fsr4Available;      // 1 = provider 枚举命中 4.x（真实设备探测；
                                 //     回退口径下可能失真——见 fsr4ProbeMode）
    uint32_t dlssAvailable;      // 1 = Streamline 运行时 + NVIDIA 硬件齐备
    uint32_t preferredBackend;   // ZrUpscalerPreferred（0=Auto）
    uint32_t activeBackend;      // 解析后的生效后端（1..5；Auto 时按能力选优）
    // ---- B-FSR-02 追加（放末尾：structSize 版本化协议允许向后兼容追加）----
    // fsr4Available 的探测口径三态（fsr4Available 的可信度由此判定）：
    //   0 = 真实设备探测（provider 枚举拿到真 ID3D12Device*，结论可信）
    //   1 = 保守回退（建备失败走了 device=null 枚举——FFX 的 null 枚举返回截断
    //       列表，结果可能失真；消费方（UI）必须按"未检测"处理）
    //   2 = 未探测（provider 枚举未执行——典型：FFX 运行时缺席；此时
    //       fsr4Available 恒 0，不代表硬件不支持）
    // 未知值（未来版本可能扩展）一律按"未检测"处理，不得当作 0。
    uint32_t fsr4ProbeMode;
} ZrUpscalerCaps;

/// 设置首选后端。越界返回 ZR_UPSCALER_ERR_RANGE，成功返回 ZR_UPSCALER_OK。
int ZR_Upscaler_SetPreferred(int preferred);

/// 取当前解析后的生效后端（1..5）；从未设置时按 Auto 解析（Bilinear 兜底）。
int ZR_Upscaler_GetActive(void);

/// 取能力/状态快照。outCaps 为空返回 ZR_UPSCALER_ERR_NULL；structSize 不符
/// 返回 ZR_UPSCALER_ERR_SIZE；成功返回 ZR_UPSCALER_OK 并填满全部字段。
int ZR_Upscaler_QueryCaps(ZrUpscalerCaps* outCaps);

// ---- TASK-B-R9-02：FG 输入（depth/MV）交接校验 C ABI ----
//
// 背景：Unity 侧真渲染走 URP，depth/MV 只能从 URP 的 RTHandle 取原生句柄
// （D3D12 下 Texture.GetNativeTexturePtr() 即 ID3D12Resource*）。本组函数只做
// **交接前体检**：非空 / 尺寸合法且两张图一致 / 格式在白名单内。它不创建任何
// 设备或 FG 上下文，也不触碰 FFX——真正的 FG 接线属后续任务。
//
// 错误码（负值，0 = 通过）：
//   ZR_FG_INPUT_ERR_NULL      desc 或 out 为空
//   ZR_FG_INPUT_ERR_ABI       structSize 与当前 ABI 不符
//   ZR_FG_INPUT_ERR_PTR       depth / motionVectors 句柄为空
//   ZR_FG_INPUT_ERR_SIZE      宽高为 0、超上限，或 maxDimension 本身非法
//   ZR_FG_INPUT_ERR_MISMATCH  depth 与 MV 尺寸不一致
//   ZR_FG_INPUT_ERR_FORMAT    格式不在白名单（depth: R32F/D32F；MV: RG16F）
#define ZR_FG_INPUT_OK 0
#define ZR_FG_INPUT_ERR_NULL (-1)
#define ZR_FG_INPUT_ERR_ABI (-2)
#define ZR_FG_INPUT_ERR_PTR (-3)
#define ZR_FG_INPUT_ERR_SIZE (-4)
#define ZR_FG_INPUT_ERR_MISMATCH (-5)
#define ZR_FG_INPUT_ERR_FORMAT (-6)

/// 纹理格式白名单（与 Unity 侧 GraphicsFormat 的映射由调用方负责）。
enum ZrFgTexFormat {
    ZR_FG_FORMAT_UNKNOWN = 0,
    ZR_FG_FORMAT_R32_SFLOAT = 1,    // 深度拷贝（URP _CameraDepthTexture，实测表现）
    ZR_FG_FORMAT_D32_SFLOAT = 2,    // 真实深度附件（RT 的 fmt=Depth）
    ZR_FG_FORMAT_R16G16_SFLOAT = 3  // 运动矢量（URP _MotionVectorTexture，实测表现）
};

/// 一次交接的描述（全定宽 POD，托管侧按布局直接封送）。
typedef struct ZrFgInputDesc {
    uint32_t structSize;    // 调用方填 sizeof(ZrFgInputDesc)
    void* depth;            // ID3D12Resource*（或其它后端等价的纹理句柄）
    uint32_t depthWidth;
    uint32_t depthHeight;
    uint32_t depthFormat;   // ZrFgTexFormat
    void* motionVectors;    // 同上
    uint32_t mvWidth;
    uint32_t mvHeight;
    uint32_t mvFormat;      // ZrFgTexFormat
    uint32_t maxDimension;  // 单边上限，0 = 默认 16384；> 32768 视为非法
} ZrFgInputDesc;

/// 校验结论（可断言；失败时 accepted=0，其余字段为 0）。
typedef struct ZrFgInputReport {
    uint32_t structSize;         // 由实现填 sizeof(ZrFgInputReport)
    uint32_t accepted;           // 1 = 可交给后续 FG 接线
    uint32_t depthOk;
    uint32_t mvOk;
    uint32_t width;              // 通过时的公共尺寸
    uint32_t height;
    uint32_t depthBytesPerPixel;
    uint32_t mvBytesPerPixel;
} ZrFgInputReport;

/// 格式 → 每像素字节数；未知格式返回 0。
uint32_t ZR_FrameGen_InputBytesPerPixel(uint32_t format);

/// 校验一次 depth/MV 交接。返回 ZR_FG_INPUT_OK 或 ZR_FG_INPUT_ERR_*。
int ZR_FrameGen_ValidateInputs(const ZrFgInputDesc* desc, ZrFgInputReport* out);

// ---- TASK-B-UNITY-01：Unity 接入最小渲染入口（外部设备 + 外部纹理清屏）----
//
// 为什么需要：ZRender 内核自身可用（--draw 60 帧），但它接不进 Unity——
// 设备由 ZrD3D12CreateDevice 自建、.def 里没有任何渲染入口。本组函数把
// "包住宿主设备 + 对宿主纹理清屏"这最小一步开放给托管侧。
//
// 参数一律用 void*（托管侧传 IntPtr）：本层不暴露 D3D12 类型，保持纯 C ABI。
//   d3d12Device       = ID3D12Device*（Unity: SystemInfo.graphicsDevice 的原生设备）
//   backBufferTexture = ID3D12Resource*（2D、单采样、RTV 可用格式）
//
// 语义与限制（先求通，不求全）：
//   - AttachDevice 只**包装**宿主设备：不创建设备、不启用/关闭调试层、不接管
//     宿主的设备生命周期（内部仅 AddRef；Detach 时释放该引用）。
//   - BeginFrame 为纹理建 RTV；EndFrame 记录 ClearRenderTargetView 并提交，
//     WaitIdle 排空后返回。清屏色固定为 ZR_UNITY_CLEAR_*（与 --draw 演示背景同值）。
//   - 纹理须处于 COMMON 状态（D3D12 首次以 RTV 使用时的隐式提升）。桥**不做**
//     状态跟踪：传入处于其它显式状态（COPY_DEST/PRESENT 等）的纹理时行为未定义。
//   - 单线程（Unity 渲染线程）；重复 AttachDevice 幂等。
//
// 错误码（负值；0 = 成功）：
#define ZR_UNITY_OK 0
#define ZR_UNITY_ERR_NULL (-1)      // 空指针参数
#define ZR_UNITY_ERR_NO_DEVICE (-2) // 尚未 AttachDevice
#define ZR_UNITY_ERR_DEVICE (-3)    // 设备包装 / 桥私有 D3D12 链路创建失败
#define ZR_UNITY_ERR_TEXTURE (-4)   // 纹理非法（非 2D / 多重采样 / 尺寸不足）
#define ZR_UNITY_ERR_STATE (-5)     // 帧状态错误（重复 BeginFrame / 无 Begin 的 End）

/// EndFrame 的清屏色（8bit UNORM 期望值；浮点定值 13/255、26/255、38/255、
/// alpha=1）。托管侧可用它做回读断言。
#define ZR_UNITY_CLEAR_R 13u
#define ZR_UNITY_CLEAR_G 26u
#define ZR_UNITY_CLEAR_B 38u
#define ZR_UNITY_CLEAR_A 255u

/// 包装外部 D3D12 设备。返回 ZR_UNITY_OK 或 ZR_UNITY_ERR_*。
int ZR_Unity_AttachDevice(void* d3d12Device);

/// 为一张外部纹理开帧（建 RTV）。返回 ZR_UNITY_OK 或 ZR_UNITY_ERR_*。
int ZR_Unity_BeginFrame(void* backBufferTexture, uint32_t width, uint32_t height);

/// 结束本帧：对 BeginFrame 的纹理清屏并等待 GPU 完成。返回 ZR_UNITY_OK 或错误码。
int ZR_Unity_EndFrame(void);

/// 解除设备包装并释放桥资源（宿主设备本身不受影响）。返回 ZR_UNITY_OK。
int ZR_Unity_DetachDevice(void);

/// 诊断/自证专用：取当前被包装的 ID3D12Device*（写入 *outDevice）。
/// 用于验证"包装的确实是宿主传入的那一个设备"，**不得**用于生产渲染路径。
int ZR_Unity_DebugGetAttachedDevice(void** outDevice);

// 托管侧 P/Invoke 可直接复用的函数指针类型（亦供 GetProcAddress 自证程序使用）
typedef int (*ZrUnityAttachDeviceFn)(void* d3d12Device);
typedef int (*ZrUnityBeginFrameFn)(void* backBufferTexture, uint32_t width, uint32_t height);
typedef int (*ZrUnityEndFrameFn)(void);
typedef int (*ZrUnityDetachDeviceFn)(void);
typedef int (*ZrUnityDebugGetAttachedDeviceFn)(void** outDevice);

// ---- TASK-B-UNITY-02：共享渲染目标（ZRender 渲染自己的纹理，Unity 采样贴屏）----
//
// 为什么与上面那组并存而不是取代它：B-UNITY-01 的形态要求宿主把 **backbuffer**
// 交出来，验收已判定它有两个硬阻断——①flip-model 的帧边界是 PRESENT，
// PRESENT→RENDER_TARGET 没有隐式提升，屏障得由我们插而序列在 Unity 手里；
// ②本桥自建 DIRECT 队列，WaitIdle 只排空自己的队列，不覆盖 Unity 队列上未完成的
// 渲染。本组把这两条一起绕开：纹理归 ZRender 所有，写完 fence 等完成再交给 Unity。
//
//   attachTexture = ID3D12Resource*（Unity: Texture.GetNativeTexturePtr()）——托管侧
//                   拿不到 ID3D12Device*，故由资源反查设备（兼容 B-UNITY-01 的
//                   AttachDevice，两条接入路径各自独立、互不影响）
//   CreateSharedTarget(w,h)  建 BGRA8 共享纹理（D3D12_HEAP_FLAG_SHARED）+ 渲染链
//   GetSharedTargetHandle()  取 NT 句柄（Unity: Texture2D.CreateExternalTexture）
//   RenderFrame()            渲染一帧；返回前把纹理转到 PIXEL_SHADER_RESOURCE 并
//                            WaitIdle（Unity 采样的正确性前提）
//   ReleaseSharedTarget()    释放纹理与渲染链（保留设备包装，便于尺寸变化后重建）
//
// 共享纹理只应创建一次：每帧 CreateTarget 会泄漏（尺寸变化时才 Release 后重建）。
//
// 错误码沿用上面的 ZR_UNITY_*，另加：
// ZR_UNITY_ERR_ARGS 注释定义（B-PLANET-02 修订轮 F7 同步扩充）：本码是通用
// "参数值非法"——除原有的尺寸非法（0 或超过 16384）、槽位越界外，自本任务起
// 复用于 double 参数 NaN/Inf 拒绝（ZR_Unity_SetCameraOrigin，见文件末尾行星通道
// 声明块组 1）；ZR_Unity_GetCameraOrigin 无 double 值参数，不受此扩充影响。
#define ZR_UNITY_ERR_ARGS (-6) // 参数值非法：尺寸非法（0 或超过 16384）/ 槽位越界 / double 参数 NaN/Inf（B-PLANET-02 起复用——SetCameraOrigin）

/// 统计快照（全定宽 POD）。centerRGBA 不是屏幕抓取，而是渲染完的**纹理回读**：
/// 它证明"这张交给 Unity 的纹理里到底有什么"，是"Blit 已调用"之外的真证据。
typedef struct ZrUnityTargetStats {
    uint32_t structSize;        // 由实现填 sizeof(ZrUnityTargetStats)（版本化）
    uint32_t renderFrameCount;  // RenderFrame 成功次数
    uint32_t width;
    uint32_t height;
    uint32_t readbackCount;     // 中心像素回读次数
    uint32_t lastError;         // 最近一次失败的 ZR_UNITY_ERR_*（0 = 无）
    uint32_t centerRGBA[4];     // 最近一次回读的中心像素（R,G,B,A 8bit）
    uint64_t sharedHandle;      // NT 句柄（0 = 未创建）—— 放末尾以免结构体内出现 padding
} ZrUnityTargetStats;

/// 由宿主纹理反查设备并包装（幂等）。返回 ZR_UNITY_OK 或 ZR_UNITY_ERR_*。
int ZR_Unity_AttachDeviceFromTexture(void* d3d12Texture);

/// 创建共享颜色目标（BGRA8，单 mip/层/采样）；同尺寸重复调用幂等返回 OK。
int ZR_Unity_CreateSharedTarget(uint32_t width, uint32_t height);

/// 取 NT 共享句柄（跨进程/跨 API 用）；未创建时写 0 并返回 ZR_UNITY_ERR_STATE。
int ZR_Unity_GetSharedTargetHandle(uint64_t* outHandle);

/// 取目标纹理的 ID3D12Resource*（同进程内给宿主包装外部纹理用）。
/// Unity 2022.3/D3D12 的 Texture2D.CreateExternalTexture 需要它，而不是 NT 句柄
/// ——传 NT 句柄实测首帧崩在 UnityPlayer（0xc0000005）。
int ZR_Unity_GetSharedTargetResource(void** outResource);

/// 渲染一帧到共享纹理（结束时为可采样状态且 GPU 已完成）。
int ZR_Unity_RenderFrame(void);

/// 释放目标（不动宿主设备与已导出的句柄语义：句柄随目标释放而关闭）。
void ZR_Unity_ReleaseSharedTarget(void);

/// 取统计快照；out 为空返回 ZR_UNITY_ERR_NULL。
int ZR_Unity_GetTargetStats(ZrUnityTargetStats* out);

// ---- TASK-B-UNITY-04：帧同步（fence 双缓冲）+ 双槽目标 ------------------------
//
// 语义变化（相对 B-UNITY-02/03）：
//   - 目标颜色/深度是**双槽**（0/1 轮流），CreateSharedTarget 一次建两套；
//   - RenderFrame **不再排空队列**，只等上一帧的 fence（帧 N 等 N-1）；
//   - 宿主每帧要采样的是 GetReadableTargetIndex() 指出的槽位（= 上一帧那份，
//     已确认画完），而不是"刚提交的槽位"——这就是不撕裂的全部理由。
//   - 宿主须为两个槽位各建一张外部纹理（GetTargetResourceAt(0/1)）。

/// 同步统计（全定宽 POD；"CPU 等待下降"的现场证据）。
typedef struct ZrUnitySyncStats {
    uint32_t structSize;    // 由实现填 sizeof(ZrUnitySyncStats)
    uint32_t frames;        // RenderFrame 调用数
    uint32_t blockedFrames; // 实际阻塞过的帧数（等待 >= 0.05ms）
    uint32_t framesInFlight;// 当前在途帧数（0..2）
    double lastWaitMs;      // 最近一次等待耗时（毫秒）
    double avgWaitMs;       // 累计平均等待耗时（毫秒）
} ZrUnitySyncStats;

/// 取第 index 个（0/1）颜色目标的 ID3D12Resource*；越界返回 ZR_UNITY_ERR_ARGS。
int ZR_Unity_GetTargetResourceAt(uint32_t index, void** outResource);

/// 取可安全采样的槽位号（= 上一帧渲染的那一份）；out 为空返回 ZR_UNITY_ERR_NULL。
int ZR_Unity_GetReadableTargetIndex(uint32_t* outIndex);

/// 取同步统计；out 为空返回 ZR_UNITY_ERR_NULL。
int ZR_Unity_GetSyncStats(ZrUnitySyncStats* out);

/// 整面回读槽位 0（TASK-B-UNITY-05）：逐行拷到 dst，dstRowPitch 是调用方行距（>= width*4）。
/// 行距一律取设备脚印（RowPitch 256 对齐），并且**由本模块自己那个资源对象**做
/// PSR→COPY_SOURCE 的屏障 —— 宿主 OpenSharedHandle 出来的对象状态跟踪是独立的，
/// 用它在渲染过的纹理上整面拷贝只会得到"每 8 行 1 行有内容、其余为 0"。
int ZR_Unity_ReadTargetPixels(void* dst, uint32_t dstRowPitch, uint32_t* outWidth,
    uint32_t* outHeight);

typedef int (*ZrUnityAttachDeviceFromTextureFn)(void* d3d12Texture);
typedef int (*ZrUnityCreateSharedTargetFn)(uint32_t width, uint32_t height);
typedef int (*ZrUnityGetSharedTargetHandleFn)(uint64_t* outHandle);
typedef int (*ZrUnityGetSharedTargetResourceFn)(void** outResource);
typedef int (*ZrUnityRenderFrameFn)(void);
typedef void (*ZrUnityReleaseSharedTargetFn)(void);
typedef int (*ZrUnityGetTargetStatsFn)(ZrUnityTargetStats* out);
typedef int (*ZrUnityGetTargetResourceAtFn)(uint32_t index, void** outResource);
typedef int (*ZrUnityGetReadableTargetIndexFn)(uint32_t* outIndex);
typedef int (*ZrUnityGetSyncStatsFn)(ZrUnitySyncStats* out);
typedef int (*ZrUnityReadTargetPixelsFn)(void* dst, uint32_t dstRowPitch, uint32_t* outWidth,
    uint32_t* outHeight);

// ---- TASK-B-UNITY-03：场景提交（把 Unity 的物件搬进 ZRender）-------------------
//
// 为什么需要：B-UNITY-02 打通了"纹理贴屏"，但 RenderFrame 内部固定画三个硬编码
// cube —— 开 -zrender 后游戏物件一个都不出现。本组把"画什么"交给调用方：
//
//   SetCamera(view16, proj16) → BeginScene() → DrawMesh(handle, world16, rgba) × N
//   → RenderFrame() → EndScene()
//
// 语义要点：
//   - 矩阵是**行主序存储 + 列向量**（clip = mvp * pos），平移量在下标 3/7/11。
//     Unity 的 Matrix4x4 是列主序，托管侧必须按元素转置后传入（见
//     ZRenderSceneBridge 的 ToNativeRowMajor），直接 memcpy 会得到转置画面。
//   - proj 必须是**标准 [0,1] 深度、NDC +Y 朝上**（PSO 固定 LESS + 清 1.0）；
//     Unity 侧要先归一化 GL.GetGPUProjectionMatrix 的 Y 翻转与反向 Z。
//   - 顶点布局 v1 固定 32B/顶点（pos3f + normal3f + uv2f），strideFloats 必须为 8。
//   - 本帧没有任何 DrawMesh → RenderFrame 回落画硬编码 cube（--draw 不受影响）。
//   - 单线程（Unity 渲染线程），与 B-UNITY-02 的接线同一约束。

/// 返回"句柄无效"（mesh 句柄过期/从未创建）。
#define ZR_UNITY_ERR_HANDLE (-7)

/// 场景提交统计（全定宽 POD；"mesh 只上传一次"的证据在这里）。
typedef struct ZrUnitySceneStats {
    uint32_t structSize;      // 由实现填 sizeof(ZrUnitySceneStats)
    uint32_t drawCount;       // 最近一帧实际提交的绘制项数（0 = 走了 cube 回落）
    uint32_t meshCount;       // 句柄表里存活的网格数
    uint32_t reserved;        // 对齐占位
    uint64_t uploadCount;     // 累计 CreateMesh 成功次数（应恒等于 mesh 种类数）
    uint64_t sceneFrameCount; // 走过场景路径的帧数
    uint64_t drawTotal;       // 累计提交的绘制项数
} ZrUnitySceneStats;

/// 设置本帧相机（view/proj 各 16 float，行主序）。空指针返回 ZR_UNITY_ERR_NULL。
int ZR_Unity_SetCamera(const float* view16, const float* proj16);

/// 上传网格（顶点 32B/顶点，strideFloats 必须 8）。成功写 outHandle 并返回 OK。
/// 每份 mesh 只该传一次：调用方按 Mesh.GetInstanceID 缓存句柄。
int ZR_Unity_CreateMesh(const float* vertices, uint32_t vertexCount, uint32_t strideFloats,
    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle);

/// 释放网格（句柄立刻失效；无效句柄静默忽略）。
void ZR_Unity_DestroyMesh(uint64_t handle);

/// 提交一次绘制（world16 行主序；rgba 为空 = 白色）。
int ZR_Unity_DrawMesh(uint64_t handle, const float* world16, const float* rgba);

/// 开始新一帧（清空绘制表；相机与网格句柄表保留）。
int ZR_Unity_BeginScene(void);

/// 结束本帧（置状态位并累计统计）。
int ZR_Unity_EndScene(void);

/// 取场景统计；out 为空返回 ZR_UNITY_ERR_NULL。
int ZR_Unity_GetSceneStats(ZrUnitySceneStats* out);

typedef int (*ZrUnitySetCameraFn)(const float* view16, const float* proj16);
typedef int (*ZrUnityCreateMeshFn)(const float* vertices, uint32_t vertexCount,
    uint32_t strideFloats, const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle);
typedef void (*ZrUnityDestroyMeshFn)(uint64_t handle);
typedef int (*ZrUnityDrawMeshFn)(uint64_t handle, const float* world16, const float* rgba);
typedef int (*ZrUnityBeginSceneFn)(void);
typedef int (*ZrUnityEndSceneFn)(void);
typedef int (*ZrUnityGetSceneStatsFn)(ZrUnitySceneStats* out);

// ---- TASK-B-PLANET-02：P2 行星专用通道 C ABI（double 原点 + chunk 句柄 + LOD + 高度采样）----
//
// 设计依据：Docs/Planning/P2-行星ABI设计.md（TASK-B-PLANET-01，2026-11-14）§14 权威签名块。
// 通道形态：chunk 不走 DrawMesh——由 RenderFrame 在行星通道内自主绘制，
// 模型平移由原生在 double 域相减后一次 cast（float(objAbs - camAbs)，
// planet_math.h RenderOffsetF「重定位正确路径」）。重定位权威在 Unity 侧
// WorldOrigin（4000m，>= 触发）；原生不重定位，只消费每帧 SetCameraOrigin。
//
// 线程归属（SRWLOCK 先例：ZR_Upscaler_* 三函数）：
//   - SetCameraOrigin/GetCameraOrigin：渲染线程（与既有 ZR_Unity_* 同约束）；
//   - Planet 组写操作（SetWorld/ChunkCreate/ChunkDestroy/SetOrigin/SetLod/ClearAll）：
//     主线程可调，SRWLOCK 独占；
//   - SampleHeight/GetStats：任意线程（共享锁拷贝快照后无锁计算）；
//   - GPU 上传/释放/档位应用只在渲染线程 RenderFrame 边界（延迟一帧语义）。
//
// chunk 句柄：uint64_t 高 32 代际、低 32 槽位，0 恒无效（与 mesh 句柄同协议）。

#define ZR_PLANET_OK 0
#define ZR_PLANET_ERR_NULL (-1)      // 指针参数为空
#define ZR_PLANET_ERR_ABI (-2)       // structSize 与当前 ABI 不符
#define ZR_PLANET_ERR_RANGE (-3)     // faceId/lodLevel/neighborMask/cellsPerEdge 越界或方向退化
#define ZR_PLANET_ERR_HANDLE (-4)    // 句柄无效（含代际不符/已释放）
#define ZR_PLANET_ERR_STATE (-5)     // 行星上下文未设置（SetWorld 前置）
#define ZR_PLANET_ERR_FULL (-6)      // chunk 槽位满（> ZR_PLANET_MAX_CHUNKS）
#define ZR_PLANET_ERR_NONFINITE (-7) // double 参数含 NaN/Inf

#define ZR_PLANET_MAX_CHUNKS 8192u   // 槽位上限（现状全量 6144 + 余量）
#define ZR_PLANET_VERTEX_STRIDE_FLOATS 8u // 32B/顶点：pos3f+normal3f+uv2f（与场景路径 v1 同宽）

// LOD 档位：0 = 远档（16x16 顶点）、1 = 近档（64x64）——v1 两档锁定（B-TECH-04 口径）。
// neighborMask 四缘位：置 1 = 该缘邻居更粗档或无数据/面界（v1 仅校验+记录，不改几何；
// 取舍分析见设计稿 §5——消缝真根因是共享边 float 基准，不是邻居信息缺失）。
#define ZR_PLANET_EDGE_NEG_U (1u << 0)
#define ZR_PLANET_EDGE_POS_U (1u << 1)
#define ZR_PLANET_EDGE_NEG_V (1u << 2)
#define ZR_PLANET_EDGE_POS_V (1u << 3)
// 高 4 位保留，必须 0，否则 ZR_PLANET_ERR_RANGE。

/// 行星上下文（进程级单例，v1 单行星静态语义；SetWorld 独占锁替换快照）。
typedef struct ZrPlanetWorld {
    uint32_t structSize;      // 调用方填 sizeof(ZrPlanetWorld)
    uint32_t seed;            // 地形噪声种子（chunk 生成与 SampleHeight 共用）
    double centerAbs[3];      // 行星中心绝对坐标（行星中心系，米）
} ZrPlanetWorld;
static_assert(sizeof(ZrPlanetWorld) == 32, "ZrPlanetWorld must be 32 bytes (no padding)");

/// chunk 创建描述（全定宽 POD，托管侧按布局直接封送）。
/// 顶点由原生按规范路由 PlanetChunkVertexAtGlobal 生成（与 doctest 同函数）；
/// 顶点局部坐标 = float(posAbs - originAbs)（double 域相减后一次 cast）。
typedef struct ZrPlanetChunkDesc {
    uint32_t structSize;      // 调用方填 sizeof(ZrPlanetChunkDesc)
    uint32_t faceId;          // 0..5（+Z/+X/-Z/-X/+Y/-Y）
    uint32_t lodLevel;        // 0=远档 1=近档（分辨率由档位映射，不带 res 字段）
    uint32_t cx;              // 面内 chunk 格 u（0..cellsPerEdge-1）
    uint32_t cy;              // 面内 chunk 格 v
    uint32_t cellsPerEdge;    // 面内 chunk 数（1..4096；现状 32）
    uint32_t neighborMask;    // ZR_PLANET_EDGE_*（创建即可带；SetLod 可更新）
    uint32_t reserved0;       // 必须 0
    double originAbs[3];      // chunk 锚点绝对坐标（建议 chunk 中心球面点；顶点局部基准）
} ZrPlanetChunkDesc;
static_assert(sizeof(ZrPlanetChunkDesc) == 56, "ZrPlanetChunkDesc must be 56 bytes (no padding)");

/// 行星通道统计（全定宽 POD；账目断言 createTotal - destroyTotal == liveChunks）。
typedef struct ZrPlanetStats {
    uint32_t structSize;       // 由实现填 sizeof(ZrPlanetStats)
    uint32_t liveChunks;       // 当前存活 chunk 数
    uint32_t pendingUploads;   // 待渲染线程上传
    uint32_t pendingFrees;     // 待渲染线程释放
    uint32_t pendingLod;       // 待应用档位变更
    uint32_t drawnLastFrame;   // 最近一帧实际绘制数（地平线剔除后）
    uint32_t culledLastFrame;  // 最近一帧剔除数
    uint32_t seamCheckFails;   // 运行期共享边 double 位形不一致计数（诊断；B-TECH-04 seams 口径）
    uint64_t createTotal;      // 累计 ChunkCreate 成功数
    uint64_t destroyTotal;     // 累计 ChunkDestroy 成功数
    uint64_t lodChangeTotal;   // 累计档位变更应用数
    uint64_t vertexBytes;      // 当前 GPU VB 字节（顶点数 x 32B）
    double lastFrameMs;        // 最近一级行星通道段耗时
    double avgFrameMs;         // 累计平均耗时
} ZrPlanetStats;
static_assert(sizeof(ZrPlanetStats) == 80, "ZrPlanetStats must be 80 bytes (no padding)");

// ---- 组 1：double 相机原点（沿用 ZR_UNITY_* 错误码；渲染线程） ----
//
// 注意：ZR_UNITY_ERR_ARGS 的定义注释（上方 ZR_UNITY_* 错误码处）已随本块落地同步扩充
// （修订轮 F7）——本组将其复用于 double 参数 NaN/Inf 拒绝（原注释仅覆盖尺寸非法口径）。

/// 设置相机绝对原点（行星中心系，米）。NaN/Inf 返回 ZR_UNITY_ERR_ARGS。
/// 跨帧持久（BeginScene 不清）；同帧多次调用最后一个生效；未设置时按 (0,0,0)。
int ZR_Unity_SetCameraOrigin(double x, double y, double z);

/// 取当前相机绝对原点（3 元素）。outOrigin 为空返回 ZR_UNITY_ERR_NULL。
/// 用途：C ABI round-trip 自证与托管诊断（TECH-VALIDATION §4.5 口径）。
int ZR_Unity_GetCameraOrigin(double* outOrigin);

// ---- 组 2：行星上下文 ----

/// 设置行星上下文（seed + 行星中心）。v1 契约：初始化期调用，单行星。
/// world 为空返回 ZR_PLANET_ERR_NULL；structSize 不符返回 ZR_PLANET_ERR_ABI；
/// centerAbs 含 NaN/Inf 返回 ZR_PLANET_ERR_NONFINITE。
int ZR_Planet_SetWorld(const ZrPlanetWorld* world);

// ---- 组 3：chunk 句柄生命周期 ----

/// 创建 chunk：原生生成顶点并排队 GPU 上传（下一 RenderFrame 生效——延迟一帧）。
/// 成功写 *outHandle 并返回 ZR_PLANET_OK；校验失败返回 ZR_PLANET_ERR_*（不写 outHandle）。
/// 同一 (faceId,cx,cy) 重复创建不去重（v1）——托管侧负责；账目以 GetStats 审计。
int ZR_Planet_ChunkCreate(const ZrPlanetChunkDesc* desc, uint64_t* outHandle);

/// 销毁 chunk：句柄立即失效（代际+1）；GPU 资源下一 RenderFrame 由渲染线程释放。
/// 幂等防护：重复释放/无效句柄返回 ZR_PLANET_ERR_HANDLE（显式错误码风格——
/// chunk 是流式资源，释放必须可审计；区别于 ZR_Unity_DestroyMesh 的 void 静默风格）。
int ZR_Planet_ChunkDestroy(uint64_t handle);

/// 更新 chunk 锚点绝对坐标（行星整体移动时由托管批量调用）。顶点不重算
/// （局部坐标相对锚点不变；锚点平移而行星中心未同步平移 = 托管侧 bug，不校验）。
/// NaN/Inf 返回 ZR_PLANET_ERR_NONFINITE；句柄无效返回 ZR_PLANET_ERR_HANDLE。
int ZR_Planet_ChunkSetOrigin(uint64_t handle, double x, double y, double z);

/// 设置期望 LOD 档位与邻居掩码。下一 RenderFrame 应用（同帧多次最后生效）。
/// 档位应用策略照搬 B-TECH-04：档变重建 VB、升容重建、退档复用不缩容。
/// lodLevel>1 或 neighborMask 高 4 位非 0 返回 ZR_PLANET_ERR_RANGE。
int ZR_Planet_SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask);

/// 取统计快照。out 为空返回 ZR_PLANET_ERR_NULL；structSize 由实现填。
int ZR_Planet_GetStats(ZrPlanetStats* out);

/// 一次性清空全部 chunk（场景卸载/Play 退出兜底；句柄全失效，GPU 下一帧释放）。
void ZR_Planet_ClearAll(void);

// ---- 组 4：高度采样（同步纯函数；任意线程） ----

/// 采样地形噪声高程（与 chunk 顶点同一 PlanetTerrainHeightM：同 (seed,dir) 逐位一致）。
/// dir = normalize(pos - 行星中心)；地表点 = 中心 + dir*(R + h)，R = kPlanetRadiusM 锁定。
/// 返回 ZR_PLANET_OK；outHeightM 为空 ERR_NULL；非有限输入 ERR_NONFINITE；
/// pos 与中心重合（方向退化）ERR_RANGE；上下文未设 ERR_STATE。
/// 频率预算：<=1000 次/帧（估算 <0.1ms，探针实测校准——设计稿 §6.2）。
int ZR_Planet_SampleHeight(double x, double y, double z, float* outHeightM);

// 托管侧 P/Invoke 可直接复用的函数指针类型
typedef int (*ZrUnitySetCameraOriginFn)(double x, double y, double z);
typedef int (*ZrUnityGetCameraOriginFn)(double* outOrigin);
typedef int (*ZrPlanetSetWorldFn)(const ZrPlanetWorld* world);
typedef int (*ZrPlanetChunkCreateFn)(const ZrPlanetChunkDesc* desc, uint64_t* outHandle);
typedef int (*ZrPlanetChunkDestroyFn)(uint64_t handle);
typedef int (*ZrPlanetChunkSetOriginFn)(uint64_t handle, double x, double y, double z);
typedef int (*ZrPlanetSetLodFn)(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask);
typedef int (*ZrPlanetGetStatsFn)(ZrPlanetStats* out);
typedef void (*ZrPlanetClearAllFn)(void);
typedef int (*ZrPlanetSampleHeightFn)(double x, double y, double z, float* outHeightM);

#ifdef __cplusplus
} // extern "C"
#endif
