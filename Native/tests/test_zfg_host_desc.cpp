// =============================================================================
// test_zfg_host_desc.cpp —— 代理建链保留宿主 desc（TASK-R10-05 A-3）
//
// 历史缺陷（R10-02 A-3，位置经审计验收更正到 zupscaler/fg_ffx.cpp——自造
// DXGI_SWAP_CHAIN_DESC1 把宿主请求的 BufferUsage/SwapEffect/Flags/Scaling/
// AlphaMode/SampleDesc 全部静默丢弃）：本仓日志实测 Unity 请求
// usage=0x30 effect=3 flags=0x842，FFX 建成 usage=0x20 effect=4 flags=0x840——
// 后台缓冲 SHADER_INPUT 位、ALLOW_TEARING 位、FLIP_SEQUENTIAL 语义被丢掉，却仍
// 返回 S_OK。
//
// 修复：CreateFgSwapchainForHwnd 增加宿主 desc 透传（requestedDesc1，空指针 =
// 历史行为逐位一致），创建 desc 以宿主值为基底、仅强制 FG 必需位
// FRAME_LATENCY_WAITABLE_OBJECT。
//
// 用例（简报验收点：审计日志那条 usage=0x30 effect=3 flags=0x842 场景断言建成
// desc 与宿主一致（必需位除外））——mock ffx 表捕获 FFX 实际收到的创建 desc 逐
// 字段断言；变异验证：fg_ffx.cpp 改回"忽略 requestedDesc1 的自造 desc"即红。
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

#include <d3d12.h>

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

// ---- 最小 mock ffx 表：只拦 CreateContext 的两条路径，捕获完整创建 desc ---------
struct DescCaptureMock {
    int createFgCalls = 0;
    int createSwapchainCalls = 0;
    uint32_t createFgRc = FFX_API_RETURN_OK;
    uint32_t createSwapchainRc = FFX_API_RETURN_OK;
    bool haveSwapchainDesc = false;
    DXGI_SWAP_CHAIN_DESC1 swapchainDesc {};
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc = nullptr;
    void* scHwnd = nullptr;
    void* scFactory = nullptr;
    void* scQueue = nullptr;
    void* outWrapped = nullptr;

    void Reset() { *this = DescCaptureMock {}; }
};

DescCaptureMock g_descMock;

ffxReturnCode_t CaptureQuery(ffxContext*, ffxQueryDescHeader* desc)
{
    if (desc != nullptr && desc->type == FFX_API_QUERY_DESC_TYPE_GET_VERSIONS) {
        auto* q = reinterpret_cast<ffxQueryDescGetVersions*>(desc);
        if (q->outputCount == nullptr) {
            return FFX_API_RETURN_ERROR_PARAMETER;
        }
        if (*q->outputCount == 0u) {
            *q->outputCount = 1u;
            return FFX_API_RETURN_OK;
        }
        if (q->versionNames != nullptr) {
            q->versionNames[0] = "mock";
        }
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t CaptureCreateContext(ffxContext* context, ffxCreateContextDescHeader* desc,
    const ffxAllocationCallbacks*)
{
    if (context == nullptr || desc == nullptr) {
        return FFX_API_RETURN_ERROR_PARAMETER;
    }
    if (desc->type == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION) {
        g_descMock.createFgCalls += 1;
        if (g_descMock.createFgRc != FFX_API_RETURN_OK) {
            return g_descMock.createFgRc;
        }
        *context = reinterpret_cast<ffxContext>(static_cast<uintptr_t>(0xA3000001ull));
        return FFX_API_RETURN_OK;
    }
    if (desc->type
        == FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATIONSWAPCHAIN_FOR_HWND_DX12) {
        g_descMock.createSwapchainCalls += 1;
        if (g_descMock.createSwapchainRc != FFX_API_RETURN_OK) {
            return g_descMock.createSwapchainRc; // 回退路径的拒绝注入点
        }
        auto* sc = reinterpret_cast<ffxCreateContextDescFrameGenerationSwapChainForHwndDX12*>(
            desc);
        g_descMock.haveSwapchainDesc = (sc->desc != nullptr);
        if (sc->desc != nullptr) {
            g_descMock.swapchainDesc = *sc->desc; // FFX 实际收到的创建 desc（完整拷贝）
        }
        g_descMock.fullscreenDesc = sc->fullscreenDesc;
        g_descMock.scHwnd = sc->hwnd;
        g_descMock.scFactory = sc->dxgiFactory;
        g_descMock.scQueue = sc->gameQueue;
        if (sc->swapchain != nullptr) {
            *sc->swapchain = reinterpret_cast<IDXGISwapChain4*>(
                static_cast<uintptr_t>(0xA3000BADll));
            g_descMock.outWrapped = *sc->swapchain;
        }
        *context = reinterpret_cast<ffxContext>(static_cast<uintptr_t>(0xA3000002ull));
        return FFX_API_RETURN_OK;
    }
    return FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t CaptureConfigure(ffxContext*, const ffxConfigureDescHeader* desc)
{
    return (desc != nullptr && desc->type == FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION)
        ? FFX_API_RETURN_OK
        : FFX_API_RETURN_ERROR_UNKNOWN_DESCTYPE;
}

ffxReturnCode_t CaptureDispatch(ffxContext*, const ffxDispatchDescHeader* desc)
{
    return (desc != nullptr) ? FFX_API_RETURN_OK : FFX_API_RETURN_ERROR_PARAMETER;
}

ffxReturnCode_t CaptureDestroyContext(ffxContext* context, const ffxAllocationCallbacks*)
{
    if (context != nullptr) {
        *context = nullptr;
    }
    return FFX_API_RETURN_OK;
}

ffxFunctions CaptureTable()
{
    ffxFunctions t {};
    t.Query = &CaptureQuery;
    t.CreateContext = &CaptureCreateContext;
    t.Configure = &CaptureConfigure;
    t.Dispatch = &CaptureDispatch;
    t.DestroyContext = &CaptureDestroyContext;
    return t;
}

/// 审计 A-3 的实测场景：Unity 请求 usage=0x30（RENDER_TARGET_OUTPUT|SHADER_INPUT）、
/// effect=3（FLIP_SEQUENTIAL）、flags=0x842（ALLOW_TEARING|FRAME_LATENCY_WAITABLE|
/// ALLOW_MODE_SWITCH）
DXGI_SWAP_CHAIN_DESC1 AuditUnityDesc()
{
    DXGI_SWAP_CHAIN_DESC1 d {};
    d.Width = 1920u;
    d.Height = 1080u;
    d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    d.SampleDesc.Count = 1u;
    d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT; // 0x30
    d.BufferCount = 3u;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; // 3
    d.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH
        | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT; // 0x842
    d.Scaling = DXGI_SCALING_STRETCH;
    d.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    return d;
}

zr::FrameGenCreateDesc ValidCreateDesc()
{
    zr::FrameGenCreateDesc d {};
    d.displayWidth = 1920u;
    d.displayHeight = 1080u;
    d.renderWidth = 1920u;
    d.renderHeight = 1080u;
    d.backBufferFfxFormat = ffxApiGetSurfaceFormatDX12(DXGI_FORMAT_R8G8B8A8_UNORM);
    d.jitterCancellation = false; // F2：Unity/代理路径
    d.depthInverted = true;       // Unity reversed-Z
    d.nativeDevice = reinterpret_cast<void*>(static_cast<uintptr_t>(0xA300DE1Cull));
    return d;
}

/// 审计场景逐字段断言（建成 desc 与宿主一致，必需位除外）
void CheckDescMatchesHost(const DXGI_SWAP_CHAIN_DESC1& host)
{
    const DXGI_SWAP_CHAIN_DESC1& built = g_descMock.swapchainDesc;
    CHECK(built.Width == host.Width);
    CHECK(built.Height == host.Height);
    CHECK(built.Format == host.Format);
    CHECK(built.BufferCount == host.BufferCount);
    CHECK(built.BufferUsage == host.BufferUsage); // SHADER_INPUT 位保住（旧实现丢 0x10）
    CHECK(built.SwapEffect == host.SwapEffect);   // FLIP_SEQUENTIAL 保住（旧实现改 FLIP_DISCARD）
    CHECK(built.Scaling == host.Scaling);
    CHECK(built.AlphaMode == host.AlphaMode);
    CHECK(built.SampleDesc.Count == host.SampleDesc.Count);
    CHECK(built.SampleDesc.Quality == host.SampleDesc.Quality);
    // 必需位除外：FFG 必需位被强制，宿主其余旗标全保留（0x842 本就含该位 → 相等）
    CHECK(built.Flags == (host.Flags | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
}

} // namespace

TEST_CASE("zfg host desc: audit scenario usage=0x30 effect=3 flags=0x842 is preserved (A-3)")
{
    g_descMock.Reset();
    ffxFunctions table = CaptureTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidCreateDesc()));

    const DXGI_SWAP_CHAIN_DESC1 host = AuditUnityDesc();
    void* wrapped = nullptr;
    void* waitable = nullptr;
    REQUIRE(fg->CreateFgSwapchainForHwnd(reinterpret_cast<void*>(static_cast<uintptr_t>(0x1F00Dull)),
        reinterpret_cast<void*>(static_cast<uintptr_t>(0xFA7ull)),
        reinterpret_cast<void*>(static_cast<uintptr_t>(0xEEEEull)), host.Width, host.Height,
        static_cast<uint32_t>(host.Format), host.BufferCount, &wrapped, &waitable, &host));
    CHECK(wrapped == g_descMock.outWrapped);
    CHECK(g_descMock.haveSwapchainDesc);
    CheckDescMatchesHost(host);
    // 宿主全屏 desc 同样透传（历史缺陷：无条件 nullptr）
    CHECK(g_descMock.fullscreenDesc == nullptr); // 本用例未传
    delete fg;
}

TEST_CASE("zfg host desc: waitable bit is forced when the host omits it (FG requirement)")
{
    g_descMock.Reset();
    ffxFunctions table = CaptureTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidCreateDesc()));

    DXGI_SWAP_CHAIN_DESC1 host = AuditUnityDesc();
    host.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH; // 无 waitable
    void* wrapped = nullptr;
    void* waitable = nullptr;
    REQUIRE(fg->CreateFgSwapchainForHwnd(nullptr, nullptr, nullptr, host.Width, host.Height,
        static_cast<uint32_t>(host.Format), host.BufferCount, &wrapped, &waitable, &host));
    // 仅 FG 必需位被补上；其余旗标不动
    CHECK(g_descMock.swapchainDesc.Flags
        == (host.Flags | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
    CHECK((g_descMock.swapchainDesc.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) != 0u);
    CheckDescMatchesHost(host);
    delete fg;
}

TEST_CASE("zfg host desc: fullscreen desc is passed through when provided (A-3)")
{
    g_descMock.Reset();
    ffxFunctions table = CaptureTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidCreateDesc()));

    const DXGI_SWAP_CHAIN_DESC1 host = AuditUnityDesc();
    DXGI_SWAP_CHAIN_FULLSCREEN_DESC fs {};
    fs.RefreshRate.Denominator = 60u;
    fs.RefreshRate.Numerator = 1u;
    fs.Scaling = DXGI_MODE_SCALING_UNSPECIFIED;
    fs.Windowed = TRUE;
    void* wrapped = nullptr;
    void* waitable = nullptr;
    REQUIRE(fg->CreateFgSwapchainForHwnd(nullptr, nullptr, nullptr, host.Width, host.Height,
        static_cast<uint32_t>(host.Format), host.BufferCount, &wrapped, &waitable, &host, &fs));
    CHECK(g_descMock.fullscreenDesc == &fs); // 同一对象指针 = 原样透传
    CheckDescMatchesHost(host);
    delete fg;
}

TEST_CASE("zfg host desc: null request keeps the legacy synthesized desc (--fg regression guard)")
{
    g_descMock.Reset();
    ffxFunctions table = CaptureTable();
    zr::FrameGenerator* fg = zr::CreateFrameGeneratorForTests(&table);
    REQUIRE(fg != nullptr);
    REQUIRE(fg->Create(ValidCreateDesc()));

    void* wrapped = nullptr;
    void* waitable = nullptr;
    // 窗口程序 --fg 的调用形态（不传宿主 desc）→ 行为与历史版本逐位一致
    REQUIRE(fg->CreateFgSwapchainForHwnd(nullptr, nullptr, nullptr, 640u, 480u,
        DXGI_FORMAT_B8G8R8A8_UNORM, 3u, &wrapped, &waitable));
    CHECK(g_descMock.swapchainDesc.BufferUsage == DXGI_USAGE_RENDER_TARGET_OUTPUT);
    CHECK(g_descMock.swapchainDesc.SwapEffect == DXGI_SWAP_EFFECT_FLIP_DISCARD);
    CHECK(g_descMock.swapchainDesc.Flags == DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
    CHECK(g_descMock.swapchainDesc.SampleDesc.Count == 1u);
    CHECK(g_descMock.swapchainDesc.Width == 640u);
    CHECK(g_descMock.swapchainDesc.Height == 480u);
    delete fg;
}
