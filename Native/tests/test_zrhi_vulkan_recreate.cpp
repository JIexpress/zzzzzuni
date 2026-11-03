// =============================================================================
// test_zrhi_vulkan_recreate.cpp —— 交换链真重建状态机（TASK-B-R9-09）
//
// R10-03 遗留补全的可证伪单测：帧闸（zrhi_vulkan_frame_gate.h）只保证
// OUT_OF_DATE 后"安全跳过"（needsRecreate 粘性），本任务把跳过升级为重建后
// 恢复。这里直接驱动产品代码用的同一份纯逻辑
//（zrhi/vulkan/zrhi_vulkan_recreate.h 的尺寸裁决 + 台账 + frame_gate.h 的粘性
// 清除），复现完整状态机：
//   · needsRecreate 粘性 → 每帧入口最多一次重建尝试（无自旋 → 不挂死）；
//   · 重建失败（最小化零尺寸）→ 粘性保留，帧继续跳过（失败回粘性）；
//   · 窗口恢复尺寸 → 重建成功 → 尺寸推进 + serial 自增 + 粘性清除 → 渲染恢复。
// 资源侧的真实重建（排空/销毁/重枚举）在 zrhi_vulkan_device.cpp
// ZrVulkanSwapchain::Recreate()，由 --resize 模式真机验证（不在此文件）。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/vulkan/zrhi_vulkan_frame_gate.h"
#include "zrhi/vulkan/zrhi_vulkan_recreate.h"

using zr::FrameGate;
using zr::RecreateExtent;
using zr::ResolveRecreateExtent;
using zr::SwapchainResizeLedger;
using zr::VkOutcome;

namespace {

/// 模拟"一帧"的产品调用次序（ZrVulkanSwapchain::AcquireNextImage 的入口逻辑 +
/// 帧闸 + 台账；extent 由调用方以表面能力喂入——真机上是 caps.currentExtent）。
///
/// 返回本帧是否真的渲染了（acquire 成功且可提交）。重建尝试发生在帧入口，
/// 每帧最多一次——这是"不挂死"的结构性保证（无内层自旋）。
bool SimulateFrame(FrameGate& gate, SwapchainResizeLedger& ledger, uint32_t currentW,
    uint32_t currentH, bool* attemptedRecreate = nullptr)
{
    // 帧入口（AcquireNextImage 开头）：粘性标记 → 尝试真重建
    if (gate.NeedsRecreate()) {
        if (attemptedRecreate != nullptr) {
            *attemptedRecreate = true;
        }
        const RecreateExtent extent = ResolveRecreateExtent(currentW, currentH, 0u, 0u, 0u, 0u,
            ledger.toWidth, ledger.toHeight);
        if (extent.accepted) {
            REQUIRE(ledger.RecordSuccess(extent.width, extent.height));
            gate.OnSwapchainRecreated(); // 清粘性
        } else {
            ledger.RecordFailure(); // 失败回粘性（不清标记）
        }
    }
    // acquire 结果（真机：窗口尺寸不符时驱动返回 OUT_OF_DATE）
    const bool sizeMatches
        = (currentW == ledger.toWidth && currentH == ledger.toHeight);
    gate.BeginFrame(sizeMatches ? VkOutcome::Success : VkOutcome::OutOfDate);
    if (!gate.CanSubmit()) {
        return false; // 帧跳过（不提交不呈现）
    }
    gate.OnSubmit(VkOutcome::Success);
    gate.OnPresent(VkOutcome::Success);
    return true;
}

} // namespace

TEST_CASE("zrhi vk recreate: 尺寸裁决 —— 正常/最小化零尺寸/特殊值/钳制")
{
    // 正常：currentExtent 权威
    const RecreateExtent normal = ResolveRecreateExtent(480u, 360u, 64u, 64u, 3840u, 2160u,
        640u, 480u);
    CHECK(normal.accepted);
    CHECK(normal.width == 480u);
    CHECK(normal.height == 360u);

    // 最小化（0,0）→ 拒绝（粘性保留的根因）
    const RecreateExtent minimized
        = ResolveRecreateExtent(0u, 0u, 0u, 0u, 0u, 0u, 640u, 480u);
    CHECK_FALSE(minimized.accepted);
    const RecreateExtent minimizedW
        = ResolveRecreateExtent(0u, 360u, 0u, 0u, 0u, 0u, 640u, 480u);
    CHECK_FALSE(minimizedW.accepted);
    const RecreateExtent minimizedH
        = ResolveRecreateExtent(480u, 0u, 0u, 0u, 0u, 0u, 640u, 480u);
    CHECK_FALSE(minimizedH.accepted);

    // 特殊值 0xFFFFFFFF（"尺寸由应用决定"）→ 回落建链尺寸（win32 不出现的防御）
    const RecreateExtent special = ResolveRecreateExtent(zr::kVkExtentUnknown,
        zr::kVkExtentUnknown, 0u, 0u, 0u, 0u, 640u, 480u);
    CHECK(special.accepted);
    CHECK(special.width == 640u);
    CHECK(special.height == 480u);

    // 钳入 [minImageExtent, maxImageExtent]
    const RecreateExtent clampedLow
        = ResolveRecreateExtent(32u, 32u, 64u, 64u, 3840u, 2160u, 640u, 480u);
    CHECK(clampedLow.accepted);
    CHECK(clampedLow.width == 64u);
    CHECK(clampedLow.height == 64u);
    const RecreateExtent clampedHigh
        = ResolveRecreateExtent(7680u, 4320u, 64u, 64u, 3840u, 2160u, 640u, 480u);
    CHECK(clampedHigh.accepted);
    CHECK(clampedHigh.width == 3840u);
    CHECK(clampedHigh.height == 2160u);
}

TEST_CASE("zrhi vk recreate: 台账 —— 建链登记 / 成功推进 / 失败计数")
{
    SwapchainResizeLedger ledger;
    ledger.RecordCreated(640u, 480u);
    CHECK(ledger.fromWidth == 640u);
    CHECK(ledger.fromHeight == 480u);
    CHECK(ledger.toWidth == 640u);
    CHECK(ledger.toHeight == 480u);
    CHECK(ledger.serial == 0u);
    CHECK_FALSE(ledger.LastRecreateChangedSize());

    // 重建成功（窗口变小 640x480 → 480x360）：尺寸推进 + serial 自增
    CHECK(ledger.RecordSuccess(480u, 360u));
    CHECK(ledger.fromWidth == 640u);
    CHECK(ledger.fromHeight == 480u);
    CHECK(ledger.toWidth == 480u);
    CHECK(ledger.toHeight == 360u);
    CHECK(ledger.serial == 1u);
    CHECK(ledger.LastRecreateChangedSize());

    // 同尺寸重建（如最小化后恢复原尺寸）：serial 仍自增，但不算改尺寸
    CHECK(ledger.RecordSuccess(480u, 360u));
    CHECK(ledger.serial == 2u);
    CHECK_FALSE(ledger.LastRecreateChangedSize());

    // 零尺寸拒绝入账（裁决前置失败的防御）
    CHECK_FALSE(ledger.RecordSuccess(0u, 360u));
    CHECK(ledger.serial == 2u);
    CHECK(ledger.toWidth == 480u);

    // 失败尝试只计不进 serial
    ledger.RecordFailure();
    ledger.RecordFailure();
    CHECK(ledger.failedAttempts == 2u);
    CHECK(ledger.serial == 2u);
}

TEST_CASE("zrhi vk recreate: 状态机主路径 —— resize → 重建 → 恢复渲染（清除粘性）")
{
    FrameGate gate;
    SwapchainResizeLedger ledger;
    ledger.RecordCreated(640u, 480u);

    // 基线：640x480 正常渲染
    CHECK(SimulateFrame(gate, ledger, 640u, 480u));

    // 窗口变小 480x360：acquire OUT_OF_DATE → 帧闸粘性（R10-03 A-5 既有语义）
    CHECK_FALSE(SimulateFrame(gate, ledger, 480u, 360u));
    CHECK(gate.NeedsRecreate());
    CHECK(gate.RecreateRequests() == 1u);
    CHECK(ledger.serial == 0u);

    // 下一帧入口：粘性 → 自动重建成功（产品路径）→ 粘性清除 → 本帧恢复渲染
    bool attempted = false;
    CHECK(SimulateFrame(gate, ledger, 480u, 360u, &attempted));
    CHECK(attempted);
    CHECK_FALSE(gate.NeedsRecreate());
    CHECK(ledger.serial == 1u);
    CHECK(ledger.fromWidth == 640u);
    CHECK(ledger.fromHeight == 480u);
    CHECK(ledger.toWidth == 480u);
    CHECK(ledger.toHeight == 360u);

    // 之后每帧正常（不再触发重建尝试）
    for (int i = 0; i < 5; ++i) {
        attempted = false;
        CHECK(SimulateFrame(gate, ledger, 480u, 360u, &attempted));
        CHECK_FALSE(attempted);
    }
    CHECK(ledger.serial == 1u);
    CHECK(ledger.failedAttempts == 0u);
}

TEST_CASE("zrhi vk recreate: 失败回粘性 —— 最小化期间逐帧重试不挂死、不清标记")
{
    FrameGate gate;
    SwapchainResizeLedger ledger;
    ledger.RecordCreated(640u, 480u);

    // resize 触发 OUT_OF_DATE（此处直接最小化到 0x0：acquire 失败 + 重建被拒）
    CHECK_FALSE(SimulateFrame(gate, ledger, 0u, 0u));
    CHECK(gate.NeedsRecreate());

    // 最小化持续 1000 帧：每帧入口恰一次重建尝试（有界）→ 全部被拒 → 帧全跳过。
    // 循环终止本身即"不挂死"的结构证据；断言计数线性对应（无内层自旋放大）。
    for (int i = 0; i < 1000; ++i) {
        const uint32_t attemptsBefore = ledger.failedAttempts;
        CHECK_FALSE(SimulateFrame(gate, ledger, 0u, 0u)); // 帧继续跳过
        CHECK(ledger.failedAttempts == attemptsBefore + 1u); // 每帧恰一次尝试
        CHECK(gate.NeedsRecreate()); // 粘性保留（失败不清标记）
        CHECK(ledger.serial == 0u);  // 尺寸未被假更新
        CHECK(ledger.toWidth == 640u);
        CHECK(ledger.toHeight == 480u);
    }
    CHECK(ledger.failedAttempts == 1000u);
    CHECK(gate.SkippedSubmitFrames() >= 1001u); // 首帧 + 1000 帧全部跳过

    // 窗口恢复 480x360：下一帧入口重建成功 → 恢复渲染（回粘性路径的出口）
    bool attempted = false;
    CHECK(SimulateFrame(gate, ledger, 480u, 360u, &attempted));
    CHECK(attempted);
    CHECK_FALSE(gate.NeedsRecreate());
    CHECK(ledger.serial == 1u);
    CHECK(ledger.fromWidth == 640u);
    CHECK(ledger.fromHeight == 480u);
    CHECK(ledger.toWidth == 480u);
    CHECK(ledger.toHeight == 360u);
    CHECK(ledger.failedAttempts == 1000u); // 失败历史保留（取证不抹除）

    // 恢复后连续渲染
    for (int i = 0; i < 3; ++i) {
        CHECK(SimulateFrame(gate, ledger, 480u, 360u));
    }
}

TEST_CASE("zrhi vk recreate: Present 侧 OUT_OF_DATE —— 同一重建入口覆盖（清除/回粘性对称）")
{
    FrameGate gate;
    SwapchainResizeLedger ledger;
    ledger.RecordCreated(640u, 480u);

    // present 阶段命中 OUT_OF_DATE（acquire/submit 都成功）→ 粘性、本帧已呈现
    gate.BeginFrame(VkOutcome::Success);
    gate.OnSubmit(VkOutcome::Success);
    gate.OnPresent(VkOutcome::OutOfDate);
    CHECK(gate.NeedsRecreate());
    CHECK(gate.RecreateRequests() == 1u);

    // 下一帧入口：重建成功（尺寸不变的最小化恢复场景——仍须清粘性、serial 自增）
    REQUIRE(ledger.RecordSuccess(640u, 480u));
    gate.OnSwapchainRecreated();
    CHECK_FALSE(gate.NeedsRecreate());
    CHECK(ledger.serial == 1u);
    CHECK_FALSE(ledger.LastRecreateChangedSize());

    // 重建失败场景的对称面：SUBOPTIMAL 置粘性后重建被拒 → 标记必须保留
    gate.BeginFrame(VkOutcome::Suboptimal); // 图像可用但标记需重建
    CHECK(gate.NeedsRecreate());
    const RecreateExtent rejected = ResolveRecreateExtent(0u, 0u, 0u, 0u, 0u, 0u, 640u, 480u);
    CHECK_FALSE(rejected.accepted);
    ledger.RecordFailure();
    // 拒绝后不调 OnSwapchainRecreated → 粘性保留，帧仍可提交（SUBOPTIMAL 语义）
    CHECK(gate.CanSubmit());
    CHECK(gate.NeedsRecreate());
}
