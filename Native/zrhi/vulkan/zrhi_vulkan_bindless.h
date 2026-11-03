// =============================================================================
// zrhi_vulkan_bindless.h —— bindless 容量判定的纯逻辑（TASK-R10-05 B-4）
//
// 历史缺陷（R10-02 B-4）：设备创建只校验了 sampled-image 的两个 update-after-bind
// 上限（per-stage + per-set）；binding1 是 STORAGE_IMAGE 数组、长度同为
// kBindlessCapacity，其 `maxPerStageDescriptorUpdateAfterBindStorageImages` /
// `maxDescriptorSetUpdateAfterBindStorageImages` 未纳入守卫。低上限设备（部分
// 移动/老驱动）上 vkCreateDescriptorSetLayout 直接失败 → 后端"不可用"，而实际
// 把容量调小就能跑（误报为不支持）。
//
// 修复：四个上限纳入同一守卫，生效容量取 min(请求值, 四上限)；任一上限为 0 =
// 设备不支持该类描述符的 update-after-bind（功能缺失，不是容量小）→ 后端不可用。
// 产品代码（zrhi_vulkan_device.cpp）与单测共用本纯函数——单测用"低上限 mock
// limits"断言容量收缩；守卫被改回"只查 sampled"即红（变异验证）。
// =============================================================================
#pragma once

#include <cstdint>

namespace zr {

/// bindless 生效容量（纯函数）。
///   requested          —— 期望槽数（ZrVulkanDevice::kBindlessCapacity）
///   sampled*/storage*  —— 设备的四个 update-after-bind 上限（0 = 不支持）
/// 返回 0 = 后端不可用；否则 = min(四上限, requested)。
inline uint32_t ZrVulkanBindlessCapacityFor(uint32_t requested, uint32_t sampledPerStage,
    uint32_t sampledPerSet, uint32_t storagePerStage, uint32_t storagePerSet)
{
    if (requested == 0u || sampledPerStage == 0u || sampledPerSet == 0u || storagePerStage == 0u
        || storagePerSet == 0u) {
        return 0u;
    }
    uint32_t capacity = requested;
    if (sampledPerStage < capacity) {
        capacity = sampledPerStage;
    }
    if (sampledPerSet < capacity) {
        capacity = sampledPerSet;
    }
    if (storagePerStage < capacity) {
        capacity = storagePerStage;
    }
    if (storagePerSet < capacity) {
        capacity = storagePerSet;
    }
    return capacity;
}

} // namespace zr
