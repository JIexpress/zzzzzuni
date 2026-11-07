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

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>

extern "C" {

uint32_t ZR_ApiVersion(void)
{
    return 1u;
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

} // extern "C"
