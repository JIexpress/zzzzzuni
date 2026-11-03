// =============================================================================
// test_zrhi_device_stats.cpp —— 设备统计的布局护栏与失败可观测性（TASK-H-05 F1）
//
// 覆盖验收：
//   1. ZrDeviceStats 是"尾部追加"结构：sizeof/offsetof 由头文件 static_assert 钉住，
//      本文件把同一事实变成运行期可读证据（护栏值与实际布局一致）。
//   2. Vulkan 描述符集分配失败不再静默：真实 VkDescriptorPool 耗尽 →
//      ZrVulkanDevice::AllocateRootDescriptorSet 返回 VK_NULL_HANDLE 并累加计数 →
//      IZrDevice::GetDeviceStats().descriptorSetAllocFailureCount 读出（累计、不覆盖）。
//      命令表 RebindDescriptorSet 的唯一分配点即该方法（见 zrhi_vulkan_commandlist.cpp），
//      故此处验证的写入/读出链路就是池耗尽时的真实链路。
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <cstddef>
#include <cstdint>

#include "zrhi/zrhi_device.h"

#if defined(ZR_HAS_VULKAN)
#include "zrhi/vulkan/zrhi_vulkan_device.h"
#endif

// =============================================================================
// 1. 布局护栏（TASK-H-05 F1）
// =============================================================================
TEST_CASE("ZrDeviceStats: 尾部追加字段且布局被 static_assert 钉住（TASK-H-05 F1）")
{
    // 头文件里的 static_assert 已经保证这几条；这里再断言一次是为了让"护栏值 ==
    // 实际布局"这条事实出现在测试输出里（否则只有改坏时才有人看见它）。
    // 28 = 4 个 uint32 + 1 个 int32 + 1 个 bool（+3B 对齐填充）+ 尾部 1 个 uint32；
    // TASK-R10-05 B-5 再尾部追加 barrierTruncationCount → 32。
    CHECK(sizeof(ZrDeviceStats) == 32u);
    CHECK(offsetof(ZrDeviceStats, aliasHeapCount) == 0u);
    CHECK(offsetof(ZrDeviceStats, imageCount) == 4u);
    CHECK(offsetof(ZrDeviceStats, bufferCount) == 8u);
    CHECK(offsetof(ZrDeviceStats, presentFailureCount) == 12u);
    CHECK(offsetof(ZrDeviceStats, lastPresentHresult) == 16u);
    CHECK(offsetof(ZrDeviceStats, deviceRemoved) == 20u);
    // TASK-H-05 F1 的追加字段落在最后：既有 6 个字段偏移不变（H-02 的追加未被破坏）
    CHECK(offsetof(ZrDeviceStats, descriptorSetAllocFailureCount) == 24u);
    // TASK-R10-05 B-5 的追加字段落在新的最后：既有 7 个字段偏移不变
    CHECK(offsetof(ZrDeviceStats, barrierTruncationCount) == 28u);

    const ZrDeviceStats fresh;
    CHECK(fresh.descriptorSetAllocFailureCount == 0u); // 默认 0（D3D12 侧的取值）
    CHECK(fresh.barrierTruncationCount == 0u);         // 默认 0（D3D12 侧恒 0）
    CHECK(fresh.presentFailureCount == 0u);
    CHECK_FALSE(fresh.deviceRemoved);
}

// =============================================================================
// 2. Vulkan 描述符集分配失败 → 设备统计可读（TASK-H-05 F1）
// =============================================================================
#if defined(ZR_HAS_VULKAN)
TEST_CASE("Vulkan: 描述符集分配失败经 GetDeviceStats 可观测（TASK-H-05 F1）")
{
    ZrVulkanDevice* device = ZrVulkanCreateDevice();
    if (device == nullptr) {
        MESSAGE("Vulkan 1.3 设备不可用 -> F1 读链用例降级（本机应可用：窗口程序可跑 Vulkan 后端）");
        return;
    }

    // 上层只持有 IZrDevice*：用接口指针读取，与窗口程序/引擎的用法一致
    IZrDevice* const rhi = device;
    const ZrDeviceStats baseline = rhi->GetDeviceStats();
    CHECK(baseline.descriptorSetAllocFailureCount == 0u); // 新设备从未失败

    // ---- 最小 set 布局（1 个 UNIFORM_BUFFER，镜像 set0 的根 CBV 段）--------------
    VkDescriptorSetLayoutBinding binding {};
    binding.binding = 0u;
    binding.descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    binding.descriptorCount = 1u;
    binding.stageFlags = VK_SHADER_STAGE_ALL;
    VkDescriptorSetLayoutCreateInfo layoutInfo {};
    layoutInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    layoutInfo.bindingCount = 1u;
    layoutInfo.pBindings = &binding;
    VkDescriptorSetLayout layout = VK_NULL_HANDLE;
    REQUIRE(vkCreateDescriptorSetLayout(device->VulkanDeviceHandle(), &layoutInfo, nullptr, &layout)
        == VK_SUCCESS);

    // ---- 只允许 1 个集合的池：第二次分配必然 VK_ERROR_OUT_OF_POOL_MEMORY --------
    VkDescriptorPoolSize poolSize {};
    poolSize.type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    poolSize.descriptorCount = 1u;
    VkDescriptorPoolCreateInfo poolInfo {};
    poolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    poolInfo.maxSets = 1u;
    poolInfo.poolSizeCount = 1u;
    poolInfo.pPoolSizes = &poolSize;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    REQUIRE(vkCreateDescriptorPool(device->VulkanDeviceHandle(), &poolInfo, nullptr, &pool)
        == VK_SUCCESS);

    // 第一次：成功，且不得被计为失败
    const VkDescriptorSet first = device->AllocateRootDescriptorSet(pool, layout);
    CHECK(first != VK_NULL_HANDLE);
    CHECK(rhi->GetDeviceStats().descriptorSetAllocFailureCount == 0u);

    // 第二次：池耗尽 —— 返回空句柄 + 计数 +1（修复前：只有没人读的私有计数器）
    const VkDescriptorSet second = device->AllocateRootDescriptorSet(pool, layout);
    CHECK(second == VK_NULL_HANDLE);
    const ZrDeviceStats afterFirst = rhi->GetDeviceStats();
    CHECK(afterFirst.descriptorSetAllocFailureCount == 1u); // ← F1 的读链实证
    CHECK(afterFirst.aliasHeapCount == baseline.aliasHeapCount); // 其它字段不被污染
    CHECK(afterFirst.imageCount == baseline.imageCount);
    CHECK(afterFirst.bufferCount == baseline.bufferCount);

    // 第三次：累计而非覆盖（"失败次数"语义；重复失败必须能堆叠）
    CHECK(device->AllocateRootDescriptorSet(pool, layout) == VK_NULL_HANDLE);
    CHECK(rhi->GetDeviceStats().descriptorSetAllocFailureCount == 2u);

    // 记录字段与计数互不干扰（VK 后端不虚报 D3D12 专属的 Present 字段）
    CHECK(afterFirst.deviceRemoved == false);
    CHECK(afterFirst.presentFailureCount == 0u);

    vkDestroyDescriptorPool(device->VulkanDeviceHandle(), pool, nullptr);
    vkDestroyDescriptorSetLayout(device->VulkanDeviceHandle(), layout, nullptr);
    ZrVulkanDestroyDevice(device);
}
#endif // ZR_HAS_VULKAN
