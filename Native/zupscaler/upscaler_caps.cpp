// =============================================================================
// upscaler_caps.cpp —— 免 D3D12 设备的保守能力探测（TASK-B-R6-02）
//
// 用途：C ABI（ZR_Upscaler_QueryCaps）与窗口程序都需要"当前机器能跑什么"的
// 结论，但设置界面侧没有 D3D12 设备句柄。本文件只做三件低风险探测：
//   1) DXGI 枚举首个非软件适配器 → vendorId/deviceId（不创建 D3D12 设备）
//   2) FFX 运行时探测 + provider 版本枚举（device=null，FFX 对 null 设备返回
//      与真实设备一致的保守列表）→ fsrRuntimePresent / fsr4Available
//   3) Streamline 运行时探测（dll 可加载性）→ dlssRuntimePresent
//
// 注意：这是**保守**结论。真实设备上的 FSR4 是否可用，以 UpscalerChain 初始化
// 时（带真实 ID3D12Device*）的 provider 枚举为准；若两者不一致，以后者为准。
// =============================================================================
#include "zupscaler/zupscaler.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <dxgi1_6.h>

namespace zr {

bool ZupProbeDeviceCaps(UpscalerDeviceCaps* outCaps)
{
    if (outCaps == nullptr) {
        return false;
    }
    *outCaps = UpscalerDeviceCaps {};

    IDXGIFactory1* factory = nullptr;
    if (SUCCEEDED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && factory != nullptr) {
        for (UINT i = 0u;; ++i) {
            IDXGIAdapter1* adapter = nullptr;
            if (factory->EnumAdapters1(i, &adapter) == DXGI_ERROR_NOT_FOUND) {
                break;
            }
            if (adapter == nullptr) {
                continue;
            }
            DXGI_ADAPTER_DESC1 desc {};
            const bool usable = SUCCEEDED(adapter->GetDesc1(&desc))
                && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u;
            if (usable) {
                outCaps->vendorId = desc.VendorId;
                outCaps->deviceId = desc.DeviceId;
            }
            adapter->Release();
            if (usable) {
                break;
            }
        }
        factory->Release();
    }

    outCaps->fsrRuntimePresent = ZupFsrRuntimeProbe();
    outCaps->fsr4Available = outCaps->fsrRuntimePresent && ZupFsr4Probe(nullptr);
    outCaps->nvidiaHardware = (outCaps->vendorId == 0x10DEu);
    outCaps->dlssRuntimePresent = ZupDlssRuntimeProbe();
    return true;
}

} // namespace zr
