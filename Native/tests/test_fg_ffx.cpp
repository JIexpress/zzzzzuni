// =============================================================================
// test_fg_ffx.cpp —— 帧生成模块单测（TASK-B-R8-02 简报第 4 点，doctest）
//
// 覆盖：
//   1. 配置纯函数：ZupFgValidateCreateDesc 尺寸/格式校验矩阵
//   2. 能力枚举映射：ZupFgClassifySupport（provider 数 × 创建返回码 → 结论）
//      + ZupFgReturnCodeName（ffx 全部返回码 → 稳定名）
//   3. 状态映射：ZupFgMapResourceState 与 AMD 官方 ffx_dx12.cpp 映射表逐项对齐
//   4. 帧计数翻倍口径：ZupFgFrameCountsDoubled（OK 行判定矩阵）
//   5. mock ffx 五函数表注入（CreateFrameGeneratorForTests）——FrameGenerator
//      状态机与 desc 构造：Create desc 链（FrameGeneration→Version→BackendDX12）
//      逐字段断言 / 代理交换链 desc 链 / ConfigureFrame 逐帧配置 / PrepareV2
//      dispatch 字段 / frameGenerationCallback 直转发 / present 回调计数（含
//      SDK pacing 线程语义：计数先于合成，资源缺失不丢计数）/ Shutdown 契约
//      顺序（Configure(false) → FG 销毁 → 交换链销毁）/ 拒绝返回码透传
//
// mock 设计：静态 C 函数表（ffxFunctions）按 desc header.type 分派；用例重置
// g_* 计数器。合成路径（CopyResource）为真 D3D12 调用，mock 表不给假命令表——
// 计数语义（先计数后合成）由此可得真断言；合成正确性由 --fg 真机 InfoQueue
// 0 错误覆盖（分工见任务日志）。
// =============================================================================
#include "doctest/doctest.h"

#include "zupscaler/fg_ffx.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstring>
#include <string>

#include <d3d12.h>
#include <wrl/client.h>

// ---- 第三方头（AMD FidelityFX SDK v2.3.0；/WX 豁免隔离段，同 fg_ffx.cpp）---------
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include "ffx_api.h"
#include "ffx_api_loader.h"
#include "ffx_framegeneration.h"
#include "dx12/ffx_api_dx12.h"
#include "dx12/ffx_api_framegeneration_dx12.h"
#pragma warning(pop)

namespace {

using zr::FrameGenSupport;

// ---- mock ffx 函数表（按 desc header.type 分派；计数器逐用例重置）---------------
struct MockState {
    // Query（GET_VERSIONS）：可配置 provider 数与名字
    uint32_t providerCount = 2u;
    int queryCalls = 0;
    uint64_t queriedCreateDescType = 0u;

    // CreateContext：可注入拒绝返回码；记录 desc 链供逐字段断言
    uint32_t createFgRc = FFX_API_RETURN_OK;
    uint32_t createSwapchainRc = FFX_API_RETURN_OK;
    int createFgCalls = 0;
    int createSwapchainCalls = 0;
    uint64_t lastFgFlags = 0u;
    uint32_t lastDisplayW = 0u, lastDisplayH = 0u;
    uint32_t lastMaxRenderW = 0u, lastMaxRenderH = 0u;
    uint32_t lastBackBufferFormat = 0xFFFFFFFFu;
    bool versionDescChained = false;
    uint32_t chainedVersionValue = 0u;
    bool backendDescChained = false;
    void* chainedBackendDevice = nullptr;
    // 交换链上下文
    void* scHwnd = nullptr;
    void* scFactory = nullptr;
    void* scQueue = nullptr;
    uint32_t scWidth = 0u, scHeight = 0u, scFormat = 0u, scBufferCount = 0u;
    bool scVersionChained = false;
    uint32_t scChainedVersionValue = 0u;
    void* lastFgContext = nullptr;
    void* lastSwapchainContext = nullptr;
    void* outWrappedSwapchain = nullptr;

    // Configure
    int configureCalls = 0;
    uint64_t lastConfigureFrameId = 0u;
    bool lastConfigureEnabled = false;
    void* lastConfigureSwapchain = nullptr;
    void* lastPresentCallback = nullptr;
    void* lastPresentCallbackCtx = nullptr;
    void* lastFgGenCallback = nullptr;
    uint32_t configureRc = FFX_API_RETURN_OK;
    int shutdownDisableConfigureCalls = 0; // enabled=false 的 Configure（Shutdown 契约）

    // Dispatch
    int prepareCalls = 0;
    int waitCalls = 0;
    int fgDispatchCalls = 0; // frameGenerationCallback 直转发的插帧 dispatch
    uint32_t dispatchRc = FFX_API_RETURN_OK;
    ffxDispatchDescFrameGenerationPrepareV2 lastPrepare {};
    uint64_t lastDispatchFrameId = 0u;

    // Destroy
    int destroyCalls = 0;
    std::string destroyOrder; // "fg" / "swapchain"（Shutdown 契约顺序断言）

    void Reset()
    {
        *this = MockState {};
    }
};

MockState g_mock;

ffxReturnCode_t MockQuery(ffxContext*, ffxQueryDescHeader* desc)
{
    g_mock.queryCalls += 1;
    if (desc->type == FFX_API_QUERY_DESC_TYPE_GET_VERSIONS) {
        auto* q = reinterpret_cast<ffxQueryDescGetVersions*>(desc);
        g_mock.queriedCreateDescType = q->createDescType;
        if (q->createDescType != FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION) {
            return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
        }
        if (q->outputCount == nullptr) {
            return FFX_API_RETURN_ERROR_PARAMETER;
        }
        if (*q->outputCount == 0u) {
            *q->outputCount = g_mock.providerCount; // 第一遍：报告数量
            return FFX_API_RETURN_OK;
        }
        static const char* kNames[2] = {"4.0.1", "3.1.5"};
        for (uint64_t i = 0; i < *q->outputCount && i < g_mock.providerCount; ++i) {
            if (q->versionIds != nullptr) {
                q->versionIds[i] = 0xABCD0000u + i;
            }
            if (q->versionNames != nullptr) {
                q->versionNames[i] = kNames[i % 2u];
            }
        }
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t MockCreateContext(ffxContext* context, ffxCreateContextDescHeader* desc,
    const ffxAllocationCallbacks*)
{
    if (context == nullptr || desc == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    if (desc->type == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION) {
        g_mock.createFgCalls += 1;
        if (g_mock.createFgRc != FFX_API_RETURN_OK) {
            return g_mock.createFgRc; // 拒绝路径：*context 不动
        }
        auto* fg = reinterpret_cast<ffxCreateContextDescFrameGeneration*>(desc);
        g_mock.lastFgFlags = fg->flags;
        g_mock.lastDisplayW = fg->displaySize.width;
        g_mock.lastDisplayH = fg->displaySize.height;
        g_mock.lastMaxRenderW = fg->maxRenderSize.width;
        g_mock.lastMaxRenderH = fg->maxRenderSize.height;
        g_mock.lastBackBufferFormat = fg->backBufferFormat;
        // pNext 链：Version（SDK 2.1+ 必填）→ BackendDX12
        if (fg->header.pNext != nullptr
            && fg->header.pNext->type
                == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION) {
            g_mock.versionDescChained = true;
            auto* ver = reinterpret_cast<ffxCreateContextDescFrameGenerationVersion*>(
                fg->header.pNext);
            g_mock.chainedVersionValue = ver->version;
            if (ver->header.pNext != nullptr
                && ver->header.pNext->type == FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12) {
                g_mock.backendDescChained = true;
                auto* be = reinterpret_cast<ffxCreateBackendDX12Desc*>(ver->header.pNext);
                g_mock.chainedBackendDevice = be->device;
            }
        }
        *context = reinterpret_cast<ffxContext>(static_cast<uintptr_t>(0xF600D000ull));
        g_mock.lastFgContext = *context;
        return FFX_API_RETURN_OK;
    }
    if (desc->type
        == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12) {
        g_mock.createSwapchainCalls += 1;
        if (g_mock.createSwapchainRc != FFX_API_RETURN_OK) {
            return g_mock.createSwapchainRc;
        }
        auto* sc = reinterpret_cast<ffxCreateContextDescFrameGenerationSwapChainForHwndDX12*>(
            desc);
        g_mock.scHwnd = sc->hwnd;
        g_mock.scFactory = sc->dxgiFactory;
        g_mock.scQueue = sc->gameQueue;
        g_mock.scWidth = sc->desc->Width;
        g_mock.scHeight = sc->desc->Height;
        g_mock.scFormat = static_cast<uint32_t>(sc->desc->Format);
        g_mock.scBufferCount = sc->desc->BufferCount;
        if (sc->header.pNext != nullptr
            && sc->header.pNext->type
                == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_VERSION_DX12) {
            g_mock.scVersionChained = true;
            auto* ver = reinterpret_cast<ffxCreateContextDescFrameGenerationSwapChainVersionDX12*>(
                sc->header.pNext);
            g_mock.scChainedVersionValue = ver->version;
        }
        *context = reinterpret_cast<ffxContext>(static_cast<uintptr_t>(0xF600D100ull));
        g_mock.lastSwapchainContext = *context;
        if (sc->swapchain != nullptr) {
            // 代理交换链哨兵（mock 无真实 COM 对象；PresentFrame 在 mock 模式记账）
            *sc->swapchain = reinterpret_cast<IDXGISwapChain4*>(
                static_cast<uintptr_t>(0xF600DBADll));
            g_mock.outWrappedSwapchain = *sc->swapchain;
        }
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t MockConfigure(ffxContext* context, const ffxConfigureDescHeader* desc)
{
    if (context == nullptr || desc == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    if (desc->type != FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION) {
        return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
    }
    g_mock.configureCalls += 1;
    auto* cfg = reinterpret_cast<const ffxConfigureDescFrameGeneration*>(desc);
    g_mock.lastConfigureFrameId = cfg->frameID;
    g_mock.lastConfigureEnabled = cfg->frameGenerationEnabled;
    g_mock.lastConfigureSwapchain = cfg->swapChain;
    g_mock.lastPresentCallback = reinterpret_cast<void*>(cfg->presentCallback);
    g_mock.lastPresentCallbackCtx = cfg->presentCallbackUserContext;
    g_mock.lastFgGenCallback = reinterpret_cast<void*>(cfg->frameGenerationCallback);
    if (!cfg->frameGenerationEnabled) {
        g_mock.shutdownDisableConfigureCalls += 1;
    }
    return g_mock.configureRc;
}

ffxReturnCode_t MockDispatch(ffxContext*, const ffxDispatchDescHeader* desc)
{
    if (desc == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    if (desc->type == FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2) {
        g_mock.prepareCalls += 1;
        g_mock.lastPrepare
            = *reinterpret_cast<const ffxDispatchDescFrameGenerationPrepareV2*>(desc);
        g_mock.lastDispatchFrameId = g_mock.lastPrepare.frameID;
        return g_mock.dispatchRc;
    }
    if (desc->type == FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION) {
        g_mock.fgDispatchCalls += 1; // frameGenerationCallback 直转发
        return g_mock.dispatchRc;
    }
    if (desc->type
        == FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_WAIT_FOR_PRESENTS_DX12) {
        g_mock.waitCalls += 1;
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t MockDestroyContext(ffxContext* context, const ffxAllocationCallbacks*)
{
    if (context == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    g_mock.destroyCalls += 1;
    if (*context == g_mock.lastFgContext) {
        g_mock.destroyOrder += "fg;";
    } else if (*context == g_mock.lastSwapchainContext) {
        g_mock.destroyOrder += "swapchain;";
    }
    *context = nullptr;
    return FFX_API_RETURN_OK;
}

ffxFunctions MockTable()
{
    ffxFunctions t {};
    t.Query = &MockQuery;
    t.CreateContext = &MockCreateContext;
    t.Configure = &MockConfigure;
    t.Dispatch = &MockDispatch;
    t.DestroyContext = &MockDestroyContext;
    return t;
}

/// 计数出口（present 回调 → 窗口程序的观测点）
struct CountSink {
    int real = 0;
    int gen = 0;
};

void CountSinkFn(bool isGeneratedFrame, void* userCtx)
{
    auto* sink = static_cast<CountSink*>(userCtx);
    if (isGeneratedFrame) {
        sink->gen += 1;
    } else {
        sink->real += 1;
    }
}

/// WARP 设备（软件适配器；进程内单例，同 test_upscaler D4 约定：不主动销毁）。
/// 用途：PrepareV2 的深度/MV 资源经官方 ffxApiGetResourceDX12 包装（内部 GetDesc
/// 真调用）——mock 表只拦 ffx 五入口，资源须为真实 D3D12 对象。
ID3D12Device* AcquireWarpDevice()
{
    static ID3D12Device* device = nullptr;
    static bool tried = false;
    if (tried) {
        return device;
    }
    tried = true;
    IDXGIFactory4* factory = nullptr;
    if (FAILED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || factory == nullptr) {
        return nullptr;
    }
    IDXGIAdapter* warp = nullptr;
    if (SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) && warp != nullptr) {
        ::D3D12CreateDevice(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
        warp->Release();
    }
    factory->Release();
    return device;
}

/// 造一份合法 Create desc（用例局部改字段）
zr::FrameGenCreateDesc ValidDesc()
{
    zr::FrameGenCreateDesc d {};
    d.displayWidth = 640u;
    d.displayHeight = 480u;
    d.renderWidth = 640u;
    d.renderHeight = 480u;
    d.backBufferFfxFormat = ffxApiGetSurfaceFormatDX12(DXGI_FORMAT_B8G8R8A8_UNORM);
    d.nativeDevice = reinterpret_cast<void*>(static_cast<uintptr_t>(0xDE1CEull));
    return d;
}

/// Prepare 参数造法二选一：真 WARP 资源（可断言包装字段）或 null（护栏路径）
zr::FrameGenPrepareArgs ValidPrepare(uint64_t frameId)
{
    zr::FrameGenPrepareArgs p {};
    p.nativeCmdList = reinterpret_cast<void*>(static_cast<uintptr_t>(0xC0DE1ull));
    p.nativeDepth = reinterpret_cast<void*>(static_cast<uintptr_t>(0xDEE7ull));
    p.nativeMotionVectors = reinterpret_cast<void*>(static_cast<uintptr_t>(0xDA7Aull));
    p.depthState = FFX_API_RESOURCE_STATE_COMPUTE_READ;
    p.mvState = FFX_API_RESOURCE_STATE_COMPUTE_READ;
    p.jitterX = 0.125f;
    p.jitterY = -0.375f;
    p.motionVectorScaleX = 320.0f;
    p.motionVectorScaleY = -240.0f;
    p.frameTimeDeltaMs = 16.6f;
    p.cameraFovY = 1.047f;
    p.cameraNear = 0.3f;
    p.cameraFar = 60.0f;
    p.reset = false;
    p.frameId = frameId;
    return p;
}

/// 真 WARP 资源对（PrepareV2 包装的 GetDesc 需要真实对象；4x4 最小尺寸）
bool MakeWarpDepthMv(ID3D12Device* warp, ID3D12Resource** outDepth, ID3D12Resource** outMv)
{
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC dd {};
    dd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    dd.Width = 4u;
    dd.Height = 4u;
    dd.DepthOrArraySize = 1u;
    dd.MipLevels = 1u;
    dd.Format = DXGI_FORMAT_D32_FLOAT;
    dd.SampleDesc.Count = 1u;
    dd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
    if (FAILED(warp->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &dd,
            D3D12_RESOURCE_STATE_DEPTH_WRITE, nullptr, IID_PPV_ARGS(outDepth)))) {
        return false;
    }
    dd.Format = DXGI_FORMAT_R16G16_FLOAT;
    dd.Flags = D3D12_RESOURCE_FLAG_NONE;
    if (FAILED(warp->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &dd,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(outMv)))) {
        return false;
    }
    return *outDepth != nullptr && *outMv != nullptr;
}

} // namespace

// =============================================================================
// 纯函数矩阵
// =============================================================================

TEST_CASE("fg: return code names are stable")
{
    using zr::ZupFgReturnCodeName;
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_OK), "OK") == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_ERROR), "ERROR") == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE),
              "ERROR_UNKNOWN_DESCTYPE")
        == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_ERROR_RUNTIME_ERROR),
              "ERROR_RUNTIME_ERROR")
        == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_NO_PROVIDER), "NO_PROVIDER") == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_ERROR_MEMORY), "ERROR_MEMORY") == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_ERROR_PARAMETER),
              "ERROR_PARAMETER")
        == 0);
    CHECK(std::strcmp(ZupFgReturnCodeName(FFX_API_RETURN_PROVIDER_NO_SUPPORT_NEW_DESCTYPE),
              "PROVIDER_NO_SUPPORT_NEW_DESCTYPE")
        == 0);
    // 未定义码 → 可辨前缀（证据行可读，不静默）
    CHECK(std::strncmp(ZupFgReturnCodeName(42u), "UNKNOWN_", 8) == 0);
}

TEST_CASE("fg: resource state mapping matches official D3D12 table")
{
    using zr::ZupFgMapResourceState;
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_GENERIC_READ)
        == D3D12_RESOURCE_STATE_GENERIC_READ);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_UNORDERED_ACCESS)
        == D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ)
        == (D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE
               | D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE));
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_COMPUTE_READ)
        == D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_PIXEL_READ)
        == D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_COPY_SRC)
        == D3D12_RESOURCE_STATE_COPY_SOURCE);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_COPY_DEST)
        == D3D12_RESOURCE_STATE_COPY_DEST);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_INDIRECT_ARGUMENT)
        == D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_PRESENT)
        == D3D12_RESOURCE_STATE_PRESENT);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_COMMON) == D3D12_RESOURCE_STATE_COMMON);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_RENDER_TARGET)
        == D3D12_RESOURCE_STATE_RENDER_TARGET);
    CHECK(ZupFgMapResourceState(FFX_API_RESOURCE_STATE_DEPTH_ATTACHMENT)
        == D3D12_RESOURCE_STATE_DEPTH_WRITE);
    // 未知值 → COMMON（present 回调对未知状态的兜底）
    CHECK(ZupFgMapResourceState(0xDEADBEEFu) == D3D12_RESOURCE_STATE_COMMON);
}

TEST_CASE("fg: create desc validation")
{
    using zr::ZupFgValidateCreateDesc;
    const uint32_t fmt = ffxApiGetSurfaceFormatDX12(DXGI_FORMAT_B8G8R8A8_UNORM);
    CHECK(fmt == FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM); // 映射 sanity（防枚举漂移）
    CHECK(ZupFgValidateCreateDesc(640u, 480u, 640u, 480u, fmt));
    CHECK(ZupFgValidateCreateDesc(1280u, 720u, 960u, 540u, fmt)); // render < display 合法
    // 任一维 0 → false
    CHECK_FALSE(ZupFgValidateCreateDesc(0u, 480u, 640u, 480u, fmt));
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 0u, 640u, 480u, fmt));
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 480u, 0u, 480u, fmt));
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 480u, 640u, 0u, fmt));
    // render > display → false（FG 输入色契约 = displaySize，本集成不放大）
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 480u, 960u, 480u, fmt));
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 480u, 640u, 540u, fmt));
    // 格式 UNKNOWN（0）→ false
    CHECK_FALSE(ZupFgValidateCreateDesc(640u, 480u, 640u, 480u, FFX_API_SURFACE_FORMAT_UNKNOWN));
}

TEST_CASE("fg: support classification")
{
    using zr::ZupFgClassifySupport;
    CHECK(ZupFgClassifySupport(0u, FFX_API_RETURN_OK) == FrameGenSupport::NoProvider);
    CHECK(ZupFgClassifySupport(0u, FFX_API_RETURN_NO_PROVIDER) == FrameGenSupport::NoProvider);
    CHECK(ZupFgClassifySupport(2u, FFX_API_RETURN_OK) == FrameGenSupport::Available);
    CHECK(ZupFgClassifySupport(2u, FFX_API_RETURN_ERROR_RUNTIME_ERROR)
        == FrameGenSupport::RejectedByRuntime);
    CHECK(ZupFgClassifySupport(2u, FFX_API_RETURN_NO_PROVIDER)
        == FrameGenSupport::RejectedByRuntime);
    CHECK(ZupFgClassifySupport(1u, 7u) == FrameGenSupport::RejectedByRuntime);
}

TEST_CASE("fg: frame count doubling criterion")
{
    using zr::ZupFgFrameCountsDoubled;
    CHECK(ZupFgFrameCountsDoubled(60u, 60u)); // 恰好翻倍（尾帧同 tick）
    CHECK(ZupFgFrameCountsDoubled(60u, 59u)); // 尾帧差 1（pacing 容忍）
    CHECK(ZupFgFrameCountsDoubled(1u, 1u));
    CHECK_FALSE(ZupFgFrameCountsDoubled(60u, 0u));  // 未产出插帧 → 不算 OK
    CHECK_FALSE(ZupFgFrameCountsDoubled(60u, 30u)); // 半途失效
    CHECK_FALSE(ZupFgFrameCountsDoubled(60u, 61u)); // 多于真帧：计数异常
    CHECK_FALSE(ZupFgFrameCountsDoubled(0u, 0u));   // 无真帧无意义
}

// =============================================================================
// mock 注入的 FrameGenerator 状态机（简报"mock 后端的 Chain 集成点"）
// =============================================================================

TEST_CASE("fg: create builds official desc chain and honors rejection")
{
    g_mock.Reset();
    ffxFunctions table = MockTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    CHECK_FALSE(fg->IsCreated());

    // ---- 创建：desc 链逐字段断言 ----------------------------------------------
    zr::FrameGenCreateDesc desc = ValidDesc();
    CHECK(fg->Create(desc));
    CHECK(fg->IsCreated());
    CHECK(fg->LastReturnCode() == FFX_API_RETURN_OK);
    CHECK(g_mock.createFgCalls == 1);
    CHECK(g_mock.lastDisplayW == 640u);
    CHECK(g_mock.lastDisplayH == 480u);
    CHECK(g_mock.lastMaxRenderW == 640u);
    CHECK(g_mock.lastMaxRenderH == 480u);
    CHECK(g_mock.lastBackBufferFormat == FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM);
    CHECK(g_mock.versionDescChained);
    CHECK(g_mock.chainedVersionValue == FFX_FRAMEGENERATION_VERSION); // SDK 2.1+ 必填
    CHECK(g_mock.backendDescChained);
    CHECK(g_mock.chainedBackendDevice == desc.nativeDevice);
    // 默认旗标：display 分辨率 MV + 调试检查；**jitter 抵消默认关闭**
    // （TASK-B-R9-03 F2：只有用含 jitter 的 mvp 算 MV 的调用方才置 true；
    //   Unity/URP 用 _NonJitteredViewProjMatrix 算 MV，置 true 会反解不存在的抖动）
    CHECK((g_mock.lastFgFlags & FFX_FRAMEGENERATION_ENABLE_DISPLAY_RESOLUTION_MOTION_VECTORS)
        != 0u);
    CHECK((g_mock.lastFgFlags & FFX_FRAMEGENERATION_ENABLE_MOTION_VECTORS_JITTER_CANCELLATION)
        == 0u);
    CHECK((g_mock.lastFgFlags & FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING) != 0u);
    CHECK((g_mock.lastFgFlags & FFX_FRAMEGENERATION_ENABLE_ASYNC_WORKLOAD_SUPPORT)
        == 0u); // 单队列集成不开 async

    // 显式置 true（窗口程序 --fg 的既有口径）→ 旗标必须真的打开（两个方向都测）
    fg->Shutdown();
    g_mock.Reset();
    desc.jitterCancellation = true;
    CHECK(fg->Create(desc));
    CHECK((g_mock.lastFgFlags & FFX_FRAMEGENERATION_ENABLE_MOTION_VECTORS_JITTER_CANCELLATION)
        != 0u);

    // ---- 拒绝路径：返回码透传（能力门控事实）-----------------------------------
    fg->Shutdown();
    g_mock.Reset();
    g_mock.createFgRc = FFX_API_RETURN_NO_PROVIDER; // 模拟 iGPU 被 FG 运行时拒绝
    CHECK_FALSE(fg->Create(desc));
    CHECK_FALSE(fg->IsCreated());
    CHECK(fg->LastReturnCode() == FFX_API_RETURN_NO_PROVIDER);
    CHECK(g_mock.createFgCalls == 1); // 拒绝发生在 ffxCreateContext——证据确切口
    delete fg;
}

TEST_CASE("fg: swapchain context + configure + prepare + present state machine")
{
    g_mock.Reset();
    ffxFunctions table = MockTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidDesc()));

    // 未建交换链上下文时 Prepare/Present 拒绝（状态机护栏）
    CHECK_FALSE(fg->PresentFrame(reinterpret_cast<void*>(0x1u), 1u));

    // ---- 代理交换链（ForHwnd desc 断言）----------------------------------------
    void* wrapped = nullptr;
    void* waitable = nullptr;
    const uintptr_t hwndSentinel = static_cast<uintptr_t>(0x11F00Dull); // 假 HWND（不触 GUI）
    CHECK(fg->CreateFgSwapchainForHwnd(reinterpret_cast<void*>(hwndSentinel),
        reinterpret_cast<void*>(static_cast<uintptr_t>(0xFA7ull)),
        reinterpret_cast<void*>(static_cast<uintptr_t>(0xEEEEull)), 640u, 480u,
        DXGI_FORMAT_B8G8R8A8_UNORM, 3u, &wrapped, &waitable));
    CHECK(fg->HasSwapchainContext());
    CHECK(wrapped == g_mock.outWrappedSwapchain); // mock 回填的代理交换链哨兵
    CHECK(waitable == nullptr);                   // mock 无真实交换链 → 无 waitable
    CHECK(g_mock.scHwnd == reinterpret_cast<void*>(hwndSentinel));
    CHECK(g_mock.scFactory == reinterpret_cast<void*>(static_cast<uintptr_t>(0xFA7ull)));
    CHECK(g_mock.scQueue == reinterpret_cast<void*>(static_cast<uintptr_t>(0xEEEEull)));
    CHECK(g_mock.scWidth == 640u);
    CHECK(g_mock.scHeight == 480u);
    CHECK(g_mock.scFormat == DXGI_FORMAT_B8G8R8A8_UNORM);
    CHECK(g_mock.scBufferCount == 3u);
    CHECK(g_mock.scVersionChained);
    CHECK(g_mock.scChainedVersionValue == FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION);

    // ---- 每帧配置（官方契约：frameID 严格 +1；回调齐备）--------------------------
    CountSink sink;
    const uintptr_t sentinelSwapchain = reinterpret_cast<uintptr_t>(wrapped);
    CHECK(fg->ConfigureFrame(wrapped, 7u, /*enabled=*/true, &CountSinkFn, &sink));
    CHECK(g_mock.configureCalls == 1);
    CHECK(g_mock.lastConfigureFrameId == 7u);
    CHECK(g_mock.lastConfigureEnabled);
    CHECK(g_mock.lastConfigureSwapchain == reinterpret_cast<void*>(sentinelSwapchain));
    CHECK(g_mock.lastPresentCallback != nullptr); // 蹦床（合成+计数）
    CHECK(g_mock.lastPresentCallbackCtx != nullptr);
    CHECK(g_mock.lastFgGenCallback != nullptr); // 蹦床（直转发插帧 dispatch）

    // ---- PrepareV2 dispatch 逐字段断言（深度/MV 用真 WARP 资源——官方
    //      ffxApiGetResourceDX12 包装内部真调 GetDesc，哨兵指针会段错误）----------
    ID3D12Device* warp = AcquireWarpDevice();
    REQUIRE(warp != nullptr);
    Microsoft::WRL::ComPtr<ID3D12Resource> depthTex;
    Microsoft::WRL::ComPtr<ID3D12Resource> mvTex;
    REQUIRE(MakeWarpDepthMv(warp, depthTex.GetAddressOf(), mvTex.GetAddressOf()));
    zr::FrameGenPrepareArgs prepare = ValidPrepare(7u);
    prepare.nativeDepth = depthTex.Get();
    prepare.nativeMotionVectors = mvTex.Get();
    CHECK(fg->DispatchPrepare(prepare));
    CHECK(g_mock.prepareCalls == 1);
    CHECK(g_mock.lastPrepare.frameID == 7u); // 与 Configure 的 frameId 一致
    CHECK(g_mock.lastPrepare.renderSize.width == 640u);
    CHECK(g_mock.lastPrepare.renderSize.height == 480u);
    CHECK(g_mock.lastPrepare.jitterOffset.x == doctest::Approx(0.125f));
    CHECK(g_mock.lastPrepare.jitterOffset.y == doctest::Approx(-0.375f));
    CHECK(g_mock.lastPrepare.motionVectorScale.x == doctest::Approx(320.0f));
    CHECK(g_mock.lastPrepare.motionVectorScale.y == doctest::Approx(-240.0f));
    CHECK(g_mock.lastPrepare.frameTimeDelta == doctest::Approx(16.6f));
    CHECK(g_mock.lastPrepare.cameraFovAngleVertical == doctest::Approx(1.047f));
    CHECK(g_mock.lastPrepare.cameraNear == doctest::Approx(0.3f));
    CHECK(g_mock.lastPrepare.cameraFar == doctest::Approx(60.0f));
    CHECK_FALSE(g_mock.lastPrepare.reset);
    CHECK(g_mock.lastPrepare.commandList
        == reinterpret_cast<void*>(static_cast<uintptr_t>(0xC0DE1ull)));
    CHECK(g_mock.lastPrepare.depth.resource == depthTex.Get()); // 真资源指针透传
    CHECK(g_mock.lastPrepare.depth.state == FFX_API_RESOURCE_STATE_COMPUTE_READ);
    // 官方包装产物 sanity：D32 → R32_FLOAT/DEPTHTARGET；尺寸取自 GetDesc
    CHECK(g_mock.lastPrepare.depth.description.format == FFX_API_SURFACE_FORMAT_R32_FLOAT);
    CHECK(g_mock.lastPrepare.depth.description.usage == FFX_API_RESOURCE_USAGE_DEPTHTARGET);
    CHECK(g_mock.lastPrepare.depth.description.width == 4u);
    CHECK(g_mock.lastPrepare.motionVectors.resource == mvTex.Get());
    CHECK(g_mock.lastPrepare.motionVectors.state == FFX_API_RESOURCE_STATE_COMPUTE_READ);
    CHECK(g_mock.lastPrepare.motionVectors.description.format
        == FFX_API_SURFACE_FORMAT_R16G16_FLOAT);
    CHECK(g_mock.lastPrepare.motionVectors.description.height == 4u);
    CHECK(g_mock.lastPrepare.cameraUp[1] == doctest::Approx(1.0f)); // 默认位姿（原点朝 +Z）
    CHECK(g_mock.lastPrepare.cameraForward[2] == doctest::Approx(1.0f));

    // ---- present 包装（mock 记账：无真实交换链 COM 调用）--------------------------
    CHECK(fg->PresentFrame(wrapped, 1u));
    fg->WaitForPresents(); // mock 记账 waitCalls（无返回值）
    CHECK(g_mock.waitCalls == 1);

    // ---- present 回调计数（SDK pacing 线程语义：计数先于合成；资源缺失不丢计数）----
    auto* presentCb = reinterpret_cast<ffxReturnCode_t (*)(ffxCallbackDescFrameGenerationPresent*,
        void*)>(g_mock.lastPresentCallback);
    auto* sharedCtx = g_mock.lastPresentCallbackCtx;
    REQUIRE(presentCb != nullptr);
    REQUIRE(sharedCtx != nullptr);
    ffxCallbackDescFrameGenerationPresent realFrame {};
    realFrame.isGeneratedFrame = false;
    realFrame.commandList = nullptr; // mock 无真实命令表 → 合成段返回参数错误
    realFrame.currentBackBuffer.resource = reinterpret_cast<void*>(0x11ull);
    realFrame.outputSwapChainBuffer.resource = reinterpret_cast<void*>(0x22ull);
    ffxCallbackDescFrameGenerationPresent genFrame = realFrame;
    genFrame.isGeneratedFrame = true;
    for (int i = 0; i < 3; ++i) {
        CHECK(presentCb(&realFrame, sharedCtx) != FFX_API_RETURN_OK); // 合成不可行=显式错误
    }
    for (int i = 0; i < 2; ++i) {
        CHECK(presentCb(&genFrame, sharedCtx) != FFX_API_RETURN_OK);
    }
    CHECK(presentCb(nullptr, sharedCtx) == FFX_API_RETURN_ERROR_PARAMETER); // 空参数护栏
    CHECK(fg->RealPresents() == 3u); // 计数不受合成失败/空参数影响（真帧/插帧区分准确）
    CHECK(fg->GeneratedPresents() == 2u);
    CHECK(zr::ZupFgFrameCountsDoubled(fg->RealPresents(), fg->GeneratedPresents()));
    CHECK(sink.real == 3); // 计数出口（窗口程序观测点）与内部计数一致
    CHECK(sink.gen == 2);

    // ---- frameGenerationCallback 直转发（官方推荐路径）---------------------------
    auto* fgCb = reinterpret_cast<ffxReturnCode_t (*)(ffxDispatchDescFrameGeneration*, void*)>(
        g_mock.lastFgGenCallback);
    REQUIRE(fgCb != nullptr);
    ffxDispatchDescFrameGeneration dispatch {};
    dispatch.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
    dispatch.numGeneratedFrames = 1u;
    CHECK(fgCb(&dispatch, sharedCtx) == FFX_API_RETURN_OK);
    CHECK(g_mock.fgDispatchCalls == 1);

    // ---- Shutdown 契约顺序：Configure(false) → FG 销毁 → 交换链销毁 ---------------
    fg->Shutdown();
    CHECK_FALSE(fg->IsCreated());
    CHECK_FALSE(fg->HasSwapchainContext());
    CHECK(g_mock.shutdownDisableConfigureCalls == 1); // enabled=false（刷新插帧 GPU 工作）
    CHECK(g_mock.lastConfigureEnabled == false);
    CHECK(g_mock.destroyCalls == 2);
    CHECK(g_mock.destroyOrder == "fg;swapchain;"); // 官方顺序（防 OBJECT_DELETED_WHILE_STILL_IN_USE）
    fg->Shutdown(); // 幂等
    CHECK(g_mock.destroyCalls == 2);
    delete fg;
}

TEST_CASE("fg: prepare and configure rejections propagate rc")
{
    g_mock.Reset();
    ffxFunctions table = MockTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidDesc()));
    void* wrapped = nullptr;
    void* waitable = nullptr;
    REQUIRE(fg->CreateFgSwapchainForHwnd(nullptr, nullptr, nullptr, 640u, 480u,
        DXGI_FORMAT_B8G8R8A8_UNORM, 3u, &wrapped, &waitable));

    // 真 WARP 资源（官方包装 GetDesc 需要；返回码注入不影响包装路径）
    ID3D12Device* warp = AcquireWarpDevice();
    REQUIRE(warp != nullptr);
    Microsoft::WRL::ComPtr<ID3D12Resource> depthTex;
    Microsoft::WRL::ComPtr<ID3D12Resource> mvTex;
    REQUIRE(MakeWarpDepthMv(warp, depthTex.GetAddressOf(), mvTex.GetAddressOf()));
    zr::FrameGenPrepareArgs prepare = ValidPrepare(1u);
    prepare.nativeDepth = depthTex.Get();
    prepare.nativeMotionVectors = mvTex.Get();

    g_mock.dispatchRc = FFX_API_RETURN_ERROR_PARAMETER; // 模拟运行时拒绝 Prepare
    CHECK_FALSE(fg->DispatchPrepare(prepare));
    CHECK(fg->LastReturnCode() == FFX_API_RETURN_ERROR_PARAMETER);

    g_mock.configureRc = FFX_API_RETURN_ERROR_RUNTIME_ERROR; // 模拟 Configure 失败
    CHECK_FALSE(fg->ConfigureFrame(wrapped, 1u, true, nullptr, nullptr));
    CHECK(fg->LastReturnCode() == FFX_API_RETURN_ERROR_RUNTIME_ERROR);
    delete fg;
}

TEST_CASE("fg: configure/prepare guards refuse missing state")
{
    g_mock.Reset();
    ffxFunctions table = MockTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    // 未 Create：Configure/Prepare/Present 全拒绝（状态机护栏）
    CHECK_FALSE(fg->ConfigureFrame(nullptr, 1u, true, nullptr, nullptr));
    CHECK_FALSE(fg->DispatchPrepare(ValidPrepare(1u)));
    CHECK_FALSE(fg->PresentFrame(nullptr, 1u));
    // Prepare 缺资源参数：拒绝（句柄+返回码约定，不跨 ABI 抛异常）
    REQUIRE(fg->Create(ValidDesc()));
    zr::FrameGenPrepareArgs noMv = ValidPrepare(1u);
    noMv.nativeMotionVectors = nullptr;
    CHECK_FALSE(fg->DispatchPrepare(noMv));
    zr::FrameGenPrepareArgs noCmd = ValidPrepare(1u);
    noCmd.nativeCmdList = nullptr;
    CHECK_FALSE(fg->DispatchPrepare(noCmd));
    delete fg;
}

TEST_CASE("fg: swapchain context creation failure keeps fg context usable")
{
    g_mock.Reset();
    ffxFunctions table = MockTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidDesc()));
    g_mock.createSwapchainRc = FFX_API_RETURN_ERROR_RUNTIME_ERROR;
    void* wrapped = nullptr;
    void* waitable = nullptr;
    CHECK_FALSE(fg->CreateFgSwapchainForHwnd(nullptr, nullptr, nullptr, 640u, 480u,
        DXGI_FORMAT_B8G8R8A8_UNORM, 3u, &wrapped, &waitable));
    CHECK_FALSE(fg->HasSwapchainContext());
    CHECK(fg->IsCreated()); // FG 效果上下文不受交换链失败影响（可诊断/可重试）
    CHECK(fg->LastReturnCode() == FFX_API_RETURN_ERROR_RUNTIME_ERROR);
    delete fg;
}
