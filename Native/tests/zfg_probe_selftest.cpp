// =============================================================================
// zfg_probe_selftest.cpp —— 探测 hook 自测（TASK-B-R9-01 阶段 0 spike）
//
// 目的：在"完全可控的目标"上先证明 hook 机制本身有效（vtable patch、参数记录、
// 原样转发、Present/ResizeBuffers 记录），再去 Unity 里跑。这样 Unity 侧一旦
// 没有日志，就能确定是"Unity 不走这条路径/时机不对"，而不是 hook 写坏了。
//
// 流程：安装 hook → 建 D3D12 设备/队列/隐藏窗口/工厂 → CreateSwapChainForHwnd
//       → Present ×3 → ResizeBuffers（先变小再变大）→ 卸载 → 校验日志内容。
//
// TASK-R10-03 追加的两组断言：
//   A-2 槽位守卫：日志里必须有 `slot guard: ... -> PASS` 与 `composition=24`
//       （装错槽的旧实现在这里会打印 17，或在守卫处直接 FAIL 并回滚安装）
//   A-4 重建路径：日志里必须有 `swapchain resized 320x200 -> 256x152`
//       （窗口变小；旧实现只转发不更新尺寸，这行根本不会出现）
// 不进 ctest（需要真实 GPU 窗口）；由施工/验收代理手动运行。
// =============================================================================
#include "zfgprobe/zfg_probe_hook.h"
#include "zfgprobe/zfg_probe_wrap.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>

#include <cstdio>
#include <cstring>

#include "zupscaler/fg_ffx.h"

namespace {

constexpr UINT kWidth = 320u;
constexpr UINT kHeight = 200u;

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    return ::DefWindowProcA(hwnd, msg, wParam, lParam);
}

HWND CreateHiddenWindowSized(UINT width, UINT height, const char* className)
{
    WNDCLASSEXA wc {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = &WndProc;
    wc.hInstance = ::GetModuleHandleA(nullptr);
    wc.lpszClassName = className;
    if (::RegisterClassExA(&wc) == 0u) {
        return nullptr;
    }
    return ::CreateWindowExA(0, wc.lpszClassName, "zfg selftest", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, static_cast<int>(width), static_cast<int>(height), nullptr,
        nullptr, wc.hInstance, nullptr);
}

HWND CreateHiddenWindow()
{
    return CreateHiddenWindowSized(kWidth, kHeight, "ZfgProbeSelftestWnd");
}

/// 日志里是否出现某子串（"证据行存在"判定）
bool LogContains(const char* path, const char* needle)
{
    FILE* f = nullptr;
    if (fopen_s(&f, path, "rb") != 0 || f == nullptr) {
        return false;
    }
    char buffer[16384] = {};
    const size_t read = fread(buffer, 1u, sizeof(buffer) - 1u, f);
    fclose(f);
    buffer[read] = '\0';
    return std::strstr(buffer, needle) != nullptr;
}

} // namespace

int main()
{
    // 日志路径：默认构建目录旁的固定名；ZFG_SELFTEST_LOG 可指到 TempLogs/<任务>/
    // （交接档案 §3.5：证据别落在 %TEMP% 或仓库根）
    char logPathBuf[MAX_PATH] = {};
    const char* logPath = "zfg_probe_selftest.log";
    if (::GetEnvironmentVariableA("ZFG_SELFTEST_LOG", logPathBuf,
            static_cast<DWORD>(sizeof(logPathBuf)))
        != 0u) {
        logPath = logPathBuf;
    }

    int failures = 0;
    auto check = [&failures](bool ok, const char* what) {
        std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", what);
        if (!ok) {
            ++failures;
        }
    };

    check(zfg::Install(logPath) == zfg::ProbeInstall::Installed, "hook install");
    check(zfg::IsInstalled(), "hook reports installed");

    ID3D12Device* device = nullptr;
    HRESULT hr = ::D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
    check(SUCCEEDED(hr) && device != nullptr, "D3D12 device");
    if (device == nullptr) {
        zfg::Uninstall();
        return 1;
    }

    D3D12_COMMAND_QUEUE_DESC queueDesc {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ID3D12CommandQueue* queue = nullptr;
    hr = device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue));
    check(SUCCEEDED(hr) && queue != nullptr, "command queue");

    HWND hwnd = CreateHiddenWindow();
    check(hwnd != nullptr, "hidden window");

    IDXGIFactory2* factory = nullptr;
    if (hwnd != nullptr && queue != nullptr) {
        hr = ::CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        check(SUCCEEDED(hr) && factory != nullptr, "DXGI factory");

        DXGI_SWAP_CHAIN_DESC1 desc {};
        desc.Width = kWidth;
        desc.Height = kHeight;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1u;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2u;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

        IDXGISwapChain1* swapchain = nullptr;
        hr = factory->CreateSwapChainForHwnd(queue, hwnd, &desc, nullptr, nullptr, &swapchain);
        check(SUCCEEDED(hr) && swapchain != nullptr, "CreateSwapChainForHwnd forwards OK");

        if (swapchain != nullptr) {
            for (UINT i = 0u; i < 3u; ++i) {
                (void)swapchain->Present(0u, 0u);
            }
            // 先**变小**（R10-02 A-4 的越界路径：旧实现按旧尺寸对新后台缓冲拷贝），
            // 再变大（证明两个方向都走同一条状态推进）。
            HRESULT resizeHr = swapchain->ResizeBuffers(2u, kWidth - 64u, kHeight - 48u,
                DXGI_FORMAT_B8G8R8A8_UNORM, 0u);
            check(SUCCEEDED(resizeHr), "ResizeBuffers smaller (320x200 -> 256x152)");
            (void)swapchain->Present(0u, 0u);
            resizeHr = swapchain->ResizeBuffers(2u, kWidth + 32u, kHeight + 32u,
                DXGI_FORMAT_B8G8R8A8_UNORM, 0u);
            check(SUCCEEDED(resizeHr), "ResizeBuffers larger (256x152 -> 352x232)");
            swapchain->Release();
        }
        factory->Release();
    }

    if (queue != nullptr) {
        queue->Release();
    }
    device->Release();
    if (hwnd != nullptr) {
        (void)::DestroyWindow(hwnd);
    }

    zfg::Uninstall();
    check(!zfg::IsInstalled(), "hook uninstall");

    // ---- 日志证据：hook 确实落在 ForHwnd 且转发成功 ----
    check(LogContains(logPath, "CreateSwapChainForHwnd"), "log: ForHwnd intercepted");
    check(LogContains(logPath, "hr=0x00000000 swapchain=0x"), "log: forwarded OK");
    check(LogContains(logPath, "IDXGISwapChain::Present #3"), "log: present intercepted");
    check(LogContains(logPath, "ResizeBuffers"), "log: resize intercepted");
    check(LogContains(logPath, "zfg probe uninstalled"), "log: uninstalled");
    // ---- A-2：槽位守卫（结构校验 + Composition 的真实槽号 24）----
    check(LogContains(logPath, "slot guard: VERIFY PASS"), "log: A-2 slot guard verified");
    check(LogContains(logPath, "CreateSwapChainForComposition slot=24"),
        "log: A-2 composition patched at slot 24 (not 17)");
    check(LogContains(logPath, "patch ok: slot=24"), "log: A-2 patch applied at slot 24");
    check(!LogContains(logPath, "CreateSwapChainForComposition slot=17"),
        "log: A-2 no composition patch at slot 17");
    // ---- A-4：重建后的尺寸推进（窗口变小 + 变大各一条）----
    check(LogContains(logPath, "swapchain resized 320x200 -> 256x152"),
        "log: A-4 shrink path updated display size");
    check(LogContains(logPath, "swapchain resized 256x152 -> 352x232"),
        "log: A-4 grow path updated display size");

    // =========================================================================
    // TASK-R10-05 追加：wrap 模式真机阶段（FFX 运行时可用时执行）
    //   A-3 代理建链保留宿主 desc：用审计实测场景（usage=0x30 effect=3
    //       flags=0x842）直调 WrapCreateSwapChainForHwnd，然后 GetDesc1 对账——
    //       建成 desc 必须与宿主一致（必需位 FRAME_LATENCY_WAITABLE_OBJECT 除外）。
    //       旧实现在这里建成 usage=0x20 effect=4 flags=0x840（audit_fg_b.log:11）。
    //   B-3 关停：WrapShutdown 后 stats 如实清零（历史缺陷是全仓无人调用）。
    // =========================================================================
    {
        ID3D12Device* wrapDevice = nullptr;
        if (SUCCEEDED(::D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0,
                IID_PPV_ARGS(&wrapDevice)))
            && wrapDevice != nullptr) {
            const zr::FrameGenProbe probe = zr::FrameGenerator::QuerySupport(wrapDevice);
            if (probe.support != zr::FrameGenSupport::Available) {
                std::printf("[LIMITED] A-3/B-3 wrap phase skipped (FG runtime unavailable: "
                            "support=%u rc=%u provider='%s')\n",
                    static_cast<unsigned>(probe.support), probe.returnCode, probe.providerName);
            } else {
                D3D12_COMMAND_QUEUE_DESC qd {};
                qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
                ID3D12CommandQueue* wrapQueue = nullptr;
                IDXGIFactory2* wrapFactory = nullptr;
                HWND wrapHwnd = CreateHiddenWindowSized(640u, 480u, "ZfgProbeSelftestWndWrap");
                if (SUCCEEDED(wrapDevice->CreateCommandQueue(&qd, IID_PPV_ARGS(&wrapQueue)))
                    && wrapQueue != nullptr
                    && SUCCEEDED(::CreateDXGIFactory1(IID_PPV_ARGS(&wrapFactory)))
                    && wrapFactory != nullptr && wrapHwnd != nullptr) {
                    // 审计 A-3 的宿主 desc（Unity 实测形态；尺寸取 FG 可接受的 640x480）
                    DXGI_SWAP_CHAIN_DESC1 hostDesc {};
                    hostDesc.Width = 640u;
                    hostDesc.Height = 480u;
                    hostDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                    hostDesc.SampleDesc.Count = 1u;
                    hostDesc.BufferUsage
                        = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT; // 0x30
                    hostDesc.BufferCount = 3u;
                    hostDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL; // 3
                    hostDesc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING
                        | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH
                        | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT; // 0x842
                    hostDesc.Scaling = DXGI_SCALING_STRETCH;
                    hostDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

                    void* wrapped = nullptr;
                    const bool wrappedOk = zfg::WrapCreateSwapChainForHwnd(wrapFactory,
                        wrapQueue, wrapHwnd, hostDesc.Width, hostDesc.Height,
                        static_cast<unsigned>(hostDesc.Format), hostDesc.BufferCount, &wrapped,
                        &hostDesc, nullptr);
                    check(wrappedOk && wrapped != nullptr,
                        "A-3/B-3: wrap proxy chain created from host desc");
                    if (wrappedOk && wrapped != nullptr) {
                        // A-3 对账：代理链实际 desc == 宿主请求（必需位除外）
                        auto* wrappedSc = static_cast<IDXGISwapChain1*>(wrapped);
                        DXGI_SWAP_CHAIN_DESC1 actual {};
                        if (SUCCEEDED(wrappedSc->GetDesc1(&actual))) {
                            check(actual.Width == hostDesc.Width && actual.Height == hostDesc.Height
                                    && actual.Format == hostDesc.Format
                                    && actual.BufferCount == hostDesc.BufferCount
                                    && actual.BufferUsage == hostDesc.BufferUsage
                                    && actual.SwapEffect == hostDesc.SwapEffect
                                    && actual.Scaling == hostDesc.Scaling
                                    && actual.AlphaMode == hostDesc.AlphaMode
                                    && actual.SampleDesc.Count == hostDesc.SampleDesc.Count
                                    && actual.Flags
                                        == (hostDesc.Flags
                                            | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT),
                                "A-3: proxy desc matches host desc (required bit excepted)");
                        } else {
                            check(false, "A-3: GetDesc1 on wrapped chain");
                        }
                        // B-3：wrap 生效时统计出口为真；关停后如实清零
                        ZfgFgStats stats {};
                        check(zfg::WrapGetStats(&stats) && stats.enabled == 0u,
                            "B-3: WrapGetStats reports the live proxy chain");
                        zfg::WrapShutdown();
                        check(!zfg::WrapGetStats(&stats) && stats.realPresents == 0u
                                && stats.generatedPresents == 0u && stats.haveInputs == 0u,
                            "B-3: after WrapShutdown stats/handles are zeroed");
                        check(zfg::WrapSwapchain() == nullptr, "B-3: no wrapped chain remains");
                    }
                } else {
                    std::printf("[LIMITED] A-3/B-3 wrap phase skipped (queue/factory/window "
                                "creation failed)\n");
                }
                if (wrapFactory != nullptr) {
                    wrapFactory->Release();
                }
                if (wrapQueue != nullptr) {
                    wrapQueue->Release();
                }
                if (wrapHwnd != nullptr) {
                    (void)::DestroyWindow(wrapHwnd);
                    (void)::UnregisterClassA("ZfgProbeSelftestWndWrap",
                        ::GetModuleHandleA(nullptr));
                }
            }
            wrapDevice->Release();
        }
    }

    std::printf("selftest log: %s\n", logPath);
    std::printf("zfg probe selftest: %s (%d failure(s))\n", (failures == 0) ? "OK" : "FAILED",
        failures);
    return (failures == 0) ? 0 : 1;
}
