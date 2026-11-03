// =============================================================================
// test_zrhi_types.cpp —— RHI 枚举/traits/屏障模型单测（TASK-B-R2-01）
//
// 覆盖验收清单：
//   1. ZrFormat 每个枚举值 traits 完整且数值合理（独立期望表逐值比对）
//   2. usage 位可组合（含/交/并语义）；access/stage 位单位互不重叠
//   3. layout 只读判定（ZrIsReadOnlyLayout）与访问兼容判定（ZrAccessCompatible）
//   4. 屏障 POD 字段齐全与默认值
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <type_traits>

#include "zrhi/zrhi.h"

namespace {

/// 恰好一个置位（位标志枚举值的基本形状）
bool IsSingleBit(uint32_t v) { return v != 0u && (v & (v - 1u)) == 0u; }

/// 置位计数（互不重叠断言用）
uint32_t PopCount32(uint32_t v)
{
    uint32_t n = 0;
    while (v != 0u) {
        v &= v - 1u;
        ++n;
    }
    return n;
}

} // namespace

TEST_CASE("ZrFormat traits：全枚举覆盖且数值合理")
{
    // 独立期望表（与实现表逐值比对，防止 traits 抄错位）
    struct Expect {
        ZrFormat format;
        uint32_t bytes;
        bool color;
        bool depth;
        bool stencil;
    };
    const Expect expects[] = {
        {ZrFormat::RGBA8Unorm, 4u, true, false, false},
        {ZrFormat::BGRA8Unorm, 4u, true, false, false},
        {ZrFormat::RGBA8sRGB, 4u, true, false, false},
        {ZrFormat::R8Unorm, 1u, true, false, false},
        {ZrFormat::R16Float, 2u, true, false, false},
        {ZrFormat::RG16Float, 4u, true, false, false},
        {ZrFormat::R11G11B10UFloat, 4u, true, false, false},
        {ZrFormat::RGBA16Float, 8u, true, false, false},
        {ZrFormat::R32Float, 4u, true, false, false},
        {ZrFormat::RGBA32Float, 16u, true, false, false},
        {ZrFormat::R32Uint, 4u, true, false, false},
        {ZrFormat::D32Float, 4u, false, true, false},
        {ZrFormat::D24UnormS8, 4u, false, true, true},
        {ZrFormat::D16Unorm, 2u, false, true, false},
    };
    constexpr uint32_t kCount = static_cast<uint32_t>(ZrFormat::Count);
    REQUIRE(sizeof(expects) / sizeof(expects[0]) == kCount); // 期望表与枚举数一致

    for (const Expect& e : expects) {
        const ZrFormatTraits& t = ZrGetFormatTraits(e.format);
        CAPTURE(static_cast<uint32_t>(e.format));
        CHECK(t.bytesPerBlock == e.bytes);
        CHECK(t.bytesPerBlock != 0u); // 非零
        CHECK(t.blockW == 1u);        // 未压缩格式块 1x1
        CHECK(t.blockH == 1u);
        CHECK(t.isColor == e.color);
        CHECK(t.hasDepth == e.depth);
        CHECK(t.hasStencil == e.stencil);
        if (t.hasDepth || t.hasStencil) {
            CHECK(!t.isColor); // 深度/模板格式与颜色互斥
        }
    }
}

TEST_CASE("ZrImageUsage 位可组合与包含/相交查询")
{
    ZrImageUsage const sampled = ZrImageUsage::Sampled;
    ZrImageUsage const color = ZrImageUsage::ColorAttachment;
    ZrImageUsage combined = sampled | color;
    CHECK(ZrHasAll(combined, sampled)); // 并集含成员
    CHECK(ZrHasAll(combined, color));
    CHECK(!ZrHasAll(sampled, combined)); // 子集不含超集
    CHECK(ZrHasAny(combined, ZrImageUsage::Sampled | ZrImageUsage::TransferSrc));
    CHECK(!ZrHasAll(combined, ZrImageUsage::TransferSrc));
    CHECK((combined & color) == color);  // 交运算
    combined |= ZrImageUsage::TransferDst;
    CHECK(ZrHasAll(combined, ZrImageUsage::TransferDst));
    CHECK(static_cast<uint32_t>(ZrImageUsage::None) == 0u);

    // 全枚举器：单 bit 且互不重叠
    const ZrImageUsage usages[] = {ZrImageUsage::TransferSrc, ZrImageUsage::TransferDst,
        ZrImageUsage::Sampled, ZrImageUsage::Storage, ZrImageUsage::ColorAttachment,
        ZrImageUsage::DepthStencilAttachment};
    uint32_t acc = 0;
    uint32_t n = 0;
    for (ZrImageUsage u : usages) {
        CHECK(IsSingleBit(static_cast<uint32_t>(u)));
        acc |= static_cast<uint32_t>(u);
        ++n;
    }
    CHECK(PopCount32(acc) == n);
}

TEST_CASE("ZrBufferUsage 位可组合与单位互不重叠")
{
    ZrBufferUsage const gpuRead = ZrBufferUsage::Storage | ZrBufferUsage::Uniform;
    ZrBufferUsage const vtx = ZrBufferUsage::Vertex | ZrBufferUsage::Index
        | ZrBufferUsage::Indirect;
    CHECK(ZrHasAll(gpuRead, ZrBufferUsage::Uniform));
    CHECK(!ZrHasAny(gpuRead, ZrBufferUsage::Vertex));
    CHECK(!ZrHasAny(gpuRead, vtx)); // 两组不重叠

    const ZrBufferUsage usages[] = {ZrBufferUsage::TransferSrc, ZrBufferUsage::TransferDst,
        ZrBufferUsage::Uniform, ZrBufferUsage::Storage, ZrBufferUsage::Vertex,
        ZrBufferUsage::Index, ZrBufferUsage::Indirect};
    uint32_t acc = 0;
    uint32_t n = 0;
    for (ZrBufferUsage u : usages) {
        CHECK(IsSingleBit(static_cast<uint32_t>(u)));
        acc |= static_cast<uint32_t>(u);
        ++n;
    }
    CHECK(PopCount32(acc) == n);
}

TEST_CASE("ZrAccessBits：单位互不重叠 + 读/写掩码划分")
{
    const ZrAccessBits accesses[] = {ZrAccessBits::IndirectCommandRead, ZrAccessBits::IndexRead,
        ZrAccessBits::VertexAttributeRead, ZrAccessBits::UniformRead,
        ZrAccessBits::InputAttachmentRead, ZrAccessBits::ShaderRead, ZrAccessBits::ShaderWrite,
        ZrAccessBits::ColorAttachmentRead, ZrAccessBits::ColorAttachmentWrite,
        ZrAccessBits::DepthStencilAttachmentRead, ZrAccessBits::DepthStencilAttachmentWrite,
        ZrAccessBits::TransferRead, ZrAccessBits::TransferWrite, ZrAccessBits::MemoryRead,
        ZrAccessBits::MemoryWrite};
    uint32_t acc = 0;
    uint32_t n = 0;
    for (ZrAccessBits a : accesses) {
        CHECK(IsSingleBit(static_cast<uint32_t>(a)));
        acc |= static_cast<uint32_t>(a);
        ++n;
    }
    CHECK(PopCount32(acc) == n); // 任意两个枚举值不共享位

    // 读/写掩码互不重叠且覆盖全部定义位
    CHECK(!ZrHasAny(ZR_ACCESS_READ_MASK, ZR_ACCESS_WRITE_MASK));
    CHECK(PopCount32(static_cast<uint32_t>(ZR_ACCESS_READ_MASK)
              | static_cast<uint32_t>(ZR_ACCESS_WRITE_MASK))
        == n);
    CHECK(static_cast<uint32_t>(ZrAccessBits::TransferWrite) == 0x1000u); // 位值对齐 Vulkan
    CHECK(static_cast<uint32_t>(ZrAccessBits::MemoryWrite) == 0x10000u);
}

TEST_CASE("ZrPipelineStageBits：单位互不重叠 + 三域分组")
{
    const ZrPipelineStageBits stages[] = {ZrPipelineStageBits::TopOfPipe,
        ZrPipelineStageBits::DrawIndirect, ZrPipelineStageBits::VertexInput,
        ZrPipelineStageBits::VertexShader, ZrPipelineStageBits::FragmentShader,
        ZrPipelineStageBits::EarlyFragmentTests, ZrPipelineStageBits::LateFragmentTests,
        ZrPipelineStageBits::ColorAttachmentOutput, ZrPipelineStageBits::ComputeShader,
        ZrPipelineStageBits::Transfer, ZrPipelineStageBits::BottomOfPipe};
    uint32_t acc = 0;
    uint32_t n = 0;
    for (ZrPipelineStageBits s : stages) {
        CHECK(IsSingleBit(static_cast<uint32_t>(s)));
        acc |= static_cast<uint32_t>(s);
        ++n;
    }
    CHECK(PopCount32(acc) == n);
    CHECK(static_cast<uint32_t>(ZrPipelineStageBits::Transfer) == 0x200u); // 位值对齐 Vulkan
    CHECK(static_cast<uint32_t>(ZrPipelineStageBits::BottomOfPipe) == 0x400u);

    // Graphics/Compute/Transfer 三域互不重叠
    CHECK(!ZrHasAny(ZR_STAGE_GRAPHICS_DOMAIN, ZR_STAGE_COMPUTE_DOMAIN));
    CHECK(!ZrHasAny(ZR_STAGE_GRAPHICS_DOMAIN, ZR_STAGE_TRANSFER_DOMAIN));
    CHECK(!ZrHasAny(ZR_STAGE_COMPUTE_DOMAIN, ZR_STAGE_TRANSFER_DOMAIN));
}

TEST_CASE("ZrImageLayout 只读判定（ZrIsReadOnlyLayout）")
{
    CHECK(ZrIsReadOnlyLayout(ZrImageLayout::ShaderReadOnlyOptimal));
    CHECK(ZrIsReadOnlyLayout(ZrImageLayout::DepthStencilReadOnlyOptimal));
    CHECK(ZrIsReadOnlyLayout(ZrImageLayout::TransferSrcOptimal));
    CHECK(ZrIsReadOnlyLayout(ZrImageLayout::Present));
    CHECK(!ZrIsReadOnlyLayout(ZrImageLayout::Undefined));
    CHECK(!ZrIsReadOnlyLayout(ZrImageLayout::General));
    CHECK(!ZrIsReadOnlyLayout(ZrImageLayout::ColorAttachmentOptimal));
    CHECK(!ZrIsReadOnlyLayout(ZrImageLayout::DepthStencilAttachmentOptimal));
    CHECK(!ZrIsReadOnlyLayout(ZrImageLayout::TransferDstOptimal));
}

TEST_CASE("ZrAccessCompatible：只读区间可共享屏障（读-读兼容，含写即冲突）")
{
    using A = ZrAccessBits;
    CHECK(ZrAccessCompatible(A::ShaderRead, A::ShaderRead));           // 读-读
    CHECK(ZrAccessCompatible(A::ShaderRead | A::InputAttachmentRead,
        A::ColorAttachmentRead | A::TransferRead));                    // 混合纯读
    CHECK(!ZrAccessCompatible(A::ShaderWrite, A::ShaderRead));         // RAW
    CHECK(!ZrAccessCompatible(A::ShaderRead, A::ShaderWrite));         // WAR（对称）
    CHECK(!ZrAccessCompatible(A::ShaderWrite, A::ShaderWrite));        // WAW
    CHECK(!ZrAccessCompatible(A::TransferWrite, A::ColorAttachmentRead)); // 跨域写-读
    CHECK(!ZrAccessCompatible(A::ShaderRead | A::ShaderWrite, A::TransferRead)); // 含写位即冲突
    CHECK(ZrAccessCompatible(A::None, A::ShaderWrite));                // 无访问不产生冒险
    CHECK(ZrAccessCompatible(A::TransferWrite, A::None));

    // 纯读判定辅助与 ZR_ACCESS_WRITE_MASK 一致
    CHECK(ZrIsReadOnlyAccess(A::ShaderRead | A::MemoryRead));
    CHECK(!ZrIsReadOnlyAccess(A::ShaderRead | A::TransferWrite));
    CHECK(ZrIsReadOnlyAccess(A::None));
}

TEST_CASE("屏障 POD：字段齐全、默认值与整资源 range")
{
    static_assert(std::is_standard_layout_v<ZrImageBarrier> && std::is_trivially_copyable_v<ZrImageBarrier>,
        "ZrImageBarrier 必须为 POD");
    static_assert(std::is_standard_layout_v<ZrBufferBarrier> && std::is_trivially_copyable_v<ZrBufferBarrier>,
        "ZrBufferBarrier 必须为 POD");
    static_assert(std::is_standard_layout_v<ZrSubresourceRange> && std::is_trivially_copyable_v<ZrSubresourceRange>,
        "ZrSubresourceRange 必须为 POD");

    ZrImageBarrier ib{};
    CHECK(!ib.image.IsValid());                              // 无效句柄默认
    CHECK(ib.prevLayout == ZrImageLayout::Undefined);
    CHECK(ib.newLayout == ZrImageLayout::Undefined);
    CHECK(ib.subresources.levelCount == ZR_REMAINING_MIP_LEVELS);   // 整资源
    CHECK(ib.subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS);
    CHECK(ib.srcAccess == ZrAccessBits::None);
    CHECK(ib.dstAccess == ZrAccessBits::None);
    CHECK(ib.srcStage == ZrPipelineStageBits::None);
    CHECK(ib.dstStage == ZrPipelineStageBits::None);

    ZrBufferBarrier bb{};
    CHECK(!bb.buffer.IsValid());
    CHECK(bb.offset == 0u);
    CHECK(bb.size == ZR_WHOLE_SIZE); // → VK_WHOLE_SIZE
    CHECK(bb.dstAccess == ZrAccessBits::None);

    // 子资源窗口与整资源帮助构造
    ZrSubresourceRange window{};
    window.baseMipLevel = 1u;
    window.levelCount = 2u;
    window.baseArrayLayer = 3u;
    window.layerCount = 4u;
    CHECK(window.baseMipLevel == 1u);
    CHECK(window.levelCount == 2u);
    const ZrSubresourceRange all = ZrSubresourceRange::All();
    CHECK(all.baseMipLevel == 0u);
    CHECK(all.levelCount == ZR_REMAINING_MIP_LEVELS);
    CHECK(all.layerCount == ZR_REMAINING_ARRAY_LAYERS);
}

TEST_CASE("ZrPresentMode/ZrSampleCount/ZrQueueType/ZrFilter 值域")
{
    // 采样数值即 Vulkan 位值（1/2/4/8）
    CHECK(static_cast<uint32_t>(ZrSampleCount::One) == 1u);
    CHECK(static_cast<uint32_t>(ZrSampleCount::Two) == 2u);
    CHECK(static_cast<uint32_t>(ZrSampleCount::Four) == 4u);
    CHECK(static_cast<uint32_t>(ZrSampleCount::Eight) == 8u);

    // 呈现模式/队列域互异（可作 switch 分派键）
    CHECK(static_cast<uint32_t>(ZrPresentMode::Fifo) != static_cast<uint32_t>(ZrPresentMode::Mailbox));
    CHECK(static_cast<uint32_t>(ZrPresentMode::Mailbox) != static_cast<uint32_t>(ZrPresentMode::Immediate));
    CHECK(static_cast<uint32_t>(ZrQueueType::Graphics) != static_cast<uint32_t>(ZrQueueType::Compute));
    CHECK(static_cast<uint32_t>(ZrQueueType::Compute) != static_cast<uint32_t>(ZrQueueType::Transfer));
    CHECK(static_cast<uint32_t>(ZrFilter::Nearest) != static_cast<uint32_t>(ZrFilter::Linear));
}
