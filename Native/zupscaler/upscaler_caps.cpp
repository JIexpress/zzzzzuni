// =============================================================================
// upscaler_caps.cpp —— 能力探测：DXGI 适配器 + 探测期短命真实 D3D12 设备
// （TASK-B-R6-02 建，TASK-B-FSR-01 改真设备探测）
//
// 用途：C ABI（ZR_Upscaler_QueryCaps）与窗口程序都需要"当前机器能跑什么"的
// 结论，而调用方（设置界面侧）并不持有可用的 D3D12 设备句柄——本文件在探测
// 过程中自建短命设备（用完即释），调用方无需任何设备。探测三件事：
//   1) DXGI 枚举首个非软件适配器 → vendorId/deviceId
//   2) FFX 运行时探测 + provider 版本枚举 → fsrRuntimePresent / fsr4Available：
//      枚举必须带真实 ID3D12Device*（在该适配器上 D3D12CreateDevice，feature
//      level 与 zrhi 建备同序 12.0→11.1→11.0）。FFX 的 null 设备枚举返回的是
//      截断列表——对端机 RX 9060 XT 实测（2026-11-13）：null=2 条（3.1.5/
//      2.3.4）vs 真设备=4 条（含 4.1.2），B-R6-02 的 FSR4 假阴性即此根因。
//      建备失败（无硬件/CI）→ 回退 device=nullptr 枚举，行为与 B-R6-02 一致
//      （fsr4Available=false 不误报）。探测口径三态记录在 fsr4ProbeMode
//      （0=真设备/1=null 回退/2=未探测，TASK-B-FSR-02）——回退口径下
//      fsr4Available 可能失真，消费方必须按"未检测"处理。
//   3) Streamline 运行时探测（dll 可加载性）→ dlssRuntimePresent
//
// 注意：真实设备上 FSR4 的最终结论仍以 UpscalerChain 初始化时（带真实
// ID3D12Device*）的 provider 枚举为准；若两者不一致，以后者为准。
// =============================================================================
#include "zupscaler/zupscaler.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>

namespace zr {

bool ZupProbeDeviceCaps(UpscalerDeviceCaps* outCaps)
{
    if (outCaps == nullptr) {
        return false;
    }
    *outCaps = UpscalerDeviceCaps {};

    IDXGIFactory1* factory = nullptr;
    IDXGIAdapter1* hardwareAdapter = nullptr; // 首个非软件适配器（引用保留待建备）
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
                hardwareAdapter = adapter; // 保留引用供 D3D12CreateDevice；见下方 Release
                break;
            }
            adapter->Release();
        }
        factory->Release();
    }

    // FSR4 探测用真实设备：FFX provider 枚举依赖设备上下文，null 设备返回截断
    // 列表（RX 9060 XT 实测 null=2 条 vs 真设备=4 条含 4.1.2，2026-11-13），
    // null 会把 FSR4 误判为不可用。建备失败（无硬件/CI）→ probeDevice=nullptr
    // 回退，枚举行为与修复前一致。
    ID3D12Device* probeDevice = nullptr;
    if (hardwareAdapter != nullptr) {
        // feature level 从高到低取首个成功（与 zrhi D3D12 建备同一策略）
        const D3D_FEATURE_LEVEL levels[]
            = {D3D_FEATURE_LEVEL_12_0, D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        for (const D3D_FEATURE_LEVEL level : levels) {
            if (SUCCEEDED(
                    ::D3D12CreateDevice(hardwareAdapter, level, IID_PPV_ARGS(&probeDevice)))) {
                break;
            }
        }
        hardwareAdapter->Release();
    }

    outCaps->fsrRuntimePresent = ZupFsrRuntimeProbe();
    outCaps->fsr4Available = outCaps->fsrRuntimePresent && ZupFsr4Probe(probeDevice);
    if (probeDevice != nullptr) {
        probeDevice->Release();
    }
    // fsr4ProbeMode（TASK-B-FSR-02）：如实记录 fsr4Available 的探测口径——
    //   0 = provider 枚举拿到了真实设备（建备成功，结论可信）
    //   1 = 建备失败/无硬件，走了 device=null 枚举（FFX null 枚举返回截断列表，
    //       结论可能失真，消费方按"未检测"处理）
    //   2 = provider 枚举根本未执行（FFX 运行时缺席；结构体零初始化默认也是 2，
    //       保证"没测过"不会被误读成"真设备探测可信"）
    if (outCaps->fsrRuntimePresent == 0u) {
        outCaps->fsr4ProbeMode = 2u;
    } else {
        outCaps->fsr4ProbeMode = (probeDevice != nullptr) ? 0u : 1u;
    }
    outCaps->nvidiaHardware = (outCaps->vendorId == 0x10DEu);
    outCaps->dlssRuntimePresent = ZupDlssRuntimeProbe();
    return true;
}

} // namespace zr
