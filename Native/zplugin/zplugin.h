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

#ifdef __cplusplus
} // extern "C"
#endif
