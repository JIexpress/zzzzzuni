// zrhi_vulkan_translate.cpp —— Vulkan 纯函数映射表实现（TASK-B-R7-01）
//
// 语义对应（zrhi → Vulkan）：抽象层位值当年按 VK 对齐（zrhi_types.h 的
// static_assert 即证据），本文件把"对齐"兑现为显式 switch——保持与 d3d12 后端
// translate 相同的纪律：唯一转译点、严禁 static_cast 隐式数值转换。
#include "zrhi/vulkan/zrhi_vulkan_translate.h"

#include <cassert>

VkFormat ZrVulkan_Format(ZrFormat format) noexcept
{
    switch (format) {
    case ZrFormat::RGBA8Unorm: return VK_FORMAT_R8G8B8A8_UNORM;
    case ZrFormat::BGRA8Unorm: return VK_FORMAT_B8G8R8A8_UNORM;
    case ZrFormat::RGBA8sRGB: return VK_FORMAT_R8G8B8A8_SRGB;
    case ZrFormat::R8Unorm: return VK_FORMAT_R8_UNORM;
    case ZrFormat::R16Float: return VK_FORMAT_R16_SFLOAT;
    case ZrFormat::RG16Float: return VK_FORMAT_R16G16_SFLOAT;
    case ZrFormat::R11G11B10UFloat: return VK_FORMAT_B10G11R11_UFLOAT_PACK32;
    case ZrFormat::RGBA16Float: return VK_FORMAT_R16G16B16A16_SFLOAT;
    case ZrFormat::R32Float: return VK_FORMAT_R32_SFLOAT;
    case ZrFormat::RGBA32Float: return VK_FORMAT_R32G32B32A32_SFLOAT;
    case ZrFormat::R32Uint: return VK_FORMAT_R32_UINT;
    case ZrFormat::D32Float: return VK_FORMAT_D32_SFLOAT;
    case ZrFormat::D24UnormS8: return VK_FORMAT_D24_UNORM_S8_UINT;
    case ZrFormat::D16Unorm: return VK_FORMAT_D16_UNORM;
    default:
        assert(false && "ZrVulkan_Format: 非法 ZrFormat 枚举值");
        return VK_FORMAT_UNDEFINED;
    }
}

VkImageLayout ZrVulkan_ImageLayout(ZrImageLayout layout) noexcept
{
    // 枚举值与 VkImageLayout 1:1 同序（zrhi_types.h 设计对齐），仍逐值显式 switch：
    // Undefined=VK_IMAGE_LAYOUT_UNDEFINED(0) ... Present=VK_IMAGE_LAYOUT_PRESENT_SRC_KHR
    switch (layout) {
    case ZrImageLayout::Undefined: return VK_IMAGE_LAYOUT_UNDEFINED;
    case ZrImageLayout::General: return VK_IMAGE_LAYOUT_GENERAL;
    case ZrImageLayout::ColorAttachmentOptimal: return VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    case ZrImageLayout::DepthStencilAttachmentOptimal:
        return VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    case ZrImageLayout::DepthStencilReadOnlyOptimal:
        return VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL;
    case ZrImageLayout::ShaderReadOnlyOptimal: return VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    case ZrImageLayout::TransferSrcOptimal: return VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    case ZrImageLayout::TransferDstOptimal: return VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    case ZrImageLayout::Present: return VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    default:
        assert(false && "ZrVulkan_ImageLayout: 非法 ZrImageLayout 枚举值");
        return VK_IMAGE_LAYOUT_UNDEFINED;
    }
}

VkPresentModeKHR ZrVulkan_PresentMode(ZrPresentMode mode) noexcept
{
    switch (mode) {
    case ZrPresentMode::Fifo: return VK_PRESENT_MODE_FIFO_KHR; // 必支持，兜底首选
    case ZrPresentMode::Mailbox: return VK_PRESENT_MODE_MAILBOX_KHR;
    case ZrPresentMode::Immediate: return VK_PRESENT_MODE_IMMEDIATE_KHR;
    default:
        assert(false && "ZrVulkan_PresentMode: 非法 ZrPresentMode 枚举值");
        return VK_PRESENT_MODE_FIFO_KHR;
    }
}

VkFilter ZrVulkan_Filter(ZrFilter filter) noexcept
{
    switch (filter) {
    case ZrFilter::Nearest: return VK_FILTER_NEAREST;
    case ZrFilter::Linear: return VK_FILTER_LINEAR;
    default:
        assert(false && "ZrVulkan_Filter: 非法 ZrFilter 枚举值");
        return VK_FILTER_NEAREST;
    }
}

VkSamplerAddressMode ZrVulkan_AddressMode(ZrSamplerAddressMode mode) noexcept
{
    switch (mode) {
    case ZrSamplerAddressMode::Repeat: return VK_SAMPLER_ADDRESS_MODE_REPEAT;
    case ZrSamplerAddressMode::MirroredRepeat: return VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT;
    case ZrSamplerAddressMode::ClampToEdge: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    case ZrSamplerAddressMode::ClampToBorder: return VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER;
    // MIRROR_CLAMP_TO_EDGE：Vulkan 1.2 起核心（本后端要求 1.3 设备，恒可用）
    case ZrSamplerAddressMode::MirrorClampToEdge:
        return VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE;
    default:
        assert(false && "ZrVulkan_AddressMode: 非法 ZrSamplerAddressMode 枚举值");
        return VK_SAMPLER_ADDRESS_MODE_REPEAT;
    }
}

VkCullModeFlagBits ZrVulkan_CullMode(ZrCullMode mode) noexcept
{
    switch (mode) {
    case ZrCullMode::None: return VK_CULL_MODE_NONE;
    case ZrCullMode::Back: return VK_CULL_MODE_BACK_BIT;
    case ZrCullMode::Front: return VK_CULL_MODE_FRONT_BIT;
    default:
        assert(false && "ZrVulkan_CullMode: 非法 ZrCullMode 枚举值");
        return VK_CULL_MODE_NONE;
    }
}

ZrVulkanBlendParams ZrVulkan_BlendFactor(ZrBlendMode mode) noexcept
{
    switch (mode) {
    case ZrBlendMode::Opaque:
        // 不透明直通：final = src（ONE/ZERO 组合，BlendEnable=false）
        return { VK_BLEND_FACTOR_ONE, VK_BLEND_FACTOR_ZERO, VK_BLEND_FACTOR_ONE,
            VK_BLEND_FACTOR_ZERO };
    default:
        assert(false && "ZrVulkan_BlendFactor: 非法 ZrBlendMode 枚举值（v1 仅 Opaque）");
        return { VK_BLEND_FACTOR_ONE, VK_BLEND_FACTOR_ZERO, VK_BLEND_FACTOR_ONE,
            VK_BLEND_FACTOR_ZERO };
    }
}

VkSampleCountFlagBits ZrVulkan_SampleCount(ZrSampleCount samples) noexcept
{
    switch (samples) {
    case ZrSampleCount::One: return VK_SAMPLE_COUNT_1_BIT;
    case ZrSampleCount::Two: return VK_SAMPLE_COUNT_2_BIT;
    case ZrSampleCount::Four: return VK_SAMPLE_COUNT_4_BIT;
    case ZrSampleCount::Eight: return VK_SAMPLE_COUNT_8_BIT;
    default:
        assert(false && "ZrVulkan_SampleCount: 非法 ZrSampleCount 枚举值");
        return VK_SAMPLE_COUNT_1_BIT;
    }
}

// 逐位显式 switch 累加（位值与 VK 对齐但坚持"唯一转译点"纪律）
VkImageUsageFlags ZrVulkan_ImageUsage(ZrImageUsage usage) noexcept
{
    VkImageUsageFlags out = 0u;
    const struct {
        ZrImageUsage bit;
        VkImageUsageFlagBits vk;
    } table[] = {
        { ZrImageUsage::TransferSrc, VK_IMAGE_USAGE_TRANSFER_SRC_BIT },
        { ZrImageUsage::TransferDst, VK_IMAGE_USAGE_TRANSFER_DST_BIT },
        { ZrImageUsage::Sampled, VK_IMAGE_USAGE_SAMPLED_BIT },
        { ZrImageUsage::Storage, VK_IMAGE_USAGE_STORAGE_BIT },
        { ZrImageUsage::ColorAttachment, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT },
        { ZrImageUsage::DepthStencilAttachment, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT },
    };
    for (const auto& row : table) {
        if (ZrHasAll(usage, row.bit)) {
            out |= row.vk;
        }
    }
    return out;
}

VkBufferUsageFlags ZrVulkan_BufferUsage(ZrBufferUsage usage) noexcept
{
    VkBufferUsageFlags out = 0u;
    const struct {
        ZrBufferUsage bit;
        VkBufferUsageFlagBits vk;
    } table[] = {
        { ZrBufferUsage::TransferSrc, VK_BUFFER_USAGE_TRANSFER_SRC_BIT },
        { ZrBufferUsage::TransferDst, VK_BUFFER_USAGE_TRANSFER_DST_BIT },
        { ZrBufferUsage::Uniform, VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT },
        { ZrBufferUsage::Storage, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT },
        { ZrBufferUsage::Vertex, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT },
        { ZrBufferUsage::Index, VK_BUFFER_USAGE_INDEX_BUFFER_BIT },
        { ZrBufferUsage::Indirect, VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT },
    };
    for (const auto& row : table) {
        if (ZrHasAll(usage, row.bit)) {
            out |= row.vk;
        }
    }
    return out;
}

// 逐位显式 switch 累加（位值与 VK 对齐但坚持"唯一转译点"纪律）
VkPipelineStageFlags ZrVulkan_PipelineStage(ZrPipelineStageBits stages) noexcept
{
    VkPipelineStageFlags out = 0u;
    const struct {
        ZrPipelineStageBits bit;
        VkPipelineStageFlagBits vk;
    } table[] = {
        { ZrPipelineStageBits::TopOfPipe, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT },
        { ZrPipelineStageBits::DrawIndirect, VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT },
        { ZrPipelineStageBits::VertexInput, VK_PIPELINE_STAGE_VERTEX_INPUT_BIT },
        { ZrPipelineStageBits::VertexShader, VK_PIPELINE_STAGE_VERTEX_SHADER_BIT },
        { ZrPipelineStageBits::FragmentShader, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT },
        { ZrPipelineStageBits::EarlyFragmentTests, VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT },
        { ZrPipelineStageBits::LateFragmentTests, VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT },
        { ZrPipelineStageBits::ColorAttachmentOutput, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT },
        { ZrPipelineStageBits::ComputeShader, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT },
        { ZrPipelineStageBits::Transfer, VK_PIPELINE_STAGE_TRANSFER_BIT },
        { ZrPipelineStageBits::BottomOfPipe, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT },
    };
    for (const auto& row : table) {
        if (ZrHasAll(stages, row.bit)) {
            out |= row.vk;
        }
    }
    return out;
}

VkAccessFlags ZrVulkan_Access(ZrAccessBits access) noexcept
{
    VkAccessFlags out = 0u;
    const struct {
        ZrAccessBits bit;
        VkAccessFlagBits vk;
    } table[] = {
        { ZrAccessBits::IndirectCommandRead, VK_ACCESS_INDIRECT_COMMAND_READ_BIT },
        { ZrAccessBits::IndexRead, VK_ACCESS_INDEX_READ_BIT },
        { ZrAccessBits::VertexAttributeRead, VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT },
        { ZrAccessBits::UniformRead, VK_ACCESS_UNIFORM_READ_BIT },
        { ZrAccessBits::InputAttachmentRead, VK_ACCESS_INPUT_ATTACHMENT_READ_BIT },
        { ZrAccessBits::ShaderRead, VK_ACCESS_SHADER_READ_BIT },
        { ZrAccessBits::ShaderWrite, VK_ACCESS_SHADER_WRITE_BIT },
        { ZrAccessBits::ColorAttachmentRead, VK_ACCESS_COLOR_ATTACHMENT_READ_BIT },
        { ZrAccessBits::ColorAttachmentWrite, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT },
        { ZrAccessBits::DepthStencilAttachmentRead, VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT },
        { ZrAccessBits::DepthStencilAttachmentWrite, VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT },
        { ZrAccessBits::TransferRead, VK_ACCESS_TRANSFER_READ_BIT },
        { ZrAccessBits::TransferWrite, VK_ACCESS_TRANSFER_WRITE_BIT },
        { ZrAccessBits::MemoryRead, VK_ACCESS_MEMORY_READ_BIT },
        { ZrAccessBits::MemoryWrite, VK_ACCESS_MEMORY_WRITE_BIT },
    };
    for (const auto& row : table) {
        if (ZrHasAll(access, row.bit)) {
            out |= row.vk;
        }
    }
    return out;
}
