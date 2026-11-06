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

/// C ABI 版本号；发生不兼容变更时递增（当前 1）。
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
//   - 首次调用会触发一次保守能力探测（DXGI + dll 可加载性），结果进程内缓存。
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
typedef struct ZrUpscalerCaps {
    uint32_t structSize;         // 调用方填 sizeof(ZrUpscalerCaps)（版本化）
    uint32_t vendorId;           // PCI vendor id（0x1002=AMD、0x10DE=NVIDIA…）
    uint32_t deviceId;           // PCI device id
    uint32_t fsrRuntimePresent;  // 1 = FFX 运行时 dll 可加载且导出齐全
    uint32_t fsr4Available;      // 1 = provider 枚举命中 4.x（保守：device=null 枚举）
    uint32_t dlssAvailable;      // 1 = Streamline 运行时 + NVIDIA 硬件齐备
    uint32_t preferredBackend;   // ZrUpscalerPreferred（0=Auto）
    uint32_t activeBackend;      // 解析后的生效后端（1..5；Auto 时按能力选优）
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
#define ZR_UNITY_ERR_ARGS (-6) // 尺寸非法（0 或超过 16384）

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

typedef int (*ZrUnityAttachDeviceFromTextureFn)(void* d3d12Texture);
typedef int (*ZrUnityCreateSharedTargetFn)(uint32_t width, uint32_t height);
typedef int (*ZrUnityGetSharedTargetHandleFn)(uint64_t* outHandle);
typedef int (*ZrUnityGetSharedTargetResourceFn)(void** outResource);
typedef int (*ZrUnityRenderFrameFn)(void);
typedef void (*ZrUnityReleaseSharedTargetFn)(void);
typedef int (*ZrUnityGetTargetStatsFn)(ZrUnityTargetStats* out);

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


#ifdef __cplusplus
} // extern "C"
#endif
