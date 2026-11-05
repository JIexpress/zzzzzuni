// =============================================================================
// zrhi_unity_bridge.cpp —— TASK-B-UNITY-01 自证程序（不进 ctest）
//
// 目的：证明"ZRender 能包住宿主设备并对宿主纹理清屏"这条桥真的通，而不是
// 只"编译通过"。全部动作在本进程内完成，不需要 Unity：
//
//   1) 自己创建 ID3D12Device（扮演 Unity 的角色）——与 Unity 一样：
//      CreateDXGIFactory2 → 首个非软件适配器 → D3D12CreateDevice。
//   2) 自己创建一张 offscreen 纹理（RGBA8Unorm + ALLOW_RENDER_TARGET，256×256），
//      先用**宿主自己的**队列把它清成哨兵色 (255,0,255) 并回读，作为"清屏前"证据。
//   3) 走 DLL 导出 ZR_Unity_AttachDevice → BeginFrame → EndFrame。
//   4) 再用**宿主自己的**队列回读同一张纹理，给出实际 RGB，与
//      ZR_UNITY_CLEAR_* 期望值比对。哨兵色 ≠ 期望色 → 该比对可证伪。
//
// 为什么能证明"用的是传入设备"：
//   a) ZR_Unity_DebugGetAttachedDevice 返回的指针与本进程自建设备指针相同；
//   b) D3D12 资源不可跨设备使用——清屏确实落在本进程创建的纹理上（回读可见），
//      若内部偷偷新建了设备，CreateRenderTargetView / 清屏不可能作用到该纹理。
//
// 为什么走 DLL 而不是静态库：判据要求"新导出在 dll 导出表里查得到"，本程序
// 用 LoadLibrary + GetProcAddress 取符号，取不到即失败——导出表是硬前提。
//
// 退出码 0 = 全部通过；非 0 = 失败。
// =============================================================================
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <cstdio>
#include <cstring>
#include <vector>

#include "zplugin/zplugin.h" // 错误码/清屏色常量/函数指针类型（不链接其实现）

using Microsoft::WRL::ComPtr;

namespace {

constexpr uint32_t kTexWidth = 256u;
constexpr uint32_t kTexHeight = 256u;

// 哨兵色（清屏前）：判据要求"清屏真的发生"，故先写入一个明显不同的颜色
constexpr float kSentinel[4] = {1.0f, 0.0f, 1.0f, 1.0f}; // 洋红

int g_failures = 0;

void Check(bool ok, const char* what)
{
    std::printf("  [%s] %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) {
        ++g_failures;
    }
}

/// 宿主侧 GPU 链路（模拟 Unity 自己的队列/命令行；与被测桥完全独立）
struct HostGpu {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE event = nullptr;
    uint64_t fenceValue = 0;

    bool Init(ID3D12Device* dev)
    {
        device = dev;
        D3D12_COMMAND_QUEUE_DESC qd {};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)))
            || FAILED(device->CreateCommandAllocator(
                   D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)))
            || FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                   allocator.Get(), nullptr, IID_PPV_ARGS(&list)))
            || FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)))) {
            return false;
        }
        list->Close();
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        return event != nullptr;
    }

    bool ExecuteAndWait()
    {
        if (FAILED(list->Close())) {
            return false;
        }
        ID3D12CommandList* lists[] = {list.Get()};
        queue->ExecuteCommandLists(1u, lists);
        fenceValue += 1u;
        if (FAILED(queue->Signal(fence.Get(), fenceValue))) {
            return false;
        }
        if (fence->GetCompletedValue() < fenceValue) {
            if (FAILED(fence->SetEventOnCompletion(fenceValue, event))) {
                return false;
            }
            if (WaitForSingleObject(event, 10000u) != WAIT_OBJECT_0) {
                return false;
            }
        }
        return true;
    }

    bool Begin()
    {
        allocator->Reset();
        return SUCCEEDED(list->Reset(allocator.Get(), nullptr));
    }
};

ComPtr<ID3D12Resource> CreateTargetTexture(ID3D12Device* device)
{
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC desc {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = kTexWidth;
    desc.Height = kTexHeight;
    desc.DepthOrArraySize = 1u;
    desc.MipLevels = 1u;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1u;
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    ComPtr<ID3D12Resource> texture;
    if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&texture)))) {
        return nullptr;
    }
    return texture;
}

/// 宿主自己清屏（哨兵色写入；不属于被测路径）
bool HostClear(HostGpu& gpu, ID3D12Resource* texture, const float color[4])
{
    D3D12_DESCRIPTOR_HEAP_DESC hd {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    hd.NumDescriptors = 1u;
    ComPtr<ID3D12DescriptorHeap> heap;
    if (FAILED(gpu.device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap)))) {
        return false;
    }
    const D3D12_CPU_DESCRIPTOR_HANDLE rtv = heap->GetCPUDescriptorHandleForHeapStart();
    gpu.device->CreateRenderTargetView(texture, nullptr, rtv);
    if (!gpu.Begin()) {
        return false;
    }
    gpu.list->ClearRenderTargetView(rtv, color, 0u, nullptr);
    return gpu.ExecuteAndWait();
}

/// 回读整张纹理（宿主自己记录 CopyTextureRegion + Map READBACK 缓冲）
bool ReadbackTexture(HostGpu& gpu, ID3D12Resource* texture, std::vector<uint8_t>* out,
    uint32_t* outRowPitch)
{
    const D3D12_RESOURCE_DESC desc = texture->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint {};
    UINT64 rowSize = 0;
    UINT numRows = 0;
    UINT64 totalBytes = 0;
    gpu.device->GetCopyableFootprints(
        &desc, 0u, 1u, 0u, &footprint, &numRows, &rowSize, &totalBytes);

    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bufDesc {};
    bufDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bufDesc.Width = totalBytes;
    bufDesc.Height = 1u;
    bufDesc.DepthOrArraySize = 1u;
    bufDesc.MipLevels = 1u;
    bufDesc.SampleDesc.Count = 1u;
    bufDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> readback;
    if (FAILED(gpu.device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &bufDesc,
            D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)))) {
        return false;
    }

    if (!gpu.Begin()) {
        return false;
    }
    // COMMON → COPY_SOURCE（ClearRenderTargetView 之后 GPU 已在 ExecuteCommandLists
    // 边界把资源衰减回 COMMON，这里显式转出即可）
    D3D12_RESOURCE_BARRIER barrier {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = texture;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    gpu.list->ResourceBarrier(1u, &barrier);

    D3D12_TEXTURE_COPY_LOCATION dst {};
    dst.pResource = readback.Get();
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint = footprint;
    D3D12_TEXTURE_COPY_LOCATION src {};
    src.pResource = texture;
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    src.SubresourceIndex = 0u;
    gpu.list->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, nullptr);

    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    gpu.list->ResourceBarrier(1u, &barrier);
    if (!gpu.ExecuteAndWait()) {
        return false;
    }

    void* mapped = nullptr;
    if (FAILED(readback->Map(0, nullptr, &mapped)) || mapped == nullptr) {
        return false;
    }
    out->assign(static_cast<size_t>(footprint.Footprint.RowPitch) * kTexHeight, 0u);
    const auto* base = static_cast<const uint8_t*>(mapped);
    for (uint32_t y = 0; y < kTexHeight; ++y) {
        std::memcpy(out->data() + static_cast<size_t>(y) * footprint.Footprint.RowPitch,
            base + static_cast<size_t>(y) * footprint.Footprint.RowPitch, kTexWidth * 4u);
    }
    readback->Unmap(0, nullptr);
    *outRowPitch = footprint.Footprint.RowPitch;
    return true;
}

void PrintPixel(const uint8_t* pixels, uint32_t rowPitch, uint32_t x, uint32_t y, const char* tag)
{
    const uint8_t* px = pixels + static_cast<size_t>(y) * rowPitch + static_cast<size_t>(x) * 4u;
    std::printf("  %s pixel(%u,%u) RGB=(%d,%d,%d) A=%d\n", tag, x, y, static_cast<int>(px[0]),
        static_cast<int>(px[1]), static_cast<int>(px[2]), static_cast<int>(px[3]));
}

/// 全帧统计：是否所有像素都等于期望 RGB（逐像素扫描，任何一个不符即失败）
bool AllPixelsEqual(const uint8_t* pixels, uint32_t rowPitch, int r, int g, int b)
{
    for (uint32_t y = 0; y < kTexHeight; ++y) {
        const uint8_t* row = pixels + static_cast<size_t>(y) * rowPitch;
        for (uint32_t x = 0; x < kTexWidth; ++x) {
            const uint8_t* px = row + static_cast<size_t>(x) * 4u;
            if (px[0] != r || px[1] != g || px[2] != b || px[3] != 255) {
                std::printf("  first mismatch at (%u,%u): RGB=(%d,%d,%d) A=%d\n", x, y,
                    static_cast<int>(px[0]), static_cast<int>(px[1]), static_cast<int>(px[2]),
                    static_cast<int>(px[3]));
                return false;
            }
        }
    }
    return true;
}

/// 取 exe 同目录下的 zrender.dll 全路径
bool SiblingDllPath(char* out, size_t outSize, const char* name)
{
    char module[MAX_PATH] = {};
    const DWORD len = GetModuleFileNameA(nullptr, module, MAX_PATH);
    if (len == 0u || len >= static_cast<DWORD>(MAX_PATH)) {
        return false;
    }
    char* slash = std::strrchr(module, '\\');
    if (slash == nullptr) {
        return false;
    }
    slash[1] = '\0';
    if (std::strlen(module) + std::strlen(name) + 1u > outSize) {
        return false;
    }
    std::snprintf(out, outSize, "%s%s", module, name);
    return true;
}

} // namespace

int main()
{
    std::printf("zrhi_unity_bridge: TASK-B-UNITY-01 self-proof (host device + host texture)\n");

    // ---- 1) 从 dll 导出表取符号（导出表是硬前提）--------------------------------
    char dllPath[MAX_PATH] = {};
    if (!SiblingDllPath(dllPath, sizeof(dllPath), "zrender.dll")) {
        std::printf("FAILED: cannot resolve zrender.dll path\n");
        return 2;
    }
    HMODULE dll = LoadLibraryA(dllPath);
    if (dll == nullptr) {
        std::printf("FAILED: LoadLibrary(%s) err=%lu\n", dllPath, GetLastError());
        return 2;
    }
    auto attach = reinterpret_cast<ZrUnityAttachDeviceFn>(
        GetProcAddress(dll, "ZR_Unity_AttachDevice"));
    auto beginFrame = reinterpret_cast<ZrUnityBeginFrameFn>(
        GetProcAddress(dll, "ZR_Unity_BeginFrame"));
    auto endFrame = reinterpret_cast<ZrUnityEndFrameFn>(
        GetProcAddress(dll, "ZR_Unity_EndFrame"));
    auto detach = reinterpret_cast<ZrUnityDetachDeviceFn>(
        GetProcAddress(dll, "ZR_Unity_DetachDevice"));
    auto queryDevice = reinterpret_cast<ZrUnityDebugGetAttachedDeviceFn>(
        GetProcAddress(dll, "ZR_Unity_DebugGetAttachedDevice"));
    std::printf("dll: %s\n", dllPath);
    std::printf("exports: Attach=%p Begin=%p End=%p Detach=%p Query=%p\n",
        reinterpret_cast<void*>(attach), reinterpret_cast<void*>(beginFrame),
        reinterpret_cast<void*>(endFrame), reinterpret_cast<void*>(detach),
        reinterpret_cast<void*>(queryDevice));
    if (attach == nullptr || beginFrame == nullptr || endFrame == nullptr
        || detach == nullptr || queryDevice == nullptr) {
        std::printf("FAILED: missing export(s) in zrender.dll\n");
        return 2;
    }
    Check(true, "五个 ZR_Unity_* 导出均可从 zrender.dll 导出表解析");

    // ---- 2) 扮演 Unity：自己建设备（不启用调试层，还原"宿主先建设备"的次序）--------
    ComPtr<IDXGIFactory4> factory;
    if (FAILED(CreateDXGIFactory2(0u, IID_PPV_ARGS(&factory)))) {
        std::printf("FAILED: CreateDXGIFactory2\n");
        return 2;
    }
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0u; i < 8u; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        if (factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        DXGI_ADAPTER_DESC1 desc {};
        candidate->GetDesc1(&desc);
        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u) {
            adapter = candidate;
            std::printf("host adapter: %ls (LUID %08lX:%08lX)\n", desc.Description,
                static_cast<unsigned long>(desc.AdapterLuid.HighPart),
                static_cast<unsigned long>(desc.AdapterLuid.LowPart));
            break;
        }
    }
    ComPtr<ID3D12Device> device;
    if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0,
            IID_PPV_ARGS(&device)))) {
        std::printf("FAILED: host D3D12CreateDevice\n");
        return 2;
    }
    std::printf("host device (Unity role): %p\n", static_cast<void*>(device.Get()));

    HostGpu gpu;
    if (!gpu.Init(device.Get())) {
        std::printf("FAILED: host queue/allocator/list/fence\n");
        return 2;
    }
    ComPtr<ID3D12Resource> texture = CreateTargetTexture(device.Get());
    if (texture == nullptr) {
        std::printf("FAILED: CreateTargetTexture\n");
        return 2;
    }
    std::printf("host texture: %ux%u RGBA8Unorm + ALLOW_RENDER_TARGET (state=COMMON)\n",
        kTexWidth, kTexHeight);

    // ---- 3) 哨兵色 → 回读，作为"清屏前"基线 --------------------------------------
    if (!HostClear(gpu, texture.Get(), kSentinel)) {
        std::printf("FAILED: host sentinel clear\n");
        return 2;
    }
    std::vector<uint8_t> pixels;
    uint32_t rowPitch = 0;
    if (!ReadbackTexture(gpu, texture.Get(), &pixels, &rowPitch)) {
        std::printf("FAILED: readback after sentinel clear\n");
        return 2;
    }
    PrintPixel(pixels.data(), rowPitch, kTexWidth / 2u, kTexHeight / 2u, "before(BEGIN )");
    const uint8_t* center = pixels.data() + static_cast<size_t>(kTexHeight / 2u) * rowPitch
        + static_cast<size_t>(kTexWidth / 2u) * 4u;
    Check(center[0] == 255 && center[1] == 0 && center[2] == 255,
        "清屏前中心像素 = 哨兵色 (255,0,255)（与期望清屏色不同，比对可证伪）");

    // ---- 4) 错误路径（先测无设备状态）-------------------------------------------
    Check(attach(nullptr) == ZR_UNITY_ERR_NULL, "AttachDevice(nullptr) 返回 ZR_UNITY_ERR_NULL");
    Check(endFrame() == ZR_UNITY_ERR_NO_DEVICE, "未 Attach 时 EndFrame 返回 ZR_UNITY_ERR_NO_DEVICE");

    // ---- 5) 走桥：Attach → 设备指针自证 → Begin → End ---------------------------
    Check(attach(static_cast<void*>(device.Get())) == ZR_UNITY_OK,
        "ZR_Unity_AttachDevice(hostDevice) = 0");
    void* wrapped = nullptr;
    const int queryRc = queryDevice(&wrapped);
    std::printf("wrapped device: %p\n", wrapped);
    Check(queryRc == ZR_UNITY_OK && wrapped == static_cast<void*>(device.Get()),
        "桥内包装的设备指针 == 本进程自建设备指针（没偷偷自己建）");

    Check(beginFrame(nullptr, kTexWidth, kTexHeight) == ZR_UNITY_ERR_NULL,
        "BeginFrame(nullptr) 返回 ZR_UNITY_ERR_NULL");
    Check(beginFrame(static_cast<void*>(texture.Get()), kTexWidth, kTexHeight) == ZR_UNITY_OK,
        "ZR_Unity_BeginFrame(hostTexture, 256, 256) = 0");
    Check(beginFrame(static_cast<void*>(texture.Get()), kTexWidth, kTexHeight)
            == ZR_UNITY_ERR_STATE,
        "重复 BeginFrame 被拒（ZR_UNITY_ERR_STATE）");
    Check(endFrame() == ZR_UNITY_OK, "ZR_Unity_EndFrame() = 0");

    // ---- 6) 回读：清屏是否真的作用在这张外部纹理上 --------------------------------
    if (!ReadbackTexture(gpu, texture.Get(), &pixels, &rowPitch)) {
        std::printf("FAILED: readback after bridge clear\n");
        return 2;
    }
    PrintPixel(pixels.data(), rowPitch, 0u, 0u, "after (BRIDGE)");
    PrintPixel(pixels.data(), rowPitch, kTexWidth / 2u, kTexHeight / 2u, "after (BRIDGE)");
    PrintPixel(pixels.data(), rowPitch, kTexWidth - 1u, kTexHeight - 1u, "after (BRIDGE)");
    const bool expected = AllPixelsEqual(pixels.data(), rowPitch, static_cast<int>(ZR_UNITY_CLEAR_R),
        static_cast<int>(ZR_UNITY_CLEAR_G), static_cast<int>(ZR_UNITY_CLEAR_B));
    std::printf("  expected = ZR_UNITY_CLEAR (%u,%u,%u) over the whole %ux%u frame\n",
        static_cast<unsigned>(ZR_UNITY_CLEAR_R), static_cast<unsigned>(ZR_UNITY_CLEAR_G),
        static_cast<unsigned>(ZR_UNITY_CLEAR_B), kTexWidth, kTexHeight);
    Check(expected, "清屏后整帧像素 == (13,26,38)（桥的 ClearRenderTargetView 生效）");

    // ---- 7) Detach 后宿主设备仍可用（不接管宿主生命周期）--------------------------
    Check(detach() == ZR_UNITY_OK, "ZR_Unity_DetachDevice() = 0");
    Check(beginFrame(static_cast<void*>(texture.Get()), kTexWidth, kTexHeight)
            == ZR_UNITY_ERR_NO_DEVICE,
        "Detach 后 BeginFrame 返回 ZR_UNITY_ERR_NO_DEVICE");
    Check(HostClear(gpu, texture.Get(), kSentinel), "Detach 后宿主仍能用自己的队列清屏");
    if (ReadbackTexture(gpu, texture.Get(), &pixels, &rowPitch)) {
        const uint8_t* c = pixels.data() + static_cast<size_t>(kTexHeight / 2u) * rowPitch
            + static_cast<size_t>(kTexWidth / 2u) * 4u;
        Check(c[0] == 255 && c[1] == 0 && c[2] == 255,
            "Detach 后宿主清屏仍可回读（宿主设备未被桥销毁）");
    }

    std::printf("%s (%d failure(s))\n",
        g_failures == 0 ? "UNITY BRIDGE OK" : "UNITY BRIDGE FAILED", g_failures);
    FreeLibrary(dll);
    return g_failures == 0 ? 0 : 1;
}
