// =============================================================================
// test_zrhi_barrier_truncate.cpp —— 屏障批次截断可观测（TASK-R10-05 B-5）
//
// 历史缺陷（R10-02 B-5）：Vulkan 命令表 EmitBarriers 对超过单批上限（128）的
// 屏障批次静默截断——多出的屏障被丢弃且无日志无计数 → 缺失同步（花屏/脏读）
// 排查成本极高。
//
// 修复：截断必留痕——warning 一条 + 计入 IZrDevice::GetDeviceStats() 的
// barrierTruncationCount（尾追加字段，写入点 NoteBarrierTruncation）。
//
// 用例（简报验收点：构造 >128 批次断言计数与告警）在**真 Vulkan 设备**上跑
// （与 test_zrhi_device_stats 的 F1 读链用例同一开关 ZR_HAS_VULKAN）：
//   · 130 条 image 屏障 → 128 发出 + 2 丢弃 → 计数 2；
//   · 不超限的批次 → 计数不变（累计语义，不是覆盖）；
//   · 再次超限 → 累计增长。
// 变异验证：EmitBarriers 改回"循环条件带 imageCount < MAX 且不留痕"→ 计数恒 0，
// 本文件红。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/zrhi_device.h"

#if defined(ZR_HAS_VULKAN)
#include "zrhi/vulkan/zrhi_vulkan_device.h"

#include <cstdint>
#include <vector>

namespace {

/// 真 Vulkan 设备（不可用则本文件整体降级——与 F1 读链用例同一约定）
ZrVulkanDevice* AcquireVulkanDevice()
{
    return ZrVulkanCreateDevice();
}

} // namespace

TEST_CASE("zrhi vulkan: emitting >128 barriers counts truncation into GetDeviceStats (B-5)")
{
    ZrVulkanDevice* device = AcquireVulkanDevice();
    if (device == nullptr) {
        MESSAGE("Vulkan 1.3 设备不可用 -> B-5 读链用例降级（本机应可用）");
        return;
    }
    IZrDevice* const rhi = device;

    const ZrDeviceStats baseline = rhi->GetDeviceStats();

    // ---- 130 张最小图像（屏障目标只需可解析的 VkImage）--------------------------
    constexpr uint32_t kBarrierCount = 130u; // > ZR_VULKAN_MAX_BARRIERS(128)
    std::vector<ZrImage> images;
    images.reserve(kBarrierCount);
    for (uint32_t i = 0; i < kBarrierCount; ++i) {
        ZrImageDesc desc {};
        desc.format = ZrFormat::RGBA8Unorm;
        desc.width = 4u;
        desc.height = 4u;
        desc.usage = ZrImageUsage::Sampled | ZrImageUsage::TransferDst;
        ZrImage img = rhi->CreateImage(desc);
        REQUIRE(img.IsValid()); // R10-02 A-5/A-6 教训：失败路径不许静默
        images.push_back(img);
    }

    // ---- 命令表：Begin → 130 条屏障（截断 2 条）→ End ---------------------------
    ZrCommandList clHandle = rhi->CreateCommandList(ZrQueueType::Graphics);
    REQUIRE(clHandle.IsValid());
    IZrCommandList* cl = rhi->GetCommandList(clHandle);
    REQUIRE(cl != nullptr);
    cl->Begin();

    std::vector<ZrImageBarrier> barriers;
    barriers.reserve(kBarrierCount);
    for (uint32_t i = 0; i < kBarrierCount; ++i) {
        ZrImageBarrier b {};
        b.image = images[i];
        b.prevLayout = ZrImageLayout::Undefined;
        b.newLayout = ZrImageLayout::TransferDstOptimal;
        b.srcAccess = ZrAccessBits::None;
        b.dstAccess = ZrAccessBits::TransferWrite;
        b.srcStage = ZrPipelineStageBits::TopOfPipe;
        b.dstStage = ZrPipelineStageBits::Transfer;
        barriers.push_back(b);
    }
    cl->ResourceBarrier(barriers.data(), static_cast<uint32_t>(barriers.size()), nullptr, 0u);
    cl->End();

    // ---- 计数断言：130 条 → 128 发出 + 2 丢弃 -----------------------------------
    const ZrDeviceStats after = rhi->GetDeviceStats();
    CHECK(after.barrierTruncationCount == baseline.barrierTruncationCount + 2u);

    // ---- 不超限的批次：计数不动（累计语义，不是覆盖）----------------------------
    cl->Begin();
    cl->ResourceBarrier(barriers.data(), 4u, nullptr, 0u); // 4 < 128，无截断
    cl->End();
    CHECK(rhi->GetDeviceStats().barrierTruncationCount == after.barrierTruncationCount);

    // ---- 再次超限：累计增长（不是重置）------------------------------------------
    cl->Begin();
    cl->ResourceBarrier(barriers.data(), static_cast<uint32_t>(barriers.size()), nullptr, 0u);
    cl->End();
    const ZrDeviceStats finalStats = rhi->GetDeviceStats();
    CHECK(finalStats.barrierTruncationCount == after.barrierTruncationCount + 2u);

    // ---- 清理（析构前资源须全部归还——设备析构有存活断言）------------------------
    rhi->Destroy(clHandle);
    for (ZrImage img : images) {
        rhi->Destroy(img);
    }
    ZrVulkanDestroyDevice(device);
}
#endif // ZR_HAS_VULKAN
