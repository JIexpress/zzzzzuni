// =============================================================================
// test_zrhi_vulkan_translate.cpp —— Vulkan 映射表单测（TASK-B-R7-01）
//
// 镜像 d3d12 的 test_zrhi_d3d12_translate.cpp 写法；简报验收清单第 4 条
// （PresentMode/format/layout 三张映射表 Vulkan 侧全枚举）：
//   1. ZrFormat→VkFormat：独立期望表逐值比对 + 全枚举互异（无重复误配）
//   2. ZrImageLayout→VkImageLayout：9 个布局逐一显式映射、互异、
//      Undefined 恰为 VK_IMAGE_LAYOUT_UNDEFINED(0)（与 VK 编译期常量直接对值）
//   3. ZrPresentMode→VkPresentModeKHR：三值正确且互异（Fifo 必支持语义）
//   附：Filter/AddressMode/CullMode/Blend/SampleCount + Access/Stage 逐位映射
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/vulkan/zrhi_vulkan_translate.h"

#include <cstdint>

// ---- 简报三张映射表之一：ZrFormat → VkFormat -------------------------------------
TEST_CASE("ZrVulkan_Format：全枚举覆盖、逐值正确且互异")
{
    // 独立期望表（与实现 switch 逐值比对，防止映射抄错位）
    struct Expect {
        ZrFormat format;
        VkFormat vk;
    };
    const Expect expects[] = {
        {ZrFormat::RGBA8Unorm, VK_FORMAT_R8G8B8A8_UNORM},
        {ZrFormat::BGRA8Unorm, VK_FORMAT_B8G8R8A8_UNORM},
        {ZrFormat::RGBA8sRGB, VK_FORMAT_R8G8B8A8_SRGB},
        {ZrFormat::R8Unorm, VK_FORMAT_R8_UNORM},
        {ZrFormat::R16Float, VK_FORMAT_R16_SFLOAT},
        {ZrFormat::RG16Float, VK_FORMAT_R16G16_SFLOAT},
        {ZrFormat::R11G11B10UFloat, VK_FORMAT_B10G11R11_UFLOAT_PACK32},
        {ZrFormat::RGBA16Float, VK_FORMAT_R16G16B16A16_SFLOAT},
        {ZrFormat::R32Float, VK_FORMAT_R32_SFLOAT},
        {ZrFormat::RGBA32Float, VK_FORMAT_R32G32B32A32_SFLOAT},
        {ZrFormat::R32Uint, VK_FORMAT_R32_UINT},
        {ZrFormat::D32Float, VK_FORMAT_D32_SFLOAT},
        {ZrFormat::D24UnormS8, VK_FORMAT_D24_UNORM_S8_UINT},
        {ZrFormat::D16Unorm, VK_FORMAT_D16_UNORM},
    };
    constexpr uint32_t kCount = static_cast<uint32_t>(ZrFormat::Count);
    REQUIRE(sizeof(expects) / sizeof(expects[0]) == kCount); // 期望表与枚举数一致

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrVulkan_Format(expects[i].format) == expects[i].vk);
        CHECK(ZrVulkan_Format(expects[i].format) != VK_FORMAT_UNDEFINED); // 合法值必映射
    }
    // 互异：任意两个格式不得映射到同一 VK 值（无重复误配）
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CHECK(ZrVulkan_Format(expects[i].format) != ZrVulkan_Format(expects[j].format));
        }
    }
}

// ---- 简报三张映射表之二：ZrImageLayout → VkImageLayout ----------------------------
TEST_CASE("ZrVulkan_ImageLayout：显式映射、互异且语义正确")
{
    // 独立期望表（VK 常量直接对值，防隐式序号转换；枚举值 1:1 同序为设计对齐）
    struct Expect {
        ZrImageLayout layout;
        VkImageLayout vk;
    };
    const Expect expects[] = {
        {ZrImageLayout::Undefined, VK_IMAGE_LAYOUT_UNDEFINED},
        {ZrImageLayout::General, VK_IMAGE_LAYOUT_GENERAL},
        {ZrImageLayout::ColorAttachmentOptimal, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL},
        {ZrImageLayout::DepthStencilAttachmentOptimal,
            VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL},
        {ZrImageLayout::DepthStencilReadOnlyOptimal,
            VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL},
        {ZrImageLayout::ShaderReadOnlyOptimal, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL},
        {ZrImageLayout::TransferSrcOptimal, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL},
        {ZrImageLayout::TransferDstOptimal, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL},
        {ZrImageLayout::Present, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR},
    };
    constexpr uint32_t kCount = 9u; // ZrImageLayout 枚举值个数（无 Count 哨兵）

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrVulkan_ImageLayout(expects[i].layout) == expects[i].vk);
    }
    // 互异：任意两个布局不得映射到同一 VK 布局（与 D3D12 的 COMMON=PRESENT 数值
    // 重合不同——VK 侧 9 布局两两互异，无例外）
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CAPTURE(j);
            CHECK(ZrVulkan_ImageLayout(expects[i].layout)
                != ZrVulkan_ImageLayout(expects[j].layout));
        }
    }
    // 关键语义：Undefined→VK_IMAGE_LAYOUT_UNDEFINED(0)；Present→PRESENT_SRC_KHR
    CHECK(static_cast<uint32_t>(ZrVulkan_ImageLayout(ZrImageLayout::Undefined)) == 0u);
    CHECK(ZrVulkan_ImageLayout(ZrImageLayout::Present) == VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
    CHECK(ZrVulkan_ImageLayout(ZrImageLayout::ColorAttachmentOptimal)
        == VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
}

// ---- 简报三张映射表之三：ZrPresentMode → VkPresentModeKHR --------------------------
TEST_CASE("ZrVulkan_PresentMode：三值正确且互异（Fifo 必支持）")
{
    // Fifo：垂直同步排队（VK 全表面必支持——后端降级兜底的目标值）
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Fifo) == VK_PRESENT_MODE_FIFO_KHR);
    // Mailbox：三缓冲晚撕裂
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Mailbox) == VK_PRESENT_MODE_MAILBOX_KHR);
    // Immediate：不排队可能撕裂
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Immediate) == VK_PRESENT_MODE_IMMEDIATE_KHR);

    // 互异
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Fifo)
        != ZrVulkan_PresentMode(ZrPresentMode::Mailbox));
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Mailbox)
        != ZrVulkan_PresentMode(ZrPresentMode::Immediate));
    CHECK(ZrVulkan_PresentMode(ZrPresentMode::Fifo)
        != ZrVulkan_PresentMode(ZrPresentMode::Immediate));
}

// ---- 附加映射：采样器 / PSO 状态 ---------------------------------------------------
TEST_CASE("ZrVulkan_Filter：双值独立映射（VK 三字段无需打包）")
{
    CHECK(ZrVulkan_Filter(ZrFilter::Nearest) == VK_FILTER_NEAREST);
    CHECK(ZrVulkan_Filter(ZrFilter::Linear) == VK_FILTER_LINEAR);
    CHECK(ZrVulkan_Filter(ZrFilter::Nearest) != ZrVulkan_Filter(ZrFilter::Linear));
}

TEST_CASE("ZrVulkan_AddressMode：全枚举显式映射且互异")
{
    struct Expect {
        ZrSamplerAddressMode mode;
        VkSamplerAddressMode address;
    };
    const Expect expects[] = {
        {ZrSamplerAddressMode::Repeat, VK_SAMPLER_ADDRESS_MODE_REPEAT},
        {ZrSamplerAddressMode::MirroredRepeat, VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT},
        {ZrSamplerAddressMode::ClampToEdge, VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE},
        {ZrSamplerAddressMode::ClampToBorder, VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER},
        {ZrSamplerAddressMode::MirrorClampToEdge, VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE},
    };
    constexpr uint32_t kCount = 5u; // ZrSamplerAddressMode 枚举值个数（无哨兵）

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrVulkan_AddressMode(expects[i].mode) == expects[i].address);
    }
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CAPTURE(j);
            CHECK(ZrVulkan_AddressMode(expects[i].mode)
                != ZrVulkan_AddressMode(expects[j].mode));
        }
    }
}

TEST_CASE("ZrVulkan_CullMode/BlendFactor/SampleCount：全枚举映射")
{
    // CullMode
    CHECK(ZrVulkan_CullMode(ZrCullMode::None) == VK_CULL_MODE_NONE);
    CHECK(ZrVulkan_CullMode(ZrCullMode::Back) == VK_CULL_MODE_BACK_BIT);
    CHECK(ZrVulkan_CullMode(ZrCullMode::Front) == VK_CULL_MODE_FRONT_BIT);

    // BlendFactor（v1 仅 Opaque：ONE/ZERO 直通）
    const ZrVulkanBlendParams opaque = ZrVulkan_BlendFactor(ZrBlendMode::Opaque);
    CHECK(opaque.src == VK_BLEND_FACTOR_ONE);
    CHECK(opaque.dst == VK_BLEND_FACTOR_ZERO);
    CHECK(opaque.srcAlpha == VK_BLEND_FACTOR_ONE);
    CHECK(opaque.dstAlpha == VK_BLEND_FACTOR_ZERO);

    // SampleCount（数值即位值）
    CHECK(ZrVulkan_SampleCount(ZrSampleCount::One) == VK_SAMPLE_COUNT_1_BIT);
    CHECK(ZrVulkan_SampleCount(ZrSampleCount::Two) == VK_SAMPLE_COUNT_2_BIT);
    CHECK(ZrVulkan_SampleCount(ZrSampleCount::Four) == VK_SAMPLE_COUNT_4_BIT);
    CHECK(ZrVulkan_SampleCount(ZrSampleCount::Eight) == VK_SAMPLE_COUNT_8_BIT);
}

// ---- 附加映射：Access/Stage 位标志（与 D3D12 后端的逐位 switch 同纪律）-------------
TEST_CASE("ZrVulkan_Access/PipelineStage：逐位显式映射")
{
    // 空集
    CHECK(ZrVulkan_Access(ZrAccessBits::None) == 0u);
    CHECK(ZrVulkan_PipelineStage(ZrPipelineStageBits::None) == 0u);

    // 单位：与 VK 位值对齐（zrhi_types.h static_assert 的运行时复核）
    CHECK(ZrVulkan_Access(ZrAccessBits::TransferWrite) == VK_ACCESS_TRANSFER_WRITE_BIT);
    CHECK(ZrVulkan_Access(ZrAccessBits::MemoryWrite) == VK_ACCESS_MEMORY_WRITE_BIT);
    CHECK(ZrVulkan_Access(ZrAccessBits::ColorAttachmentWrite)
        == VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT);
    CHECK(ZrVulkan_PipelineStage(ZrPipelineStageBits::Transfer) == VK_PIPELINE_STAGE_TRANSFER_BIT);
    CHECK(ZrVulkan_PipelineStage(ZrPipelineStageBits::BottomOfPipe)
        == VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);

    // 组合：OR 累加（多屏屏障批次的并集语义）
    const ZrAccessBits combined = ZrAccessBits::ShaderRead | ZrAccessBits::ShaderWrite;
    CHECK(ZrVulkan_Access(combined)
        == (VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT));
    const ZrPipelineStageBits graphics = ZrPipelineStageBits::VertexShader
        | ZrPipelineStageBits::FragmentShader;
    CHECK(ZrVulkan_PipelineStage(graphics)
        == (VK_PIPELINE_STAGE_VERTEX_SHADER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT));
}

// ---- 附加映射：Image/Buffer 用途位（设备创建路径使用）-------------------------------
TEST_CASE("ZrVulkan_ImageUsage/BufferUsage：逐位显式映射")
{
    CHECK(ZrVulkan_ImageUsage(ZrImageUsage::None) == 0u);
    CHECK(ZrVulkan_ImageUsage(ZrImageUsage::ColorAttachment) == VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT);
    CHECK(ZrVulkan_ImageUsage(ZrImageUsage::Sampled | ZrImageUsage::Storage)
        == (VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT));

    CHECK(ZrVulkan_BufferUsage(ZrBufferUsage::None) == 0u);
    CHECK(ZrVulkan_BufferUsage(ZrBufferUsage::Vertex) == VK_BUFFER_USAGE_VERTEX_BUFFER_BIT);
    CHECK(ZrVulkan_BufferUsage(ZrBufferUsage::Index | ZrBufferUsage::TransferDst)
        == (VK_BUFFER_USAGE_INDEX_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT));
}

// ---- TASK-B-R7-02 接线镜像：--shadows/--lights 路径依赖的映射逐值断言 ----------------
// （CSM 级联阴影图 D32 数组图 + R32F 孪生解释采样 + 深度只读采样 + 级联层清屏 +
//  depth-only PSO 正面剔除——VK 后端本轮接线的全部映射依赖）
TEST_CASE("ZrVulkan 阴影接线镜像：格式/布局/访问/阶段/剔除逐值")
{
    // 阴影图源格式与孪生解释格式（R7-02 修正：VK 侧视图用源格式 D32 + DEPTH
    // aspect，采样 .r 语义与 D3D12 R32F 解释一致——两格式映射各自仍须正确）
    CHECK(ZrVulkan_Format(ZrFormat::D32Float) == VK_FORMAT_D32_SFLOAT);
    CHECK(ZrVulkan_Format(ZrFormat::R32Float) == VK_FORMAT_R32_SFLOAT);
    CHECK(ZrVulkan_Format(ZrFormat::D32Float) != ZrVulkan_Format(ZrFormat::R32Float));

    // 阴影图布局三态：级联写（附件）→ 主 pass 采样（深度只读）→ 描述符布局依据
    CHECK(ZrVulkan_ImageLayout(ZrImageLayout::DepthStencilAttachmentOptimal)
        == VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);
    CHECK(ZrVulkan_ImageLayout(ZrImageLayout::DepthStencilReadOnlyOptimal)
        == VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL);
    CHECK(ZrVulkan_ImageLayout(ZrImageLayout::DepthStencilReadOnlyOptimal)
        != ZrVulkan_ImageLayout(ZrImageLayout::ShaderReadOnlyOptimal)); // 深度/颜色采样分轨

    // 级联 pass 访问/阶段（写侧与读侧）
    CHECK(ZrVulkan_Access(ZrAccessBits::DepthStencilAttachmentWrite)
        == VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT);
    CHECK(ZrVulkan_Access(ZrAccessBits::DepthStencilAttachmentRead)
        == VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT);
    CHECK(ZrVulkan_PipelineStage(ZrPipelineStageBits::LateFragmentTests)
        == VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT);
    CHECK(ZrVulkan_PipelineStage(ZrPipelineStageBits::FragmentShader)
        == VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);

    // depth-only 阴影 PSO 的正面剔除（对位 D3D12 CullMode::Front）+ 主 pass None
    CHECK(ZrVulkan_CullMode(ZrCullMode::Front) == VK_CULL_MODE_FRONT_BIT);
    CHECK(ZrVulkan_CullMode(ZrCullMode::None) == VK_CULL_MODE_NONE);
    CHECK(ZrVulkan_CullMode(ZrCullMode::Front) != ZrVulkan_CullMode(ZrCullMode::Back));
}
