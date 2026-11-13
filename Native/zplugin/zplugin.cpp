// zplugin.cpp —— zrender.dll 对外入口实现（TASK-B-R0-01）
//
// ZR_GetBuildInfo 聚合五模块版本函数，验证静态库间的链接与聚合路径。
#include "zplugin/zplugin.h"

#include "zkernel/zkernel.h"
#include "zrender/zrender.h"
#include "zrender/zmesh.h"
#include "zrhi/d3d12/zrhi_d3d12_unity_bridge.h" // TASK-B-UNITY-01（无 D3D12 头依赖）
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h" // TASK-B-UNITY-02（同上）
#include "zrhi/zrhi.h"
#include "zupscaler/zupscaler.h"
// TASK-B-PLANET-06：采样与 chunk 顶点同源的生产头（§6.1/D7——PlanetTerrainHeightM
// 语义零改动；header-only，消费经 zplugin 既有 PUBLIC include 根 Native/，
// planet_math.h:8-9 注明的两种合法消费方式之一，零 CMake 增量）
#include "zplanet/planet_math.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

extern "C" {

uint32_t ZR_ApiVersion(void)
{
    // v3（TASK-B-PLANET-02）：追加行星通道 ABI 声明（zplugin.h 末尾声明块）。
    // 本任务只落声明与版本号——行星导出函数随 B-PLANET-04/05/06 实现任务逐个落
    // （zrender.def 不加行：未实现符号进 def 会造成加载期缺符号）。
    return 3u;
}

const char* ZR_GetVersion(void)
{
    return ZR_VERSION_STRING; // 由 CMake 注入，单一事实来源
}

const char* ZR_GetBuildInfo(void)
{
    static char s_info[512];
    static bool s_initialized = false;
    if (!s_initialized)
    {
#if defined(_MSC_VER)
        static const char s_compiler[] = "MSVC";
        const int compilerMajor = _MSC_VER / 100;
        const int compilerMinor = _MSC_VER % 100;
#else
        static const char s_compiler[] = "unknown";
        const int compilerMajor = 0;
        const int compilerMinor = 0;
#endif
        std::snprintf(s_info, sizeof(s_info),
            "zrender %s | compiler=%s %d.%02d | backend=placeholder | "
            "modules: kernel=%s rhi=%s render=%s upscaler=%s plugin=%s | built %s %s",
            ZR_VERSION_STRING, s_compiler, compilerMajor, compilerMinor,
            ZK_GetVersion(), ZRHI_GetVersion(), ZREN_GetVersion(), ZUP_GetVersion(),
            ZR_VERSION_STRING, __DATE__, __TIME__);
        s_initialized = true;
    }
    return s_info;
}

// ---- TASK-B-R4-01：.zmesh 句柄表（简单静态表 + 代际） -------------------------
// 约定见 zplugin.h：句柄 = (代际<<32)|槽位；0 恒无效；非线程安全（加载期使用）。

namespace {

constexpr uint32_t kMeshSlotCount = 64;

struct MeshEntry {
    bool used;
    uint32_t generation; // 从 1 起，释放即递增（0 表槽从未用过）
    void* buffer;        // malloc 的数据拷贝（ZmeshData 列指针指向其中）
    ZmeshData data;
};

MeshEntry g_meshTable[kMeshSlotCount] = {};

uint64_t MakeMeshHandle(uint32_t slot, uint32_t generation)
{
    return (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(slot);
}

MeshEntry* LookupMesh(uint64_t handle)
{
    const uint32_t slot = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slot >= kMeshSlotCount || generation == 0u) {
        return nullptr;
    }
    MeshEntry& entry = g_meshTable[slot];
    if (!entry.used || entry.generation != generation) {
        return nullptr;
    }
    return &entry;
}

} // namespace

int ZR_MeshLoadFromMemory(const void* data, uint32_t sizeBytes, uint64_t* outHandle)
{
    if (data == nullptr || outHandle == nullptr) {
        return ZMESH_ERR_BAD_ARGS;
    }
    // 先在原 buffer 上做全量校验（不碰句柄表），合法后再拷贝入库。
    ZmeshData parsed;
    const int parseResult = zmesh_Parse(data, sizeBytes, &parsed);
    if (parseResult != ZMESH_OK) {
        return parseResult;
    }

    for (uint32_t slot = 0; slot < kMeshSlotCount; ++slot) {
        MeshEntry& entry = g_meshTable[slot];
        if (entry.used) {
            continue;
        }
        if (entry.generation == 0u) {
            entry.generation = 1u; // 槽位首次启用
        }
        entry.buffer = std::malloc(sizeBytes);
        if (entry.buffer == nullptr) {
            return ZMESH_ERR_BAD_ARGS; // 分配失败按参数性错误上报（非解析错误）
        }
        std::memcpy(entry.buffer, data, sizeBytes);

        // 在自有拷贝上重新解析，列指针指向句柄表持有的内存。
        const int reparse = zmesh_Parse(entry.buffer, sizeBytes, &entry.data);
        if (reparse != ZMESH_OK) {
            std::free(entry.buffer);
            entry.buffer = nullptr;
            return reparse; // 理论不可达（同数据已验证），防御保留
        }
        entry.used = true;
        *outHandle = MakeMeshHandle(slot, entry.generation);
        return ZMESH_OK;
    }
    return ZMESH_ERR_BAD_ARGS; // 句柄表满（64 足够加载期使用）
}

int ZR_MeshStatsGet(uint64_t handle, ZmeshStats* out)
{
    if (out == nullptr) {
        return ZMESH_ERR_BAD_ARGS;
    }
    const MeshEntry* entry = LookupMesh(handle);
    if (entry == nullptr) {
        return ZMESH_ERR_BAD_ARGS;
    }
    *out = entry->data.stats;
    return ZMESH_OK;
}

int ZR_MeshFree(uint64_t handle)
{
    MeshEntry* entry = LookupMesh(handle);
    if (entry == nullptr) {
        return ZMESH_ERR_BAD_ARGS;
    }
    std::free(entry->buffer);
    entry->buffer = nullptr;
    entry->used = false;
    entry->generation += 1u; // 代际推进：旧句柄此后全部失效
    return ZMESH_OK;
}

// ---- TASK-B-R7-01：RHI 后端选择 C ABI ---------------------------------------
// 只维护"验证程序选哪个 RHI 设备"的进程内视图（不创建设备，见 zplugin.h 注）。
namespace {
int g_rhiBackend = 0; // 0 = D3D12（缺省）
} // namespace

int ZR_SetBackend(int backend)
{
    if (backend < 0 || backend > 1) {
        return -1; // 越界拒绝（0=D3D12 / 1=Vulkan）
    }
    g_rhiBackend = backend;
    return 0;
}

int ZR_GetBackend(void)
{
    return g_rhiBackend;
}

// ---- TASK-B-R6-02：超分设置 C ABI -------------------------------------------
// 语义与线程安全约定见 zplugin.h。内部状态全部由 g_upscalerLock 保护；
// 能力探测只在首次访问时做一次（进程内缓存），避免每次设置查询都枚举 dll。

namespace {

SRWLOCK g_upscalerLock = SRWLOCK_INIT;
int g_upscalerPreferred = ZR_UPSCALER_PREF_AUTO;
zr::UpscalerDeviceCaps g_upscalerCaps {};
bool g_upscalerCapsReady = false;

/// preferred（0..5）→ 后端枚举；Auto 与越界都映射到 Bilinear（仅内部兜底用，
/// 对外越界已在 SetPreferred 拒绝）
zr::UpscalerBackend BackendFromPreferred(int preferred)
{
    switch (preferred) {
    case ZR_UPSCALER_PREF_FSR2:
        return zr::UpscalerBackend::Fsr2;
    case ZR_UPSCALER_PREF_FSR3:
        return zr::UpscalerBackend::Fsr3;
    case ZR_UPSCALER_PREF_FSR4:
        return zr::UpscalerBackend::Fsr4;
    case ZR_UPSCALER_PREF_DLSS:
        return zr::UpscalerBackend::Dlss;
    case ZR_UPSCALER_PREF_BILINEAR:
    default:
        return zr::UpscalerBackend::Bilinear;
    }
}

/// 解析生效后端（调用方须持锁）：显式偏好原样返回；Auto 按默认优先序取首个
/// 能力满足者，全不满足则 Bilinear（恒可用）。
int ResolveActiveLocked()
{
    if (!g_upscalerCapsReady) {
        zr::ZupProbeDeviceCaps(&g_upscalerCaps);
        g_upscalerCapsReady = true;
    }
    if (g_upscalerPreferred != ZR_UPSCALER_PREF_AUTO) {
        return g_upscalerPreferred;
    }
    zr::UpscalerBackend order[8] = {};
    const uint32_t count = zr::UpscalerDefaultPriority(order, 8u);
    for (uint32_t i = 0; i < count; ++i) {
        if (zr::UpscalerBackendSupported(order[i], g_upscalerCaps)) {
            return static_cast<int>(order[i]) + 1; // 枚举值 + 1 = C ABI 编号
        }
    }
    return ZR_UPSCALER_PREF_BILINEAR;
}

} // namespace

int ZR_Upscaler_SetPreferred(int preferred)
{
    if (preferred < ZR_UPSCALER_PREF_AUTO || preferred >= ZR_UPSCALER_PREF_COUNT) {
        return ZR_UPSCALER_ERR_RANGE;
    }
    ::AcquireSRWLockExclusive(&g_upscalerLock);
    g_upscalerPreferred = preferred;
    (void)ResolveActiveLocked(); // 立即刷新缓存（GetActive 不必再探测）
    ::ReleaseSRWLockExclusive(&g_upscalerLock);
    return ZR_UPSCALER_OK;
}

int ZR_Upscaler_GetActive(void)
{
    ::AcquireSRWLockExclusive(&g_upscalerLock);
    const int active = ResolveActiveLocked();
    ::ReleaseSRWLockExclusive(&g_upscalerLock);
    return active;
}

int ZR_Upscaler_QueryCaps(ZrUpscalerCaps* outCaps)
{
    if (outCaps == nullptr) {
        return ZR_UPSCALER_ERR_NULL;
    }
    if (outCaps->structSize != sizeof(ZrUpscalerCaps)) {
        return ZR_UPSCALER_ERR_SIZE; // ABI 版本不符：调用方须填当前 sizeof
    }
    ::AcquireSRWLockExclusive(&g_upscalerLock);
    const int active = ResolveActiveLocked();
    outCaps->vendorId = g_upscalerCaps.vendorId;
    outCaps->deviceId = g_upscalerCaps.deviceId;
    outCaps->fsrRuntimePresent = g_upscalerCaps.fsrRuntimePresent ? 1u : 0u;
    outCaps->fsr4Available = g_upscalerCaps.fsr4Available ? 1u : 0u;
    outCaps->dlssAvailable
        = (g_upscalerCaps.nvidiaHardware && g_upscalerCaps.dlssRuntimePresent) ? 1u : 0u;
    outCaps->preferredBackend = static_cast<uint32_t>(g_upscalerPreferred);
    outCaps->activeBackend = static_cast<uint32_t>(active);
    outCaps->fsr4ProbeMode = g_upscalerCaps.fsr4ProbeMode; // 探测口径三态透传（B-FSR-02）
    ::ReleaseSRWLockExclusive(&g_upscalerLock);
    return ZR_UPSCALER_OK;
}

// ---- TASK-B-R9-02：FG 输入（depth/MV）交接校验 ----

namespace {

/// 单边尺寸默认上限（8K 的 4 倍余量；真正的上限由调用方按项目口径传 maxDimension）
constexpr uint32_t kFgMaxDimDefault = 16384u;
/// 实现侧的硬上限：调用方传入的 maxDimension 超过此值视为非法（防呆）
constexpr uint32_t kFgMaxDimHardLimit = 32768u;

bool FgDepthFormatOk(uint32_t format)
{
    return format == ZR_FG_FORMAT_R32_SFLOAT || format == ZR_FG_FORMAT_D32_SFLOAT;
}

bool FgMvFormatOk(uint32_t format)
{
    return format == ZR_FG_FORMAT_R16G16_SFLOAT;
}

bool FgSizeOk(uint32_t width, uint32_t height, uint32_t maxDim)
{
    return width > 0u && height > 0u && width <= maxDim && height <= maxDim;
}

} // namespace

uint32_t ZR_FrameGen_InputBytesPerPixel(uint32_t format)
{
    switch (format) {
    case ZR_FG_FORMAT_R32_SFLOAT:
    case ZR_FG_FORMAT_D32_SFLOAT:
        return 4u;
    case ZR_FG_FORMAT_R16G16_SFLOAT:
        return 4u; // 2 × half
    default:
        return 0u;
    }
}

int ZR_FrameGen_ValidateInputs(const ZrFgInputDesc* desc, ZrFgInputReport* out)
{
    if (out == nullptr) {
        return ZR_FG_INPUT_ERR_NULL;
    }
    std::memset(out, 0, sizeof(*out));
    out->structSize = static_cast<uint32_t>(sizeof(ZrFgInputReport));
    if (desc == nullptr) {
        return ZR_FG_INPUT_ERR_NULL;
    }
    if (desc->structSize != static_cast<uint32_t>(sizeof(ZrFgInputDesc))) {
        return ZR_FG_INPUT_ERR_ABI;
    }
    if (desc->depth == nullptr || desc->motionVectors == nullptr) {
        return ZR_FG_INPUT_ERR_PTR;
    }

    const uint32_t maxDim = (desc->maxDimension == 0u) ? kFgMaxDimDefault : desc->maxDimension;
    if (maxDim > kFgMaxDimHardLimit) {
        return ZR_FG_INPUT_ERR_SIZE;
    }
    if (!FgSizeOk(desc->depthWidth, desc->depthHeight, maxDim)
        || !FgSizeOk(desc->mvWidth, desc->mvHeight, maxDim)) {
        return ZR_FG_INPUT_ERR_SIZE;
    }
    if (desc->depthWidth != desc->mvWidth || desc->depthHeight != desc->mvHeight) {
        return ZR_FG_INPUT_ERR_MISMATCH;
    }
    if (!FgDepthFormatOk(desc->depthFormat) || !FgMvFormatOk(desc->mvFormat)) {
        return ZR_FG_INPUT_ERR_FORMAT;
    }

    out->accepted = 1u;
    out->depthOk = 1u;
    out->mvOk = 1u;
    out->width = desc->depthWidth;
    out->height = desc->depthHeight;
    out->depthBytesPerPixel = ZR_FrameGen_InputBytesPerPixel(desc->depthFormat);
    out->mvBytesPerPixel = ZR_FrameGen_InputBytesPerPixel(desc->mvFormat);
    return ZR_FG_INPUT_OK;
}

// ---- TASK-B-UNITY-01：Unity 接入最小渲染入口 --------------------------------
// 薄转发：实现全部在 zrhi 的 D3D12 桥（zrhi/d3d12/zrhi_d3d12_unity_bridge.*）。
// 这样本 TU 保持"纯 C ABI + 无 D3D12 头依赖"，后端差异不会渗进导出层。
// 错误码在两侧逐一对应（见 zrhi_d3d12_unity_bridge.h 的 kErr*）。

int ZR_Unity_AttachDevice(void* d3d12Device)
{
    return zr::unity_bridge::AttachDevice(d3d12Device);
}

int ZR_Unity_BeginFrame(void* backBufferTexture, uint32_t width, uint32_t height)
{
    return zr::unity_bridge::BeginFrame(backBufferTexture, width, height);
}

int ZR_Unity_EndFrame(void)
{
    return zr::unity_bridge::EndFrame();
}

int ZR_Unity_DetachDevice(void)
{
    return zr::unity_bridge::DetachDevice();
}

int ZR_Unity_DebugGetAttachedDevice(void** outDevice)
{
    return zr::unity_bridge::QueryAttachedDevice(outDevice);
}

// ---- TASK-B-UNITY-02：共享渲染目标（ZRender 画自己的纹理，Unity 采样贴屏）----
// 同样是薄转发；实现与状态全在 zr::unity_target（zrhi/d3d12/..._unity_target.*）。
// 与上面那组的分工：B-UNITY-01 是"宿主给我一张纹理，我清屏"；本组是"我自己建
// 纹理，渲染完交给宿主采样"——后者不需要碰宿主 backbuffer 状态，也不需要跨队列
// fence（验收已判定前者有硬阻断）。

int ZR_Unity_AttachDeviceFromTexture(void* d3d12Texture)
{
    return zr::unity_target::AttachDeviceFromTexture(d3d12Texture);
}

int ZR_Unity_CreateSharedTarget(uint32_t width, uint32_t height)
{
    return zr::unity_target::CreateTarget(width, height);
}

int ZR_Unity_GetSharedTargetHandle(uint64_t* outHandle)
{
    return zr::unity_target::GetTargetHandle(outHandle);
}

int ZR_Unity_GetSharedTargetResource(void** outResource)
{
    return zr::unity_target::GetTargetResource(outResource);
}

int ZR_Unity_RenderFrame(void)
{
    return zr::unity_target::RenderFrame();
}

void ZR_Unity_ReleaseSharedTarget(void)
{
    zr::unity_target::ReleaseTarget();
}

int ZR_Unity_GetTargetStats(ZrUnityTargetStats* out)
{
    if (out == nullptr) {
        return ZR_UNITY_ERR_NULL;
    }
    zr::unity_target::TargetStats stats;
    const int result = zr::unity_target::GetStats(&stats);
    if (result != ZR_UNITY_OK) {
        return result;
    }
    out->structSize = static_cast<uint32_t>(sizeof(ZrUnityTargetStats));
    out->renderFrameCount = stats.frameCount;
    out->width = stats.width;
    out->height = stats.height;
    out->readbackCount = stats.readbackCount;
    out->sharedHandle = stats.sharedHandle;
    for (uint32_t i = 0; i < 4u; ++i) {
        out->centerRGBA[i] = stats.centerRGBA[i];
    }
    out->lastError = stats.lastError;
    return ZR_UNITY_OK;
}

// ---- TASK-B-UNITY-04：帧同步（fence 双缓冲）+ 双槽目标 -----------------------

int ZR_Unity_GetTargetResourceAt(uint32_t index, void** outResource)
{
    return zr::unity_target::GetTargetResourceAt(index, outResource);
}

int ZR_Unity_GetReadableTargetIndex(uint32_t* outIndex)
{
    return zr::unity_target::GetReadableTargetIndex(outIndex);
}

int ZR_Unity_ReadTargetPixels(void* dst, uint32_t dstRowPitch, uint32_t* outWidth,
    uint32_t* outHeight)
{
    return zr::unity_target::ReadTargetPixels(dst, dstRowPitch, outWidth, outHeight);
}

int ZR_Unity_GetSyncStats(ZrUnitySyncStats* out)
{
    if (out == nullptr) {
        return ZR_UNITY_ERR_NULL;
    }
    zr::unity_target::SyncStats stats;
    const int result = zr::unity_target::GetSyncStats(&stats);
    if (result != ZR_UNITY_OK) {
        return result;
    }
    out->structSize = static_cast<uint32_t>(sizeof(ZrUnitySyncStats));
    out->frames = stats.frames;
    out->blockedFrames = stats.blockedFrames;
    out->framesInFlight = stats.framesInFlight;
    out->lastWaitMs = stats.lastWaitMs;
    out->avgWaitMs = stats.avgWaitMs;
    return ZR_UNITY_OK;
}

// ---- TASK-B-UNITY-03：场景提交（相机 + 网格 + 绘制）----------------------------
// 薄转发；状态全在 zr::unity_target。唯一带逻辑的是统计结构体的布局转换
// （原生 SceneStats → ABI ZrUnitySceneStats，两者字段一一对应）。

int ZR_Unity_SetCamera(const float* view16, const float* proj16)
{
    return zr::unity_target::SetCamera(view16, proj16);
}

int ZR_Unity_CreateMesh(const float* vertices, uint32_t vertexCount, uint32_t strideFloats,
    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle)
{
    return zr::unity_target::CreateMesh(
        vertices, vertexCount, strideFloats, indices, indexCount, outHandle);
}

void ZR_Unity_DestroyMesh(uint64_t handle)
{
    zr::unity_target::DestroyMesh(handle);
}

int ZR_Unity_DrawMesh(uint64_t handle, const float* world16, const float* rgba)
{
    return zr::unity_target::DrawMesh(handle, world16, rgba);
}

int ZR_Unity_BeginScene(void)
{
    return zr::unity_target::BeginScene();
}

int ZR_Unity_EndScene(void)
{
    return zr::unity_target::EndScene();
}

int ZR_Unity_GetSceneStats(ZrUnitySceneStats* out)
{
    if (out == nullptr) {
        return ZR_UNITY_ERR_NULL;
    }
    zr::unity_target::SceneStats stats;
    const int result = zr::unity_target::GetSceneStats(&stats);
    if (result != ZR_UNITY_OK) {
        return result;
    }
    out->structSize = static_cast<uint32_t>(sizeof(ZrUnitySceneStats));
    out->drawCount = stats.drawCount;
    out->meshCount = stats.meshCount;
    out->reserved = 0u;
    out->uploadCount = stats.uploadCount;
    out->sceneFrameCount = stats.sceneFrameCount;
    out->drawTotal = stats.drawTotal;
    return ZR_UNITY_OK;
}

// ---- TASK-B-PLANET-05（B-PLANET-04 接管消费）：行星相机原点 --------------------
//
// 协议（设计稿 Docs/Planning/P2-行星ABI设计.md §3.3，权威文本 §14 组 1）：
//   - SetCameraOrigin：NaN/Inf 任一分量 → ZR_UNITY_ERR_ARGS（zplugin.h ZR_UNITY_ERR_ARGS
//     的 B-PLANET-02 扩充口径）；同帧多次调用最后一个生效；**跨帧持久**——
//     BeginScene 只清场景绘制表（zr::unity_target），相机与网格句柄表保留
//    （zplugin.h 场景提交语义），相机原点同属"保留"侧、不在其清空范围；
//   - GetCameraOrigin：out 为空 → ZR_UNITY_ERR_NULL；初始 (0,0,0)——未 Set 就
//     RenderFrame 按行星中心 = 世界原点处理（合法初始态，不报错，§3.3 末条）。
//   - 线程（设计稿 §8）：渲染线程（与既有 ZR_Unity_* 同约束），锁 = 无（单线程
//     约定下读写不竞争）——与 zplugin 既有 g_rhiBackend / 网格句柄表同口径。
//
// 存储迁移（B-PLANET-04 主控裁决①接管消费）：原点静态从本 TU 迁至
// zr::unity_target::planet（RenderFrame 的 chunk 模型平移消费者所在侧——zrhi
// 不反向依赖 zplugin，渲染线程可读的状态必须落在消费侧）。本 TU 保留薄转发 +
// 校验：NaN/Inf 拒绝、初始 (0,0,0)、跨帧持久、同帧最后生效逐字不变（既有
// doctest round-trip/持久性用例的观测面完全一致——迁移不改 ABI 行为）。

int ZR_Unity_SetCameraOrigin(double x, double y, double z)
{
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) {
        return ZR_UNITY_ERR_ARGS; // NaN/Inf 任一分量拒绝；状态保持上一次成功值
    }
    zr::unity_target::planet::SetCameraOrigin(x, y, z);
    return ZR_UNITY_OK;
}

int ZR_Unity_GetCameraOrigin(double* outOrigin)
{
    if (outOrigin == nullptr) {
        return ZR_UNITY_ERR_NULL;
    }
    zr::unity_target::planet::GetCameraOrigin(outOrigin);
    return ZR_UNITY_OK;
}

// ---- TASK-B-PLANET-06：行星上下文快照 + 高度采样（zplugin.h 行星声明块组 2/组 4）----
//
// 协议（设计稿 Docs/Planning/P2-行星ABI设计.md §6.1，权威文本 §14 组 2/组 4）：
//   - SetWorld：进程级单行星上下文快照（seed + centerAbs），SRWLOCK 独占锁替换。
//     校验序 = NULL → ABI → NONFINITE：structSize 不符时布局未知，不得解引用
//     centerAbs，故 ABI 先于 NONFINITE；拒绝时不触碰既有快照。错误码适用面
//    （§4.5 七码组核对）：NULL/ABI/NONFINITE 三码——半径不入参（R = kPlanetRadiusM
//     锁定于 planet_math.h）、seed 为 uint32 全域合法，故 RANGE/HANDLE/FULL/STATE
//     不适用于本函数。v1 契约：初始化期调用，单行星静态语义（无清除路径；
//     多行星上下文明确不做，设计稿 §12-11）。
//   - SampleHeight：同步纯函数（D7——异步回填明确不做），任意线程可调：
//     共享锁内拷贝快照后无锁计算（§8 表锁协议）。dir = normalize(pos − C)，
//     h = PlanetTerrainHeightM(seed, dir)——与 chunk 顶点生成同一生产头纯函数
//    （§6.3「同一函数同一值」）。校验序 = NULL → NONFINITE（纯输入域检查）→
//     STATE（上下文前置）→ RANGE（|pos−C| = 0 方向退化）——退化判据依赖中心 C，
//     只能在 STATE 之后成立（zplugin.h 组 4 注释按码罗列，非优先级序）。
//     normalize 的 double 舍入会带来 1-2 ULP 方向漂移（d·R 与 sqrt(Σ(d·R)²) 各自
//     正确舍入所致，构造性不可避免），经 float 输出粒度（ULP(2.4km) = 0.244mm）
//     完全吸收——实测 40896 样本零失配、最大高度漂移 3.5e-10 m，见任务日志。
//   - 状态落点：zplugin 进程级静态（B-PLANET-06 权威快照，SampleHeight 的锁协议
//     不变）。B-PLANET-04 追加：快照成功后向 zr::unity_target::planet 推送渲染侧
//     镜像副本（seed/center——chunk 顶点生成与 LOD 重建在 RenderFrame 边界重算
//     时需要；两份快照同一次调用内先后写入，v1 初始化期契约下无漂移窗口）。
//     归属核对：GetStats/ClearAll 属 §14 组 3（chunk 句柄生命周期），归
//     B-PLANET-04——不在本任务组 2/组 4 职责内（§14/§6.1 核对结论，见任务日志）。

namespace {

struct PlanetWorldSnapshot {
    bool set = false;                      // SetWorld 成功过（v1 无清除路径）
    uint32_t seed = 0u;                    // 地形噪声种子（chunk 生成与 SampleHeight 共用）
    double centerAbs[3] = {0.0, 0.0, 0.0}; // 行星中心绝对坐标（米）
};

SRWLOCK g_planetWorldLock = SRWLOCK_INIT;
PlanetWorldSnapshot g_planetWorld;

} // namespace

int ZR_Planet_SetWorld(const ZrPlanetWorld* world)
{
    if (world == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    if (world->structSize != sizeof(ZrPlanetWorld)) {
        return ZR_PLANET_ERR_ABI; // 布局未知，不得解引用 centerAbs
    }
    if (!std::isfinite(world->centerAbs[0]) || !std::isfinite(world->centerAbs[1])
        || !std::isfinite(world->centerAbs[2])) {
        return ZR_PLANET_ERR_NONFINITE; // 拒绝时保持既有快照不变
    }
    ::AcquireSRWLockExclusive(&g_planetWorldLock);
    g_planetWorld.seed = world->seed;
    g_planetWorld.centerAbs[0] = world->centerAbs[0];
    g_planetWorld.centerAbs[1] = world->centerAbs[1];
    g_planetWorld.centerAbs[2] = world->centerAbs[2];
    g_planetWorld.set = true;
    ::ReleaseSRWLockExclusive(&g_planetWorldLock);
    // B-PLANET-04：渲染侧镜像（seed/center）——顶点生成与 LOD 重建在 RenderFrame
    // 边界消费；校验已全部通过，镜像写入无独立失败路径。
    zr::unity_target::planet::SetWorldContext(world->seed, world->centerAbs);
    return ZR_PLANET_OK;
}

// ---- TASK-B-PLANET-04：chunk 句柄生命周期六导出（zplugin.h 行星声明块组 3）----
//
// 分工（设计稿 §4.5 校验链 + §8 线程表）：本 TU 做**纯输入域**校验（NULL/ABI/
// RANGE/NONFINITE——不触任何可变状态）与 STATE 前置（B-PLANET-06 权威快照），
// 槽位/FULL/代际/顶点生成/GPU 队列全部在 zr::unity_target::planet（渲染线程
// 可读侧——zrhi 不反向依赖 zplugin）。校验序 = 表序（§4.5）：NULL → ABI →
// RANGE → NONFINITE → STATE →（通道内）FULL。
//
// RANGE 桶的 cx/cy 越界检查说明：设计稿 §4.5 表未逐项列出，zplugin.h desc 注释
// 已声明 "cx/cy：面内 chunk 格 u（0..cellsPerEdge-1）"——越界即按 RANGE 桶拒绝
// （头部注释声明的合法域的执行，非协议新增）。
//
// 账目恒等式（§7.3②）：createTotal − destroyTotal == liveChunks 全程成立——
// ClearAll/桥级兜底按销毁记账（通道内实现），GetStats 断言由 doctest 锁定。

int ZR_Planet_ChunkCreate(const ZrPlanetChunkDesc* desc, uint64_t* outHandle)
{
    if (desc == nullptr || outHandle == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    if (desc->structSize != static_cast<uint32_t>(sizeof(ZrPlanetChunkDesc))) {
        return ZR_PLANET_ERR_ABI;
    }
    if (desc->faceId > 5u || desc->lodLevel > 1u || desc->cellsPerEdge == 0u
        || desc->cellsPerEdge > 4096u || desc->cx >= desc->cellsPerEdge
        || desc->cy >= desc->cellsPerEdge
        || (desc->neighborMask & 0xF0u) != 0u // 高 4 位保留必须 0（§14 组 3）
        || desc->reserved0 != 0u) {
        return ZR_PLANET_ERR_RANGE;
    }
    if (!std::isfinite(desc->originAbs[0]) || !std::isfinite(desc->originAbs[1])
        || !std::isfinite(desc->originAbs[2])) {
        return ZR_PLANET_ERR_NONFINITE;
    }
    {
        ::AcquireSRWLockShared(&g_planetWorldLock);
        const bool worldSet = g_planetWorld.set;
        ::ReleaseSRWLockShared(&g_planetWorldLock);
        if (!worldSet) {
            return ZR_PLANET_ERR_STATE; // §4.5：SetWorld 前置
        }
    }
    return zr::unity_target::planet::ChunkCreate(*desc, outHandle);
}

int ZR_Planet_ChunkDestroy(uint64_t handle)
{
    // 显式错误码风格（D4——chunk 是流式动态资源，重复释放/代际不符必须可检测）；
    // 无纯输入域校验（uint64 全域合法），代际裁决在通道内。
    return zr::unity_target::planet::ChunkDestroy(handle);
}

int ZR_Planet_ChunkSetOrigin(uint64_t handle, double x, double y, double z)
{
    // 校验序（§4.3 注释序）：NONFINITE（纯输入域）→ HANDLE（状态域）——
    // 与 SampleHeight 的"纯输入域先于状态域"先例一致。
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) {
        return ZR_PLANET_ERR_NONFINITE;
    }
    return zr::unity_target::planet::ChunkSetOrigin(handle, x, y, z);
}

int ZR_Planet_SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask)
{
    // RANGE（纯输入域）→ HANDLE（状态域）；§14 组 3：lodLevel>1 或 neighborMask
    // 高 4 位非 0 → RANGE。
    if (lodLevel > 1u || (neighborMask & 0xF0u) != 0u) {
        return ZR_PLANET_ERR_RANGE;
    }
    return zr::unity_target::planet::SetLod(handle, lodLevel, neighborMask);
}

int ZR_Planet_GetStats(ZrPlanetStats* out)
{
    if (out == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    return zr::unity_target::planet::GetStats(out);
}

void ZR_Planet_ClearAll(void)
{
    // 场景卸载/Play 退出兜底（§7.3 第 3 层）：句柄全失效 + 渲染线程排队释放。
    zr::unity_target::planet::ClearAll();
}

int ZR_Planet_SampleHeight(double x, double y, double z, float* outHeightM)
{
    if (outHeightM == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) {
        return ZR_PLANET_ERR_NONFINITE;
    }
    // 共享锁内拷贝快照（32B 量级），随后无锁计算——任意线程并发的锁协议（§8）
    bool worldSet = false;
    uint32_t seed = 0u;
    double centerAbs[3] = {0.0, 0.0, 0.0};
    ::AcquireSRWLockShared(&g_planetWorldLock);
    worldSet = g_planetWorld.set;
    if (worldSet) {
        seed = g_planetWorld.seed;
        centerAbs[0] = g_planetWorld.centerAbs[0];
        centerAbs[1] = g_planetWorld.centerAbs[1];
        centerAbs[2] = g_planetWorld.centerAbs[2];
    }
    ::ReleaseSRWLockShared(&g_planetWorldLock);
    if (!worldSet) {
        return ZR_PLANET_ERR_STATE;
    }
    const double dx = x - centerAbs[0];
    const double dy = y - centerAbs[1];
    const double dz = z - centerAbs[2];
    const double len = std::sqrt(dx * dx + dy * dy + dz * dz);
    if (len == 0.0) {
        return ZR_PLANET_ERR_RANGE; // pos 与中心重合，方向退化
    }
    const double h = zplanet::PlanetTerrainHeightM(seed, dx / len, dy / len, dz / len);
    *outHeightM = static_cast<float>(h);
    return ZR_PLANET_OK;
}

} // extern "C"
