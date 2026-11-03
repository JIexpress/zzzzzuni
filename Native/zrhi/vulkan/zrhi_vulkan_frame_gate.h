// =============================================================================
// zrhi_vulkan_frame_gate.h —— VkResult 统一分类 + 每帧提交/呈现闸（TASK-R10-03 A-5/A-6）
//
// 缺陷背景（R10-02 A-5/A-6）：
//   · AcquireNextImage 拿到 VK_ERROR_OUT_OF_DATE_KHR / VK_SUBOPTIMAL_KHR 只 printf
//     就继续 → 登记一个**永不 signal** 的 imageAvailable 为挂起 acquire →
//     下一次 Submit 的 pWaitSemaphores 等一个永不 signal 的二进制信号量 → 队列卡死；
//   · vkQueueSubmit 返回值被直接丢弃 → fence 永不 signal → 下一帧 Begin 里
//     vkWaitForFences(UINT64_MAX) 永久阻塞（比"报错退出"更难诊断）。
//
// 本文件是"VkResult 的唯一转译点"（产品侧用 static_assert 钉住常量与
// vulkan.h 的枚举值一致），并提供纯逻辑帧闸：
//   Acquire 结果 → 本帧能否 Submit；Submit 结果 → 本帧能否 Present。
// 不依赖 vulkan.h（只用 int32_t），因此可以在无 GPU 的单测里直接驱动。
// =============================================================================
#pragma once

#include <cstdint>

namespace zr {

/// VkResult 的语义分类（本后端只关心这 6 类）
enum class VkOutcome : int32_t {
    Success = 0,
    Suboptimal = 1,  // VK_SUBOPTIMAL_KHR：图像可用，但交换链该重建
    OutOfDate = 2,   // VK_ERROR_OUT_OF_DATE_KHR：本帧拿不到可用图像
    SurfaceLost = 3, // VK_ERROR_SURFACE_LOST_KHR
    DeviceLost = 4,  // VK_ERROR_DEVICE_LOST
    Error = 5,       // 其余失败
};

// ---- VkResult 数值（与 vulkan.h 一致；产品侧 static_assert 校验，单测不需要 SDK）----
constexpr int32_t kVkSuccess = 0;
constexpr int32_t kVkSuboptimalKhr = 1000001003;
constexpr int32_t kVkErrorOutOfDateKhr = -1000001004;
constexpr int32_t kVkErrorSurfaceLostKhr = -1000000000;
constexpr int32_t kVkErrorDeviceLost = -4;

/// 唯一的 VkResult → 语义分类转译点（产品代码不得再各自 if/else）
constexpr VkOutcome ClassifyVkResult(int32_t result)
{
    return result == kVkSuccess            ? VkOutcome::Success
        : result == kVkSuboptimalKhr       ? VkOutcome::Suboptimal
        : result == kVkErrorOutOfDateKhr   ? VkOutcome::OutOfDate
        : result == kVkErrorSurfaceLostKhr ? VkOutcome::SurfaceLost
        : result == kVkErrorDeviceLost     ? VkOutcome::DeviceLost
                                           : VkOutcome::Error;
}

/// 该结果是否要求"标记交换链需重建"
constexpr bool VkOutcomeNeedsRecreate(VkOutcome outcome)
{
    return outcome == VkOutcome::OutOfDate || outcome == VkOutcome::SurfaceLost
        || outcome == VkOutcome::Suboptimal;
}

/// 该结果是否"本帧不可用"（不得提交、不得呈现）
constexpr bool VkOutcomeBlocksFrame(VkOutcome outcome)
{
    return outcome == VkOutcome::OutOfDate || outcome == VkOutcome::SurfaceLost
        || outcome == VkOutcome::DeviceLost || outcome == VkOutcome::Error;
}

/// 每帧"acquire → submit → present"的闸门。
///
/// 语义（对位 R10-02 A-5/A-6 的修复口径）：
///   · acquire 失败/过期 → 本帧**不提交**（避免登记永不 signal 的信号量）、不呈现；
///   · submit 失败 → 本帧**不呈现**（renderFinished 永远不会被 signal，Present 会挂死）；
///   · 需要重建的标记是**粘性**的，直到交换链真的重建过（OnSwapchainRecreated）。
///   · 离屏设备（无交换链）不受 acquire 闸约束（TASK-B-R9-11，见 SetSwapchainAttached）。
class FrameGate {
public:
    /// 交换链挂接开关（TASK-B-R9-11 根因修复）。
    ///
    /// 缺陷背景：B-R9-10 首次组合出"VK 离屏 lights"（--headless，无交换链），
    /// readback 全零、validation errors=0、0xAA 填充探针同样不可见——最终定位：
    /// 离屏路径没有 AcquireNextImage → BeginFrame 永不被调 → m_acquired 恒 false
    /// → CanSubmit() 恒 false → SubmitCommandList 里 vkQueueSubmit 被静默跳过
    /// （离屏无挂起信号量，跳过时的诊断行条件永不满足，零信号）。
    /// 离屏提交没有可卡死的信号量依赖（pWaitSemaphores 为空），不受闸约束。
    /// 默认 true = 挂接（保守沿用 R10-03 既有语义）；由设备在构造时置 false
    /// （设备生而无交换链），CreateSwapchain 成功置 true，交换链全销毁回 false。
    void SetSwapchainAttached(bool attached) { m_swapchainAttached = attached; }
    bool SwapchainAttached() const { return m_swapchainAttached; }

    /// 每帧开始（AcquireNextImage 入口调用一次）：清本帧状态并记录 acquire 结果
    void BeginFrame(VkOutcome acquire)
    {
        m_acquired = false;
        m_submitted = false;
        m_skipPresent = true; // 默认不允许呈现，直到 submit 成功
        switch (acquire) {
        case VkOutcome::Success:
            m_acquired = true;
            break;
        case VkOutcome::Suboptimal:
            // 图像可用（可以继续提交/呈现），但交换链该重建
            m_acquired = true;
            RequestRecreate();
            break;
        default:
            ++m_skippedSubmit;
            ++m_skippedPresent;
            if (VkOutcomeNeedsRecreate(acquire)) {
                RequestRecreate();
            }
            break;
        }
    }

    /// 本帧是否可以提交（false = 跳过 vkQueueSubmit，不要登记任何挂起信号量）。
    /// 离屏（未挂交换链）设备恒可提交——无 acquire 握手，无信号量可卡（TASK-B-R9-11）。
    bool CanSubmit() const { return !m_swapchainAttached || m_acquired; }

    /// 记录 vkQueueSubmit 的结果
    void OnSubmit(VkOutcome result)
    {
        if (result == VkOutcome::Success) {
            m_submitted = true;
            m_skipPresent = false;
            return;
        }
        ++m_submitFailures;
        ++m_skippedPresent;
        m_submitted = false;
        m_skipPresent = true; // 绝不 Present：等待的信号量永远不会被 signal
        if (VkOutcomeNeedsRecreate(result) || result == VkOutcome::DeviceLost) {
            RequestRecreate();
        }
    }

    /// 本帧是否可以呈现（false = 跳过 vkQueuePresentKHR）
    bool CanPresent() const { return m_acquired && m_submitted && !m_skipPresent; }

    /// 记录 vkQueuePresentKHR 的结果
    void OnPresent(VkOutcome result)
    {
        if (result == VkOutcome::Success) {
            return;
        }
        if (VkOutcomeNeedsRecreate(result)) {
            RequestRecreate();
        } else {
            ++m_presentFailures;
        }
    }

    /// 交换链已重建（由重建流程调用）：清掉粘性标记
    void OnSwapchainRecreated() { m_needRecreate = false; }

    bool NeedsRecreate() const { return m_needRecreate; }
    bool AcquiredThisFrame() const { return m_acquired; }
    bool SubmittedThisFrame() const { return m_submitted; }

    uint64_t SkippedSubmitFrames() const { return m_skippedSubmit; }
    uint64_t SkippedPresentFrames() const { return m_skippedPresent; }
    uint64_t SubmitFailures() const { return m_submitFailures; }
    uint64_t PresentFailures() const { return m_presentFailures; }
    uint64_t RecreateRequests() const { return m_recreateRequests; }

private:
    void RequestRecreate()
    {
        if (!m_needRecreate) {
            ++m_recreateRequests;
        }
        m_needRecreate = true;
    }

    bool m_swapchainAttached = true; // TASK-B-R9-11：设备构造/交换链增删时切换
    bool m_acquired = false;
    bool m_submitted = false;
    bool m_skipPresent = true;
    bool m_needRecreate = false;
    uint64_t m_skippedSubmit = 0u;
    uint64_t m_skippedPresent = 0u;
    uint64_t m_submitFailures = 0u;
    uint64_t m_presentFailures = 0u;
    uint64_t m_recreateRequests = 0u;
};

} // namespace zr
