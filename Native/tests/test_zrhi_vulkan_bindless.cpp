// =============================================================================
// test_zrhi_vulkan_bindless.cpp —— bindless 容量守卫（TASK-R10-05 B-4）
//
// 历史缺陷（R10-02 B-4）：设备创建只校验 sampled-image 的两个 update-after-bind
// 上限；binding1 是 STORAGE_IMAGE 数组（长度同为 kBindlessCapacity），其
// per-stage/per-set 上限未纳入守卫——低上限设备上 vkCreateDescriptorSetLayout
// 失败，后端被误报"不可用"，实际容量收缩即可跑。
//
// 用例（简报验收点：低上限 mock limits → 容量收缩断言）直接驱动产品同一份
// 纯函数（zrhi/vulkan/zrhi_vulkan_bindless.h；ZrVulkanDevice::Create 即用它）。
// 变异验证：守卫改回"只查 sampled 两个上限"（storage 上限不参与 min）→
// "storage 低上限收缩"用例红。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/vulkan/zrhi_vulkan_bindless.h"

#include "zrhi/vulkan/zrhi_vulkan_device.h"

using zr::ZrVulkanBindlessCapacityFor;

TEST_CASE("zrhi vulkan bindless: all limits high -> requested capacity kept (780M shape)")
{
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 2000000u, 1000000u, 2000000u) == 8192u);
    CHECK(ZrVulkanBindlessCapacityFor(ZrVulkanDevice::kBindlessCapacity,
        0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu)
        == ZrVulkanDevice::kBindlessCapacity);
}

TEST_CASE("zrhi vulkan bindless: low storage limit clamps capacity (B-4 core regression)")
{
    // per-stage storage 上限 1024 < 请求 8192：历史实现不查 storage → 8192（错），
    // 修复实现收缩到 1024。
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 2000000u, 1024u, 2000000u) == 1024u);
    // per-set storage 上限更低
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 2000000u, 1000000u, 512u) == 512u);
    // sampled 与 storage 同时低 → 取最小
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 2048u, 1000000u, 4096u, 1000000u) == 2048u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 4096u, 4096u, 1024u, 1000000u) == 1024u);
}

TEST_CASE("zrhi vulkan bindless: zero limit = feature missing -> backend unavailable")
{
    // 0 = 该类描述符的 update-after-bind 不被支持（不是容量小）→ 0（后端不可用）
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 0u, 1000000u, 1000000u, 1000000u) == 0u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 0u, 1000000u, 1000000u) == 0u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 1000000u, 0u, 1000000u) == 0u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 1000000u, 1000000u, 1000000u, 0u) == 0u);
    CHECK(ZrVulkanBindlessCapacityFor(0u, 1000000u, 1000000u, 1000000u, 1000000u) == 0u);
}

TEST_CASE("zrhi vulkan bindless: capacity floor respects every dimension symmetrically")
{
    // 四个维度任一为最小值都会成为生效容量（对称性钉死，防止将来又漏一类）
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 777u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu) == 777u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 0xFFFFFFFFu, 777u, 0xFFFFFFFFu, 0xFFFFFFFFu) == 777u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 0xFFFFFFFFu, 0xFFFFFFFFu, 777u, 0xFFFFFFFFu) == 777u);
    CHECK(ZrVulkanBindlessCapacityFor(8192u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 777u) == 777u);
}
