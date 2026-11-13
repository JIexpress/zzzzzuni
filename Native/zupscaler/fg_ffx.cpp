// =============================================================================
// fg_ffx.cpp —— AMD FidelityFX FrameGeneration 封装实现（TASK-B-R8-02）
//
// 后端：ThirdParty/ffx-fsr/bin/amd_fidelityfx_framegeneration_dx12.dll（v2.3.0
// 预编译，直接导出 ffx_api 五函数；vendor/核验见 SOURCE.md "B-R8-02 追加"节）。
// 集成模式与 fsr_upscaler.cpp 相同：LoadLibrary → ffxLoadFunctions → desc 链。
//
// 两上下文模型（官方 frame-interpolation-swap-chain.md 契约）：
//   ① FG 效果上下文（Create）：FrameGeneration desc → Version desc →
//      BackendDX12 desc；持有插帧（光流+合成）实现。
//   ② FG 代理交换链上下文（CreateFgSwapchainForHwnd，ForHwnd 路径）：SDK 自建
//      底层交换链并包装为 IDXGISwapChain4；接管 present 时序（pacing 线程 +
//      合成线程）；每帧 Configure 携带 presentCallback/frameGenerationCallback。
// 帧循环（窗口程序 --fg 模式消费）：
//   ConfigureFrame(frameId) → [应用渲染] → DispatchPrepare(深度+MV) →
//   PresentFrame（SDK 内部：frameGenerationCallback→ffxDispatch 插帧 +
//   presentCallback→合成拷贝+计数 isGeneratedFrame）。
//
// mock 注入（CreateFrameGeneratorForTests）：m_runtime 指向 module==nullptr 的
// FgRuntime（函数表来自测试），Create/Configure/Dispatch 全走 mock 表；
// PresentFrame 的交换链 COM 调用被记账替代（mock 无真实交换链）。状态机、
// desc 构造与返回码路径两种模式共用同一份代码——mock 测的就是真机逻辑。
//
// 第三方头 /WX 豁免：ffx 官方头以 #pragma warning(push) 段隔离（同 fsr_upscaler.cpp
// 约定），本文件其余代码段不豁免。
// =============================================================================
#include "zupscaler/fg_ffx.h"

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
#include <vector>

#include <d3d12.h>
#include <dxgi.h>

// ---- 第三方头（AMD FidelityFX SDK v2.3.0；/WX 豁免隔离段）----------------------
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include "ffx_api.h"
#include "ffx_api_types.h"
#include "ffx_api_loader.h"
#include "dx12/ffx_api_dx12.h"
#include "ffx_framegeneration.h"
#include "dx12/ffx_api_framegeneration_dx12.h"
#pragma warning(pop)

namespace zr {
namespace {

/// FG 组件 dll 名（ThirdParty/ffx-fsr/bin/ 由 CMake 拷至 exe 目录）
constexpr const char* kFgDll = "amd_fidelityfx_framegeneration_dx12.dll";

/// ffx 函数表持有者（进程级单次加载；引用计数归零 FreeLibrary——与
/// fsr_upscaler.cpp 的 FfxRuntime 同模式，但加载的是 FG 组件 dll）。
/// mock 注入复用本结构：module==nullptr 且 refCount 固定 1（不 FreeLibrary）。
struct FgRuntime {
    HMODULE module = nullptr;
    ffxFunctions funcs {};
    uint32_t refCount = 0;
};

bool TryLoadFgRuntime(FgRuntime** outRuntime, bool quiet)
{
    static FgRuntime runtime;
    if (runtime.refCount > 0u) {
        runtime.refCount += 1u;
        *outRuntime = &runtime;
        return true;
    }
    const HMODULE module = ::LoadLibraryA(kFgDll);
    if (module == nullptr) {
        if (!quiet) {
            std::printf("  fg: LoadLibrary('%s') FAILED (GetLastError=%lu)\n", kFgDll,
                ::GetLastError());
        }
        return false;
    }
    ffxFunctions funcs {};
    ffxLoadFunctions(&funcs, module);
    if (funcs.CreateContext == nullptr || funcs.Dispatch == nullptr
        || funcs.DestroyContext == nullptr || funcs.Query == nullptr
        || funcs.Configure == nullptr) {
        if (!quiet) {
            std::printf("  fg: '%s' missing ffx entry points\n", kFgDll);
        }
        ::FreeLibrary(module);
        return false;
    }
    runtime.module = module;
    runtime.funcs = funcs;
    runtime.refCount = 1u;
    if (!quiet) {
        char path[MAX_PATH] = {};
        ::GetModuleFileNameA(module, path, MAX_PATH);
        std::printf("  fg: runtime loaded: %s\n", path);
    }
    *outRuntime = &runtime;
    return true;
}

void ReleaseFgRuntime(FgRuntime* runtime)
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

/// 枚举 FRAMEGENERATION provider。nativeDevice 可为 null（无硬件环境的回退），
/// 但 FFX 对 null 设备的 provider 枚举返回**截断**列表——同族行为已在 UPSCALER
/// 效果族实测证伪"null 与真机一致"：RX 9060 XT（2026-11-13）null=2 条 vs 真设备
/// =4 条含 4.1.2（见 Docs/Logs/TASK-B-FSR-01.md）；FRAMEGENERATION 族未做双设备
/// 对照，null 口径的结论只能当"未检测"，不得据此宣布 FG 不支持。能力判定应传真
/// 设备（窗口/探针路径均传真实 ID3D12Device*）。返回写入数；outFirst 安全拷贝
/// 首个 provider 名。
uint32_t EnumerateFgProviders(FgRuntime* runtime, void* nativeDevice,
    char* outFirst, uint32_t outFirstCapacity, bool verbose)
{
    if (runtime == nullptr || runtime->funcs.Query == nullptr) {
        return 0u;
    }
    ffxQueryDescGetVersions query {};
    query.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    query.header.pNext = nullptr;
    // FG 效果的 provider 族（ffx_api.h：FFX_API_EFFECT_ID_FRAMEGENERATION）
    query.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    query.device = nativeDevice;
    uint64_t count = 0u;
    query.outputCount = &count;
    if (runtime->funcs.Query(nullptr, &query.header) != FFX_API_RETURN_OK || count == 0u) {
        if (verbose) {
            std::printf("  fg: provider enumeration empty (ffxQuery GET_VERSIONS "
                        "FRAMEGENERATION)\n");
        }
        return 0u;
    }
    std::vector<uint64_t> ids(count);
    std::vector<const char*> names(count);
    query.versionIds = ids.data();
    query.versionNames = names.data();
    if (runtime->funcs.Query(nullptr, &query.header) != FFX_API_RETURN_OK) {
        if (verbose) {
            std::printf("  fg: provider enumeration FAILED on second pass\n");
        }
        return 0u;
    }
    for (uint64_t i = 0; i < count; ++i) {
        const char* name = (names[i] != nullptr) ? names[i] : "";
        if (verbose) {
            std::printf("  fg: provider[%llu] id=0x%llX name=%s\n",
                static_cast<unsigned long long>(i),
                static_cast<unsigned long long>(ids[i]), name);
        }
        if (i == 0u && outFirst != nullptr && outFirstCapacity > 0u) {
            ::strncpy_s(outFirst, outFirstCapacity, name, _TRUNCATE);
        }
    }
    return static_cast<uint32_t>(count);
}

/// ffx 消息回调（运行时告警可见——不静默）
void FgMessageCallback(uint32_t type, const wchar_t* message)
{
    char utf8[512] = {};
    ::WideCharToMultiByte(CP_UTF8, 0, (message != nullptr) ? message : L"(null)", -1, utf8,
        sizeof(utf8), nullptr, nullptr);
    std::printf("  fg: [ffx %s] %s\n",
        (type == FFX_API_MESSAGE_TYPE_ERROR) ? "ERROR" : "WARN", utf8);
}

} // namespace

// =============================================================================
// 纯函数（公共头契约；单测见 tests/test_fg_ffx.cpp）
// =============================================================================

FrameGenSupport ZupFgClassifySupport(uint32_t providerCount, uint32_t createReturnCode)
{
    if (providerCount == 0u) {
        return FrameGenSupport::NoProvider;
    }
    if (createReturnCode != FFX_API_RETURN_OK) {
        return FrameGenSupport::RejectedByRuntime;
    }
    return FrameGenSupport::Available;
}

const char* ZupFgReturnCodeName(uint32_t rc)
{
    switch (rc) {
    case FFX_API_RETURN_OK:
        return "OK";
    case FFX_API_RETURN_ERROR:
        return "ERROR";
    case FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE:
        return "ERROR_UNKNOWN_DESCTYPE";
    case FFX_API_RETURN_ERROR_RUNTIME_ERROR:
        return "ERROR_RUNTIME_ERROR";
    case FFX_API_RETURN_NO_PROVIDER:
        return "NO_PROVIDER";
    case FFX_API_RETURN_ERROR_MEMORY:
        return "ERROR_MEMORY";
    case FFX_API_RETURN_ERROR_PARAMETER:
        return "ERROR_PARAMETER";
    case FFX_API_RETURN_PROVIDER_NO_SUPPORT_NEW_DESCTYPE:
        return "PROVIDER_NO_SUPPORT_NEW_DESCTYPE";
    default:
        break;
    }
    static char buf[32];
    std::snprintf(buf, sizeof(buf), "UNKNOWN_%u", static_cast<unsigned>(rc));
    return buf;
}

/// FfxApiResourceState → D3D12_RESOURCE_STATES（与官方 backend/dx12/ffx_dx12.cpp
/// ffxGetDX12StateFromResourceState 逐项对齐；未知值 → COMMON）
uint32_t ZupFgMapResourceState(uint32_t ffxState)
{
    switch (ffxState) {
    case FFX_API_RESOURCE_STATE_GENERIC_READ:
        return D3D12_RESOURCE_STATE_GENERIC_READ;
    case FFX_API_RESOURCE_STATE_UNORDERED_ACCESS:
        return D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    case FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ:
        return D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE
            | D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_COMPUTE_READ:
        return D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_PIXEL_READ:
        return D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
    case FFX_API_RESOURCE_STATE_COPY_SRC:
        return D3D12_RESOURCE_STATE_COPY_SOURCE;
    case FFX_API_RESOURCE_STATE_COPY_DEST:
        return D3D12_RESOURCE_STATE_COPY_DEST;
    case FFX_API_RESOURCE_STATE_INDIRECT_ARGUMENT:
        return D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT;
    case FFX_API_RESOURCE_STATE_PRESENT:
        return D3D12_RESOURCE_STATE_PRESENT;
    case FFX_API_RESOURCE_STATE_COMMON:
        return D3D12_RESOURCE_STATE_COMMON;
    case FFX_API_RESOURCE_STATE_RENDER_TARGET:
        return D3D12_RESOURCE_STATE_RENDER_TARGET;
    case FFX_API_RESOURCE_STATE_DEPTH_ATTACHMENT:
        return D3D12_RESOURCE_STATE_DEPTH_WRITE;
    default:
        return D3D12_RESOURCE_STATE_COMMON;
    }
}

bool ZupFgValidateCreateDesc(uint32_t displayWidth, uint32_t displayHeight,
    uint32_t renderWidth, uint32_t renderHeight, uint32_t backBufferFfxFormat)
{
    if (displayWidth == 0u || displayHeight == 0u || renderWidth == 0u
        || renderHeight == 0u) {
        return false;
    }
    if (renderWidth > displayWidth || renderHeight > displayHeight) {
        return false; // FG 输入色契约 = displaySize；本集成不放大
    }
    if (backBufferFfxFormat == FFX_API_SURFACE_FORMAT_UNKNOWN) {
        return false;
    }
    return true;
}

bool ZupFgFrameCountsDoubled(uint64_t realPresents, uint64_t generatedPresents)
{
    if (realPresents == 0u) {
        return false;
    }
    // 每真帧恰 1 插帧；尾帧 pacing 差 1 容忍（关停相位的最后一帧可能只呈现真帧）
    return generatedPresents + 1u >= realPresents && generatedPresents <= realPresents;
}

// =============================================================================
// FrameGenerator
// =============================================================================

namespace {

/// 回调共享态：FFX 蹦床（SDK pacing/合成线程触发）与本类解耦的桥——
/// 头文件不引入第三方类型，蹦床经本结构访问计数器/出口/运行时。
struct FgFrameShared {
    std::atomic<uint64_t>* realPresents = nullptr;
    std::atomic<uint64_t>* genPresents = nullptr;
    FgPresentCountFn countFn = nullptr;   // 计数出口（窗口程序注入）
    void* countFnUserCtx = nullptr;
    FgRuntime* runtime = nullptr;         // mock 注入与真机共用（mock: module==nullptr）
    ffxContext* fgContext = nullptr;      // 指向 FrameGenerator::m_fgContext（存续期=
                                          //   FrameGenerator 实例）
    FgPresentObserveFn observeFn = nullptr; // 像素校验旁路（TASK-B-R9-03；可为空）
    void* observeCtx = nullptr;
};

/// present 回调（SDK 合成线程逐帧触发）：真帧/插帧统一在此把游戏后台缓冲合成
/// 到呈现缓冲（本集成无 UI → 纯 CopyResource；官方 UiCompositionCallback 同款
/// 状态迁移模式），并计数 isGeneratedFrame（帧计数翻倍证据的来源）。
ffxReturnCode_t FgPresentCallbackTrampoline(ffxCallbackDescFrameGenerationPresent* params,
    void* pUserCtx)
{
    auto* shared = static_cast<FgFrameShared*>(pUserCtx);
    if (shared == nullptr || params == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    // 计数先于合成（SDK pacing 线程的呈现事实不因资源缺失丢失——帧计数翻倍
    // 证据的完整性优先）
    if (params->isGeneratedFrame) {
        shared->genPresents->fetch_add(1u, std::memory_order_relaxed);
    } else {
        shared->realPresents->fetch_add(1u, std::memory_order_relaxed);
    }
    if (shared->countFn != nullptr) {
        shared->countFn(params->isGeneratedFrame, shared->countFnUserCtx);
    }
    // 合成：output ← currentBackBuffer（状态均由 SDK 以 FfxApiResourceState 声明；
    // 本集成无 UI 资源 → 拷贝即合成）。任一步缺失不吞——返回参数错误码。
    if (params->outputSwapChainBuffer.resource == nullptr
        || params->currentBackBuffer.resource == nullptr) {
        std::printf("  fg: present callback: missing buffers (counters kept)\n");
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    auto* cmd = static_cast<ID3D12GraphicsCommandList*>(params->commandList);
    auto* dst = static_cast<ID3D12Resource*>(params->outputSwapChainBuffer.resource);
    auto* src = static_cast<ID3D12Resource*>(params->currentBackBuffer.resource);
    if (cmd == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    const auto dstBefore = static_cast<D3D12_RESOURCE_STATES>(
        ZupFgMapResourceState(params->outputSwapChainBuffer.state));
    const auto srcBefore = static_cast<D3D12_RESOURCE_STATES>(
        ZupFgMapResourceState(params->currentBackBuffer.state));
    D3D12_RESOURCE_BARRIER barriers[2] = {};
    barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[0].Transition.pResource = src;
    barriers[0].Transition.StateBefore = srcBefore;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barriers[1].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[1].Transition.pResource = dst;
    barriers[1].Transition.StateBefore = dstBefore;
    barriers[1].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    cmd->ResourceBarrier(2u, barriers);
    cmd->CopyResource(dst, src);
    // 旁路观察者：此时 dst 处 COPY_DEST、内容即本帧最终画面（真帧 = 游戏画面，
    // 插帧 = 合成结果）。观察者若再记录命令，必须把 dst 归还到 COPY_DEST。
    if (shared->observeFn != nullptr) {
        shared->observeFn(params->isGeneratedFrame, cmd, dst, shared->observeCtx);
    }
    for (D3D12_RESOURCE_BARRIER& b : barriers) {
        const D3D12_RESOURCE_STATES tmp = b.Transition.StateBefore;
        b.Transition.StateBefore = b.Transition.StateAfter;
        b.Transition.StateAfter = tmp;
    }
    cmd->ResourceBarrier(2u, barriers);
    return FFX_API_RETURN_OK;
}

/// frameGenerationCallback（官方推荐路径：SDK 在 Present 内部以就绪的
/// ffxDispatchDescFrameGeneration 调用本回调 → 直转发 ffxDispatch）
ffxReturnCode_t FgFrameGenDispatchTrampoline(ffxDispatchDescFrameGeneration* params,
    void* pUserCtx)
{
    auto* shared = static_cast<FgFrameShared*>(pUserCtx);
    if (shared == nullptr || params == nullptr || shared->runtime == nullptr
        || shared->fgContext == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    return shared->runtime->funcs.Dispatch(shared->fgContext, &params->header);
}

} // namespace

FrameGenerator::FrameGenerator() = default;

FrameGenerator::~FrameGenerator()
{
    Shutdown();
    delete static_cast<FgFrameShared*>(m_shared);
    m_shared = nullptr;
    FgRuntime* rt = static_cast<FgRuntime*>(m_runtime);
    if (rt != nullptr && rt->module == nullptr) {
        delete rt; // mock 注入：实例所有，随实例释放（真机 dll 运行时归 ReleaseFgRuntime）
    }
    m_runtime = nullptr;
}

uint64_t FrameGenerator::RealPresents() const
{
    return m_realPresents.load(std::memory_order_relaxed);
}

uint64_t FrameGenerator::GeneratedPresents() const
{
    return m_genPresents.load(std::memory_order_relaxed);
}

FrameGenProbe FrameGenerator::QuerySupport(void* nativeDevice)
{
    FrameGenProbe probe {};
    FgRuntime* runtime = nullptr;
    if (!TryLoadFgRuntime(&runtime, /*quiet=*/true)) {
        probe.support = FrameGenSupport::Unavailable;
        return probe;
    }
    char first[32] = {};
    const uint32_t count
        = EnumerateFgProviders(runtime, nativeDevice, first, sizeof(first), /*verbose=*/false);
    probe.returnCode = (count == 0u) ? FFX_API_RETURN_NO_PROVIDER : FFX_API_RETURN_OK;
    probe.support = ZupFgClassifySupport(count, probe.returnCode);
    ::strncpy_s(probe.providerName, first, _TRUNCATE);
    ReleaseFgRuntime(runtime);
    return probe;
}

bool FrameGenerator::Create(const FrameGenCreateDesc& desc)
{
    if (m_fgContext != nullptr) {
        Shutdown(); // 幂等重建
    }
    if (desc.nativeDevice == nullptr
        || !ZupFgValidateCreateDesc(desc.displayWidth, desc.displayHeight, desc.renderWidth,
            desc.renderHeight, desc.backBufferFfxFormat)) {
        std::printf("  fg: Create FAILED (invalid desc: display=%ux%u render=%ux%u fmt=%u)\n",
            desc.displayWidth, desc.displayHeight, desc.renderWidth, desc.renderHeight,
            desc.backBufferFfxFormat);
        return false;
    }
    // mock 注入（m_runtime 已就位）复用测试函数表；真机路径加载 FG dll
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime == nullptr) {
        if (!TryLoadFgRuntime(&runtime, /*quiet=*/false)) {
            return false; // 失败点已打印（dll 缺失/无导出）
        }
        m_runtime = runtime;
    }

    // desc 链：FrameGeneration → pNext Version（SDK 2.1+ 必填）→ pNext BackendDX12
    ffxCreateContextDescFrameGeneration fgDesc {};
    fgDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    fgDesc.displaySize.width = desc.displayWidth;
    fgDesc.displaySize.height = desc.displayHeight;
    fgDesc.maxRenderSize.width = desc.renderWidth;
    fgDesc.maxRenderSize.height = desc.renderHeight;
    fgDesc.backBufferFormat = desc.backBufferFfxFormat;
    fgDesc.flags = 0u;
    if (desc.displayResolutionMv) {
        fgDesc.flags |= FFX_FRAMEGENERATION_ENABLE_DISPLAY_RESOLUTION_MOTION_VECTORS;
    }
    if (desc.jitterCancellation) {
        fgDesc.flags |= FFX_FRAMEGENERATION_ENABLE_MOTION_VECTORS_JITTER_CANCELLATION;
    }
    if (desc.depthInverted) {
        fgDesc.flags |= FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED;
    }
    if (desc.debugChecking) {
        fgDesc.flags |= FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING;
    }
    ffxCreateContextDescFrameGenerationVersion versionDesc {};
    versionDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    versionDesc.version = FFX_FRAMEGENERATION_VERSION;

    ffxCreateBackendDX12Desc backendDesc {};
    backendDesc.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backendDesc.device = static_cast<ID3D12Device*>(desc.nativeDevice);
    fgDesc.header.pNext = &versionDesc.header;
    versionDesc.header.pNext = &backendDesc.header;
    backendDesc.header.pNext = nullptr;

    std::printf("  fg: ffxCreateContext begin (device=%p display=%ux%u maxRender=%ux%u "
                "fmt=%u flags=0x%X fgVersion=0x%X)\n",
        desc.nativeDevice, fgDesc.displaySize.width, fgDesc.displaySize.height,
        fgDesc.maxRenderSize.width, fgDesc.maxRenderSize.height, fgDesc.backBufferFormat,
        fgDesc.flags, FFX_FRAMEGENERATION_VERSION);
    ffxContext context = nullptr;
    const ffxReturnCode_t rc = runtime->funcs.CreateContext(&context, &fgDesc.header, nullptr);
    m_lastRc = static_cast<uint32_t>(rc);
    std::printf("  fg: ffxCreateContext rc=%u (%s) context=%p\n", static_cast<unsigned>(rc),
        ZupFgReturnCodeName(m_lastRc), static_cast<void*>(context));
    if (rc != FFX_API_RETURN_OK || context == nullptr) {
        FgRuntime* rt = static_cast<FgRuntime*>(m_runtime);
        if (rt != nullptr && rt->module != nullptr) {
            ReleaseFgRuntime(rt); // 真机路径：释放 dll 引用
            m_runtime = nullptr;
        }
        // mock 注入（module==nullptr）：保留测试函数表（同一实例可重试/断言）
        return false;
    }
    m_fgContext = context;
    m_displayWidth = desc.displayWidth;
    m_displayHeight = desc.displayHeight;
    m_renderWidth = desc.renderWidth;
    m_renderHeight = desc.renderHeight;

    // 回调共享态（存续期 = 本实例；析构释放）
    delete static_cast<FgFrameShared*>(m_shared);
    auto* shared = new (std::nothrow) FgFrameShared();
    if (shared == nullptr) {
        return false;
    }
    shared->realPresents = &m_realPresents;
    shared->genPresents = &m_genPresents;
    shared->runtime = runtime;
    shared->fgContext = static_cast<ffxContext*>(&m_fgContext);
    m_shared = shared;
    return true;
}

bool FrameGenerator::CreateFgSwapchainForHwnd(void* hwnd, void* dxgiFactory, void* gameQueue,
    uint32_t width, uint32_t height, uint32_t dxgiFormat, uint32_t bufferCount,
    void** outWrappedSwapchain, void** outFrameLatencyWaitable, const void* requestedDesc1,
    const void* requestedFullscreenDesc1)
{
    if (m_fgContext == nullptr) {
        std::printf("  fg: CreateFgSwapchainForHwnd FAILED (FG context not created)\n");
        return false;
    }
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime == nullptr || runtime->funcs.CreateContext == nullptr) {
        return false;
    }
    DXGI_SWAP_CHAIN_DESC1 scd {};
    if (requestedDesc1 != nullptr) {
        // A-3（TASK-R10-05）：宿主字段全保留。历史缺陷（R10-02 A-3，位置经审计
        // 验收更正到本文件——不是 zfg_probe_wrap.cpp）：自造 desc 把宿主请求的
        // BufferUsage/SwapEffect/Flags/Scaling/AlphaMode/SampleDesc 全部静默丢弃，
        // 实测 Unity 请求 usage=0x30 effect=3 flags=0x842 被建成 usage=0x20
        // effect=4 flags=0x840。
        scd = *static_cast<const DXGI_SWAP_CHAIN_DESC1*>(requestedDesc1);
    } else {
        // 无宿主 desc（窗口程序 --fg 形态）→ 历史合成值（行为基线，逐位不变）
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    }
    if (scd.SampleDesc.Count == 0u) {
        scd.SampleDesc.Count = 1u; // 防御：0 采样数非法（宿主异常 desc 的兜底；历史默认值）
    }
    // 显式参数为权威值（调用方已校验：尺寸非 0、格式已映射）
    scd.Width = width;
    scd.Height = height;
    scd.Format = static_cast<DXGI_FORMAT>(dxgiFormat);
    scd.BufferCount = bufferCount;
    // 应用侧须自带 FRAME_LATENCY_WAITABLE_OBJECT 旗标——官方代理交换链仅在
    // desc.Flags 命中该位时创建 frame latency 信号量（FrameInterpolationSwapchainDX12
    // 创建路径 `desc->Flags & FRAME_LATENCY_WAITABLE_OBJECT` 判定），否则
    // GetFrameLatencyWaitableObject 返回空句柄、帧起步等待 WAIT_FAILED。
    // A-3：按"强制必需位"处理（|=），宿主其余旗标位保留，不再整字段覆盖。
    scd.Flags |= DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    // desc 链：SwapChainForHwndDX12 → pNext SwapChainVersionDX12（SDK 2.1+ 必填）
    ffxCreateContextDescFrameGenerationSwapChainForHwndDX12 scDesc {};
    scDesc.header.type
        = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12;
    scDesc.hwnd = static_cast<HWND>(hwnd);
    scDesc.desc = &scd;
    // A-3：宿主全屏 desc 原样透传（FFX API 收非 const 指针但只读；历史缺陷是
    // 无条件 nullptr，宿主的全屏语义同样被丢弃）
    scDesc.fullscreenDesc = const_cast<DXGI_SWAP_CHAIN_FULLSCREEN_DESC*>(
        static_cast<const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*>(requestedFullscreenDesc1));
    scDesc.dxgiFactory = static_cast<IDXGIFactory*>(dxgiFactory);
    scDesc.gameQueue = static_cast<ID3D12CommandQueue*>(gameQueue);
    ffxCreateContextDescFrameGenerationSwapChainVersionDX12 scVersion {};
    scVersion.header.type
        = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12;
    scVersion.version = FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION;
    scDesc.header.pNext = &scVersion.header;
    scVersion.header.pNext = nullptr;

    IDXGISwapChain4* wrapped = nullptr;
    scDesc.swapchain = &wrapped;
    std::printf("  fg: swapchain ffxCreateContext begin (hwnd=%p %ux%u buffers=%u fmt=%d)\n",
        hwnd, width, height, bufferCount, static_cast<int>(dxgiFormat));
    const ffxReturnCode_t rc
        = runtime->funcs.CreateContext(&m_swapchainContext, &scDesc.header, nullptr);
    m_lastRc = static_cast<uint32_t>(rc);
    std::printf("  fg: swapchain ffxCreateContext rc=%u (%s) context=%p wrapped=%p\n",
        static_cast<unsigned>(rc), ZupFgReturnCodeName(m_lastRc),
        static_cast<void*>(m_swapchainContext), static_cast<void*>(wrapped));
    if (rc != FFX_API_RETURN_OK || m_swapchainContext == nullptr || wrapped == nullptr) {
        m_swapchainContext = nullptr;
        return false;
    }
    m_wrappedSwapchain = wrapped;
    // 帧起步等待句柄（每次调用返回新句柄；Shutdown 统一 CloseHandle——官方契约；
    // mock 模式无真实交换链，跳过）
    if (runtime->module != nullptr) {
        m_waitable = static_cast<void*>(wrapped->GetFrameLatencyWaitableObject());
    }
    if (outWrappedSwapchain != nullptr) {
        *outWrappedSwapchain = wrapped;
    }
    if (outFrameLatencyWaitable != nullptr) {
        *outFrameLatencyWaitable = m_waitable;
    }
    return true;
}

bool FrameGenerator::ConfigureFrame(void* wrappedSwapchain, uint64_t frameId, bool enabled,
    FgPresentCountFn cb, void* cbUserCtx)
{
    if (m_fgContext == nullptr || m_shared == nullptr) {
        return false;
    }
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime == nullptr || runtime->funcs.Configure == nullptr) {
        return false;
    }
    auto* shared = static_cast<FgFrameShared*>(m_shared);
    shared->countFn = cb;
    shared->countFnUserCtx = cbUserCtx;
    ffxConfigureDescFrameGeneration cfg {};
    cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    cfg.swapChain = wrappedSwapchain;
    cfg.presentCallback = &FgPresentCallbackTrampoline;
    cfg.presentCallbackUserContext = shared;
    cfg.frameGenerationCallback = &FgFrameGenDispatchTrampoline;
    cfg.frameGenerationCallbackUserContext = shared;
    cfg.frameGenerationEnabled = enabled ? true : false;
    cfg.allowAsyncWorkloads = false; // 单队列集成（zrhi 基座仅 DIRECT 队列）
    cfg.HUDLessColor = ffxApiGetResourceDX12(nullptr);
    cfg.flags = 0u;
    cfg.onlyPresentGenerated = false;
    cfg.generationRect.left = 0;
    cfg.generationRect.top = 0;
    cfg.generationRect.width = static_cast<int32_t>(m_displayWidth);  // 全屏（无 letterbox）
    cfg.generationRect.height = static_cast<int32_t>(m_displayHeight);
    cfg.frameID = frameId;
    const ffxReturnCode_t rc = runtime->funcs.Configure(&m_fgContext, &cfg.header);
    m_lastRc = static_cast<uint32_t>(rc);
    m_frameId = frameId;
    if (rc != FFX_API_RETURN_OK) {
        std::printf("  fg: ffxConfigure FAILED (rc=%u %s)\n", static_cast<unsigned>(rc),
            ZupFgReturnCodeName(m_lastRc));
        return false;
    }
    return true;
}

bool FrameGenerator::DispatchPrepare(const FrameGenPrepareArgs& args)
{
    if (m_fgContext == nullptr) {
        return false;
    }
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime == nullptr || runtime->funcs.Dispatch == nullptr || args.nativeCmdList == nullptr
        || args.nativeDepth == nullptr || args.nativeMotionVectors == nullptr) {
        std::printf("  fg: DispatchPrepare FAILED (invalid args)\n");
        return false;
    }
    ffxDispatchDescFrameGenerationPrepareV2 prepare {};
    prepare.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
    prepare.header.pNext = nullptr;
    prepare.frameID = args.frameId;
    prepare.flags = 0u;
    prepare.commandList = args.nativeCmdList;
    prepare.renderSize.width = m_renderWidth;  // Create 时锁定的渲染分辨率
    prepare.renderSize.height = m_renderHeight;
    prepare.jitterOffset.x = args.jitterX;
    prepare.jitterOffset.y = args.jitterY;
    prepare.motionVectorScale.x = args.motionVectorScaleX;
    prepare.motionVectorScale.y = args.motionVectorScaleY;
    prepare.frameTimeDelta = args.frameTimeDeltaMs;
    prepare.reset = args.reset ? true : false;
    prepare.cameraNear = args.cameraNear;
    prepare.cameraFar = args.cameraFar;
    prepare.cameraFovAngleVertical = args.cameraFovY;
    prepare.viewSpaceToMetersFactor = 1.0f;
    prepare.depth = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(args.nativeDepth),
        args.depthState);
    prepare.motionVectors = ffxApiGetResourceDX12(
        static_cast<ID3D12Resource*>(args.nativeMotionVectors), args.mvState);
    std::memcpy(prepare.cameraPosition, args.cameraPosition, sizeof(prepare.cameraPosition));
    std::memcpy(prepare.cameraUp, args.cameraUp, sizeof(prepare.cameraUp));
    std::memcpy(prepare.cameraRight, args.cameraRight, sizeof(prepare.cameraRight));
    std::memcpy(prepare.cameraForward, args.cameraForward, sizeof(prepare.cameraForward));

    const ffxReturnCode_t rc = runtime->funcs.Dispatch(&m_fgContext, &prepare.header);
    m_lastRc = static_cast<uint32_t>(rc);
    if (rc != FFX_API_RETURN_OK) {
        std::printf("  fg: PrepareV2 dispatch FAILED (rc=%u %s)\n", static_cast<unsigned>(rc),
            ZupFgReturnCodeName(m_lastRc));
        return false;
    }
    return true;
}

void FrameGenerator::SetPresentObserver(FgPresentObserveFn fn, void* userCtx)
{
    auto* shared = static_cast<FgFrameShared*>(m_shared);
    if (shared == nullptr) {
        return;
    }
    shared->observeFn = fn;
    shared->observeCtx = userCtx;
}

bool FrameGenerator::PresentFrame(void* wrappedSwapchain, uint32_t syncInterval){
    if (m_swapchainContext == nullptr || wrappedSwapchain == nullptr) {
        return false;
    }
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime != nullptr && runtime->module == nullptr) {
        // mock 注入：无真实交换链可调——记账后返回（状态机路径已由 Configure 覆盖）
        return true;
    }
    auto* swapchain = static_cast<IDXGISwapChain4*>(wrappedSwapchain);
    const HRESULT hr = swapchain->Present(syncInterval, 0u);
    if (FAILED(hr)) {
        std::printf("  fg: Present FAILED (hr=0x%08lX)\n", static_cast<unsigned long>(hr));
        return false;
    }
    return true;
}

void FrameGenerator::WaitForPresents()
{
    if (m_swapchainContext == nullptr) {
        return;
    }
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    if (runtime == nullptr || runtime->funcs.Dispatch == nullptr) {
        return;
    }
    ffxDispatchDescFrameGenerationSwapChainWaitForPresentsDX12 wait {};
    wait.header.type
        = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12;
    const ffxReturnCode_t rc = runtime->funcs.Dispatch(&m_swapchainContext, &wait.header);
    m_lastRc = static_cast<uint32_t>(rc);
    if (rc != FFX_API_RETURN_OK) {
        std::printf("  fg: WaitForPresents rc=%u (%s)\n", static_cast<unsigned>(rc),
            ZupFgReturnCodeName(m_lastRc));
    }
}

void FrameGenerator::Shutdown()
{
    FgRuntime* runtime = static_cast<FgRuntime*>(m_runtime);
    // 官方 Shutdown 契约：先 Configure(enabled=false)（内部 waitForPresents 刷新
    // 插帧/合成 GPU 工作），再销毁 FG 上下文，最后销毁代理交换链上下文。
    if (m_fgContext != nullptr && runtime != nullptr && runtime->funcs.Configure != nullptr) {
        ffxConfigureDescFrameGeneration cfg {};
        cfg.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
        cfg.swapChain = m_wrappedSwapchain;
        cfg.presentCallback = nullptr;
        cfg.presentCallbackUserContext = nullptr;
        cfg.frameGenerationCallback = nullptr;
        cfg.frameGenerationCallbackUserContext = nullptr;
        cfg.frameGenerationEnabled = false;
        cfg.allowAsyncWorkloads = false;
        cfg.HUDLessColor = ffxApiGetResourceDX12(nullptr);
        cfg.flags = 0u;
        cfg.onlyPresentGenerated = false;
        cfg.generationRect.left = 0;
        cfg.generationRect.top = 0;
        cfg.generationRect.width = static_cast<int32_t>(m_displayWidth);
        cfg.generationRect.height = static_cast<int32_t>(m_displayHeight);
        cfg.frameID = m_frameId + 1u; // 严格 +1 契约延续
        const ffxReturnCode_t rc = runtime->funcs.Configure(&m_fgContext, &cfg.header);
        if (rc != FFX_API_RETURN_OK) {
            std::printf("  fg: shutdown Configure(false) rc=%u (%s)\n", static_cast<unsigned>(rc),
                ZupFgReturnCodeName(static_cast<uint32_t>(rc)));
        }
    }
    if (m_fgContext != nullptr && runtime != nullptr
        && runtime->funcs.DestroyContext != nullptr) {
        runtime->funcs.DestroyContext(&m_fgContext, nullptr);
        m_fgContext = nullptr;
    }
    if (m_swapchainContext != nullptr && runtime != nullptr
        && runtime->funcs.DestroyContext != nullptr) {
        runtime->funcs.DestroyContext(&m_swapchainContext, nullptr);
        m_swapchainContext = nullptr;
        m_wrappedSwapchain = nullptr; // 生命周期归代理交换链上下文
    }
    if (m_waitable != nullptr) {
        ::CloseHandle(static_cast<HANDLE>(m_waitable));
        m_waitable = nullptr;
    }
    if (runtime != nullptr && runtime->module != nullptr) {
        ReleaseFgRuntime(runtime); // 真机：dll 引用计数
        m_runtime = nullptr;
    }
    // mock 注入（module==nullptr）：函数表归实例所有、保留——幂等 Shutdown 后
    // 仍可重建上下文（单测重入）；实例析构时统一释放（见 ~FrameGenerator）
}

// =============================================================================
// 单测注入（mock ffx 函数表）
// =============================================================================

FrameGenerator* CreateFrameGeneratorForTests(const void* ffxFuncsTable)
{
    auto* self = new (std::nothrow) FrameGenerator();
    if (self == nullptr) {
        return nullptr;
    }
    auto* mock = new (std::nothrow) FgRuntime();
    if (mock == nullptr) {
        delete self;
        return nullptr;
    }
    mock->refCount = 1u; // 固定计数：ReleaseFgRuntime 对 module==nullptr 无副作用
    if (ffxFuncsTable != nullptr) {
        mock->funcs = *static_cast<const ffxFunctions*>(ffxFuncsTable);
    }
    self->m_runtime = mock;
    return self;
}

} // namespace zr
