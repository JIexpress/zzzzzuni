// =============================================================================
// zrhi_d3d12_unity_bridge.cpp —— Unity 接入桥实现（TASK-B-UNITY-01）
//
// 背景：ZRender 内核接不进 Unity 的两条硬事实——①ZrD3D12CreateDevice 无参数、
// 自建设备；②zrender.def 无任何渲染入口。本文件解决第①条的后端侧（包装外部
// 设备）与"对一张外部纹理清屏"的最小闭环；第②条由 zplugin 的 ZR_Unity_* 转发。
//
// 与 Vulkan 语义的对应：
//   CreateFrom(external)   ≈ vkImportDevice / 借用 VkDevice（无 Vulkan 等价物，
//                            语义上是"不创建、只包住一个已存在的设备对象"）
//   BeginFrame(tex)        ≈ 为 vkImage 建 VkImageView（RTV）+ 记录清屏命令
//   EndFrame()             ≈ vkCmdClearColorImage + vkQueueSubmit + vkQueueWaitIdle
//
// 单线程独占（Unity 渲染线程）；状态为进程内单例，语义见头文件。
// =============================================================================
#include "zrhi/d3d12/zrhi_d3d12_unity_bridge.h"

#include "zrhi/d3d12/zrhi_d3d12_device.h"

namespace zr {
namespace unity_bridge {
namespace {

// 清屏色：与 zrhi_d3d12_window --draw 的背景色同值（RGB=(13,26,38)），
// 便于用"是否等于演示背景"直接判断清屏是否生效。
// 归一化写成 n/255：8bit UNORM 目标回读应为精确的 (13,26,38)（±1 舍入）。
constexpr float kClearColor[4] = {
    13.0f / 255.0f, 26.0f / 255.0f, 38.0f / 255.0f, 1.0f
};

// 桥私有的一帧命令链路 + 当前包装的设备（单线程，进程内单例）
struct BridgeState {
    ZrD3D12Device* device = nullptr; // 包装对象（CreateFrom 产物，非宿主设备本体）
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> rtvHeap;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv {};
    Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
    Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> list;
    Microsoft::WRL::ComPtr<ID3D12Resource> target; // 帧内持有（AddRef 到 EndFrame）
    bool inFrame = false;
};

BridgeState g_bridge;

/// 释放桥私有对象与设备包装（不动宿主设备本体：引用计数由宿主持有）
void ReleaseWrapped()
{
    if (g_bridge.device != nullptr) {
        g_bridge.device->WaitIdle(); // 排空本桥已提交的工作后才允许释放命令表
        ZrD3D12DestroyDevice(g_bridge.device);
        g_bridge.device = nullptr;
    }
    g_bridge.target.Reset();
    g_bridge.list.Reset();
    g_bridge.allocator.Reset();
    g_bridge.rtvHeap.Reset();
    g_bridge.rtv = D3D12_CPU_DESCRIPTOR_HANDLE { 0 };
    g_bridge.inFrame = false;
}

} // namespace

int AttachDevice(void* d3d12Device)
{
    if (d3d12Device == nullptr) {
        return kErrNull;
    }
    ReleaseWrapped(); // 幂等：重复 Attach 先释放旧包装

    ID3D12Device* external = static_cast<ID3D12Device*>(d3d12Device);
    ZrD3D12Device* device = ZrD3D12CreateDeviceFrom(external);
    if (device == nullptr) {
        return kErrDevice;
    }

    // 桥私有链路：RTV 堆（4 槽，够 mip0 单视图重复覆盖）+ 分配器 + 命令表
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc {};
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    heapDesc.NumDescriptors = 4u;
    if (FAILED(external->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&g_bridge.rtvHeap)))
        || FAILED(external->CreateCommandAllocator(
               D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_bridge.allocator)))
        || FAILED(external->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
               g_bridge.allocator.Get(), nullptr, IID_PPV_ARGS(&g_bridge.list)))) {
        ZrD3D12DestroyDevice(device);
        g_bridge.rtvHeap.Reset();
        g_bridge.allocator.Reset();
        g_bridge.list.Reset();
        return kErrDevice;
    }
    g_bridge.list->Close(); // 创建即记录态：空记录关闭（BeginFrame 时 Reset）
    g_bridge.rtv = g_bridge.rtvHeap->GetCPUDescriptorHandleForHeapStart();
    g_bridge.device = device;
    return kOk;
}

int BeginFrame(void* backBufferTexture, uint32_t width, uint32_t height)
{
    if (g_bridge.device == nullptr) {
        return kErrNoDevice;
    }
    if (backBufferTexture == nullptr || width == 0u || height == 0u) {
        return kErrNull;
    }
    if (g_bridge.inFrame) {
        return kErrState;
    }

    ID3D12Resource* texture = static_cast<ID3D12Resource*>(backBufferTexture);
    const D3D12_RESOURCE_DESC desc = texture->GetDesc();
    if (desc.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D
        || desc.SampleDesc.Count != 1u // 多重采样目标需 MS 专用 RTV 视图，最小实现不支持
        || desc.Width < width || desc.Height < height) {
        return kErrTexture;
    }

    // RTV：pDesc=nullptr → 按资源自身格式建 mip0/全层视图（Unity 后台缓冲即此形态）
    g_bridge.device->D3D12Device()->CreateRenderTargetView(texture, nullptr, g_bridge.rtv);
    g_bridge.target = texture; // AddRef：帧内纹理不会被调用方提前释放
    g_bridge.inFrame = true;
    return kOk;
}

int EndFrame(void)
{
    if (g_bridge.device == nullptr) {
        return kErrNoDevice;
    }
    if (!g_bridge.inFrame) {
        return kErrState;
    }

    HRESULT hr = g_bridge.allocator->Reset();
    if (SUCCEEDED(hr)) {
        hr = g_bridge.list->Reset(g_bridge.allocator.Get(), nullptr);
    }
    if (SUCCEEDED(hr)) {
        // 目标处于 COMMON（D3D12 隐式提升为 RENDER_TARGET，无需显式屏障）；
        // 命令表结束后隐式衰减回 COMMON，下一帧可再次直接用
        g_bridge.list->ClearRenderTargetView(g_bridge.rtv, kClearColor, 0u, nullptr);
        hr = g_bridge.list->Close();
    }
    if (SUCCEEDED(hr)) {
        ID3D12CommandList* lists[] = {g_bridge.list.Get()};
        g_bridge.device->D3D12Queue()->ExecuteCommandLists(1u, lists);
        g_bridge.device->WaitIdle(); // 同队列 fence 排空：EndFrame 返回即 GPU 侧完成
    }

    g_bridge.target.Reset();
    g_bridge.inFrame = false;
    return SUCCEEDED(hr) ? kOk : kErrDevice;
}

int DetachDevice(void)
{
    ReleaseWrapped();
    return kOk;
}

int QueryAttachedDevice(void** outDevice)
{
    if (outDevice == nullptr) {
        return kErrNull;
    }
    if (g_bridge.device == nullptr) {
        *outDevice = nullptr;
        return kErrNoDevice;
    }
    *outDevice = g_bridge.device->D3D12Device();
    return kOk;
}

} // namespace unity_bridge
} // namespace zr
