// =============================================================================
// zrhi_vulkan_recreate.h —— 交换链真重建的纯逻辑判定（TASK-B-R9-09）
//
// R10-03 遗留补全：A-5/A-6 的帧闸把 OUT_OF_DATE 后的帧安全地跳过
// （needsRecreate 粘性），但"跳过"不是"恢复"——本任务把跳过升级为
// 重建后恢复。与 R10-03 A-4 的 D3D12 侧先例（zfgprobe/zfg_probe_resize.h）
// 同构：状态怎么变的判定抽成纯逻辑（无 vulkan.h 依赖），产品代码与单测共用：
//   · ResolveRecreateExtent  —— 表面能力 → 重建尺寸裁决（零尺寸 = 最小化 →
//     拒绝重建，粘性保留；0xFFFFFFFF 特殊值 → 回落建链尺寸防御）
//   · SwapchainResizeLedger  —— 旧/新尺寸 + 成功重建序号台账
//     （`VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)` 取证行的数据源）
//
// 真正动 VK 资源的部分（排空/销毁/重枚举）在 zrhi_vulkan_device.cpp
// 的 ZrVulkanSwapchain::Recreate()；粘性标记的置位/清除由
// zrhi_vulkan_frame_gate.h 的 FrameGate 承担（OnSwapchainRecreated）。
// =============================================================================
#pragma once

#include <cstdint>

namespace zr {

/// VkSurfaceCapabilitiesKHR::currentExtent 的"尺寸由应用决定"特殊值
/// （win32 surface 不出现——客户区尺寸权威；X11/Wayland 语义的防御性回落）
constexpr uint32_t kVkExtentUnknown = 0xFFFFFFFFu;

/// 一次重建的尺寸裁决结论
struct RecreateExtent {
    bool accepted = false; // false = 本次不重建（最小化/能力查询异常），粘性保留
    uint32_t width = 0u;
    uint32_t height = 0u;
};

/// 表面能力 → 重建尺寸裁决。
///
/// 裁决规则（对位 VK 规范 + A-4 的"宁可保守"口径）：
///   1. currentExtent 为 0xFFFFFFFF（特殊值）→ 回落建链尺寸（win32 不出现；
///      出现时旧尺寸至少是"上次真实可呈现的尺寸"，不会写假尺寸）；
///   2. 尺寸为 0（窗口最小化）→ **拒绝**：vkCreateSwapchainKHR 本会失败，
///      提前拒绝可保持旧链完好、needsRecreate 粘性保留（失败回粘性）；
///   3. 其余 → currentExtent 钳入 [minImageExtent, maxImageExtent]。
inline RecreateExtent ResolveRecreateExtent(uint32_t currentW, uint32_t currentH,
    uint32_t minW, uint32_t minH, uint32_t maxW, uint32_t maxH, uint32_t fallbackW,
    uint32_t fallbackH)
{
    uint32_t w = currentW;
    uint32_t h = currentH;
    if (w == kVkExtentUnknown || h == kVkExtentUnknown) {
        w = fallbackW;
        h = fallbackH;
    }
    if (w == 0u || h == 0u) {
        return RecreateExtent {}; // 最小化：不重建（粘性保留，等窗口恢复尺寸）
    }
    if (w < minW) {
        w = minW;
    }
    if (h < minH) {
        h = minH;
    }
    if (maxW != 0u && w > maxW) {
        w = maxW;
    }
    if (maxH != 0u && h > maxH) {
        h = maxH;
    }
    RecreateExtent out;
    out.accepted = true;
    out.width = w;
    out.height = h;
    return out;
}

/// 交换链尺寸台账：最近一次成功重建的 {旧尺寸 → 新尺寸} + 成功重建序号。
/// serial = 成功重建次数（`serial=N→M` 的 N/M）；失败尝试只计不进 serial。
struct SwapchainResizeLedger {
    uint32_t fromWidth = 0u;  // 最近一次成功重建的旧尺寸（建链尺寸为初始值）
    uint32_t fromHeight = 0u;
    uint32_t toWidth = 0u;    // 最近一次成功重建的新尺寸（= 当前尺寸）
    uint32_t toHeight = 0u;
    uint32_t serial = 0u;     // 成功重建次数（取证行 serial=N→M）
    uint32_t failedAttempts = 0u; // 失败尝试累计（有界增长 = 每帧最多一次，不挂死）

    /// 建链时登记初始尺寸（from = to = 建链尺寸，serial = 0）
    void RecordCreated(uint32_t width, uint32_t height)
    {
        fromWidth = width;
        fromHeight = height;
        toWidth = width;
        toHeight = height;
    }

    /// 重建成功：尺寸推进 + serial 自增。零尺寸拒绝入账（裁决前置失败不该到这里）。
    bool RecordSuccess(uint32_t newWidth, uint32_t newHeight)
    {
        if (newWidth == 0u || newHeight == 0u) {
            return false;
        }
        fromWidth = toWidth;
        fromHeight = toHeight;
        toWidth = newWidth;
        toHeight = newHeight;
        ++serial;
        return true;
    }

    /// 重建失败：粘性保留路径的取证凭据（计数有界增长——每帧入口最多尝试一次）
    void RecordFailure() { ++failedAttempts; }

    /// 最近一次重建是否真的改了尺寸（同尺寸重建——如最小化恢复——不算改尺寸）
    bool LastRecreateChangedSize() const
    {
        return fromWidth != toWidth || fromHeight != toHeight;
    }
};

} // namespace zr
