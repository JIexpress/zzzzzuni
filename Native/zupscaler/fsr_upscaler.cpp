// =============================================================================
// fsr_upscaler.cpp —— AMD FidelityFX SDK 统一运行时后端（TASK-B-R6-01 建 / R6-02 扩）
//
// 后端来源：Native/ThirdParty/ffx-fsr/（官方预编译 amd_fidelityfx_upscaler_dx12.dll，
// 统一 FFX 2.0 C API；详见 SOURCE.md——版本/tag/sha256/许可证）。
//
// 集成方式：LoadLibrary 运行时组件 dll → ffxLoadFunctions（GetProcAddress 五函数）→
//   ffxCreateContext（desc 链：ffxCreateBackendDX12Desc → ffxCreateContextDescUpscale
//   → ffxOverrideVersion）→ ffxDispatch（ffxDispatchDescUpscale，每帧）。
//
// 版本族（TASK-B-R6-02）：同一 dll 内含 FSR 3.1.x 与 4.x provider，用
// ffxQueryDescGetVersions 枚举后按名字前缀选择：
//   Fsr4 后端 → 只接受 "4." 前缀（4.1.1 ML 路径）
//   Fsr3 后端 → 优先 "3.1"、回退 "2.3"（B-R6-01 行为不变）
// 枚举为空或未命中 → Initialize 显式失败并打印原因（含 provider 列表），
// 由 UpscalerChain 回退下一候选——不得静默降级。
//
// 780M/RDNA3 iGPU 实测：provider 列表只有 3.1.5 / 2.3.4，4.x 未注册（SDK v2.3.0
// 文档要求 7000/9000 系**离散** GPU）。因此 Fsr4 在本机走"能力不可用"分支，
// 这是环境的真实结论，不是实现缺陷（见 Docs/Logs/TASK-B-R6-02.md）。
//
// 上下文 max 尺寸取 Initialize 的输入/输出尺寸；Resize 走 dispatch 内动态
// renderSize/upscaleSize（≤ max 时无需重建上下文）。
//
// 第三方头 /WX 豁免：ffx_api*.h / ffx_upscale.h 为 AMD 官方头（简报允许第三方
// 豁免 /WX），本编译单元对其 include 显式 push warning 隔离，我们的代码段不豁免。
// =============================================================================
#include "zupscaler/zupscaler.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <new>
#include <string>
#include <vector>

// ---- 第三方头（AMD FidelityFX SDK v2.3.0；/WX 豁免隔离段）----------------------
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include "ffx_api.h"
#include "ffx_api_types.h"
#include "ffx_api_loader.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_upscale.h"
#pragma warning(pop)

namespace zr {
namespace {

/// FFX 组件 dll 名（ThirdParty/ffx-fsr/bin/ 由 CMake 拷至 exe 目录）
constexpr const char* kFfxUpscalerDll = "amd_fidelityfx_upscaler_dx12.dll";
constexpr const char* kFfxLoaderDll = "amd_fidelityfx_loader_dx12.dll";

/// 全局 ffx 函数表（进程级单次加载；引用计数归零 FreeLibrary）
struct FfxRuntime {
    HMODULE module = nullptr;
    ffxFunctions funcs {};
    uint32_t refCount = 0;
    char dllPath[MAX_PATH] = {};
};

bool TryLoadFfxRuntime(FfxRuntime** outRuntime, bool quiet)
{
    static FfxRuntime runtime;
    if (runtime.refCount > 0u) {
        runtime.refCount += 1u;
        *outRuntime = &runtime;
        return true;
    }
    // 首选组件 dll；回退 loader dll（两者都在 exe 目录——DLL 搜索路径含 exe 目录）
    const char* candidates[2] = { kFfxUpscalerDll, kFfxLoaderDll };
    for (const char* name : candidates) {
        const HMODULE module = ::LoadLibraryA(name);
        if (module == nullptr) {
            if (!quiet) {
                std::printf("  fsr: LoadLibrary('%s') FAILED (GetLastError=%lu)\n", name,
                    ::GetLastError());
            }
            continue;
        }
        ffxFunctions funcs {};
        ffxLoadFunctions(&funcs, module);
        if (funcs.CreateContext == nullptr || funcs.Dispatch == nullptr
            || funcs.DestroyContext == nullptr) {
            if (!quiet) {
                std::printf("  fsr: '%s' missing ffx entry points\n", name);
            }
            ::FreeLibrary(module);
            continue;
        }
        runtime.module = module;
        runtime.funcs = funcs;
        runtime.refCount = 1u;
        ::GetModuleFileNameA(module, runtime.dllPath, MAX_PATH);
        if (!quiet) {
            std::printf("  fsr: runtime loaded: %s\n", runtime.dllPath);
        }
        *outRuntime = &runtime;
        return true;
    }
    if (!quiet) {
        std::printf("  fsr: FAILED to load FFX runtime (checked %s, %s; cwd/exe dir must "
                    "contain ThirdParty/ffx-fsr/bin dlls)\n",
            kFfxUpscalerDll, kFfxLoaderDll);
    }
    return false;
}

void ReleaseFfxRuntime(FfxRuntime* runtime)
{
    if (runtime == nullptr) {
        return;
    }
    if (runtime->refCount > 0u) {
        runtime->refCount -= 1u;
    }
    if (runtime->refCount == 0u && runtime->module != nullptr) {
        ::FreeLibrary(runtime->module);
        runtime->module = nullptr;
        runtime->funcs = ffxFunctions {};
    }
}

/// 枚举实现（nativeDevice 可为 null；FFX 对 null 设备返回同样的保守列表）
uint32_t EnumerateProvidersImpl(FfxRuntime* runtime, void* nativeDevice,
    ZupProviderVersion* out, uint32_t capacity, bool verbose)
{
    if (runtime == nullptr || runtime->funcs.Query == nullptr) {
        return 0u;
    }
    ffxQueryDescGetVersions query {};
    query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    query.header.pNext = nullptr;
    query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    query.device = nativeDevice;
    uint64_t count = 0u;
    query.outputCount = &count;
    if (runtime->funcs.Query(nullptr, &query.header) != FFX_API_RETURN_OK || count == 0u) {
        if (verbose) {
            std::printf("  fsr: provider enumeration empty (ffxQuery GET_VERSIONS)\n");
        }
        return 0u;
    }
    std::vector<uint64_t> ids(count);
    std::vector<const char*> names(count);
    query.versionIds = ids.data();
    query.versionNames = names.data();
    if (runtime->funcs.Query(nullptr, &query.header) != FFX_API_RETURN_OK) {
        if (verbose) {
            std::printf("  fsr: provider enumeration FAILED on second pass\n");
        }
        return 0u;
    }
    uint32_t written = 0u;
    for (uint64_t i = 0; i < count; ++i) {
        const char* name = (names[i] != nullptr) ? names[i] : "";
        if (verbose) {
            std::printf("  fsr: provider[%llu] id=0x%llX name=%s\n",
                static_cast<unsigned long long>(i),
                static_cast<unsigned long long>(ids[i]), name);
        }
        if (out != nullptr && written < capacity) {
            out[written].versionId = ids[i];
            ::strncpy_s(out[written].name, sizeof(out[written].name), name, _TRUNCATE);
            written += 1u;
        }
    }
    return written;
}

/// ZrUpscalerResourceState → FfxApiResourceState
uint32_t MapState(uint32_t zrState)
{
    uint32_t ffxState = 0u;
    if ((zrState & ZR_UPSCALER_STATE_COMPUTE_READ) != 0u) {
        ffxState |= FFX_API_RESOURCE_STATE_COMPUTE_READ;
    }
    if ((zrState & ZR_UPSCALER_STATE_PIXEL_READ) != 0u) {
        ffxState |= FFX_API_RESOURCE_STATE_PIXEL_READ;
    }
    if ((zrState & ZR_UPSCALER_STATE_UNORDERED_ACCESS) != 0u) {
        ffxState |= FFX_API_RESOURCE_STATE_UNORDERED_ACCESS;
    }
    if (ffxState == 0u) {
        ffxState = FFX_API_RESOURCE_STATE_COMPUTE_READ; // 保守缺省
    }
    return ffxState;
}

/// ffx 消息回调（运行时告警可见——不静默）
void FfxMessageCallback(uint32_t type, const wchar_t* message)
{
    char utf8[512] = {};
    ::WideCharToMultiByte(CP_UTF8, 0, (message != nullptr) ? message : L"(null)", -1, utf8,
        sizeof(utf8), nullptr, nullptr);
    std::printf("  fsr: [ffx %s] %s\n",
        (type == FFX_API_MESSAGE_TYPE_ERROR) ? "ERROR" : "WARN", utf8);
}

/// FSR 统一运行时后端（按构造时指定的版本族选择 provider）
class FsrUpscaler final : public IUpscaler {
public:
    explicit FsrUpscaler(UpscalerBackend backend) : m_backend(backend) {}

    ~FsrUpscaler() override { Shutdown(); }

    UpscalerBackend Backend() const override { return m_backend; }

    const char* BackendVersion() const override
    {
        return m_versionName[0] != '\0' ? m_versionName : "ffx runtime (version unknown)";
    }

    bool Initialize(const UpscalerInitDesc& desc) override
    {
        if (m_initialized) {
            Shutdown();
        }
        if (desc.nativeDevice == nullptr || desc.inputWidth == 0u || desc.inputHeight == 0u
            || desc.outputWidth == 0u || desc.outputHeight == 0u) {
            std::printf("  fsr: Initialize FAILED (invalid desc)\n");
            return false;
        }
        FfxRuntime* runtime = nullptr;
        if (!TryLoadFfxRuntime(&runtime, /*quiet=*/false)) {
            return false; // 失败点已打印（dll 缺失/无导出）
        }
        m_runtime = runtime;

        auto* device = static_cast<ID3D12Device*>(desc.nativeDevice);
        m_maxInputWidth = desc.inputWidth;
        m_maxInputHeight = desc.inputHeight;
        // 输出资源按 maxUpscaleSize 分配（见下 maxUpscaleSize），Resize 必须守住同一上限
        m_maxOutputWidth = desc.outputWidth;
        m_maxOutputHeight = desc.outputHeight;
        m_inputWidth = desc.inputWidth;
        m_inputHeight = desc.inputHeight;
        m_outputWidth = desc.outputWidth;
        m_outputHeight = desc.outputHeight;
        m_cameraFovY = desc.cameraFovY;
        m_cameraNear = desc.cameraNear;
        m_cameraFar = desc.cameraFar;

        // desc 链：backend(device) → upscale（pNext 指向效果描述）
        m_upscaleDesc = {};
        m_upscaleDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        m_upscaleDesc.header.pNext = nullptr;
        // LDR 线性色 + 渲染分辨率 MV；MV 内含相机 jitter（两帧 mvp 均带各自
        // jitter）→ 置 JITTER_CANCELLATION 位（运行时按 jitterOffset 反解）
        m_upscaleDesc.flags = FFX_UPSCALE_ENABLE_MOTION_VECTORS_JITTER_CANCELLATION;
        m_upscaleDesc.maxRenderSize.width = desc.inputWidth;
        m_upscaleDesc.maxRenderSize.height = desc.inputHeight;
        m_upscaleDesc.maxUpscaleSize.width = desc.outputWidth;
        m_upscaleDesc.maxUpscaleSize.height = desc.outputHeight;
        m_upscaleDesc.fpMessage = &FfxMessageCallback;

        // desc 链首必须为效果描述（ffxCreateContext 以 desc->type 选 provider）：
        // upscale → pNext → override(versionId) → backend(device)。
        ffxCreateBackendDX12Desc backendDesc {};
        backendDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backendDesc.header.pNext = nullptr;
        backendDesc.device = device;

        // 枚举 provider 版本并按版本族选择（B-R6-02：Fsr4 只认 4.x）
        ZupProviderVersion providers[8] = {};
        const uint32_t providerCount
            = EnumerateProvidersImpl(m_runtime, device, providers, 8u, /*verbose=*/true);
        uint64_t chosenId = 0u;
        char chosenName[32] = {};
        const bool versionHit = ZupSelectProviderVersion(m_backend, providers, providerCount,
            &chosenId, chosenName, sizeof(chosenName));
        if (versionHit) {
            std::snprintf(m_versionName, sizeof(m_versionName), "FSR %s", chosenName);
        } else if (m_backend == UpscalerBackend::Fsr4) {
            // 能力不可用：显式失败（绝不静默创建 3.1 上下文冒充 FSR4）
            std::printf("  fsr4: provider 4.x NOT enumerated for this device (providers=%u:",
                providerCount);
            for (uint32_t i = 0; i < providerCount; ++i) {
                std::printf(" %s", providers[i].name);
            }
            std::printf("); FSR4 needs a discrete RX 7000/RX 9000 GPU (SDK v2.3.0 doc) "
                        "-> unavailable on this adapter, refusing silent fallback\n");
            ReleaseFfxRuntime(m_runtime);
            m_runtime = nullptr;
            return false;
        } else {
            // 3.1/2.3 都缺席（运行时异常）：退回默认选优路径，不钉版本
            std::printf("  fsr: provider enumeration empty; create without override\n");
        }

        m_overrideDesc = {};
        m_overrideDesc.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
        if (chosenId != 0u) {
            m_overrideDesc.versionId = chosenId;
            m_upscaleDesc.header.pNext = &m_overrideDesc.header;
            m_overrideDesc.header.pNext = &backendDesc.header;
        } else {
            m_upscaleDesc.header.pNext = &backendDesc.header;
        }

        std::printf("  fsr: ffxCreateContext begin (backend=%s device=%p, maxRender=%ux%u "
                    "maxUpscale=%ux%u flags=0x%X overrideId=0x%llX)\n",
            UpscalerBackendName(m_backend), static_cast<void*>(device),
            m_upscaleDesc.maxRenderSize.width, m_upscaleDesc.maxRenderSize.height,
            m_upscaleDesc.maxUpscaleSize.width, m_upscaleDesc.maxUpscaleSize.height,
            m_upscaleDesc.flags, static_cast<unsigned long long>(m_overrideDesc.versionId));
        ffxContext context = nullptr;
        const ffxReturnCode_t rc = m_runtime->funcs.CreateContext(
            &context, &m_upscaleDesc.header, nullptr);
        std::printf("  fsr: ffxCreateContext rc=%u context=%p\n", static_cast<unsigned>(rc),
            static_cast<void*>(context));
        if (rc != FFX_API_RETURN_OK || context == nullptr) {
            std::printf("  fsr: ffxCreateContext FAILED (rc=%u%s)\n", static_cast<unsigned>(rc),
                (rc == FFX_API_RETURN_NO_PROVIDER) ? " = NO_PROVIDER" : "");
            ReleaseFfxRuntime(m_runtime);
            m_runtime = nullptr;
            return false;
        }
        m_context = context;

        // provider 版本名（证据行：实际生效的 FSR 档位）
        ffxQueryGetProviderVersion versionQuery {};
        versionQuery.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        versionQuery.header.pNext = nullptr;
        if (m_runtime->funcs.Query(&m_context, &versionQuery.header) == FFX_API_RETURN_OK
            && versionQuery.versionName != nullptr) {
            ::strncpy_s(m_versionName, versionQuery.versionName, _TRUNCATE);
        }
        m_initialized = true;
        return true;
    }

    bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight) override
    {
        if (!m_initialized) {
            return false;
        }
        // 动态分辨率：输入与输出都须 ≤ 创建时上限（dispatch 每帧带
        // renderSize/upscaleSize；输出资源按 maxUpscaleSize 分配，目标输出超上限
        // 会写到未分配内存——TASK-H-02 D2）
        if (!ZupResizeWithinLimits(inputWidth, inputHeight, outputWidth, outputHeight,
                m_maxInputWidth, m_maxInputHeight, m_maxOutputWidth, m_maxOutputHeight)) {
            std::printf("  fsr: Resize rejected (%ux%u -> %ux%u, max %ux%u -> %ux%u)\n",
                inputWidth, inputHeight, outputWidth, outputHeight, m_maxInputWidth,
                m_maxInputHeight, m_maxOutputWidth, m_maxOutputHeight);
            return false;
        }
        m_inputWidth = inputWidth;
        m_inputHeight = inputHeight;
        m_outputWidth = outputWidth;
        m_outputHeight = outputHeight;
        return true;
    }

    bool Evaluate(const UpscalerFrameArgs& args) override
    {
        if (!m_initialized || m_runtime == nullptr || args.nativeCmdList == nullptr
            || args.inputColor == nullptr || args.inputDepth == nullptr
            || args.inputMotionVectors == nullptr || args.output == nullptr) {
            return false;
        }

        ffxDispatchDescUpscale dispatch {};
        dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
        dispatch.header.pNext = nullptr;
        dispatch.commandList = args.nativeCmdList;
        dispatch.color = ffxApiGetResourceDX12(
            static_cast<ID3D12Resource*>(args.inputColor), MapState(args.inputColorState));
        dispatch.depth = ffxApiGetResourceDX12(
            static_cast<ID3D12Resource*>(args.inputDepth), MapState(args.inputDepthState));
        dispatch.motionVectors = ffxApiGetResourceDX12(
            static_cast<ID3D12Resource*>(args.inputMotionVectors),
            MapState(args.inputMvState));
        dispatch.exposure = ffxApiGetResourceDX12(nullptr); // 无（未开 AUTO_EXPOSURE）
        dispatch.reactive = ffxApiGetResourceDX12(nullptr);
        dispatch.transparencyAndComposition = ffxApiGetResourceDX12(nullptr);
        dispatch.output = ffxApiGetResourceDX12(
            static_cast<ID3D12Resource*>(args.output), MapState(args.outputState));
        dispatch.jitterOffset.x = args.jitterX;
        dispatch.jitterOffset.y = args.jitterY;
        dispatch.motionVectorScale.x = args.motionVectorScaleX;
        dispatch.motionVectorScale.y = args.motionVectorScaleY;
        dispatch.renderSize.width = m_inputWidth;
        dispatch.renderSize.height = m_inputHeight;
        dispatch.upscaleSize.width = m_outputWidth;
        dispatch.upscaleSize.height = m_outputHeight;
        dispatch.enableSharpening = args.sharpness > 0.0f;
        dispatch.sharpness = args.sharpness;
        dispatch.frameTimeDelta = args.frameTimeDeltaMs;
        dispatch.preExposure = 1.0f;
        dispatch.reset = args.resetHistory;
        dispatch.cameraNear = m_cameraNear;
        dispatch.cameraFar = m_cameraFar;
        dispatch.cameraFovAngleVertical = m_cameraFovY;
        dispatch.viewSpaceToMetersFactor = 1.0f;
        dispatch.flags = 0u;

        const ffxReturnCode_t rc = m_runtime->funcs.Dispatch(&m_context, &dispatch.header);
        if (rc != FFX_API_RETURN_OK) {
            std::printf("  fsr: ffxDispatch FAILED (rc=%u)\n", static_cast<unsigned>(rc));
            return false;
        }
        return true;
    }

    void Shutdown() override
    {
        if (m_context != nullptr && m_runtime != nullptr) {
            m_runtime->funcs.DestroyContext(&m_context, nullptr);
            m_context = nullptr;
        }
        if (m_runtime != nullptr) {
            ReleaseFfxRuntime(m_runtime);
            m_runtime = nullptr;
        }
        m_initialized = false;
    }

    bool IsInitialized() const override { return m_initialized; }

private:
    UpscalerBackend m_backend = UpscalerBackend::Fsr3;
    bool m_initialized = false;
    FfxRuntime* m_runtime = nullptr;
    ffxContext m_context = nullptr;
    ffxCreateContextDescUpscale m_upscaleDesc {};
    ffxOverrideVersion m_overrideDesc {};
    uint32_t m_maxInputWidth = 0u;
    uint32_t m_maxInputHeight = 0u;
    uint32_t m_maxOutputWidth = 0u;   // 创建时 maxUpscaleSize（输出资源分配上限）
    uint32_t m_maxOutputHeight = 0u;
    uint32_t m_inputWidth = 0u;
    uint32_t m_inputHeight = 0u;
    uint32_t m_outputWidth = 0u;
    uint32_t m_outputHeight = 0u;
    float m_cameraFovY = 0.0f;
    float m_cameraNear = 0.0f;
    float m_cameraFar = 0.0f;
    char m_versionName[96] = {};
};

} // namespace

// ---- 版本选择（纯逻辑，可 mock 单测） ----------------------------------------
namespace {

bool HasPrefix(const char* text, const char* prefix)
{
    return std::strncmp(text, prefix, std::strlen(prefix)) == 0;
}

} // namespace

bool ZupSelectProviderVersion(UpscalerBackend backend, const ZupProviderVersion* providers,
    uint32_t count, uint64_t* outVersionId, char* outName, uint32_t outNameCapacity)
{
    if (providers == nullptr || count == 0u) {
        return false;
    }
    // 候选前缀（按优先级）：命中第一个即定；不依赖枚举顺序
    const char* primary = nullptr;
    const char* secondary = nullptr;
    switch (backend) {
    case UpscalerBackend::Fsr4:
        primary = "4.";
        break;
    case UpscalerBackend::Fsr3:
        primary = "3.1";
        secondary = "2.3";
        break;
    case UpscalerBackend::Fsr2:
        primary = "2.3";
        break;
    default:
        return false; // Bilinear / Dlss 不走 FFX provider
    }

    const ZupProviderVersion* chosen = nullptr;
    for (uint32_t i = 0; i < count; ++i) {
        if (HasPrefix(providers[i].name, primary)) {
            chosen = &providers[i];
            break;
        }
        if (secondary != nullptr && chosen == nullptr
            && HasPrefix(providers[i].name, secondary)) {
            chosen = &providers[i]; // 仅作主前缀缺席时的回退
        }
    }
    if (chosen == nullptr) {
        return false;
    }
    if (outVersionId != nullptr) {
        *outVersionId = chosen->versionId;
    }
    if (outName != nullptr && outNameCapacity > 0u) {
        ::strncpy_s(outName, outNameCapacity, chosen->name, _TRUNCATE);
    }
    return true;
}

uint32_t ZupFsrEnumerateProviders(void* nativeDevice, ZupProviderVersion* out, uint32_t capacity)
{
    FfxRuntime* runtime = nullptr;
    if (!TryLoadFfxRuntime(&runtime, /*quiet=*/true)) {
        return 0u;
    }
    const uint32_t written = EnumerateProvidersImpl(runtime, nativeDevice, out, capacity,
        /*verbose=*/false);
    ReleaseFfxRuntime(runtime);
    return written;
}

bool ZupFsr4Probe(void* nativeDevice)
{
    ZupProviderVersion providers[8] = {};
    const uint32_t count = ZupFsrEnumerateProviders(nativeDevice, providers, 8u);
    uint64_t id = 0u;
    return ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, providers, count, &id, nullptr, 0u);
}

IUpscaler* CreateFsrUpscaler()
{
    return new (std::nothrow) FsrUpscaler(UpscalerBackend::Fsr3);
}

IUpscaler* CreateFsr4Upscaler()
{
    return new (std::nothrow) FsrUpscaler(UpscalerBackend::Fsr4);
}

bool ZupFsrRuntimeProbe()
{
    FfxRuntime* runtime = nullptr;
    if (!TryLoadFfxRuntime(&runtime, /*quiet=*/true)) {
        return false;
    }
    ReleaseFfxRuntime(runtime);
    return true;
}

} // namespace zr
