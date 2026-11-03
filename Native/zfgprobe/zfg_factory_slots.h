// =============================================================================
// zfg_factory_slots.h —— DXGI vtable 槽位的单一事实来源（TASK-R10-03 A-2）
//
// 为什么单独成头：槽号属于"装错也静默生效"的常量。R10-02 A-2 的实证是
// `kSlotCreateSwapChainForComposition = 17`（17 实为 GetSharedResourceAdapterLuid），
// 而 PatchSlotTracked 只校验"槽非空 + 不是自家代码"，槽 17 里躺着真实 dxgi 函数
// 指针 → 静默放行，日志还打印 composition=0x… 制造"已接管"假象。
//
// 现在产品代码与单测共用同一份表；单测再用 SDK 头（CINTERFACE 下的
// IDXGIFactory2Vtbl / IDXGISwapChain1Vtbl 结构体）逐槽反算偏移，任何常量改错、
// 或 SDK 接口布局变化，用例都会红。
//
// 槽号推导（SDK 10.0.26100：dxgi.h / dxgi1_2.h，按 C 风格 Vtbl 结构体成员顺序，
// 与验收代理的两遍独立核对一致）：
//   0-2   IUnknown      QueryInterface / AddRef / Release
//   3-6   IDXGIObject   SetPrivateData / SetPrivateDataInterface / GetPrivateData / GetParent
//   7-11  IDXGIFactory  EnumAdapters(7) / MakeWindowAssociation(8) / GetWindowAssociation(9) /
//                       CreateSwapChain(10) / CreateSoftwareAdapter(11)
//   12-13 IDXGIFactory1 EnumAdapters1(12) / IsCurrent(13)
//   14    IsWindowedStereoEnabled
//   15    CreateSwapChainForHwnd
//   16    CreateSwapChainForCoreWindow
//   17    GetSharedResourceAdapterLuid     ← R10-02 A-2 曾把这里当 Composition（错 7 个槽）
//   18-23 RegisterStereoStatusWindow / RegisterStereoStatusEvent / UnregisterStereoStatus /
//         RegisterOcclusionStatusWindow / RegisterOcclusionStatusEvent / UnregisterOcclusionStatus
//   24    CreateSwapChainForComposition   ← IDXGIFactory2 的最后一个方法（结构体共 25 项）
//
//   IDXGISwapChain1Vtbl：IUnknown(0-2) + IDXGIDeviceSubObject(3-6) + IDXGISwapChain
//     Present(8) / GetBuffer(9) / SetFullscreenState(10) / GetFullscreenState(11) /
//     GetDesc(12) / ResizeBuffers(13) / ResizeTarget(14) / GetContainingOutput(15) /
//     GetFrameStatistics(16) / GetLastPresentCount(17) + IDXGISwapChain1
//     GetDesc1(18) / GetFullscreenDesc(19) / GetHwnd(20) / GetCoreWindow(21) /
//     Present1(22) / IsTemporaryMonoSupported(23) / GetRestrictToOutput(24) /
//     SetBackgroundColor(25) / GetBackgroundColor(26) / SetRotation(27) / GetRotation(28)
// =============================================================================
#pragma once

#include <cstddef>

namespace zfg {

/// IDXGIFactory2Vtbl 的成员数（= CreateSwapChainForComposition 的下标 + 1）
constexpr size_t kIdxgiFactory2VtblCount = 25u;

/// 被 hook 的两族接口（只用于把槽位表分组，避免工厂槽与交换链槽混用）
enum class VtblFamily {
    Factory2 = 0,
    SwapChain1 = 1,
};

/// 一个被 patch（或仅被校验）的 vtable 槽
struct VtblSlotSpec {
    VtblFamily family;
    const char* method;   // 期望的函数名（与 SDK 头逐字一致）
    size_t slot;          // vtable 下标（0 起）
    bool patched;         // 本 hook 是否改写该槽
    bool lastOfInterface; // 是否为所属接口的最后一个方法（结构不变量）
};

/// 本 hook 涉及的全部槽位（顺序 = 安装顺序）
constexpr VtblSlotSpec kVtblSlots[] = {
    {VtblFamily::Factory2, "CreateSwapChain", 10u, true, false},
    {VtblFamily::Factory2, "CreateSwapChainForHwnd", 15u, true, false},
    {VtblFamily::Factory2, "CreateSwapChainForCoreWindow", 16u, true, false},
    {VtblFamily::Factory2, "CreateSwapChainForComposition", 24u, true, true},
    {VtblFamily::SwapChain1, "Present", 8u, true, false},
    {VtblFamily::SwapChain1, "ResizeBuffers", 13u, true, false},
    {VtblFamily::SwapChain1, "Present1", 22u, true, false},
};
constexpr size_t kVtblSlotCount = sizeof(kVtblSlots) / sizeof(kVtblSlots[0]);

// ---- 具名常量（产品代码引用；单测断言它们与 kVtblSlots 同源）-----------------
constexpr size_t kSlotCreateSwapChain = 10u;
constexpr size_t kSlotCreateSwapChainForHwnd = 15u;
constexpr size_t kSlotCreateSwapChainForCoreWindow = 16u;
constexpr size_t kSlotCreateSwapChainForComposition = 24u;
constexpr size_t kSlotPresent = 8u;
constexpr size_t kSlotResizeBuffers = 13u;
constexpr size_t kSlotPresent1 = 22u;

} // namespace zfg
