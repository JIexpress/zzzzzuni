// =============================================================================
// test_zrhi_vulkan_result.cpp —— VkResult 统一分类 + 每帧提交/呈现闸
//                            （TASK-R10-03 A-5/A-6 + TASK-B-R9-11 离屏提交）
//
// 覆盖 R10-02 的两条 Vulkan 缺陷：
//   A-5 AcquireNextImage 拿到 OUT_OF_DATE/SUBOPTIMAL 只打印就继续 → 登记一个
//       **永不 signal** 的 imageAvailable 为挂起 acquire → 下一次 Submit 的
//       pWaitSemaphores 等一个永不 signal 的二进制信号量 → 队列永久卡死。
//   A-6 vkQueueSubmit 返回值丢弃 → fence 永不 signal → 下一帧 Begin 里
//       vkWaitForFences(UINT64_MAX) 永久阻塞。
// B-R9-11 追加：离屏设备（无交换链）detached 语义——acquire 闸不拦离屏提交
//   （B-R9-10 VK 离屏 lights readback 全零的根因修复，见文件末两用例）。
//
// 用例直接驱动产品代码用的同一份纯逻辑（zrhi/vulkan/zrhi_vulkan_frame_gate.h），
// 不需要 GPU / 不需要 vulkan.h；产品侧的常量与真实枚举由
// zrhi_vulkan_device.cpp 的 static_assert 钉住。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/vulkan/zrhi_vulkan_frame_gate.h"

using zr::ClassifyVkResult;
using zr::FrameGate;
using zr::VkOutcome;
using zr::VkOutcomeBlocksFrame;
using zr::VkOutcomeNeedsRecreate;

TEST_CASE("zrhi vk result: VkResult -> 语义分类（唯一转译点）")
{
    CHECK(ClassifyVkResult(0) == VkOutcome::Success);
    CHECK(ClassifyVkResult(1000001003) == VkOutcome::Suboptimal);
    CHECK(ClassifyVkResult(-1000001004) == VkOutcome::OutOfDate);
    CHECK(ClassifyVkResult(-1000000000) == VkOutcome::SurfaceLost);
    CHECK(ClassifyVkResult(-4) == VkOutcome::DeviceLost);
    CHECK(ClassifyVkResult(-1) == VkOutcome::Error);       // VK_ERROR_OUT_OF_HOST_MEMORY
    CHECK(ClassifyVkResult(-1000000001) == VkOutcome::Error); // 其它 KHR 错误

    // 分类语义：哪些要求重建、哪些阻断本帧
    CHECK(VkOutcomeNeedsRecreate(VkOutcome::OutOfDate));
    CHECK(VkOutcomeNeedsRecreate(VkOutcome::Suboptimal));
    CHECK(VkOutcomeNeedsRecreate(VkOutcome::SurfaceLost));
    CHECK_FALSE(VkOutcomeNeedsRecreate(VkOutcome::Success));
    CHECK_FALSE(VkOutcomeNeedsRecreate(VkOutcome::Error));

    CHECK(VkOutcomeBlocksFrame(VkOutcome::OutOfDate));
    CHECK(VkOutcomeBlocksFrame(VkOutcome::DeviceLost));
    CHECK(VkOutcomeBlocksFrame(VkOutcome::Error));
    CHECK_FALSE(VkOutcomeBlocksFrame(VkOutcome::Success));
    CHECK_FALSE(VkOutcomeBlocksFrame(VkOutcome::Suboptimal)); // 图像仍可用
}

TEST_CASE("zrhi vk gate: 正常帧 —— 可提交、可呈现")
{
    FrameGate gate;
    gate.BeginFrame(VkOutcome::Success);
    CHECK(gate.AcquiredThisFrame());
    CHECK(gate.CanSubmit());
    CHECK_FALSE(gate.CanPresent()); // 提交前不允许呈现（renderFinished 还没 signal）
    gate.OnSubmit(VkOutcome::Success);
    CHECK(gate.SubmittedThisFrame());
    CHECK(gate.CanPresent());
    gate.OnPresent(VkOutcome::Success);
    CHECK_FALSE(gate.NeedsRecreate());
    CHECK(gate.SubmitFailures() == 0u);
    CHECK(gate.SkippedSubmitFrames() == 0u);
    CHECK(gate.SkippedPresentFrames() == 0u);
}

TEST_CASE("zrhi vk gate: 模拟 OUT_OF_DATE —— 本帧不再提交到旧链（A-5 核心）")
{
    FrameGate gate;
    gate.BeginFrame(VkOutcome::OutOfDate);
    // 旧实现：照常 SetPendingAcquire + vkQueueSubmit → 等一个永不 signal 的信号量
    CHECK_FALSE(gate.AcquiredThisFrame());
    CHECK_FALSE(gate.CanSubmit());  // ← 不提交
    CHECK_FALSE(gate.CanPresent()); // ← 不呈现
    CHECK(gate.NeedsRecreate());    // ← 标记需重建
    CHECK(gate.SkippedSubmitFrames() == 1u);
    CHECK(gate.SkippedPresentFrames() == 1u);
    CHECK(gate.RecreateRequests() == 1u);

    // 后续帧仍然过期：不重复累加"需重建请求"（粘性标记），但继续跳过
    gate.BeginFrame(VkOutcome::OutOfDate);
    CHECK_FALSE(gate.CanSubmit());
    CHECK(gate.SkippedSubmitFrames() == 2u);
    CHECK(gate.RecreateRequests() == 1u);

    // 交换链重建后：标记清除，恢复提交
    gate.OnSwapchainRecreated();
    CHECK_FALSE(gate.NeedsRecreate());
    gate.BeginFrame(VkOutcome::Success);
    CHECK(gate.CanSubmit());
    gate.OnSubmit(VkOutcome::Success);
    CHECK(gate.CanPresent());
}

TEST_CASE("zrhi vk gate: SUBOPTIMAL —— 图像仍可用（继续提交/呈现），但标记需重建")
{
    FrameGate gate;
    gate.BeginFrame(VkOutcome::Suboptimal);
    CHECK(gate.AcquiredThisFrame());
    CHECK(gate.CanSubmit());
    CHECK(gate.NeedsRecreate());
    gate.OnSubmit(VkOutcome::Success);
    CHECK(gate.CanPresent());
    CHECK(gate.SkippedSubmitFrames() == 0u);
}

TEST_CASE("zrhi vk gate: 提交失败 —— 本帧绝不呈现（A-6 核心）")
{
    FrameGate gate;
    gate.BeginFrame(VkOutcome::Success);
    REQUIRE(gate.CanSubmit());
    gate.OnSubmit(VkOutcome::DeviceLost);
    CHECK(gate.SubmitFailures() == 1u);
    CHECK_FALSE(gate.SubmittedThisFrame()); // 不能置 pendingSubmit → 下一帧 Begin 不会等 fence
    CHECK_FALSE(gate.CanPresent());         // 呈现会等一个永不 signal 的信号量
    CHECK(gate.SkippedPresentFrames() == 1u);
    CHECK(gate.NeedsRecreate());            // 设备丢失 → 标记重建

    // 非"需重建"类失败（如 VK_ERROR_OUT_OF_HOST_MEMORY）：不呈现，但不强制重建
    FrameGate gate2;
    gate2.BeginFrame(VkOutcome::Success);
    gate2.OnSubmit(VkOutcome::Error);
    CHECK(gate2.SubmitFailures() == 1u);
    CHECK_FALSE(gate2.CanPresent());
    CHECK_FALSE(gate2.NeedsRecreate());

    // 下一帧恢复正常：闸门必须重新放行（失败状态不得粘住）
    gate2.BeginFrame(VkOutcome::Success);
    CHECK(gate2.CanSubmit());
    gate2.OnSubmit(VkOutcome::Success);
    CHECK(gate2.CanPresent());
}

TEST_CASE("zrhi vk gate: 呈现 OUT_OF_DATE —— 标记需重建（粘性）")
{
    FrameGate gate;
    gate.BeginFrame(VkOutcome::Success);
    gate.OnSubmit(VkOutcome::Success);
    REQUIRE(gate.CanPresent());
    gate.OnPresent(VkOutcome::OutOfDate);
    CHECK(gate.NeedsRecreate());
    CHECK(gate.RecreateRequests() == 1u);
    // 下一帧提交仍被允许（旧链还能用），但重建标记保留到 OnSwapchainRecreated
    gate.BeginFrame(VkOutcome::Success);
    CHECK(gate.CanSubmit());
    CHECK(gate.NeedsRecreate());
    gate.OnSwapchainRecreated();
    CHECK_FALSE(gate.NeedsRecreate());
}

TEST_CASE("zrhi vk gate: 跳过计数跨帧累计且可读（取证口径）")
{
    FrameGate gate;
    for (int i = 0; i < 5; ++i) {
        gate.BeginFrame(VkOutcome::OutOfDate);
    }
    CHECK(gate.SkippedSubmitFrames() == 5u);
    CHECK(gate.SkippedPresentFrames() == 5u);
    CHECK(gate.RecreateRequests() == 1u); // 只请求一次

    // 失败提交也会计入"跳过呈现"
    gate.BeginFrame(VkOutcome::Success);
    gate.OnSubmit(VkOutcome::Error);
    CHECK(gate.SkippedPresentFrames() == 6u);
    CHECK(gate.SubmitFailures() == 1u);
}

TEST_CASE("zrhi vk gate: 未 acquire 的帧不得提交（防止空提交等信号量）")
{
    FrameGate gate; // 默认构造 = 还没开始任何帧
    CHECK_FALSE(gate.CanSubmit());
    CHECK_FALSE(gate.CanPresent());
    gate.BeginFrame(VkOutcome::SurfaceLost);
    CHECK_FALSE(gate.CanSubmit());
    CHECK(gate.NeedsRecreate());
}

// =============================================================================
// TASK-B-R9-11：离屏设备（无交换链）的提交语义
//
// 缺陷背景：B-R9-10 首次组合出 "VK 离屏 lights"（--headless，无交换链），
// readback 全零。根因：离屏路径没有 AcquireNextImage → BeginFrame 永不被调
// → m_acquired 恒 false → CanSubmit() 恒 false → vkQueueSubmit 被静默跳过
// （离屏无挂起信号量，跳过诊断行条件永不满足，零信号）。修复：设备构造时
// detached、CreateSwapchain 成功挂接、交换链全销毁回 detached；detached 时
// CanSubmit() 恒 true（无信号量依赖可卡）。
// =============================================================================

TEST_CASE("zrhi vk gate: 离屏设备（detached）提交不受 acquire 闸约束（B-R9-11 根因修复）")
{
    FrameGate gate; // 默认 = 挂接（保守沿用 R10-03 语义）
    REQUIRE(gate.SwapchainAttached());
    CHECK_FALSE(gate.CanSubmit()); // 挂接态沿用既有口径：未 acquire 不得提交

    // 设备生而无交换链 → detached：无 acquire 握手也可提交
    gate.SetSwapchainAttached(false);
    CHECK_FALSE(gate.SwapchainAttached());
    CHECK(gate.CanSubmit());
    gate.OnSubmit(VkOutcome::Success); // 离屏帧正常记账
    CHECK(gate.SubmittedThisFrame());
    CHECK_FALSE(gate.CanPresent()); // 离屏无呈现（从未 acquire）
    CHECK(gate.SkippedSubmitFrames() == 0u);

    // 60 帧离屏循环（--headless bench 形态）：从不 BeginFrame 也不丢帧
    for (int i = 0; i < 60; ++i) {
        CHECK(gate.CanSubmit());
    }
}

TEST_CASE("zrhi vk gate: 挂接状态切换 —— 建链即受闸，全销毁即离屏")
{
    FrameGate gate;
    gate.SetSwapchainAttached(false);
    REQUIRE(gate.CanSubmit()); // 离屏可提交

    // 建链（CreateSwapchain 成功 → attached）：acquire 闸恢复效力
    gate.SetSwapchainAttached(true);
    CHECK_FALSE(gate.CanSubmit()); // 首帧 acquire 前
    gate.BeginFrame(VkOutcome::Success);
    CHECK(gate.CanSubmit());

    // 全销毁（Destroy 后表空 → detached）：立即回到离屏可提交
    gate.SetSwapchainAttached(false);
    CHECK(gate.CanSubmit());

    // 重新挂接 + 失败 acquire：R10-03 语义原样生效（不得提交）
    gate.SetSwapchainAttached(true);
    gate.BeginFrame(VkOutcome::OutOfDate);
    CHECK_FALSE(gate.CanSubmit());
    CHECK(gate.NeedsRecreate());
}
