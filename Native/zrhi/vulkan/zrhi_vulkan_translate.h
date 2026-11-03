// =============================================================================
// zrhi_vulkan_translate.h —— zrhi 语义 → Vulkan 纯函数映射表（TASK-B-R7-01）
//
// RHI 抽象层（zrhi_types.h）当年 1:1 对齐 VK 语义，本文件是兑现承诺的最薄转译点：
// 全部为无副作用纯函数（可单测，无需 GPU），镜像 d3d12 后端的 translate 模块：
//   - ZrVulkan_Format        : ZrFormat      → VkFormat
//   - ZrVulkan_ImageLayout   : ZrImageLayout → VkImageLayout（位值同序，仍显式
//                              switch——严禁隐式数值转换的模块约定）
//   - ZrVulkan_PresentMode   : ZrPresentMode → VkPresentModeKHR
//   - ZrVulkan_Filter        : ZrFilter      → VkFilter（三字段独立，无 D3D12 打包）
//   - ZrVulkan_AddressMode   : ZrSamplerAddressMode → VkSamplerAddressMode
//   - ZrVulkan_CullMode      : ZrCullMode    → VkCullModeFlagBits
//   - ZrVulkan_BlendFactor   : ZrBlendMode   → 混合因子（v1 仅 Opaque 直通）
//   - ZrVulkan_SampleCount   : ZrSampleCount → VkSampleCountFlagBits
//   - ZrVulkan_PipelineStage : ZrPipelineStageBits → VkPipelineStageFlags（逐位 switch）
//   - ZrVulkan_Access        : ZrAccessBits  → VkAccessFlags（逐位 switch）
//
// 非法枚举值（含 Count 哨兵）：Format 返回 VK_FORMAT_UNDEFINED、其余返回各自
// 的零值/兜底并伴 assert。
// =============================================================================
#pragma once

#include <vulkan/vulkan.h>

#include "zrhi/zrhi_types.h"
#include "zrhi/zrhi_device.h" // TASK-B-R4-03：ZrGraphicsPsoDesc（blend/cull 字段）

/// ZrFormat → VkFormat（全枚举显式 switch）
VkFormat ZrVulkan_Format(ZrFormat format) noexcept;

/// ZrImageLayout → VkImageLayout（全枚举显式 switch；两套枚举值恰同序纯属设计对齐）
VkImageLayout ZrVulkan_ImageLayout(ZrImageLayout layout) noexcept;

/// ZrPresentMode → VkPresentModeKHR（全枚举显式 switch）
VkPresentModeKHR ZrVulkan_PresentMode(ZrPresentMode mode) noexcept;

/// ZrFilter → VkFilter（显式 switch；Vulkan 三字段独立无需打包）
VkFilter ZrVulkan_Filter(ZrFilter filter) noexcept;

/// ZrSamplerAddressMode → VkSamplerAddressMode（全枚举显式 switch）
VkSamplerAddressMode ZrVulkan_AddressMode(ZrSamplerAddressMode mode) noexcept;

/// ZrCullMode → VkCullModeFlagBits（全枚举显式 switch）
VkCullModeFlagBits ZrVulkan_CullMode(ZrCullMode mode) noexcept;

/// 混合模式 → 四通道混合因子（v1 仅 Opaque：src=ONE / dst=ZERO 直通不混合）
struct ZrVulkanBlendParams {
    VkBlendFactor src;
    VkBlendFactor dst;
    VkBlendFactor srcAlpha;
    VkBlendFactor dstAlpha;
};
ZrVulkanBlendParams ZrVulkan_BlendFactor(ZrBlendMode mode) noexcept;

/// ZrSampleCount → VkSampleCountFlagBits（数值即位值，仍显式 switch）
VkSampleCountFlagBits ZrVulkan_SampleCount(ZrSampleCount samples) noexcept;

/// ZrPipelineStageBits → VkPipelineStageFlags（逐位显式 switch 累加，None 合法 → 0）
VkPipelineStageFlags ZrVulkan_PipelineStage(ZrPipelineStageBits stages) noexcept;

/// ZrAccessBits → VkAccessFlags（逐位显式 switch 累加，None 合法 → 0）
VkAccessFlags ZrVulkan_Access(ZrAccessBits access) noexcept;

/// ZrImageUsage → VkImageUsageFlags（逐位显式 switch 累加）
VkImageUsageFlags ZrVulkan_ImageUsage(ZrImageUsage usage) noexcept;

/// ZrBufferUsage → VkBufferUsageFlags（逐位显式 switch 累加）
VkBufferUsageFlags ZrVulkan_BufferUsage(ZrBufferUsage usage) noexcept;
