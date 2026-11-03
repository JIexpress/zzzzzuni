// =============================================================================
// zrhi_d3d12_translate.h —— zrhi 语义 → D3D12 纯函数映射表（TASK-B-R2-02）
//
// 三个映射全部为无副作用纯函数（可单测，无需 GPU）：
//   - ZrD3D12_Format         : ZrFormat      → DXGI_FORMAT
//   - ZrD3D12_ResourceStates : ZrImageLayout → D3D12_RESOURCE_STATES（显式 switch
//                              查表转译，严禁隐式数值转换——两套枚举序不同，
//                              B-R2-01 已知风险）
//   - ZrD3D12_PresentParams  : ZrPresentMode → Present(SyncInterval, Flags) 参数
//   - ZrD3D12_Filter         : (ZrFilter mag, min, mip) → D3D12_FILTER（8 组合全枚举，
//                              TASK-B-R2-03 采样器）
//   - ZrD3D12_AddressMode    : ZrSamplerAddressMode → D3D12_TEXTURE_ADDRESS_MODE
//                              （全枚举显式 switch，TASK-B-R2-03 采样器）
//
// 非法枚举值（含 Count 哨兵）：Format 返回 DXGI_FORMAT_UNKNOWN、ResourceStates
// 返回 D3D12_RESOURCE_STATE_COMMON(0)、PresentParams 返回 Fifo 参数；均伴 assert。
// =============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>   // D3D12_RESOURCE_STATES
#include <dxgi1_5.h> // DXGI_PRESENT_ALLOW_TEARING（dxgi 头链一并就位）

#include "zrhi/zrhi_types.h"
#include "zrhi/zrhi_device.h" // TASK-B-R4-03：ZrGraphicsPsoDesc

/// ZrFormat → DXGI_FORMAT（全枚举显式 switch）
DXGI_FORMAT ZrD3D12_Format(ZrFormat format) noexcept;

/// ZrImageLayout → D3D12_RESOURCE_STATES（全枚举显式 switch，两套枚举序不同）
D3D12_RESOURCE_STATES ZrD3D12_ResourceStates(ZrImageLayout layout) noexcept;

/// split barrier 半边 → D3D12_RESOURCE_BARRIER_FLAG（TASK-B-R3-02）：
/// beginOnly=true → BEGIN_ONLY（写 Pass 结束即发起）；false → END_ONLY（读 Pass
/// 前补可见性）。这是 Zr split 语义到 D3D12 原生 split barrier 的唯一映射点
D3D12_RESOURCE_BARRIER_FLAGS ZrD3D12_SplitBarrierFlag(bool beginOnly) noexcept;

/// 呈现参数（→ IDXGISwapChain::Present 的 SyncInterval 与 Flags）
struct ZrD3D12PresentParams {
    UINT syncInterval;  // 0 = 不等待垂直同步；1 = 每个垂直同步呈现一帧
    UINT presentFlags;  // 如 DXGI_PRESENT_ALLOW_TEARING
};

/// ZrPresentMode → Present 参数（全枚举显式 switch；映射表注释见 .cpp）
ZrD3D12PresentParams ZrD3D12_PresentParams(ZrPresentMode mode) noexcept;

// =============================================================================
// Present 结果跟踪（TASK-H-02 D3：Present 的 HRESULT 不得被丢弃）
// =============================================================================
/// IDXGISwapChain::Present 的返回值分类与累计（纯逻辑结构，可单测）：
///   hr >= 0（S_OK / DXGI_STATUS_OCCLUDED）→ 记为成功：被遮挡是正常状态而非失败
///   hr <  0（FAILED）→ 失败计数 +1、记录最近失败码；DEVICE_REMOVED / DEVICE_RESET /
///                      DRIVER_INTERNAL_ERROR 额外置粘滞位 deviceRemoved
/// 失败只在设备内部状态里累计（经 IZrDevice::GetDeviceStats 查询），库内不打印不中止。
struct ZrD3D12PresentStatus {
    uint32_t failureCount = 0;  // Present 失败次数（FAILED(hr) 计数）
    long lastFailureHresult = 0; // 最近一次失败的 HRESULT（无失败 = 0）
    bool deviceRemoved = false;  // 曾观察到设备移除/复位/驱动内部错误（粘滞）

    void Note(long hresult) noexcept;
    bool HasFailure() const noexcept { return failureCount > 0u; }
};

/// 三级过滤（mag/min/mip 各 Nearest|Linear）→ 单一 D3D12_FILTER 组合枚举
/// （Vulkan 三字段独立 → D3D12 打包枚举；8 种组合显式映射，非法值断言）
D3D12_FILTER ZrD3D12_Filter(ZrFilter mag, ZrFilter min, ZrFilter mip) noexcept;

/// ZrSamplerAddressMode → D3D12_TEXTURE_ADDRESS_MODE（全枚举显式 switch）
D3D12_TEXTURE_ADDRESS_MODE ZrD3D12_AddressMode(ZrSamplerAddressMode mode) noexcept;

// =============================================================================
// TASK-B-R4-03：图形 PSO 映射（纯函数，可单测）
// =============================================================================

/// ZrCullMode → D3D12_CULL_MODE（全枚举显式 switch）
D3D12_CULL_MODE ZrD3D12_CullMode(ZrCullMode mode) noexcept;

/// 混合模式 → 四通道混合因子（v1 仅 Opaque：src=ONE / dest=ZERO 直通不混合）
struct ZrD3D12BlendParams {
    D3D12_BLEND src;
    D3D12_BLEND dest;
    D3D12_BLEND srcAlpha;
    D3D12_BLEND destAlpha;
};
ZrD3D12BlendParams ZrD3D12_Blend(ZrBlendMode mode) noexcept;

/// v1 固定顶点布局：Pos3f+Normal3f+Uv2f 紧凑交错（POSITION/NORMAL/TEXCOORD0，
/// 32B/顶点——与 first_draw.hlsl 输入签名及 ZrGraphicsPsoDesc 注释一致）。
/// 返回静态表指针（进程生命周期），*outElementCount 写元素数
const D3D12_INPUT_ELEMENT_DESC* ZrD3D12_GraphicsInputLayout(uint32_t* outElementCount) noexcept;

/// v1 固定顶点布局的顶点跨度（字节）
constexpr uint32_t ZR_D3D12_GRAPHICS_VERTEX_STRIDE = 32u;

/// ZrGraphicsPsoDesc → D3D12_GRAPHICS_PIPELINE_STATE_DESC（纯映射；调用方先以
/// ZrValidateGraphicsPsoDesc 校验 desc）。覆盖：根签名指针（D3D12 PSO 的
/// pRootSignature 字段）/输入布局（固定 v1 版）/光栅化（SOLID+desc.cullMode）/
/// 混合（Opaque）/深度状态（LESS，写掩码按 desc.depthWrite）/RTV·DSV 格式
/// （dsvFormat==Count → DXGI_FORMAT_UNKNOWN 且 DepthEnable=false）
D3D12_GRAPHICS_PIPELINE_STATE_DESC ZrD3D12_GraphicsPsoDesc(const ZrGraphicsPsoDesc& desc,
    ID3D12RootSignature* rootSignature, D3D12_SHADER_BYTECODE vs,
    D3D12_SHADER_BYTECODE ps) noexcept;
