// =============================================================================
// test_zrhi_d3d12_translate.cpp —— D3D12 映射表单测（TASK-B-R2-02）
//
// 覆盖验收：三个纯函数映射全枚举断言（无需 GPU，纯查表逻辑）：
//   1. ZrFormat→DXGI_FORMAT：独立期望表逐值比对 + 全枚举互异（无重复误配）
//   2. ZrImageLayout→D3D12_RESOURCE_STATES：9 个布局逐一显式映射、互异、
//      Undefined 恰为 COMMON(0)（与 D3D12 编译期常量直接对值，防抄错）
//   3. ZrPresentMode→(SyncInterval, Flags)：三值参数正确且互异
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/d3d12/zrhi_d3d12_translate.h"

#include <cstdint>

TEST_CASE("ZrFormat→DXGI_FORMAT：全枚举覆盖、逐值正确且互异")
{
    // 独立期望表（与实现 switch 逐值比对，防止映射抄错位）
    struct Expect {
        ZrFormat format;
        DXGI_FORMAT dxgi;
    };
    const Expect expects[] = {
        {ZrFormat::RGBA8Unorm, DXGI_FORMAT_R8G8B8A8_UNORM},
        {ZrFormat::BGRA8Unorm, DXGI_FORMAT_B8G8R8A8_UNORM},
        {ZrFormat::RGBA8sRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB},
        {ZrFormat::R8Unorm, DXGI_FORMAT_R8_UNORM},
        {ZrFormat::R16Float, DXGI_FORMAT_R16_FLOAT},
        {ZrFormat::RG16Float, DXGI_FORMAT_R16G16_FLOAT},
        {ZrFormat::R11G11B10UFloat, DXGI_FORMAT_R11G11B10_FLOAT},
        {ZrFormat::RGBA16Float, DXGI_FORMAT_R16G16B16A16_FLOAT},
        {ZrFormat::R32Float, DXGI_FORMAT_R32_FLOAT},
        {ZrFormat::RGBA32Float, DXGI_FORMAT_R32G32B32A32_FLOAT},
        {ZrFormat::R32Uint, DXGI_FORMAT_R32_UINT},
        {ZrFormat::D32Float, DXGI_FORMAT_D32_FLOAT},
        {ZrFormat::D24UnormS8, DXGI_FORMAT_D24_UNORM_S8_UINT},
        {ZrFormat::D16Unorm, DXGI_FORMAT_D16_UNORM},
    };
    constexpr uint32_t kCount = static_cast<uint32_t>(ZrFormat::Count);
    REQUIRE(sizeof(expects) / sizeof(expects[0]) == kCount); // 期望表与枚举数一致

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrD3D12_Format(expects[i].format) == expects[i].dxgi);
        CHECK(ZrD3D12_Format(expects[i].format) != DXGI_FORMAT_UNKNOWN); // 合法值必映射
    }
    // 互异：任意两个格式不得映射到同一 DXGI 值（无重复误配）
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CHECK(ZrD3D12_Format(expects[i].format) != ZrD3D12_Format(expects[j].format));
        }
    }
}

TEST_CASE("ZrImageLayout→D3D12_RESOURCE_STATES：显式映射、互异且语义正确")
{
    // 独立期望表（与实现 switch 逐值比对；D3D12 常量直接对值，防隐式序号转换）
    struct Expect {
        ZrImageLayout layout;
        D3D12_RESOURCE_STATES states;
    };
    const Expect expects[] = {
        {ZrImageLayout::Undefined, D3D12_RESOURCE_STATE_COMMON},
        {ZrImageLayout::General, D3D12_RESOURCE_STATE_UNORDERED_ACCESS},
        {ZrImageLayout::ColorAttachmentOptimal, D3D12_RESOURCE_STATE_RENDER_TARGET},
        {ZrImageLayout::DepthStencilAttachmentOptimal, D3D12_RESOURCE_STATE_DEPTH_WRITE},
        {ZrImageLayout::DepthStencilReadOnlyOptimal, D3D12_RESOURCE_STATE_DEPTH_READ},
        {ZrImageLayout::ShaderReadOnlyOptimal,
            D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
                | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE},
        {ZrImageLayout::TransferSrcOptimal, D3D12_RESOURCE_STATE_COPY_SOURCE},
        {ZrImageLayout::TransferDstOptimal, D3D12_RESOURCE_STATE_COPY_DEST},
        {ZrImageLayout::Present, D3D12_RESOURCE_STATE_PRESENT},
    };
    constexpr uint32_t kCount = 9u; // ZrImageLayout 枚举值个数（无 Count 哨兵）

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrD3D12_ResourceStates(expects[i].layout) == expects[i].states);
    }
    // 互异：任意两个布局不得映射到同一状态组合（无重复误配）。
    // 唯一例外：Undefined/Common 与 Present——D3D12 SDK 中
    // D3D12_RESOURCE_STATE_PRESENT == D3D12_RESOURCE_STATE_COMMON == 0（呈现态即
    // 公共态，d3d12.h 显式定义），映射数值重合是 D3D12 自身设计而非误配
    CHECK(ZrD3D12_ResourceStates(ZrImageLayout::Present)
        == D3D12_RESOURCE_STATE_PRESENT); // 语义映射目标正确
    CHECK(D3D12_RESOURCE_STATE_PRESENT == D3D12_RESOURCE_STATE_COMMON); // SDK 事实
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            const bool sdkDefinedCoincidence = (i == 0u && j == 8u); // Undefined/Present
            if (sdkDefinedCoincidence) {
                continue;
            }
            CAPTURE(i);
            CHECK(ZrD3D12_ResourceStates(expects[i].layout)
                != ZrD3D12_ResourceStates(expects[j].layout));
        }
    }
    // 关键语义：Undefined→COMMON(0)（D3D12 初始态）；Present 与 D3D12 常量对值
    CHECK(static_cast<uint32_t>(ZrD3D12_ResourceStates(ZrImageLayout::Undefined)) == 0u);
    CHECK(ZrD3D12_ResourceStates(ZrImageLayout::Present) == D3D12_RESOURCE_STATE_PRESENT);
    CHECK(ZrD3D12_ResourceStates(ZrImageLayout::ColorAttachmentOptimal)
        == D3D12_RESOURCE_STATE_RENDER_TARGET);
}

// =============================================================================
// TASK-B-R2-03：采样器映射（Filter / AddressMode）
// =============================================================================
TEST_CASE("ZrD3D12_Filter：8 种过滤组合显式映射且互异")
{
    const ZrFilter point = ZrFilter::Nearest;
    const ZrFilter linear = ZrFilter::Linear;

    // 独立期望表（枚举名即语义：MIN(+)MAG(+)MIP(+) 的点/线性组合）
    struct Expect {
        ZrFilter mag;
        ZrFilter min;
        ZrFilter mip;
        D3D12_FILTER filter;
    };
    const Expect expects[] = {
        {point, point, point, D3D12_FILTER_MIN_MAG_MIP_POINT},
        {point, point, linear, D3D12_FILTER_MIN_MAG_POINT_MIP_LINEAR},
        {point, linear, point, D3D12_FILTER_MIN_LINEAR_MAG_MIP_POINT},
        {point, linear, linear, D3D12_FILTER_MIN_LINEAR_MAG_POINT_MIP_LINEAR},
        {linear, point, point, D3D12_FILTER_MIN_POINT_MAG_LINEAR_MIP_POINT},
        {linear, point, linear, D3D12_FILTER_MIN_POINT_MAG_MIP_LINEAR},
        {linear, linear, point, D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT},
        {linear, linear, linear, D3D12_FILTER_MIN_MAG_MIP_LINEAR},
    };
    constexpr uint32_t kCount = 8u;

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrD3D12_Filter(expects[i].mag, expects[i].min, expects[i].mip)
            == expects[i].filter);
    }
    // 互异：任意两组合不得映射到同一 FILTER（无重复误配）
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CAPTURE(j);
            CHECK(ZrD3D12_Filter(expects[i].mag, expects[i].min, expects[i].mip)
                != ZrD3D12_Filter(expects[j].mag, expects[j].min, expects[j].mip));
        }
    }
}

TEST_CASE("ZrD3D12_AddressMode：全枚举显式映射且互异")
{
    struct Expect {
        ZrSamplerAddressMode mode;
        D3D12_TEXTURE_ADDRESS_MODE address;
    };
    const Expect expects[] = {
        {ZrSamplerAddressMode::Repeat, D3D12_TEXTURE_ADDRESS_MODE_WRAP},
        {ZrSamplerAddressMode::MirroredRepeat, D3D12_TEXTURE_ADDRESS_MODE_MIRROR},
        {ZrSamplerAddressMode::ClampToEdge, D3D12_TEXTURE_ADDRESS_MODE_CLAMP},
        {ZrSamplerAddressMode::ClampToBorder, D3D12_TEXTURE_ADDRESS_MODE_BORDER},
        {ZrSamplerAddressMode::MirrorClampToEdge, D3D12_TEXTURE_ADDRESS_MODE_MIRROR_ONCE},
    };
    constexpr uint32_t kCount = 5u; // ZrSamplerAddressMode 枚举值个数（无哨兵）

    for (uint32_t i = 0; i < kCount; ++i) {
        CAPTURE(i);
        CHECK(ZrD3D12_AddressMode(expects[i].mode) == expects[i].address);
    }
    for (uint32_t i = 0; i < kCount; ++i) {
        for (uint32_t j = i + 1; j < kCount; ++j) {
            CAPTURE(i);
            CAPTURE(j);
            CHECK(ZrD3D12_AddressMode(expects[i].mode) != ZrD3D12_AddressMode(expects[j].mode));
        }
    }
}

TEST_CASE("ZrPresentMode→Present(SyncInterval, Flags)：三值参数正确且互异")
{
    // Fifo：垂直同步（SyncInterval 1，无标志）
    const ZrD3D12PresentParams fifo = ZrD3D12_PresentParams(ZrPresentMode::Fifo);
    CHECK(fifo.syncInterval == 1u);
    CHECK(fifo.presentFlags == 0u);

    // Mailbox：FLIP 下即时替换（SyncInterval 0，无标志）
    const ZrD3D12PresentParams mailbox = ZrD3D12_PresentParams(ZrPresentMode::Mailbox);
    CHECK(mailbox.syncInterval == 0u);
    CHECK(mailbox.presentFlags == 0u);

    // Immediate：立即呈现 + 允许撕裂（能力不支持时后端降级去标志，映射表本身恒定）
    const ZrD3D12PresentParams immediate = ZrD3D12_PresentParams(ZrPresentMode::Immediate);
    CHECK(immediate.syncInterval == 0u);
    CHECK(immediate.presentFlags == DXGI_PRESENT_ALLOW_TEARING);

    // 互异（doctest 分解器不支持 || 复合表达式：先求值再断言）
    const bool fifoVsMailbox = (fifo.syncInterval != mailbox.syncInterval)
        || (fifo.presentFlags != mailbox.presentFlags);
    const bool mailboxVsImmediate = (mailbox.syncInterval != immediate.syncInterval)
        || (mailbox.presentFlags != immediate.presentFlags);
    const bool fifoVsImmediate = (fifo.syncInterval != immediate.syncInterval)
        || (fifo.presentFlags != immediate.presentFlags);
    CHECK(fifoVsMailbox);
    CHECK(mailboxVsImmediate);
    CHECK(fifoVsImmediate);
}

// =============================================================================
// TASK-H-02 D3：Present 结果分类（Present 的 HRESULT 不得被静默丢弃）
// =============================================================================
TEST_CASE("ZrD3D12PresentStatus: Present 失败累计且设备移除粘滞（TASK-H-02 D3）")
{
    ZrD3D12PresentStatus status;
    CHECK_FALSE(status.HasFailure());
    CHECK(status.failureCount == 0u);
    CHECK(status.lastFailureHresult == 0);

    // 成功码：S_OK 与 DXGI_STATUS_OCCLUDED（被遮挡/最小化属正常状态，不计失败）
    status.Note(S_OK);
    status.Note(DXGI_STATUS_OCCLUDED);
    CHECK_FALSE(status.HasFailure());
    CHECK(status.lastFailureHresult == 0);
    CHECK_FALSE(status.deviceRemoved);

    // 非致命失败：被遮挡仍不足以呈现（WAS_STILL_DRAWING）→ 计数但不置移除位
    status.Note(DXGI_ERROR_WAS_STILL_DRAWING);
    CHECK(status.failureCount == 1u);
    CHECK(status.lastFailureHresult == DXGI_ERROR_WAS_STILL_DRAWING);
    CHECK_FALSE(status.deviceRemoved);

    // 设备移除：计数 + 粘滞位
    status.Note(DXGI_ERROR_DEVICE_REMOVED);
    CHECK(status.failureCount == 2u);
    CHECK(status.lastFailureHresult == DXGI_ERROR_DEVICE_REMOVED);
    CHECK(status.deviceRemoved);

    // 之后的成功不清除历史（粘滞：上层仍需重建设备）
    status.Note(S_OK);
    CHECK(status.failureCount == 2u);
    CHECK(status.lastFailureHresult == DXGI_ERROR_DEVICE_REMOVED);
    CHECK(status.deviceRemoved);

    // 复位 / 驱动内部错误同样视为设备失效
    ZrD3D12PresentStatus resetStatus;
    resetStatus.Note(DXGI_ERROR_DEVICE_RESET);
    CHECK(resetStatus.deviceRemoved);
    ZrD3D12PresentStatus driverStatus;
    driverStatus.Note(DXGI_ERROR_DRIVER_INTERNAL_ERROR);
    CHECK(driverStatus.deviceRemoved);
}
