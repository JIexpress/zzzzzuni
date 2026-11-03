// =============================================================================
// zrhi_types.h —— RHI 核心枚举与格式 traits（TASK-B-R2-01）
//
// Vulkan 语义对齐的核心数据类型层（零 Vulkan 头依赖：对应关系仅以注释与
// 位值对齐表达，后端任务再桥接真实 API）：
//   - ZrFormat / ZrFormatTraits：资源格式 + 逐格式 traits 表（全枚举覆盖）
//   - ZrImageUsage / ZrBufferUsage：用途位标志
//   - ZrImageLayout：图像布局
//   - ZrAccessBits / ZrPipelineStageBits：访问与管线阶段位标志
//     （Graphics/Compute/Transfer 三域；位值与 Vulkan 对齐，见各 static_assert）
//   - ZrPresentMode / ZrSampleCount / ZrQueueType / ZrFilter / ZrSamplerAddressMode
//
// 位标志约定：enum class + 运算符重载（ZR_DEFINE_BITWISE_ENUM 生成
// | & ~ |= &= == !=），强类型——不同标志体系之间不可混算。
// =============================================================================
#pragma once

#include <cstdint> // uint32_t / uint64_t

// =============================================================================
// ZrFormat —— 资源格式（→ VkFormat 子集）
// =============================================================================
enum class ZrFormat : uint32_t {
    RGBA8Unorm,      // 4x8bit 无符号归一化（→ VK_FORMAT_R8G8B8A8_UNORM）
    BGRA8Unorm,      // 交换链常用通道序（→ VK_FORMAT_B8G8R8A8_UNORM）
    RGBA8sRGB,       // sRGB 传递函数（→ VK_FORMAT_R8G8B8A8_SRGB）
    R8Unorm,         // 单通道 8bit（→ VK_FORMAT_R8_UNORM）
    R16Float,        // 半精度单通道（→ VK_FORMAT_R16_SFLOAT）
    RG16Float,       // 半精度双通道（→ VK_FORMAT_R16G16_SFLOAT）
    R11G11B10UFloat, // 无符号浮点打包（→ VK_FORMAT_B10G11R11_UFLOAT_PACK32）
    RGBA16Float,     // 半精度四通道（→ VK_FORMAT_R16G16B16A16_SFLOAT）
    R32Float,        // 32bit 浮点（→ VK_FORMAT_R32_SFLOAT）
    RGBA32Float,     // 32bit 浮点四通道（→ VK_FORMAT_R32G32B32A32_SFLOAT）
    R32Uint,         // 32bit 无符号整数（原子读写/SSBO；→ VK_FORMAT_R32_UINT）
    D32Float,        // 深度 32bit 浮点（→ VK_FORMAT_D32_SFLOAT）
    D24UnormS8,      // 深度 24bit + 模板 8bit，4 字节打包（→ VK_FORMAT_D24_UNORM_S8_UINT）
    D16Unorm,        // 深度 16bit（→ VK_FORMAT_D16_UNORM）

    Count, // 哨兵：枚举值个数（供 traits 覆盖检查，不是合法格式）
};

/// 格式 traits：每个 ZrFormat 枚举值必须覆盖（ZrGetFormatTraits 查询）
struct ZrFormatTraits {
    uint32_t bytesPerBlock; // 单块字节数（未压缩格式 = 单像素字节数）
    uint32_t blockW;        // 块宽（像素）——未压缩格式恒 1；压缩格式（BC/ASTC，后续任务）为 4 等
    uint32_t blockH;        // 块高（像素）
    bool hasDepth;          // 含深度分量
    bool hasStencil;        // 含模板分量
    bool isColor;           // 颜色格式（可采样/可作颜色目标）
};

/// 逐格式 traits 表（下标 = ZrFormat 枚举值；static_assert 保证全枚举覆盖）
/// 字段序：{ bytesPerBlock, blockW, blockH, hasDepth, hasStencil, isColor }
constexpr ZrFormatTraits ZR_FORMAT_TRAITS[] = {
    {4u, 1u, 1u, false, false, true},  // RGBA8Unorm
    {4u, 1u, 1u, false, false, true},  // BGRA8Unorm
    {4u, 1u, 1u, false, false, true},  // RGBA8sRGB
    {1u, 1u, 1u, false, false, true},  // R8Unorm
    {2u, 1u, 1u, false, false, true},  // R16Float
    {4u, 1u, 1u, false, false, true},  // RG16Float
    {4u, 1u, 1u, false, false, true},  // R11G11B10UFloat（11+11+10=32bit 打包 4 字节）
    {8u, 1u, 1u, false, false, true},  // RGBA16Float
    {4u, 1u, 1u, false, false, true},  // R32Float
    {16u, 1u, 1u, false, false, true}, // RGBA32Float
    {4u, 1u, 1u, false, false, true},  // R32Uint
    {4u, 1u, 1u, true, false, false},  // D32Float
    {4u, 1u, 1u, true, true, false},   // D24UnormS8（24+8=32bit 打包 4 字节）
    {2u, 1u, 1u, true, false, false},  // D16Unorm
};

static_assert(sizeof(ZR_FORMAT_TRAITS) / sizeof(ZR_FORMAT_TRAITS[0])
        == static_cast<uint32_t>(ZrFormat::Count),
    "traits 表必须覆盖每个 ZrFormat 枚举值");
static_assert(ZR_FORMAT_TRAITS[static_cast<uint32_t>(ZrFormat::RGBA8Unorm)].bytesPerBlock == 4u,
    "RGBA8Unorm 应为 4 字节/块");
static_assert(ZR_FORMAT_TRAITS[static_cast<uint32_t>(ZrFormat::D24UnormS8)].bytesPerBlock == 4u,
    "D24UnormS8 应为 4 字节/块（24+8bit 打包）");
static_assert(!ZR_FORMAT_TRAITS[static_cast<uint32_t>(ZrFormat::D24UnormS8)].isColor
        && ZR_FORMAT_TRAITS[static_cast<uint32_t>(ZrFormat::D24UnormS8)].hasDepth
        && ZR_FORMAT_TRAITS[static_cast<uint32_t>(ZrFormat::D24UnormS8)].hasStencil,
    "D24UnormS8 应为深度+模板格式（非颜色）");

/// 查询格式 traits（format 必须为合法枚举值；Count 哨兵非法）
constexpr const ZrFormatTraits& ZrGetFormatTraits(ZrFormat format)
{
    return ZR_FORMAT_TRAITS[static_cast<uint32_t>(format)];
}

// =============================================================================
// 位标志基座 —— enum class + 运算符（强类型位掩码）
// =============================================================================
#define ZR_DEFINE_BITWISE_ENUM(E)                                                   \
    constexpr E operator|(E a, E b) noexcept                                        \
    {                                                                               \
        return static_cast<E>(static_cast<uint32_t>(a) | static_cast<uint32_t>(b)); \
    }                                                                               \
    constexpr E operator&(E a, E b) noexcept                                        \
    {                                                                               \
        return static_cast<E>(static_cast<uint32_t>(a) & static_cast<uint32_t>(b)); \
    }                                                                               \
    constexpr E operator~(E a) noexcept                                             \
    {                                                                               \
        return static_cast<E>(~static_cast<uint32_t>(a));                           \
    }                                                                               \
    constexpr E& operator|=(E& a, E b) noexcept                                     \
    {                                                                               \
        a = a | b;                                                                  \
        return a;                                                                   \
    }                                                                               \
    constexpr E& operator&=(E& a, E b) noexcept                                     \
    {                                                                               \
        a = a & b;                                                                  \
        return a;                                                                   \
    }                                                                               \
    constexpr bool operator==(E a, E b) noexcept                                    \
    {                                                                               \
        return static_cast<uint32_t>(a) == static_cast<uint32_t>(b);                \
    }                                                                               \
    constexpr bool operator!=(E a, E b) noexcept { return !(a == b); }

/// 位包含测试：value 含 test 的全部位（仅用于位标志枚举）
template <class E>
constexpr bool ZrHasAll(E value, E test) noexcept
{
    return (static_cast<uint32_t>(value) & static_cast<uint32_t>(test))
        == static_cast<uint32_t>(test);
}

/// 位相交测试：value 与 test 至少共有一位（仅用于位标志枚举）
template <class E>
constexpr bool ZrHasAny(E value, E test) noexcept
{
    return (static_cast<uint32_t>(value) & static_cast<uint32_t>(test)) != 0u;
}

// =============================================================================
// ZrImageUsage —— 图像用途位标志（→ VkImageUsageFlagBits，组合即 VkImageUsageFlags）
// =============================================================================
enum class ZrImageUsage : uint32_t {
    None = 0u,
    TransferSrc = 1u << 0,          // 拷贝/blit 源（→ VK_IMAGE_USAGE_TRANSFER_SRC_BIT）
    TransferDst = 1u << 1,          // 拷贝/ blit 目标（→ VK_IMAGE_USAGE_TRANSFER_DST_BIT）
    Sampled = 1u << 2,              // 纹理采样（→ VK_IMAGE_USAGE_SAMPLED_BIT）
    Storage = 1u << 3,              // 无序访问读写（→ VK_IMAGE_USAGE_STORAGE_BIT）
    ColorAttachment = 1u << 4,      // 颜色渲染目标（→ VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT）
    DepthStencilAttachment = 1u << 5, // 深度模板目标（→ VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT）
};
ZR_DEFINE_BITWISE_ENUM(ZrImageUsage)

// =============================================================================
// ZrBufferUsage —— 缓冲用途位标志（→ VkBufferUsageFlagBits，组合即 VkBufferUsageFlags）
// =============================================================================
enum class ZrBufferUsage : uint32_t {
    None = 0u,
    TransferSrc = 1u << 0, // 拷贝源（→ VK_BUFFER_USAGE_TRANSFER_SRC_BIT）
    TransferDst = 1u << 1, // 拷贝目标（→ VK_BUFFER_USAGE_TRANSFER_DST_BIT）
    Uniform = 1u << 2,     // 统一缓冲（→ VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT）
    Storage = 1u << 3,     // SSBO（→ VK_BUFFER_USAGE_STORAGE_BUFFER_BIT）
    Vertex = 1u << 4,      // 顶点拉取（→ VK_BUFFER_USAGE_VERTEX_BUFFER_BIT）
    Index = 1u << 5,       // 索引拉取（→ VK_BUFFER_USAGE_INDEX_BUFFER_BIT）
    Indirect = 1u << 6,    // 间接绘制/分发参数（→ VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT）
};
ZR_DEFINE_BITWISE_ENUM(ZrBufferUsage)

// =============================================================================
// ZrImageLayout —— 图像布局（→ VkImageLayout）
// =============================================================================
enum class ZrImageLayout : uint32_t {
    Undefined = 0,                 // 内容不保证（→ VK_IMAGE_LAYOUT_UNDEFINED）
    General,                       // 万能读写（→ VK_IMAGE_LAYOUT_GENERAL）
    ColorAttachmentOptimal,        // 颜色目标最优（→ VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL）
    DepthStencilAttachmentOptimal, // 深度模板读写最优（→ VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL）
    DepthStencilReadOnlyOptimal,   // 深度模板只读最优（→ VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL）
    ShaderReadOnlyOptimal,         // 着色器采样最优（→ VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL）
    TransferSrcOptimal,            // 拷贝源最优（→ VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL）
    TransferDstOptimal,            // 拷贝目标最优（→ VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL）
    Present,                       // 呈现（→ VK_IMAGE_LAYOUT_PRESENT_SRC_KHR）
};

// =============================================================================
// ZrAccessBits —— 内存访问位标志（→ VkAccessFlagBits 子集）
// 位值与 Vulkan 完全对齐（跳过 Host 域两位）；Graphics/Compute/Transfer 三域：
//   Graphics: IndirectCommandRead/IndexRead/VertexAttributeRead/InputAttachmentRead/
//             ColorAttachment*/DepthStencilAttachment*
//   Compute : ShaderRead/ShaderWrite/UniformRead
//   Transfer: TransferRead/TransferWrite
//   全域兜底: MemoryRead/MemoryWrite（缓存全局可见/可用，→ VK_ACCESS_MEMORY_*_BIT）
// =============================================================================
enum class ZrAccessBits : uint32_t {
    None = 0u,                      // → VK_ACCESS_NONE
    IndirectCommandRead = 1u << 0,  // → VK_ACCESS_INDIRECT_COMMAND_READ_BIT
    IndexRead = 1u << 1,            // → VK_ACCESS_INDEX_READ_BIT
    VertexAttributeRead = 1u << 2,  // → VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT
    UniformRead = 1u << 3,          // → VK_ACCESS_UNIFORM_READ_BIT
    InputAttachmentRead = 1u << 4,  // → VK_ACCESS_INPUT_ATTACHMENT_READ_BIT
    ShaderRead = 1u << 5,           // → VK_ACCESS_SHADER_READ_BIT
    ShaderWrite = 1u << 6,          // → VK_ACCESS_SHADER_WRITE_BIT
    ColorAttachmentRead = 1u << 7,  // → VK_ACCESS_COLOR_ATTACHMENT_READ_BIT
    ColorAttachmentWrite = 1u << 8, // → VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT
    DepthStencilAttachmentRead = 1u << 9,   // → VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT
    DepthStencilAttachmentWrite = 1u << 10, // → VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT
    TransferRead = 1u << 11,        // → VK_ACCESS_TRANSFER_READ_BIT
    TransferWrite = 1u << 12,       // → VK_ACCESS_TRANSFER_WRITE_BIT
    MemoryRead = 1u << 15,          // → VK_ACCESS_MEMORY_READ_BIT（Host 域位 13/14 跳过）
    MemoryWrite = 1u << 16,         // → VK_ACCESS_MEMORY_WRITE_BIT
};
ZR_DEFINE_BITWISE_ENUM(ZrAccessBits)

static_assert(static_cast<uint32_t>(ZrAccessBits::TransferWrite) == 0x1000u,
    "位值须与 VkAccessFlagBits 对齐");
static_assert(static_cast<uint32_t>(ZrAccessBits::MemoryWrite) == 0x10000u,
    "位值须与 VkAccessFlagBits 对齐");

/// 全部"写"访问位（冲突判定的写足迹；读-读无冒险，见 ZrAccessCompatible）
constexpr ZrAccessBits ZR_ACCESS_WRITE_MASK = ZrAccessBits::ShaderWrite
    | ZrAccessBits::ColorAttachmentWrite | ZrAccessBits::DepthStencilAttachmentWrite
    | ZrAccessBits::TransferWrite | ZrAccessBits::MemoryWrite;

/// 全部"读"访问位（与 ZR_ACCESS_WRITE_MASK 互不重叠，单测保证）
constexpr ZrAccessBits ZR_ACCESS_READ_MASK = ZrAccessBits::IndirectCommandRead
    | ZrAccessBits::IndexRead | ZrAccessBits::VertexAttributeRead | ZrAccessBits::UniformRead
    | ZrAccessBits::InputAttachmentRead | ZrAccessBits::ShaderRead
    | ZrAccessBits::ColorAttachmentRead | ZrAccessBits::DepthStencilAttachmentRead
    | ZrAccessBits::TransferRead | ZrAccessBits::MemoryRead;

static_assert(!ZrHasAny(ZR_ACCESS_READ_MASK, ZR_ACCESS_WRITE_MASK),
    "读/写访问位必须互不重叠");

// =============================================================================
// ZrPipelineStageBits —— 管线阶段位标志（→ VkPipelineStageFlagBits 子集，位值对齐）
// Graphics: TopOfPipe..ColorAttachmentOutput；Compute: ComputeShader；Transfer: Transfer
// TopOfPipe/BottomOfPipe 为全阶段通配（Vulkan 1.3 已弃用，保留语义对齐）
// =============================================================================
enum class ZrPipelineStageBits : uint32_t {
    None = 0u,               // → VK_PIPELINE_STAGE_NONE
    TopOfPipe = 1u << 0,     // → VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT
    DrawIndirect = 1u << 1,  // → VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT
    VertexInput = 1u << 2,   // → VK_PIPELINE_STAGE_VERTEX_INPUT_BIT
    VertexShader = 1u << 3,  // → VK_PIPELINE_STAGE_VERTEX_SHADER_BIT
    FragmentShader = 1u << 4, // → VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT
    EarlyFragmentTests = 1u << 5,   // → VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT
    LateFragmentTests = 1u << 6,    // → VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT
    ColorAttachmentOutput = 1u << 7, // → VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT
    ComputeShader = 1u << 8,        // → VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT
    Transfer = 1u << 9,             // → VK_PIPELINE_STAGE_TRANSFER_BIT
    BottomOfPipe = 1u << 10,        // → VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT
};
ZR_DEFINE_BITWISE_ENUM(ZrPipelineStageBits)

static_assert(static_cast<uint32_t>(ZrPipelineStageBits::Transfer) == 0x200u,
    "位值须与 VkPipelineStageFlagBits 对齐");
static_assert(static_cast<uint32_t>(ZrPipelineStageBits::BottomOfPipe) == 0x400u,
    "位值须与 VkPipelineStageFlagBits 对齐");

/// Graphics 域阶段组（→ VK_PIPELINE_STAGE_ALL_GRAPHICS_BITS 语义子集）
constexpr ZrPipelineStageBits ZR_STAGE_GRAPHICS_DOMAIN = ZrPipelineStageBits::DrawIndirect
    | ZrPipelineStageBits::VertexInput | ZrPipelineStageBits::VertexShader
    | ZrPipelineStageBits::FragmentShader | ZrPipelineStageBits::EarlyFragmentTests
    | ZrPipelineStageBits::LateFragmentTests | ZrPipelineStageBits::ColorAttachmentOutput;

/// Compute 域阶段组
constexpr ZrPipelineStageBits ZR_STAGE_COMPUTE_DOMAIN = ZrPipelineStageBits::ComputeShader;

/// Transfer 域阶段组
constexpr ZrPipelineStageBits ZR_STAGE_TRANSFER_DOMAIN = ZrPipelineStageBits::Transfer;

static_assert(!ZrHasAny(ZR_STAGE_GRAPHICS_DOMAIN, ZR_STAGE_COMPUTE_DOMAIN | ZR_STAGE_TRANSFER_DOMAIN),
    "三域阶段组必须互不重叠");
static_assert(!ZrHasAny(ZR_STAGE_COMPUTE_DOMAIN, ZR_STAGE_TRANSFER_DOMAIN),
    "三域阶段组必须互不重叠");

// =============================================================================
// 着色器阶段 / 图形管线固定状态枚举（TASK-B-R4-03 图形 PSO v1）
// =============================================================================

/// 着色器阶段（→ VkShaderStageFlagBits 子集；D3D12：VS/PS）
enum class ZrShaderStage : uint32_t {
    Vertex, // → VK_SHADER_STAGE_VERTEX_BIT（D3D12 顶点着色器）
    Pixel,  // → VK_SHADER_STAGE_FRAGMENT_BIT（D3D12 像素着色器）
};

/// 混合模式（v1 仅不透明预设；→ VkPipelineColorBlendAttachmentState 子集）
enum class ZrBlendMode : uint32_t {
    Opaque, // 不透明：src=1、dst=0 直通（不混合；→ D3D12_BLEND_ONE/ZERO）
};

/// 背面剔除（→ VkCullModeFlagBits；D3D12：FrontCounterClockwise=FALSE 前提下映射）
enum class ZrCullMode : uint32_t {
    None,  // → VK_CULL_MODE_NONE / D3D12_CULL_MODE_NONE
    Back,  // → VK_CULL_MODE_BACK_BIT / D3D12_CULL_MODE_BACK
    Front, // → VK_CULL_MODE_FRONT_BIT / D3D12_CULL_MODE_FRONT
};

// =============================================================================
// 呈现 / 采样 / 队列 / 采样器小枚举
// =============================================================================

/// 呈现模式（→ VkPresentModeKHR）
enum class ZrPresentMode : uint32_t {
    Fifo,     // 垂直同步排队（必支持；→ VK_PRESENT_MODE_FIFO_KHR）
    Mailbox,  // 三缓冲晚撕裂（→ VK_PRESENT_MODE_MAILBOX_KHR）
    Immediate, // 不排队可能撕裂（→ VK_PRESENT_MODE_IMMEDIATE_KHR）
};

/// 采样数（数值即位值，→ VkSampleCountFlagBits：1/2/4/8）
enum class ZrSampleCount : uint32_t {
    One = 1u,
    Two = 2u,
    Four = 4u,
    Eight = 8u,
};

/// 队列域（→ VkQueueFamilyProperties 的队列族划分；单设备按三域映射）
enum class ZrQueueType : uint32_t {
    Graphics, // 图形+计算+传输全能（→ queueFlags 含 GRAPHIC|COMPUTE|TRANSFER）
    Compute,  // 异步计算（→ VK_QUEUE_COMPUTE_BIT）
    Transfer, // 异步拷贝（→ VK_QUEUE_TRANSFER_BIT）
};

/// 过滤方式（→ VkFilter；Cubic 不在本子集）
enum class ZrFilter : uint32_t {
    Nearest, // → VK_FILTER_NEAREST
    Linear,  // → VK_FILTER_LINEAR
};

/// 寻址模式（→ VkSamplerAddressMode）
enum class ZrSamplerAddressMode : uint32_t {
    Repeat,           // → VK_SAMPLER_ADDRESS_MODE_REPEAT
    MirroredRepeat,   // → VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT
    ClampToEdge,      // → VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
    ClampToBorder,    // → VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER
    MirrorClampToEdge, // → VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE
};
