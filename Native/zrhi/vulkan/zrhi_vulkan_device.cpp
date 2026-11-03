// zrhi_vulkan_device.cpp —— Vulkan 后端设备实现（TASK-B-R7-01）
//
// 语义对应见 zrhi_vulkan_device.h 头注。要点：
//   - 全部 VK 枚举转译经 zrhi_vulkan_translate（唯一转译点，无 static_cast 枚举）
//   - 内存：每资源一 VkDeviceMemory（基座不做子分配；别名堆例外——堆内落位）
//   - initData：staging（HOST_VISIBLE）→ vkCmdCopyBuffer → fence 等待
//     （≈ D3D12 后端 UPLOAD 中转 + CopyBufferRegion + fence 同构）
//   - 交换链：VK_KHR_win32_surface；PresentMode 表面不支持时降级 Fifo（必支持）
//   - 验证层：VK_LAYER_KHRONOS_validation 可用则开启，ERROR 级消息计数可查询
#include "zrhi/vulkan/zrhi_vulkan_device.h"

#include "zrhi/vulkan/zrhi_vulkan_bindless.h"
#include "zrhi/vulkan/zrhi_vulkan_commandlist.h"
#include "zrhi/vulkan/zrhi_vulkan_translate.h"

#include <cassert>
#include <cstdio>
#include <cstring>

// ---- VkResult 数值自证（TASK-R10-03 A-5/A-6）--------------------------------
// zrhi_vulkan_frame_gate.h 用整型常量描述 VkResult（这样单测不需要 SDK），
// 这里用 static_assert 把它钉在真实枚举上：SDK 改了值 → 编译期红。
static_assert(zr::kVkSuccess == static_cast<int32_t>(VK_SUCCESS), "VK_SUCCESS mismatch");
static_assert(zr::kVkSuboptimalKhr == static_cast<int32_t>(VK_SUBOPTIMAL_KHR),
    "VK_SUBOPTIMAL_KHR mismatch");
static_assert(zr::kVkErrorOutOfDateKhr == static_cast<int32_t>(VK_ERROR_OUT_OF_DATE_KHR),
    "VK_ERROR_OUT_OF_DATE_KHR mismatch");
static_assert(zr::kVkErrorSurfaceLostKhr == static_cast<int32_t>(VK_ERROR_SURFACE_LOST_KHR),
    "VK_ERROR_SURFACE_LOST_KHR mismatch");
static_assert(zr::kVkErrorDeviceLost == static_cast<int32_t>(VK_ERROR_DEVICE_LOST),
    "VK_ERROR_DEVICE_LOST mismatch");

// =============================================================================
// 内部助手
// =============================================================================

namespace {

/// 内存类型选择（device local / host visible 优先序由调用方属性表表达）
uint32_t FindMemoryType(VkPhysicalDevice physicalDevice, uint32_t typeBits,
    VkMemoryPropertyFlags props)
{
    VkPhysicalDeviceMemoryProperties memProps {};
    vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
    for (uint32_t i = 0; i < memProps.memoryTypeCount; ++i) {
        if ((typeBits & (1u << i)) != 0u
            && (memProps.memoryTypes[i].propertyFlags & props) == props) {
            return i;
        }
    }
    return 0xFFFFFFFFu;
}

/// 图像格式 → aspect 掩码（与 commandlist 同一约定；副本属内部可见范围）
VkImageAspectFlags AspectForFormat(VkFormat format)
{
    switch (format) {
    case VK_FORMAT_D32_SFLOAT:
    case VK_FORMAT_D16_UNORM:
        return VK_IMAGE_ASPECT_DEPTH_BIT;
    case VK_FORMAT_D24_UNORM_S8_UINT:
        return VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT;
    default:
        return VK_IMAGE_ASPECT_COLOR_BIT;
    }
}

/// 验证层回调：ERROR 级消息计数 + stderr（≈ D3D12 InfoQueue 行为）
VKAPI_ATTR VkBool32 VKAPI_CALL VulkanDebugCallback(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
    VkDebugUtilsMessageTypeFlagsEXT /*types*/, const VkDebugUtilsMessengerCallbackDataEXT* data,
    void* userData)
{
    if (severity >= VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT) {
        auto* device = static_cast<ZrVulkanDevice*>(userData);
        if (device != nullptr) {
            device->NoteValidationError();
        }
        std::fprintf(stderr, "[zrhi][vulkan][validation] %s\n",
            data != nullptr && data->pMessage != nullptr ? data->pMessage : "(null)");
    }
    return VK_FALSE; // 不中断
}

/// PresentMode 裁决（表面不支持 → FIFO 必支持兜底；建链与重建共用——TASK-B-R9-09
/// 把这段从 CreateSwapchain 拆出，避免重建路径复制一份裁决逻辑后悄悄分叉）
VkPresentModeKHR PickPresentMode(
    VkPhysicalDevice physicalDevice, VkSurfaceKHR surface, ZrPresentMode requestedMode)
{
    uint32_t presentModeCount = 0u;
    vkGetPhysicalDeviceSurfacePresentModesKHR(physicalDevice, surface, &presentModeCount, nullptr);
    std::vector<VkPresentModeKHR> presentModes(presentModeCount);
    vkGetPhysicalDeviceSurfacePresentModesKHR(
        physicalDevice, surface, &presentModeCount, presentModes.data());
    const VkPresentModeKHR requestedPresent = ZrVulkan_PresentMode(requestedMode);
    for (VkPresentModeKHR pm : presentModes) {
        if (pm == requestedPresent) {
            return requestedPresent;
        }
    }
    std::printf("[zrhi][vulkan] present mode %d unsupported, degrading to FIFO\n",
        static_cast<int>(requestedPresent));
    return VK_PRESENT_MODE_FIFO_KHR;
}

} // namespace

// =============================================================================
// 创建入口
// =============================================================================
ZrVulkanDevice* ZrVulkanCreateDevice() { return ZrVulkanDevice::Create(); }

void ZrVulkanDestroyDevice(ZrVulkanDevice* device) { delete device; }

ZrVulkanDevice* ZrVulkanDevice::Create()
{
    // ---- 实例版本：要求 Vulkan 1.3（动态渲染核心化前提）---------------------------
    uint32_t apiVersion = 0u;
    if (vkEnumerateInstanceVersion(&apiVersion) != VK_SUCCESS) {
        std::printf("[zrhi][vulkan] vkEnumerateInstanceVersion FAILED\n");
        return nullptr;
    }
    if (apiVersion < VK_API_VERSION_1_3) {
        std::printf("[zrhi][vulkan] loader API version %u.%u < 1.3, backend unavailable\n",
            VK_API_VERSION_MAJOR(apiVersion), VK_API_VERSION_MINOR(apiVersion));
        return nullptr;
    }

    std::unique_ptr<ZrVulkanDevice> d(new ZrVulkanDevice());
    // TASK-B-R9-11：设备生而无交换链 → 离屏（detached）——提交不受 acquire 闸约束。
    // 根因：离屏路径无 AcquireNextImage → BeginFrame 永不被调 → m_acquired 恒 false
    // → vkQueueSubmit 被静默跳过（readback 全零、零 validation 错误、零信号）。
    d->m_frameGate.SetSwapchainAttached(false);

    // ---- 验证层（可用则开启；≈ D3D12 DEBUG layer）---------------------------------
    const char* validationLayer = "VK_LAYER_KHRONOS_validation";
    uint32_t layerCount = 0u;
    vkEnumerateInstanceLayerProperties(&layerCount, nullptr);
    std::vector<VkLayerProperties> layers(layerCount);
    vkEnumerateInstanceLayerProperties(&layerCount, layers.data());
    bool validationAvailable = false;
    for (const VkLayerProperties& lp : layers) {
        if (std::strcmp(lp.layerName, validationLayer) == 0) {
            validationAvailable = true;
            break;
        }
    }
    d->m_validationEnabled = validationAvailable;

    VkApplicationInfo appInfo {};
    appInfo.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    appInfo.apiVersion = VK_API_VERSION_1_3;

    const char* instanceExtensions[] = {
        "VK_KHR_surface",
        "VK_KHR_win32_surface",
        "VK_EXT_debug_utils",
    };
    VkInstanceCreateInfo instanceInfo {};
    instanceInfo.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    instanceInfo.pApplicationInfo = &appInfo;
    instanceInfo.enabledExtensionCount = 3u;
    instanceInfo.ppEnabledExtensionNames = instanceExtensions;
    if (validationAvailable) {
        instanceInfo.enabledLayerCount = 1u;
        instanceInfo.ppEnabledLayerNames = &validationLayer;
    }
    if (vkCreateInstance(&instanceInfo, nullptr, &d->m_instance) != VK_SUCCESS) {
        std::printf("[zrhi][vulkan] vkCreateInstance FAILED\n");
        return nullptr;
    }

    // ---- 调试 messenger（ERROR 级计数；验证层不可用时静默降级）----------------------
    if (validationAvailable) {
        VkDebugUtilsMessengerCreateInfoEXT dbgInfo {};
        dbgInfo.sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT;
        dbgInfo.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT
            | VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT;
        dbgInfo.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT
            | VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
        dbgInfo.pfnUserCallback = &VulkanDebugCallback;
        dbgInfo.pUserData = d.get();
        const auto vkCreateDebugUtilsMessengerEXT_ =
            reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(
                vkGetInstanceProcAddr(d->m_instance, "vkCreateDebugUtilsMessengerEXT"));
        if (vkCreateDebugUtilsMessengerEXT_ != nullptr
            && vkCreateDebugUtilsMessengerEXT_(d->m_instance, &dbgInfo, nullptr,
                   &d->m_debugMessenger)
                != VK_SUCCESS) {
            d->m_debugMessenger = VK_NULL_HANDLE; // 计数降级（validationEnabled 保持）
        }
    }

    // ---- 物理设备：首个支持 Vulkan 1.3 + 图形队列者（独显优先）----------------------
    uint32_t physicalCount = 0u;
    vkEnumeratePhysicalDevices(d->m_instance, &physicalCount, nullptr);
    if (physicalCount == 0u) {
        std::printf("[zrhi][vulkan] no Vulkan physical devices\n");
        return nullptr;
    }
    std::vector<VkPhysicalDevice> physicals(physicalCount);
    vkEnumeratePhysicalDevices(d->m_instance, &physicalCount, physicals.data());

    VkPhysicalDevice chosen = VK_NULL_HANDLE;
    uint32_t chosenFamily = 0xFFFFFFFFu;
    for (VkPhysicalDevice candidate : physicals) {
        VkPhysicalDeviceProperties props {};
        vkGetPhysicalDeviceProperties(candidate, &props);
        if (props.apiVersion < VK_API_VERSION_1_3) {
            continue; // 设备级 1.3 必须（780M/RDNA3 支持）
        }
        uint32_t familyCount = 0u;
        vkGetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, nullptr);
        std::vector<VkQueueFamilyProperties> families(familyCount);
        vkGetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, families.data());
        for (uint32_t f = 0; f < familyCount; ++f) {
            const VkQueueFamilyProperties& qp = families[f];
            const bool graphics = (qp.queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0u;
            const bool compute = (qp.queueFlags & VK_QUEUE_COMPUTE_BIT) != 0u;
            const bool transfer = (qp.queueFlags & VK_QUEUE_TRANSFER_BIT) != 0u;
            if (graphics && compute && transfer) {
                chosen = candidate;
                chosenFamily = f;
                break;
            }
        }
        if (chosen != VK_NULL_HANDLE) {
            break;
        }
    }
    if (chosen == VK_NULL_HANDLE) {
        std::printf("[zrhi][vulkan] no Vulkan 1.3 device with graphics queue\n");
        return nullptr;
    }
    d->m_physicalDevice = chosen;
    d->m_queueFamily = chosenFamily;

    VkPhysicalDeviceProperties chosenProps {};
    vkGetPhysicalDeviceProperties(chosen, &chosenProps);
    VkPhysicalDeviceLimits limits {};
    {
        VkPhysicalDeviceProperties2 props2 {};
        props2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
        vkGetPhysicalDeviceProperties2(chosen, &props2);
        limits = props2.properties.limits;
    }
    d->m_timestampPeriodNs = static_cast<double>(limits.timestampPeriod);

    // ---- 特性链：动态渲染（1.3）+ descriptor indexing（update-after-bind bindless）--
    VkPhysicalDeviceDescriptorIndexingFeatures indexingFeatures {};
    indexingFeatures.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_INDEXING_FEATURES;
    indexingFeatures.descriptorBindingSampledImageUpdateAfterBind = VK_TRUE;
    indexingFeatures.descriptorBindingStorageImageUpdateAfterBind = VK_TRUE;
    indexingFeatures.descriptorBindingUniformBufferUpdateAfterBind = VK_TRUE;
    indexingFeatures.descriptorBindingPartiallyBound = VK_TRUE;
    indexingFeatures.descriptorBindingUpdateUnusedWhilePending = VK_TRUE;
    VkPhysicalDeviceVulkan13Features features13 {};
    features13.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES;
    features13.pNext = &indexingFeatures;
    features13.dynamicRendering = VK_TRUE;
    VkPhysicalDeviceFeatures2 features2 {};
    features2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
    features2.pNext = &features13;
    // 显式开启图形基础特性（features2 传入后核心特性不再走默认值路径；几何/
    // 曲面细分着色器本机支持——验证层对禁用特性的阶段展开检查会误报
    // COLOR_ATTACHMENT_OUTPUT 等待掩码，实证见施工报告）
    features2.features.geometryShader = VK_TRUE;
    features2.features.tessellationShader = VK_TRUE;
    features2.features.fillModeNonSolid = VK_TRUE;

    // 上限守卫（TASK-R10-05 B-4）：bindless 槽数受**四个** update-after-bind 上限
    // 共同约束（sampled/storage × per-stage/per-set）。历史缺陷：只查了 sampled 的
    // 两个上限——storage-image 上限更低的设备上 vkCreateDescriptorSetLayout 直接
    // 失败，后端被误报为"不可用"，而实际把容量收缩就能跑。任一上限为 0 = 该类
    // 描述符的 update-after-bind 不被支持（功能缺失）→ 后端不可用。
    VkPhysicalDeviceDescriptorIndexingProperties indexingProps {};
    indexingProps.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_INDEXING_PROPERTIES;
    VkPhysicalDeviceProperties2 props3 {};
    props3.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
    props3.pNext = &indexingProps;
    vkGetPhysicalDeviceProperties2(chosen, &props3);
    const uint32_t bindlessCapacity = zr::ZrVulkanBindlessCapacityFor(kBindlessCapacity,
        indexingProps.maxPerStageDescriptorUpdateAfterBindSampledImages,
        indexingProps.maxDescriptorSetUpdateAfterBindSampledImages,
        indexingProps.maxPerStageDescriptorUpdateAfterBindStorageImages,
        indexingProps.maxDescriptorSetUpdateAfterBindStorageImages);
    if (bindlessCapacity == 0u) {
        std::printf("[zrhi][vulkan] update-after-bind image limits zero "
                    "(sampled=%u/%u storage=%u/%u), backend unavailable\n",
            indexingProps.maxPerStageDescriptorUpdateAfterBindSampledImages,
            indexingProps.maxDescriptorSetUpdateAfterBindSampledImages,
            indexingProps.maxPerStageDescriptorUpdateAfterBindStorageImages,
            indexingProps.maxDescriptorSetUpdateAfterBindStorageImages);
        return nullptr;
    }
    if (bindlessCapacity < kBindlessCapacity) {
        // B-4 的核心修复：低上限设备不再被判"不可用"，容量收缩后继续跑
        std::printf("[zrhi][vulkan] bindless capacity clamped to %u (requested %u; "
                    "device update-after-bind limits sampled=%u/%u storage=%u/%u)\n",
            bindlessCapacity, kBindlessCapacity,
            indexingProps.maxPerStageDescriptorUpdateAfterBindSampledImages,
            indexingProps.maxDescriptorSetUpdateAfterBindSampledImages,
            indexingProps.maxPerStageDescriptorUpdateAfterBindStorageImages,
            indexingProps.maxDescriptorSetUpdateAfterBindStorageImages);
    }
    d->m_bindlessCapacity = bindlessCapacity;

    float queuePriority = 1.0f;
    VkDeviceQueueCreateInfo queueInfo {};
    queueInfo.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queueInfo.queueFamilyIndex = chosenFamily;
    queueInfo.queueCount = 1u;
    queueInfo.pQueuePriorities = &queuePriority;

    const char* deviceExtensions[] = { "VK_KHR_swapchain" };
    VkDeviceCreateInfo deviceInfo {};
    deviceInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    deviceInfo.pNext = &features2;
    deviceInfo.queueCreateInfoCount = 1u;
    deviceInfo.pQueueCreateInfos = &queueInfo;
    deviceInfo.enabledExtensionCount = 1u;
    deviceInfo.ppEnabledExtensionNames = deviceExtensions;
    if (vkCreateDevice(chosen, &deviceInfo, nullptr, &d->m_device) != VK_SUCCESS) {
        std::printf("[zrhi][vulkan] vkCreateDevice FAILED\n");
        return nullptr;
    }
    vkGetDeviceQueue(d->m_device, chosenFamily, 0u, &d->m_queue);

    // ---- initData 上传链路（staging → CopyBuffer → fence；专用池）--------------------
    VkCommandPoolCreateInfo uploadPoolInfo {};
    uploadPoolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    uploadPoolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    uploadPoolInfo.queueFamilyIndex = chosenFamily;
    if (vkCreateCommandPool(d->m_device, &uploadPoolInfo, nullptr, &d->m_uploadPool)
        != VK_SUCCESS) {
        return nullptr;
    }
    VkCommandBufferAllocateInfo uploadAlloc {};
    uploadAlloc.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    uploadAlloc.commandPool = d->m_uploadPool;
    uploadAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    uploadAlloc.commandBufferCount = 1u;
    if (vkAllocateCommandBuffers(d->m_device, &uploadAlloc, &d->m_uploadCmd) != VK_SUCCESS) {
        return nullptr;
    }
    VkFenceCreateInfo uploadFenceInfo {};
    uploadFenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    if (vkCreateFence(d->m_device, &uploadFenceInfo, nullptr, &d->m_uploadFence) != VK_SUCCESS) {
        return nullptr;
    }

    // ---- 全局 bindless 大 set（descriptor indexing；layout 见类注）-------------------
    {
        // B-4（TASK-R10-05）：容量经四上限收缩后的生效值（默认路径 == kBindlessCapacity）
        const uint32_t cbv = d->m_bindlessCapacity;
        VkDescriptorSetLayoutBinding bindings[2] = {};
        bindings[0].binding = 0u; // SAMPLED_IMAGE 数组（SRV 槽）
        bindings[0].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
        bindings[0].descriptorCount = cbv;
        bindings[0].stageFlags = VK_SHADER_STAGE_ALL;
        bindings[0].pImmutableSamplers = nullptr;
        bindings[1].binding = 1u; // STORAGE_IMAGE 数组（UAV 槽）
        bindings[1].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
        bindings[1].descriptorCount = cbv;
        bindings[1].stageFlags = VK_SHADER_STAGE_ALL;

        VkDescriptorBindingFlags bindingFlags[2] = {
            VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT | VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT
                | VK_DESCRIPTOR_BINDING_UPDATE_UNUSED_WHILE_PENDING_BIT,
            VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT | VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT
                | VK_DESCRIPTOR_BINDING_UPDATE_UNUSED_WHILE_PENDING_BIT,
        };
        VkDescriptorSetLayoutBindingFlagsCreateInfo flagsInfo {};
        flagsInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_BINDING_FLAGS_CREATE_INFO;
        flagsInfo.bindingCount = 2u;
        flagsInfo.pBindingFlags = bindingFlags;

        VkDescriptorSetLayoutCreateInfo layoutInfo {};
        layoutInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
        layoutInfo.pNext = &flagsInfo;
        layoutInfo.flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_UPDATE_AFTER_BIND_POOL_BIT;
        layoutInfo.bindingCount = 2u;
        layoutInfo.pBindings = bindings;
        if (vkCreateDescriptorSetLayout(d->m_device, &layoutInfo, nullptr, &d->m_bindlessLayout)
            != VK_SUCCESS) {
            return nullptr;
        }

        VkDescriptorPoolSize poolSizes[2] = {};
        poolSizes[0].type = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
        poolSizes[0].descriptorCount = cbv;
        poolSizes[1].type = VK_DESCRIPTOR_TYPE_STORAGE_IMAGE;
        poolSizes[1].descriptorCount = cbv;
        VkDescriptorPoolCreateInfo poolInfo {};
        poolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
        poolInfo.flags = VK_DESCRIPTOR_POOL_CREATE_UPDATE_AFTER_BIND_BIT;
        poolInfo.maxSets = 1u;
        poolInfo.poolSizeCount = 2u;
        poolInfo.pPoolSizes = poolSizes;
        if (vkCreateDescriptorPool(d->m_device, &poolInfo, nullptr, &d->m_bindlessPool)
            != VK_SUCCESS) {
            return nullptr;
        }
        VkDescriptorSetAllocateInfo setAlloc {};
        setAlloc.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
        setAlloc.descriptorPool = d->m_bindlessPool;
        setAlloc.descriptorSetCount = 1u;
        setAlloc.pSetLayouts = &d->m_bindlessLayout;
        if (vkAllocateDescriptorSets(d->m_device, &setAlloc, &d->m_bindlessSet) != VK_SUCCESS) {
            return nullptr;
        }
    }

    // ---- 设备级 Point/Clamp 静态采样器（SRV 表的 s_i 对位）----------------------------
    {
        VkSamplerCreateInfo samplerInfo {};
        samplerInfo.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
        samplerInfo.magFilter = VK_FILTER_NEAREST;
        samplerInfo.minFilter = VK_FILTER_NEAREST;
        samplerInfo.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
        samplerInfo.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        samplerInfo.maxLod = VK_LOD_CLAMP_NONE;
        if (vkCreateSampler(d->m_device, &samplerInfo, nullptr, &d->m_pointClampSampler)
            != VK_SUCCESS) {
            return nullptr;
        }
    }

    // ---- 队列包装（Graphics/Compute/Transfer；基座共用 graphics 全能队列）--------------
    for (uint32_t i = 0; i < 3u; ++i) {
        d->m_queues[i].reset(new ZrVulkanQueue(d.get(), static_cast<ZrQueueType>(i)));
    }

    std::printf("[zrhi][vulkan] device created: %s (api %u.%u), validation %s\n",
        chosenProps.deviceName, VK_API_VERSION_MAJOR(chosenProps.apiVersion),
        VK_API_VERSION_MINOR(chosenProps.apiVersion), validationAvailable ? "ENABLED" : "UNAVAILABLE (degraded)");
    return d.release();
}

ZrVulkanDevice::~ZrVulkanDevice()
{
    // 调用方须先 WaitIdle 并 Destroy 全部资源（≈ Vulkan 设备销毁前资源归还次序）
    assert(m_imageTable.AliveCount() == 0u && m_bufferTable.AliveCount() == 0u
        && m_imageViewTable.AliveCount() == 0u && m_samplerTable.AliveCount() == 0u
        && m_queryPoolTable.AliveCount() == 0u && m_commandListTable.AliveCount() == 0u
        && m_swapchainTable.AliveCount() == 0u && m_aliasHeapTable.AliveCount() == 0u
        && m_shaderTable.AliveCount() == 0u && m_psoTable.AliveCount() == 0u);
    if (m_device != VK_NULL_HANDLE) {
        vkDestroySampler(m_device, m_pointClampSampler, nullptr);
        vkDestroyDescriptorSetLayout(m_device, m_bindlessLayout, nullptr);
        vkDestroyDescriptorPool(m_device, m_bindlessPool, nullptr);
        vkDestroyFence(m_device, m_uploadFence, nullptr);
        vkDestroyCommandPool(m_device, m_uploadPool, nullptr);
        vkDestroyDevice(m_device, nullptr);
    }
    if (m_debugMessenger != VK_NULL_HANDLE) {
        const auto vkDestroyDebugUtilsMessengerEXT_ =
            reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(
                vkGetInstanceProcAddr(m_instance, "vkDestroyDebugUtilsMessengerEXT"));
        if (vkDestroyDebugUtilsMessengerEXT_ != nullptr) {
            vkDestroyDebugUtilsMessengerEXT_(m_instance, m_debugMessenger, nullptr);
        }
    }
    if (m_surface != VK_NULL_HANDLE) {
        vkDestroySurfaceKHR(m_instance, m_surface, nullptr);
    }
    if (m_instance != VK_NULL_HANDLE) {
        vkDestroyInstance(m_instance, nullptr);
    }
}

// =============================================================================
// 内存与创建：缓冲 / 图像
// =============================================================================
bool ZrVulkanDevice::AllocateMemory(VkMemoryRequirements reqs, VkMemoryPropertyFlags props,
    VkDeviceMemory* outMemory)
{
    const uint32_t typeIndex = FindMemoryType(m_physicalDevice, reqs.memoryTypeBits, props);
    if (typeIndex == 0xFFFFFFFFu) {
        return false;
    }
    VkMemoryAllocateInfo allocInfo {};
    allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocInfo.allocationSize = reqs.size;
    allocInfo.memoryTypeIndex = typeIndex;
    return vkAllocateMemory(m_device, &allocInfo, nullptr, outMemory) == VK_SUCCESS;
}

ZrBuffer ZrVulkanDevice::CreateBuffer(const ZrBufferDesc& desc, const void* initData)
{
    if (desc.size == 0 || (desc.hostVisible && desc.readback)) {
        return ZrBuffer{};
    }
    // → vkCreateBuffer + 内存域选择（desc.hostVisible/readback → HOST_VISIBLE 域）
    ZrVulkanBufferObj* obj = new ZrVulkanBufferObj();
    obj->desc = desc;
    obj->device = m_device;

    VkBufferCreateInfo bufferInfo {};
    bufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bufferInfo.size = desc.size;
    bufferInfo.usage = ZrVulkan_BufferUsage(desc.usage);
    if (bufferInfo.usage == 0u) {
        delete obj;
        return ZrBuffer{};
    }
    bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE; // 单设备独占假设（desc 契约）
    if (vkCreateBuffer(m_device, &bufferInfo, nullptr, &obj->buffer) != VK_SUCCESS) {
        delete obj;
        return ZrBuffer{};
    }
    VkMemoryRequirements reqs {};
    vkGetBufferMemoryRequirements(m_device, obj->buffer, &reqs);
    const VkMemoryPropertyFlags props = (desc.hostVisible || desc.readback)
        ? (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)
        : VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
    if (!AllocateMemory(reqs, props, &obj->memory)
        || vkBindBufferMemory(m_device, obj->buffer, obj->memory, 0u) != VK_SUCCESS) {
        Destroy(m_bufferTable.Acquire(obj));
        return ZrBuffer{};
    }
    // HOST_VISIBLE 堆：创建期持久映射（IZrBuffer::Map 契约——持久映射语义）
    if (desc.hostVisible || desc.readback) {
        if (vkMapMemory(m_device, obj->memory, 0u, desc.size, 0u, &obj->persistentMap)
            != VK_SUCCESS) {
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
    }

    // ---- 初始数据上传：staging（HOST_VISIBLE）→ vkCmdCopyBuffer → fence 等待 ----
    // （≈ Vulkan 的经典 staging 路径；D3D12 后端 UPLOAD 中转链的等价实现）
    if (initData != nullptr && !desc.hostVisible && !desc.readback) {
        VkBufferCreateInfo stagingInfo = bufferInfo;
        stagingInfo.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT;
        VkBuffer staging = VK_NULL_HANDLE;
        if (vkCreateBuffer(m_device, &stagingInfo, nullptr, &staging) != VK_SUCCESS) {
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        VkMemoryRequirements stagingReqs {};
        vkGetBufferMemoryRequirements(m_device, staging, &stagingReqs);
        VkDeviceMemory stagingMemory = VK_NULL_HANDLE;
        if (!AllocateMemory(stagingReqs,
                VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                &stagingMemory)) {
            vkDestroyBuffer(m_device, staging, nullptr);
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        vkBindBufferMemory(m_device, staging, stagingMemory, 0u);
        void* mapped = nullptr;
        if (vkMapMemory(m_device, stagingMemory, 0u, desc.size, 0u, &mapped) != VK_SUCCESS
            || mapped == nullptr) {
            vkFreeMemory(m_device, stagingMemory, nullptr);
            vkDestroyBuffer(m_device, staging, nullptr);
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        std::memcpy(mapped, initData, static_cast<size_t>(desc.size));
        vkUnmapMemory(m_device, stagingMemory);

        vkResetCommandBuffer(m_uploadCmd, 0u);
        VkCommandBufferBeginInfo beginInfo {};
        beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        vkBeginCommandBuffer(m_uploadCmd, &beginInfo);
        VkBufferCopy region {};
        region.size = desc.size;
        vkCmdCopyBuffer(m_uploadCmd, staging, obj->buffer, 1u, &region);
        vkEndCommandBuffer(m_uploadCmd);
        VkSubmitInfo submitInfo {};
        submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
        submitInfo.commandBufferCount = 1u;
        submitInfo.pCommandBuffers = &m_uploadCmd;
        vkQueueSubmit(m_queue, 1u, &submitInfo, m_uploadFence);
        vkWaitForFences(m_device, 1u, &m_uploadFence, VK_TRUE, UINT64_MAX);
        vkResetFences(m_device, 1u, &m_uploadFence);

        vkDestroyBuffer(m_device, staging, nullptr);
        vkFreeMemory(m_device, stagingMemory, nullptr);
    }
    return m_bufferTable.Acquire(obj);
}

ZrImage ZrVulkanDevice::CreateImage(const ZrImageDesc& desc)
{
    assert(desc.width > 0u && desc.height > 0u && desc.mipLevels > 0u && desc.arrayLayers > 0u);
    assert(desc.samples == ZrSampleCount::One); // 多重采样后续任务支持
    if (desc.width == 0u || desc.height == 0u || desc.mipLevels == 0u || desc.arrayLayers == 0u
        || desc.samples != ZrSampleCount::One) {
        return ZrImage{};
    }

    ZrVulkanImageObj* obj = new ZrVulkanImageObj();
    obj->desc = desc;

    VkImageCreateInfo imageInfo {};
    imageInfo.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    imageInfo.imageType = VK_IMAGE_TYPE_2D;
    imageInfo.format = ZrVulkan_Format(desc.format);
    imageInfo.extent = { desc.width, desc.height, 1u };
    imageInfo.mipLevels = desc.mipLevels;
    imageInfo.arrayLayers = desc.arrayLayers;
    imageInfo.samples = ZrVulkan_SampleCount(desc.samples);
    imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
    imageInfo.usage = ZrVulkan_ImageUsage(desc.usage);
    // 清屏路径保障：VK 的 vkCmdClearColorImage / vkCmdClearDepthStencilImage 合法域
    // 要求 TRANSFER_DST 用途（D3D12 的 RTV/DSV 清屏无此约束——颜色/深度附件图叠加
    // TransferDst 位保证等价能力）
    if ((ZrHasAll(desc.usage, ZrImageUsage::ColorAttachment)
            || ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment))
        && !ZrHasAll(desc.usage, ZrImageUsage::TransferDst)) {
        imageInfo.usage |= VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    }
    imageInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    imageInfo.initialLayout = ZrVulkan_ImageLayout(desc.initialLayout);
    // 深度图加 MUTABLE_FORMAT（TASK-B-R7-02）：CSM 阴影图的 R32Float 孪生解释
    // 采样视图要求源图像可变格式（VUID-VkImageViewCreateInfo-image-12397）
    if (ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment)) {
        imageInfo.flags |= VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
    }
    if (imageInfo.format == VK_FORMAT_UNDEFINED || imageInfo.usage == 0u
        || vkCreateImage(m_device, &imageInfo, nullptr, &obj->image) != VK_SUCCESS) {
        delete obj;
        return ZrImage{};
    }
    VkMemoryRequirements reqs {};
    vkGetImageMemoryRequirements(m_device, obj->image, &reqs);
    if (!AllocateMemory(reqs, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, &obj->memory)
        || vkBindImageMemory(m_device, obj->image, obj->memory, 0u) != VK_SUCCESS) {
        Destroy(m_imageTable.Acquire(obj));
        return ZrImage{};
    }
    return FinalizeImageCreation(obj, desc);
}

// =============================================================================
// 别名堆 + placed resource + 统计（每槽一段 VkDeviceMemory；offset 恒 0 模型）
// =============================================================================
ZrAliasHeap ZrVulkanDevice::CreateAliasHeap(uint64_t sizeBytes)
{
    assert(sizeBytes > 0u);
    if (sizeBytes == 0u) {
        return ZrAliasHeap{};
    }
    VkMemoryAllocateInfo allocInfo {};
    allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocInfo.allocationSize = sizeBytes;
    // 全内存类型兼容（DEDICATED_BIT 不设；堆要承载任意 placed image）：
    // 取 0xFFFF 掩码下首个 DEVICE_LOCAL 类型—— placed 临时贴图专用
    const uint32_t typeIndex = FindMemoryType(m_physicalDevice, 0xFFFFu,
        VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (typeIndex == 0xFFFFFFFFu) {
        return ZrAliasHeap{};
    }
    allocInfo.memoryTypeIndex = typeIndex;

    ZrVulkanAliasHeapObj* obj = new ZrVulkanAliasHeapObj();
    obj->sizeBytes = sizeBytes;
    if (vkAllocateMemory(m_device, &allocInfo, nullptr, &obj->memory) != VK_SUCCESS) {
        delete obj;
        return ZrAliasHeap{};
    }
    return m_aliasHeapTable.Acquire(obj);
}

uint64_t ZrVulkanDevice::GetImagePlacementSize(const ZrImageDesc& desc)
{
    // → vkGetImageMemoryRequirements：临时创建图像查 requirements 后销毁
    // （D3D12 后端 GetResourceAllocationInfo 的免资源查询在 VK 无对位物）
    VkImageCreateInfo imageInfo {};
    imageInfo.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    imageInfo.imageType = VK_IMAGE_TYPE_2D;
    imageInfo.format = ZrVulkan_Format(desc.format);
    imageInfo.extent = { desc.width, desc.height, 1u };
    imageInfo.mipLevels = desc.mipLevels;
    imageInfo.arrayLayers = desc.arrayLayers;
    imageInfo.samples = ZrVulkan_SampleCount(desc.samples);
    imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
    imageInfo.usage = ZrVulkan_ImageUsage(desc.usage);
    if ((ZrHasAll(desc.usage, ZrImageUsage::ColorAttachment)
            || ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment))
        && !ZrHasAll(desc.usage, ZrImageUsage::TransferDst)) {
        imageInfo.usage |= VK_IMAGE_USAGE_TRANSFER_DST_BIT; // 清屏合法域（见 CreateImage 注）
    }
    imageInfo.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    VkImage temp = VK_NULL_HANDLE;
    if (vkCreateImage(m_device, &imageInfo, nullptr, &temp) != VK_SUCCESS) {
        return 0u;
    }
    VkMemoryRequirements reqs {};
    vkGetImageMemoryRequirements(m_device, temp, &reqs);
    vkDestroyImage(m_device, temp, nullptr);
    return reqs.size;
}

ZrImage ZrVulkanDevice::CreateImageInHeap(
    const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset)
{
    assert(desc.width > 0u && desc.height > 0u && desc.mipLevels > 0u && desc.arrayLayers > 0u);
    assert(desc.samples == ZrSampleCount::One);
    void* payload = nullptr;
    if (desc.width == 0u || desc.height == 0u || desc.mipLevels == 0u || desc.arrayLayers == 0u
        || desc.samples != ZrSampleCount::One || !m_aliasHeapTable.TryGet(heap, payload)) {
        return ZrImage{};
    }
    ZrVulkanAliasHeapObj* heapObj = static_cast<ZrVulkanAliasHeapObj*>(payload);

    ZrVulkanImageObj* obj = new ZrVulkanImageObj();
    obj->desc = desc;
    obj->inAliasHeap = true;
    // → vkCreateImage + vkBindImageMemory(heap, offset)：placed image 与堆上其它
    // placed image 真共享物理内存（别名复用的物理基础，与 D3D12 CreatePlacedResource 对位）
    VkImageCreateInfo imageInfo {};
    imageInfo.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    imageInfo.imageType = VK_IMAGE_TYPE_2D;
    imageInfo.format = ZrVulkan_Format(desc.format);
    imageInfo.extent = { desc.width, desc.height, 1u };
    imageInfo.mipLevels = desc.mipLevels;
    imageInfo.arrayLayers = desc.arrayLayers;
    imageInfo.samples = ZrVulkan_SampleCount(desc.samples);
    imageInfo.tiling = VK_IMAGE_TILING_OPTIMAL;
    imageInfo.usage = ZrVulkan_ImageUsage(desc.usage);
    if ((ZrHasAll(desc.usage, ZrImageUsage::ColorAttachment)
            || ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment))
        && !ZrHasAll(desc.usage, ZrImageUsage::TransferDst)) {
        imageInfo.usage |= VK_IMAGE_USAGE_TRANSFER_DST_BIT; // 清屏合法域（见 CreateImage 注）
    }
    imageInfo.initialLayout = ZrVulkan_ImageLayout(desc.initialLayout);
    // 深度图加 MUTABLE_FORMAT（同 CreateImage 注：R32Float 孪生解释视图前提）
    if (ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment)) {
        imageInfo.flags |= VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
    }
    if (imageInfo.format == VK_FORMAT_UNDEFINED || imageInfo.usage == 0u
        || vkCreateImage(m_device, &imageInfo, nullptr, &obj->image) != VK_SUCCESS
        || vkBindImageMemory(m_device, obj->image, heapObj->memory, offset) != VK_SUCCESS) {
        Destroy(m_imageTable.Acquire(obj));
        return ZrImage{};
    }
    return FinalizeImageCreation(obj, desc);
}

ZrDeviceStats ZrVulkanDevice::GetDeviceStats()
{
    ZrDeviceStats stats;
    stats.aliasHeapCount = m_aliasHeapTable.AliveCount();
    stats.imageCount = m_imageTable.AliveCount();
    stats.bufferCount = m_bufferTable.AliveCount();
    // TASK-H-05 F1：命令表 set0 分配失败累计（写入点 AllocateRootDescriptorSet）。
    // Present 三字段为 D3D12 专属（VK 的呈现失败在 ZrVulkanSwapchain 内不分类），
    // 保持结构默认值 0，不虚报。
    stats.descriptorSetAllocFailureCount = m_descriptorSetAllocFailures;
    // TASK-R10-05 B-5：屏障批次截断累计（写入点 ZrVulkanCommandList::EmitBarriers）。
    stats.barrierTruncationCount = m_barrierTruncations;
    return stats;
}

VkDescriptorSet ZrVulkanDevice::AllocateRootDescriptorSet(
    VkDescriptorPool pool, VkDescriptorSetLayout layout)
{
    VkDescriptorSetAllocateInfo allocInfo {};
    allocInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    allocInfo.descriptorPool = pool;
    allocInfo.descriptorSetCount = 1u;
    allocInfo.pSetLayouts = &layout;
    VkDescriptorSet set = VK_NULL_HANDLE;
    if (vkAllocateDescriptorSets(m_device, &allocInfo, &set) != VK_SUCCESS) {
        // 池耗尽：不能在此重建池（已记录进命令缓冲的集合可能仍被 GPU 引用，销毁池是 UB），
        // 调用方只能跳过本次重绑。计数必须让上层可查——Release 下 assert 会被 NDEBUG
        // 编译掉，只计不报等于没修（TASK-H-03 B-4 的教训）。
        m_descriptorSetAllocFailures += 1u;
        return VK_NULL_HANDLE;
    }
    return set;
}

// 图像创建公共尾段：视图句柄无需预建（VK 视图按需创建）——仅入册
ZrImage ZrVulkanDevice::FinalizeImageCreation(ZrVulkanImageObj* obj, const ZrImageDesc& desc)
{
    (void)desc;
    return m_imageTable.Acquire(obj);
}

// =============================================================================
// 视图 / 采样器 / 着色器（SPIR-V）
// =============================================================================
ZrImageView ZrVulkanDevice::CreateImageView(const ZrImageViewDesc& desc)
{
    ZrVulkanImageObj* image = ResolveImage(desc.image);
    if (image == nullptr || image->image == VK_NULL_HANDLE) {
        return ZrImageView{};
    }
    // 视图格式（TASK-B-R7-02 修正）：深度图的"非深度孪生解释"（CSM 阴影图
    // D32Float → R32Float 采样视图，对位 D3D12 深度格式 SRV 解释）在 VK 侧
    // 直接以源图像格式建视图——D32 视图 + DEPTH aspect 采样在 .r 返回深度值，
    // 与 D3D12 R32F 解释读 .r 语义一致；而 R32F 视图与 D32 不同兼容类
    // （MUTABLE_FORMAT 也救不了深度格式类），validation 实证
    const bool srcIsDepthImage
        = (AspectForFormat(ZrVulkan_Format(image->desc.format)) & VK_IMAGE_ASPECT_DEPTH_BIT) != 0u;
    const ZrFormat viewFormat
        = (desc.overrideFormat && !srcIsDepthImage) ? desc.format : image->desc.format;
    const VkFormat vkFormat = ZrVulkan_Format(viewFormat);
    if (vkFormat == VK_FORMAT_UNDEFINED) {
        return ZrImageView{};
    }
    // mip 窗口（REMAINING 语义 → clamp 到资源实际范围；与 D3D12 后端同式）
    const uint32_t mipBase = desc.subresources.baseMipLevel < image->desc.mipLevels
        ? desc.subresources.baseMipLevel
        : image->desc.mipLevels;
    const uint32_t mipCount = (desc.subresources.levelCount == ZR_REMAINING_MIP_LEVELS)
        ? (image->desc.mipLevels - mipBase)
        : ((desc.subresources.baseMipLevel + desc.subresources.levelCount) < image->desc.mipLevels
                ? desc.subresources.levelCount
                : (image->desc.mipLevels - mipBase));
    const uint32_t layerBase = desc.subresources.baseArrayLayer < image->desc.arrayLayers
        ? desc.subresources.baseArrayLayer
        : image->desc.arrayLayers;
    const uint32_t layerCount = (desc.subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS)
        ? (image->desc.arrayLayers - layerBase)
        : desc.subresources.layerCount;

    // aspect 归源图像（TASK-B-R7-02）：深度图的非深度孪生解释（CSM 阴影图
    // D32Float → R32Float 采样视图，对位 D3D12 深度格式 SRV 的 R32Float 解释）
    // 仍须取 DEPTH aspect——视图格式推 COLOR 会因源图像无 COLOR aspect 而非法
    // （vkCreateImageView 规定 view aspect ⊆ image format 的 aspect 集）
    const VkImageAspectFlags srcAspect = AspectForFormat(ZrVulkan_Format(image->desc.format));
    const VkImageAspectFlags aspectMask
        = (srcAspect & VK_IMAGE_ASPECT_DEPTH_BIT) != 0u ? VK_IMAGE_ASPECT_DEPTH_BIT
                                                        : AspectForFormat(vkFormat);

    ZrVulkanViewObj* obj = new ZrVulkanViewObj();
    obj->desc = desc;

    // 深度孪生解释视图（TASK-B-R7-02）：视图格式（R32Float）与源图像（D32）
    // 不同且用途继承自图像（含 DEPTH_STENCIL_ATTACHMENT）——R32Float 不支持
    // DS 附件用途 → VkImageViewUsageCreateInfo 收窄为 SAMPLED（该视图仅采样）
    VkImageViewUsageCreateInfo viewUsage {};
    const bool reinterpretedDepthView
        = (srcAspect & VK_IMAGE_ASPECT_DEPTH_BIT) != 0u && aspectMask == VK_IMAGE_ASPECT_DEPTH_BIT
        && vkFormat != ZrVulkan_Format(image->desc.format);
    if (reinterpretedDepthView) {
        viewUsage.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_USAGE_CREATE_INFO;
        viewUsage.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
    }

    VkImageViewCreateInfo viewInfo {};
    viewInfo.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    viewInfo.pNext = reinterpretedDepthView ? &viewUsage : nullptr;
    viewInfo.image = image->image;
    viewInfo.format = vkFormat;
    viewInfo.subresourceRange.aspectMask = aspectMask;
    viewInfo.subresourceRange.baseMipLevel = mipBase;
    viewInfo.subresourceRange.levelCount = mipCount;
    viewInfo.subresourceRange.baseArrayLayer = layerBase;
    viewInfo.subresourceRange.layerCount = layerCount;
    if (image->desc.arrayLayers > 1u) {
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D_ARRAY; // 数组图（CSM 级联等）
    } else {
        viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
    }

    // SRV/UAV：视图 + 全局 bindless 大 set 槽位注册（RTV/DSV 仅附件视图）
    const bool needsBindless = desc.viewType == ZrImageViewType::Srv
        || desc.viewType == ZrImageViewType::Uav;
    if (needsBindless) {
        obj->bindlessSlot = m_bindlessSlots.Allocate();
        // TASK-H-05 F5（与 D3D12 侧 ZR_SLOT_INVALID 检查同策）：槽位耗尽时绝不能
        // 让视图"创建成功却落在 ZR_SLOT_INVALID"——WriteBindlessDescriptor 的边界
        // 兜底只保证不越界写，绑定阶段拿不到有效槽就是静默画错。此处直接失败返回。
        // B-4（TASK-R10-05）：分配器按名义容量（kBindlessCapacity）预分配，但
        // 生效容量可能被设备上限收缩——超出生效容量的槽位同样视为耗尽（描述符
        // 数组没有那么长，写入即越界）。
        if (obj->bindlessSlot == ZR_SLOT_INVALID || obj->bindlessSlot >= m_bindlessCapacity) {
            assert(false && "CreateImageView: bindless 槽位耗尽（m_bindlessCapacity）");
            if (obj->bindlessSlot != ZR_SLOT_INVALID) {
                m_bindlessSlots.Free(obj->bindlessSlot);
            }
            delete obj;
            return ZrImageView{};
        }
    }
    if (vkCreateImageView(m_device, &viewInfo, nullptr, &obj->view) != VK_SUCCESS) {
        if (obj->bindlessSlot != ZR_SLOT_INVALID) {
            m_bindlessSlots.Free(obj->bindlessSlot);
        }
        delete obj;
        return ZrImageView{};
    }
    if (needsBindless) {
        WriteBindlessDescriptor(obj->bindlessSlot, obj->view,
            desc.viewType == ZrImageViewType::Uav);
    }
    const ZrImageView viewHandle = m_imageViewTable.Acquire(obj);
    if (image->swapchainOwned) {
        // TASK-B-R9-09：依赖面登记——引用交换链后台缓冲的视图进登记表，
        // 重建/销毁交换链时统一销毁（句柄表无遍历接口，创建期登记是检索结构）
        m_swapchainViews.push_back(viewHandle);
    }
    return viewHandle;
}

void ZrVulkanDevice::WriteBindlessDescriptor(uint32_t slot, VkImageView view, bool storage)
{
    // B-4（TASK-R10-05）：上限改用生效容量（可能被设备 update-after-bind 上限收缩）
    assert(slot < m_bindlessCapacity);
    if (slot >= m_bindlessCapacity) {
        return;
    }
    VkDescriptorImageInfo imageInfo {};
    imageInfo.imageView = view;
    imageInfo.imageLayout = storage ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

    VkWriteDescriptorSet write {};
    write.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    write.dstSet = m_bindlessSet;
    write.dstBinding = storage ? 1u : 0u; // binding0=SAMPLED / binding1=STORAGE
    write.dstArrayElement = slot;         // 槽位即 GPU 索引（bindless 契约）
    write.descriptorCount = 1u;
    write.descriptorType = storage ? VK_DESCRIPTOR_TYPE_STORAGE_IMAGE : VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
    write.pImageInfo = &imageInfo;
    vkUpdateDescriptorSets(m_device, 1u, &write, 0u, nullptr);
}

ZrSampler ZrVulkanDevice::CreateSampler(const ZrSamplerDesc& desc)
{
    // → vkCreateSampler（三字段过滤独立；寻址/LOD/各向异性映射）
    ZrVulkanSamplerObj* obj = new ZrVulkanSamplerObj();
    obj->desc = desc;

    VkSamplerCreateInfo samplerInfo {};
    samplerInfo.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    samplerInfo.magFilter = ZrVulkan_Filter(desc.magFilter);
    samplerInfo.minFilter = ZrVulkan_Filter(desc.minFilter);
    samplerInfo.mipmapMode = desc.mipFilter == ZrFilter::Linear
        ? VK_SAMPLER_MIPMAP_MODE_LINEAR
        : VK_SAMPLER_MIPMAP_MODE_NEAREST;
    samplerInfo.addressModeU = ZrVulkan_AddressMode(desc.addressU);
    samplerInfo.addressModeV = ZrVulkan_AddressMode(desc.addressV);
    samplerInfo.addressModeW = ZrVulkan_AddressMode(desc.addressW);
    samplerInfo.mipLodBias = desc.mipLodBias;
    samplerInfo.maxAnisotropy = static_cast<float>(desc.maxAnisotropy);
    samplerInfo.minLod = desc.minLod;
    samplerInfo.maxLod = desc.maxLod;
    if (vkCreateSampler(m_device, &samplerInfo, nullptr, &obj->sampler) != VK_SUCCESS) {
        delete obj;
        return ZrSampler{};
    }
    return m_samplerTable.Acquire(obj);
}

ZrShader ZrVulkanDevice::CreateShader(const ZrShaderDesc& desc)
{
    // → vkCreateShaderModule：持有 SPIR-V 字节码拷贝（PSO 创建期被引用）
    if (desc.code == nullptr || desc.codeSize == 0u
        || (desc.codeSize % 4u) != 0u) { // SPIR-V 须为 4 的倍数（ZrShaderDesc 契约）
        return ZrShader{};
    }
    ZrVulkanShaderObj* obj = new ZrVulkanShaderObj();
    obj->desc = desc;
    const uint8_t* bytes = static_cast<const uint8_t*>(desc.code);
    obj->bytecode.assign(bytes, bytes + desc.codeSize);

    VkShaderModuleCreateInfo moduleInfo {};
    moduleInfo.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    moduleInfo.codeSize = desc.codeSize;
    moduleInfo.pCode = reinterpret_cast<const uint32_t*>(desc.code);
    VkShaderModule module = VK_NULL_HANDLE;
    if (vkCreateShaderModule(m_device, &moduleInfo, nullptr, &module) != VK_SUCCESS) {
        delete obj;
        return ZrShader{};
    }
    obj->desc.entryPoint = desc.entryPoint; // SPIR-V OpEntryPoint 名（glslc 产物）
    obj->module = module;
    return m_shaderTable.Acquire(obj);
}

// =============================================================================
// 图形 PSO（→ vkCreateGraphicsPipelines + VkPipelineLayout；动态渲染）
// =============================================================================

ZrPipelineState ZrVulkanDevice::CreatePipelineState(const ZrGraphicsPsoDesc& desc)
{
    if (!ZrValidateGraphicsPsoDesc(desc)) {
        assert(false && "CreatePipelineState: ZrGraphicsPsoDesc 校验失败（缺 shader/"
                        "非法格式/深度状态矛盾）");
        return ZrPipelineState{};
    }
    ZrVulkanShaderObj* vs = ResolveShader(desc.vertexShader);
    ZrVulkanShaderObj* ps = nullptr;
    if (!desc.depthOnly) {
        ps = ResolveShader(desc.pixelShader);
    }
    if (vs == nullptr || (!desc.depthOnly && ps == nullptr)) {
        return ZrPipelineState{}; // stale 句柄
    }
    assert(vs->desc.stage == ZrShaderStage::Vertex);
    assert(desc.depthOnly || ps->desc.stage == ZrShaderStage::Pixel);

    // 根 CBV 槽数 1..5 / SRV 表数 0..2（与 D3D12 后端同源约束）
    const uint32_t cbvCount = desc.rootCbvCount;
    if (cbvCount == 0u || cbvCount > 5u) {
        assert(false && "CreatePipelineState: rootCbvCount 须为 1..5");
        return ZrPipelineState{};
    }
    const uint32_t srvTableCount
        = desc.srvTableCount != 0u ? desc.srvTableCount : (desc.srvTable ? 1u : 0u);
    if (srvTableCount > 2u) {
        assert(false && "CreatePipelineState: srvTableCount 须为 0..2");
        return ZrPipelineState{};
    }

    ZrVulkanPsoObj* obj = new ZrVulkanPsoObj();
    obj->desc = desc;
    // Write the normalised count back into the stored desc. Callers may legally set only
    // srvTable=true and leave srvTableCount at 0 (the window demo does exactly that), while
    // RebindDescriptorSet walks obj->desc.srvTableCount to fill the SRV slots - leaving them
    // out of sync builds SRV bindings in the layout that never receive a descriptor.
    obj->desc.srvTableCount = srvTableCount;

    // ---- set0 布局：binding0..N-1 UNIFORM_BUFFER（根 CBV 槽）+ N..N+M-1
    //      SAMPLED_IMAGE + N+M..N+2M-1 SAMPLER（SRV 表）——TASK-B-R7-02 修正：
    //      glslc -x hlsl 把 Texture2DArray t_i / SamplerState s_i 出成两个独立
    //      UniformConstant 描述符（OpSampledImage 在 shader 内合成），非
    //      COMBINED_IMAGE_SAMPLER；B-R7-01 的 combined 方案无带纹理 shader 实测
    //      而未暴露。编译侧经 -fvk-t-shift/-fvk-s-shift 把 t0→binding N、
    //      s0→binding N+M（--shadows：N=4 M=1，见 CMakeLists）
    VkDescriptorSetLayoutBinding bindings[9] = {};
    const uint32_t bindingCount = cbvCount + srvTableCount * 2u;
    for (uint32_t i = 0; i < cbvCount; ++i) {
        bindings[i].binding = i;
        bindings[i].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
        bindings[i].descriptorCount = 1u;
        bindings[i].stageFlags = VK_SHADER_STAGE_ALL; // D3D12 根 CBV ALL 可见性对位
    }
    for (uint32_t t = 0; t < srvTableCount; ++t) {
        bindings[cbvCount + t].binding = cbvCount + t;
        bindings[cbvCount + t].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
        bindings[cbvCount + t].descriptorCount = 1u;
        bindings[cbvCount + t].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
        bindings[cbvCount + srvTableCount + t].binding = cbvCount + srvTableCount + t;
        bindings[cbvCount + srvTableCount + t].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLER;
        bindings[cbvCount + srvTableCount + t].descriptorCount = 1u;
        bindings[cbvCount + srvTableCount + t].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    }
    // UPDATE_AFTER_BIND：根 CBV/SRV 表为"每绘直绑"语义（对位 D3D12 根参数直绑），
    // 描述符在集合已绑定的记录期内改写——须 update-after-bind（特性已开），
    // 否则命令缓冲失效（validation VUID-vkUpdateDescriptorSets 实证）
    VkDescriptorBindingFlags bindingFlags[9] = {};
    for (uint32_t i = 0; i < bindingCount; ++i) {
        bindingFlags[i] = VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT
            | VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT;
    }
    VkDescriptorSetLayoutBindingFlagsCreateInfo flagsInfo {};
    flagsInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_BINDING_FLAGS_CREATE_INFO;
    flagsInfo.bindingCount = bindingCount;
    flagsInfo.pBindingFlags = bindingFlags;
    VkDescriptorSetLayoutCreateInfo setLayoutInfo {};
    setLayoutInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    setLayoutInfo.pNext = &flagsInfo;
    setLayoutInfo.flags = VK_DESCRIPTOR_SET_LAYOUT_CREATE_UPDATE_AFTER_BIND_POOL_BIT;
    setLayoutInfo.bindingCount = bindingCount;
    setLayoutInfo.pBindings = bindings;
    if (vkCreateDescriptorSetLayout(m_device, &setLayoutInfo, nullptr, &obj->setLayout)
        != VK_SUCCESS) {
        delete obj;
        return ZrPipelineState{};
    }
    VkPipelineLayoutCreateInfo layoutInfo {};
    layoutInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    layoutInfo.setLayoutCount = 1u;
    layoutInfo.pSetLayouts = &obj->setLayout;
    if (vkCreatePipelineLayout(m_device, &layoutInfo, nullptr, &obj->pipelineLayout)
        != VK_SUCCESS) {
        Destroy(m_psoTable.Acquire(obj));
        return ZrPipelineState{};
    }

    // ---- 着色器阶段（SPIR-V；depth-only 无 PS）---------------------------------------
    VkPipelineShaderStageCreateInfo stages[2] = {};
    stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    stages[0].module = vs->module;
    stages[0].pName = vs->desc.entryPoint;
    uint32_t stageCount = 1u;
    if (!desc.depthOnly) {
        stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        stages[1].module = ps->module;
        stages[1].pName = ps->desc.entryPoint;
        stageCount = 2u;
    }

    // ---- 顶点输入：v1 固定语义版（Pos3f+Normal3f+Uv2f，stride 32B——与 D3D12 同源）----
    VkVertexInputBindingDescription vertexBinding {};
    vertexBinding.binding = 0u;
    vertexBinding.stride = 32u; // ZR_VULKAN_GRAPHICS_VERTEX_STRIDE（与 commandlist 同源）
    vertexBinding.inputRate = VK_VERTEX_INPUT_RATE_VERTEX;
    VkVertexInputAttributeDescription vertexAttrs[3] = {};
    vertexAttrs[0] = { 0u, 0u, VK_FORMAT_R32G32B32_SFLOAT, 0u };  // POSITION
    vertexAttrs[1] = { 1u, 0u, VK_FORMAT_R32G32B32_SFLOAT, 12u }; // NORMAL
    vertexAttrs[2] = { 2u, 0u, VK_FORMAT_R32G32_SFLOAT, 24u };    // TEXCOORD0
    VkPipelineVertexInputStateCreateInfo vertexInput {};
    vertexInput.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vertexInput.vertexBindingDescriptionCount = 1u;
    vertexInput.pVertexBindingDescriptions = &vertexBinding;
    vertexInput.vertexAttributeDescriptionCount = 3u;
    vertexInput.pVertexAttributeDescriptions = vertexAttrs;

    VkPipelineInputAssemblyStateCreateInfo inputAssembly {};
    inputAssembly.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    inputAssembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST; // v1 固定拓扑

    VkPipelineViewportStateCreateInfo viewportState {};
    viewportState.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    viewportState.viewportCount = 1u; // 动态（负高度视口，见 commandlist）
    viewportState.scissorCount = 1u;

    // ---- 光栅化：实体填充 + desc.cullMode；frontFace=CW 配负视口复刻 D3D12 CW-------
    // （TASK-B-R8-01 修正：B-R7-01 原取 CCW，实为反向。推导：负高度视口使 VK 帧缓冲
    //  的 NDC→行坐标映射与 D3D12 完全一致 → 同一三角形两侧行坐标绕序相同；
    //  D3D12 FrontCounterClockwise=FALSE 的 front=CW-on-RT，VK frontFace 须同为 CW
    //  才能圈定同一集合。CCW 时代未暴露：既有模式全部 CullMode::None 或
    //  剔除不敏感（--shadows 光视 pass 的地面探针判据分不出近/远面存储）——
    //  --r5 OutlinePass（反向壳 FrontCull，相机透视投影）首次暴露：壳正面未被剔除
    //  且深度先于 toon 主体 → 整个剪影被描边色覆盖。--r5 VK 实证。）
    VkPipelineRasterizationStateCreateInfo rasterizer {};
    rasterizer.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rasterizer.polygonMode = VK_POLYGON_MODE_FILL;
    rasterizer.cullMode = ZrVulkan_CullMode(desc.cullMode);
    rasterizer.frontFace = VK_FRONT_FACE_CLOCKWISE;
    rasterizer.lineWidth = 1.0f;
    rasterizer.depthClampEnable = VK_FALSE; // D3D12 DepthClipEnable=TRUE = 深度裁剪语义；
                                            // VK FALSE 即裁剪（clamp 需额外特性且语义相反）

    VkPipelineMultisampleStateCreateInfo multisample {};
    multisample.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;

    // ---- 深度状态：LESS 比较（D3D12 同源）；无深度附件时禁用---------------------------
    VkPipelineDepthStencilStateCreateInfo depthStencil {};
    depthStencil.sType = VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO;
    depthStencil.depthTestEnable
        = (desc.dsvFormat != ZrFormat::Count && desc.depthTest) ? VK_TRUE : VK_FALSE;
    depthStencil.depthWriteEnable = desc.depthWrite ? VK_TRUE : VK_FALSE;
    depthStencil.depthCompareOp = VK_COMPARE_OP_LESS;
    depthStencil.stencilTestEnable = VK_FALSE;

    // ---- 混合：v1 全 Opaque（RenderTarget0 直通）-------------------------------------
    const ZrVulkanBlendParams blend = ZrVulkan_BlendFactor(desc.blend);
    VkPipelineColorBlendAttachmentState blendAttachment {};
    blendAttachment.blendEnable = VK_FALSE;
    blendAttachment.srcColorBlendFactor = blend.src;
    blendAttachment.dstColorBlendFactor = blend.dst;
    blendAttachment.colorBlendOp = VK_BLEND_OP_ADD;
    blendAttachment.srcAlphaBlendFactor = blend.srcAlpha;
    blendAttachment.dstAlphaBlendFactor = blend.dstAlpha;
    blendAttachment.alphaBlendOp = VK_BLEND_OP_ADD;
    blendAttachment.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT
        | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    VkPipelineColorBlendStateCreateInfo colorBlend {};
    colorBlend.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    colorBlend.attachmentCount = desc.depthOnly ? 0u : 1u;
    colorBlend.pAttachments = &blendAttachment;

    // ---- 动态状态：视口/剪裁（SetViewportScissor 命令侧下发）--------------------------
    const VkDynamicState dynamicStates[2] = { VK_DYNAMIC_STATE_VIEWPORT,
        VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo dynamicState {};
    dynamicState.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    dynamicState.dynamicStateCount = 2u;
    dynamicState.pDynamicStates = dynamicStates;

    // ---- 动态渲染：附件格式经 VkPipelineRenderingCreateInfo 链入（无 RenderPass 对象，
    //      简报"VK_KHR_dynamic_rendering 可用则免 RenderPass 对象"的兑现——1.3 核心）--
    VkFormat rtvFormat = VK_FORMAT_UNDEFINED;
    if (!desc.depthOnly) {
        rtvFormat = ZrVulkan_Format(desc.rtvFormat);
    }
    VkFormat dsvFormat = VK_FORMAT_UNDEFINED;
    if (desc.dsvFormat != ZrFormat::Count) {
        dsvFormat = ZrVulkan_Format(desc.dsvFormat);
    }
    VkPipelineRenderingCreateInfo renderingInfo {};
    renderingInfo.sType = VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO;
    renderingInfo.colorAttachmentCount = desc.depthOnly ? 0u : 1u;
    renderingInfo.pColorAttachmentFormats = &rtvFormat;
    renderingInfo.depthAttachmentFormat = dsvFormat;

    VkGraphicsPipelineCreateInfo pipelineInfo {};
    pipelineInfo.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    pipelineInfo.pNext = &renderingInfo;
    pipelineInfo.stageCount = stageCount;
    pipelineInfo.pStages = stages;
    pipelineInfo.pVertexInputState = &vertexInput;
    pipelineInfo.pInputAssemblyState = &inputAssembly;
    pipelineInfo.pViewportState = &viewportState;
    pipelineInfo.pRasterizationState = &rasterizer;
    pipelineInfo.pMultisampleState = &multisample;
    pipelineInfo.pDepthStencilState = &depthStencil;
    pipelineInfo.pColorBlendState = &colorBlend;
    pipelineInfo.pDynamicState = &dynamicState;
    pipelineInfo.layout = obj->pipelineLayout;
    pipelineInfo.renderPass = VK_NULL_HANDLE; // 动态渲染
    pipelineInfo.subpass = 0u;
    if (vkCreateGraphicsPipelines(m_device, VK_NULL_HANDLE, 1u, &pipelineInfo, nullptr,
            &obj->pipeline)
        != VK_SUCCESS) {
        Destroy(m_psoTable.Acquire(obj));
        return ZrPipelineState{};
    }
    return m_psoTable.Acquire(obj);
}

// ---- ZrShader / ZrPipelineState 销毁（≈ vkDestroyShaderModule / vkDestroyPipeline）--
void ZrVulkanDevice::Destroy(ZrShader handle)
{
    void* payload = nullptr;
    if (!m_shaderTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanShaderObj* obj = static_cast<ZrVulkanShaderObj*>(payload);
    if (obj->module != VK_NULL_HANDLE) {
        vkDestroyShaderModule(m_device, obj->module, nullptr);
    }
    delete obj;
    m_shaderTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrPipelineState handle)
{
    void* payload = nullptr;
    if (!m_psoTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanPsoObj* obj = static_cast<ZrVulkanPsoObj*>(payload);
    if (obj->pipeline != VK_NULL_HANDLE) {
        vkDestroyPipeline(m_device, obj->pipeline, nullptr);
    }
    if (obj->pipelineLayout != VK_NULL_HANDLE) {
        vkDestroyPipelineLayout(m_device, obj->pipelineLayout, nullptr);
    }
    if (obj->setLayout != VK_NULL_HANDLE) {
        vkDestroyDescriptorSetLayout(m_device, obj->setLayout, nullptr);
    }
    delete obj;
    m_psoTable.Release(handle);
}

// =============================================================================
// 交换链（→ vkCreateSwapchainKHR；VK_KHR_win32_surface）
// =============================================================================
ZrSwapchain ZrVulkanDevice::CreateSwapchain(const ZrSwapchainDesc& desc)
{
    assert(desc.windowHandle != nullptr); // 无窗口场景请用离屏图像
    assert(desc.width > 0u && desc.height > 0u && desc.imageCount >= 2u);
    if (desc.windowHandle == nullptr || desc.width == 0u || desc.height == 0u
        || desc.imageCount < 2u) {
        return ZrSwapchain{};
    }

    // Win32 surface（设备级缓存一个——单窗口基座）
    if (m_surface == VK_NULL_HANDLE) {
        VkWin32SurfaceCreateInfoKHR surfaceInfo {};
        surfaceInfo.sType = VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR;
        surfaceInfo.hinstance = ::GetModuleHandleW(nullptr);
        surfaceInfo.hwnd = static_cast<HWND>(desc.windowHandle);
        if (vkCreateWin32SurfaceKHR(m_instance, &surfaceInfo, nullptr, &m_surface)
            != VK_SUCCESS) {
            return ZrSwapchain{};
        }
        // 呈现支持校验（物理设备选择时尚无 surface，此处补验——单 GPU 环境恒过）
        VkBool32 presentSupported = VK_FALSE;
        vkGetPhysicalDeviceSurfaceSupportKHR(
            m_physicalDevice, m_queueFamily, m_surface, &presentSupported);
        if (!presentSupported) {
            std::printf("[zrhi][vulkan] queue family does not support present\n");
            return ZrSwapchain{};
        }
    }

    // 表面能力：格式/呈现模式/图像数裁剪
    VkSurfaceCapabilitiesKHR caps {};
    if (vkGetPhysicalDeviceSurfaceCapabilitiesKHR(m_physicalDevice, m_surface, &caps)
        != VK_SUCCESS) {
        return ZrSwapchain{};
    }
    uint32_t formatCount = 0u;
    vkGetPhysicalDeviceSurfaceFormatsKHR(m_physicalDevice, m_surface, &formatCount, nullptr);
    std::vector<VkSurfaceFormatKHR> formats(formatCount);
    vkGetPhysicalDeviceSurfaceFormatsKHR(m_physicalDevice, m_surface, &formatCount, formats.data());
    const VkFormat requestedFormat = ZrVulkan_Format(desc.format);
    bool formatSupported = false;
    for (const VkSurfaceFormatKHR& f : formats) {
        // VK_FORMAT_UNDEFINED 表面 = 无偏好（任意格式可用）
        if (f.format == requestedFormat || f.format == VK_FORMAT_UNDEFINED) {
            formatSupported = true;
            break;
        }
    }
    if (!formatSupported) {
        std::printf("[zrhi][vulkan] requested swapchain format not supported\n");
        return ZrSwapchain{};
    }

    // PresentMode：表面不支持时降级 Fifo（必支持兜底；D3D12 侧"撕裂不支持降级"同策）
    const VkPresentModeKHR requestedPresent
        = PickPresentMode(m_physicalDevice, m_surface, desc.presentMode);

    ZrVulkanSwapchain* sc = new ZrVulkanSwapchain(this);
    sc->m_presentMode = desc.presentMode;

    uint32_t imageCount = desc.imageCount;
    if (caps.minImageCount > imageCount) {
        imageCount = caps.minImageCount;
    }
    if (caps.maxImageCount != 0u && imageCount > caps.maxImageCount) {
        imageCount = caps.maxImageCount;
    }

    VkSwapchainCreateInfoKHR swapInfo {};
    swapInfo.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    swapInfo.surface = m_surface;
    swapInfo.minImageCount = imageCount;
    swapInfo.imageFormat = requestedFormat;
    swapInfo.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    swapInfo.imageExtent = { desc.width, desc.height };
    swapInfo.imageArrayLayers = 1u;
    swapInfo.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT
        | VK_IMAGE_USAGE_TRANSFER_DST_BIT; // 清屏合法域（ClearPass 对后台缓冲清屏）
    swapInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swapInfo.preTransform = caps.currentTransform;
    swapInfo.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swapInfo.presentMode = requestedPresent;
    swapInfo.clipped = VK_TRUE;
    swapInfo.oldSwapchain = VK_NULL_HANDLE; // 首建无旧链（重建路径 Recreate() 传旧链）
    if (vkCreateSwapchainKHR(m_device, &swapInfo, nullptr, &sc->swapchain) != VK_SUCCESS) {
        delete sc;
        return ZrSwapchain{};
    }

    // ---- 后台缓冲图像句柄化（vkGetSwapchainImagesKHR）+ 每图呈现信号量 ----------------
    uint32_t actualCount = 0u;
    vkGetSwapchainImagesKHR(m_device, sc->swapchain, &actualCount, nullptr);
    std::vector<VkImage> images(actualCount);
    vkGetSwapchainImagesKHR(m_device, sc->swapchain, &actualCount, images.data());
    sc->m_backbufferImages.resize(actualCount);
    sc->renderFinished.resize(actualCount);

    VkSemaphoreCreateInfo semaphoreInfo {};
    semaphoreInfo.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    if (vkCreateSemaphore(m_device, &semaphoreInfo, nullptr, &sc->imageAvailable)
        != VK_SUCCESS) {
        Destroy(m_swapchainTable.Acquire(sc));
        return ZrSwapchain{};
    }
    for (uint32_t i = 0; i < actualCount; ++i) {
        ZrVulkanImageObj* obj = new ZrVulkanImageObj();
        obj->image = images[i];
        obj->desc.format = desc.format;
        obj->desc.width = desc.width;
        obj->desc.height = desc.height;
        obj->desc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        obj->swapchainOwned = true;
        sc->m_backbufferImages[i] = m_imageTable.Acquire(obj);
        // 后台缓冲初始布局 = UNDEFINED（Acquire 语义；≈ D3D12 COMMON 创建）
        if (vkCreateSemaphore(m_device, &semaphoreInfo, nullptr, &sc->renderFinished[i])
            != VK_SUCCESS) {
            Destroy(m_swapchainTable.Acquire(sc));
            return ZrSwapchain{};
        }
    }
    // TASK-B-R9-09：登记建链描述与尺寸台账（重建按 m_desc 的格式/呈现模式/缓冲数；
    // 台账 from=to=建链尺寸、serial=0 —— 取证行 serial=N→M 的起点）
    sc->m_desc = desc;
    sc->m_ledger.RecordCreated(desc.width, desc.height);
    // TASK-B-R9-11：链已挂接 → 提交重新受 acquire 闸约束（R10-03 A-5/A-6 语义）
    m_frameGate.SetSwapchainAttached(true);
    return m_swapchainTable.Acquire(sc);
}

// =============================================================================
// 交换链真重建（TASK-B-R9-09：OUT_OF_DATE 恢复路径补全，R10-03 遗留）
// =============================================================================
bool ZrVulkanDevice::RecreateSwapchain(ZrSwapchain handle)
{
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return false;
    }
    return static_cast<ZrVulkanSwapchain*>(payload)->Recreate();
}

bool ZrVulkanDevice::GetSwapchainResizeInfo(
    ZrSwapchain handle, zr::SwapchainResizeLedger* out) const
{
    if (out == nullptr) {
        return false;
    }
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return false;
    }
    *out = static_cast<const ZrVulkanSwapchain*>(payload)->m_ledger;
    return true;
}

void ZrVulkanDevice::DestroySwapchainDependentViews(const ZrVulkanSwapchain& chain)
{
    // 依赖面收口（TASK-B-R9-09）：销毁所有 desc.image 挂在本链后台缓冲上的视图。
    // 走 Destroy(ZrImageView) 公共路径（vkDestroyImageView + bindless 槽回收 +
    // 句柄表释放）。调用方持有的这些视图句柄此后 stale（TryGet 拒绝 = 安全失败），
    // 须按新链句柄重建——依赖面全文见 Recreate() 注。
    for (size_t i = m_swapchainViews.size(); i > 0u; --i) {
        const size_t idx = i - 1u;
        const ZrImageView viewHandle = m_swapchainViews[idx];
        void* payload = nullptr;
        if (!m_imageViewTable.TryGet(viewHandle, payload)) {
            // 调用方已自行销毁：登记表剔除（保证登记表有界）
            m_swapchainViews.erase(m_swapchainViews.begin()
                + static_cast<std::ptrdiff_t>(idx));
            continue;
        }
        auto* view = static_cast<ZrVulkanViewObj*>(payload);
        bool belongsToThisChain = false;
        for (const ZrImage image : chain.m_backbufferImages) {
            if (image == view->desc.image) {
                belongsToThisChain = true;
                break;
            }
        }
        if (belongsToThisChain) {
            Destroy(viewHandle);
            m_swapchainViews.erase(m_swapchainViews.begin()
                + static_cast<std::ptrdiff_t>(idx));
        }
        // 其它链的视图（多链场景）保留，留待其所属链重建/销毁时处理
    }
}

bool ZrVulkanSwapchain::Recreate()
{
    ZrVulkanDevice* dev = m_owner;
    const VkDevice vkDev = dev->VulkanDeviceHandle();
    const VkPhysicalDevice phys = dev->VulkanPhysicalDevice();
    const VkSurfaceKHR surface = dev->m_surface; // 建链时已创建（单窗口基座）

    // ① 能力查询 + 尺寸裁决（纯逻辑 ResolveRecreateExtent；零尺寸 = 最小化 → 拒绝，
    //    粘性保留——vkCreateSwapchainKHR 在零尺寸下本会失败，提前拒绝让旧链保持完好）
    VkSurfaceCapabilitiesKHR caps {};
    if (surface == VK_NULL_HANDLE
        || vkGetPhysicalDeviceSurfaceCapabilitiesKHR(phys, surface, &caps) != VK_SUCCESS) {
        m_ledger.RecordFailure();
        return false;
    }
    const zr::RecreateExtent extent = zr::ResolveRecreateExtent(caps.currentExtent.width,
        caps.currentExtent.height, caps.minImageExtent.width, caps.minImageExtent.height,
        caps.maxImageExtent.width, caps.maxImageExtent.height, m_desc.width, m_desc.height);
    if (!extent.accepted) {
        m_ledger.RecordFailure();
        return false;
    }

    // ② 排空：在途提交引用旧链图像（以及调用方视图），必须等 GPU 完成才能销毁
    //    （与析构契约同源：交换链销毁前 WaitIdle）
    vkDeviceWaitIdle(vkDev);
    // 挂起 acquire/present 握手随旧链整组作废（正常到达这里时本就已消费/清空；
    // 防御性清零，避免留旧链信号量给新链的提交）
    dev->m_pendingAcquire = VK_NULL_HANDLE;
    dev->m_pendingPresentSignal = VK_NULL_HANDLE;
    dev->m_pendingImageIndex = 0u;
    m_currentIndex = 0u;

    // ③ 新链创建（oldSwapchain 传参——新旧链资源平滑交接；规格推荐次序）。
    //    放在销毁旧资源**之前**：建链失败时旧链与旧视图全部完好，粘性保留可重试。
    //    格式沿用建链描述（同 surface 已验证过；呈现模式重新裁决——表面能力可变）
    const VkPresentModeKHR presentMode = PickPresentMode(phys, surface, m_desc.presentMode);
    uint32_t imageCount = m_desc.imageCount;
    if (caps.minImageCount > imageCount) {
        imageCount = caps.minImageCount;
    }
    if (caps.maxImageCount != 0u && imageCount > caps.maxImageCount) {
        imageCount = caps.maxImageCount;
    }
    VkSwapchainCreateInfoKHR swapInfo {};
    swapInfo.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    swapInfo.surface = surface;
    swapInfo.minImageCount = imageCount;
    swapInfo.imageFormat = ZrVulkan_Format(m_desc.format);
    swapInfo.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    swapInfo.imageExtent = { extent.width, extent.height };
    swapInfo.imageArrayLayers = 1u;
    swapInfo.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT
        | VK_IMAGE_USAGE_TRANSFER_DST_BIT; // 与建链同用途（清屏合法域 + readback 拷贝源）
    swapInfo.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swapInfo.preTransform = caps.currentTransform;
    swapInfo.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swapInfo.presentMode = presentMode;
    swapInfo.clipped = VK_TRUE;
    swapInfo.oldSwapchain = swapchain;
    VkSwapchainKHR newChain = VK_NULL_HANDLE;
    if (vkCreateSwapchainKHR(vkDev, &swapInfo, nullptr, &newChain) != VK_SUCCESS
        || newChain == VK_NULL_HANDLE) {
        m_ledger.RecordFailure();
        std::printf("[zrhi][vulkan] swapchain recreate: vkCreateSwapchainKHR FAILED — old chain "
                    "kept, sticky flag retained\n");
        return false;
    }
    swapchain = newChain;
    if (swapInfo.oldSwapchain != VK_NULL_HANDLE) {
        // ④ 旧链销毁——前置条件已满足：
        //    · GPU 排空（②）
        //    · 引用旧链图像的视图已销毁（⑤前置于本步：依赖面收口，见下）
        //    依赖面说明（实测代码取证，非推断）：
        //    · 本基座用动态渲染（Vulkan 1.3 核心），**无 RenderPass/Framebuffer 对象**
        //      ——不存在 framebuffer 引用旧链图像的依赖；
        //    · 真正的依赖面是"调用方经 CreateImageView 在后台缓冲上建的 RTV/SRV/UAV
        //      视图"（验证程序按索引缓存 rtvViews[index]）——视图本体是设备侧
        //      VkImageView，指向旧链图像，链毁即悬垂；
        //    · 深度图/DSV 与链无关（独立 VkImage），不受重建影响，无需重建。
        dev->DestroySwapchainDependentViews(*this);
        vkDestroySwapchainKHR(vkDev, swapInfo.oldSwapchain, nullptr);
    }

    // ⑥ 后台缓冲重枚举 + 新句柄（旧句柄自句柄表释放——调用方旧句柄 stale 化，
    //    Resolve 拒绝 = 安全失败；图像本体归交换链所有，无 vkDestroyImage）
    uint32_t actualCount = 0u;
    vkGetSwapchainImagesKHR(vkDev, swapchain, &actualCount, nullptr);
    std::vector<VkImage> images(actualCount);
    if (actualCount == 0u
        || vkGetSwapchainImagesKHR(vkDev, swapchain, &actualCount, images.data())
            != VK_SUCCESS) {
        // 枚举失败：新链已建但不可用——粘性保留，下一帧入口重试（此态可自愈：
        // 重试会以本链为 oldSwapchain 再走一遍全流程）
        m_ledger.RecordFailure();
        std::printf("[zrhi][vulkan] swapchain recreate: vkGetSwapchainImagesKHR FAILED\n");
        return false;
    }
    for (const ZrImage image : m_backbufferImages) {
        void* payload = nullptr;
        if (m_owner->m_imageTable.TryGet(image, payload)) {
            delete static_cast<ZrVulkanImageObj*>(payload);
            m_owner->m_imageTable.Release(image);
        }
    }
    m_backbufferImages.clear();

    // 旧呈现信号量销毁（旧链整组语义；GPU 已排空 + 旧链已销毁，等待方不复存在）。
    // imageAvailable 与链无关（单信号量），保留复用。漏掉这一步 = 旧信号量句柄被
    // resize 覆盖泄漏（vkDestroyDevice 泄漏报告实证过——本注释为防回归而留）。
    for (VkSemaphore s : renderFinished) {
        if (s != VK_NULL_HANDLE) {
            vkDestroySemaphore(vkDev, s, nullptr);
        }
    }
    renderFinished.clear();

    m_backbufferImages.resize(actualCount);
    renderFinished.resize(actualCount);
    VkSemaphoreCreateInfo semaphoreInfo {};
    semaphoreInfo.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    if (imageAvailable == VK_NULL_HANDLE) {
        // 理论不可达（imageAvailable 与链无关，仅在首建失败清理路径为空）；防御兜底
        if (vkCreateSemaphore(vkDev, &semaphoreInfo, nullptr, &imageAvailable) != VK_SUCCESS) {
            m_ledger.RecordFailure();
            return false;
        }
    }
    for (uint32_t i = 0; i < actualCount; ++i) {
        ZrVulkanImageObj* obj = new ZrVulkanImageObj();
        obj->image = images[i];
        obj->desc.format = m_desc.format;
        obj->desc.width = extent.width; // 新尺寸（视口跟随——负视口/CCW 语义不变）
        obj->desc.height = extent.height;
        obj->desc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc;
        obj->swapchainOwned = true;
        m_backbufferImages[i] = m_owner->m_imageTable.Acquire(obj);
        if (vkCreateSemaphore(vkDev, &semaphoreInfo, nullptr, &renderFinished[i])
            != VK_SUCCESS) {
            // 信号量中途失败：状态可自愈（下一帧入口重试，以新链为 oldSwapchain
            // 再走全流程——renderFinished 清空重建，图像句柄重新登记）
            m_ledger.RecordFailure();
            std::printf("[zrhi][vulkan] swapchain recreate: semaphore creation FAILED\n");
            return false;
        }
    }

    // ⑦ 台账推进 + 描述更新 + 清粘性（needsRecreate=false → 下一帧正常渲染）
    m_desc.width = extent.width;
    m_desc.height = extent.height;
    m_desc.imageCount = actualCount;
    m_ledger.RecordSuccess(extent.width, extent.height);
    dev->OnSwapchainRecreated();
    return true;
}

ZrVulkanSwapchain::~ZrVulkanSwapchain()
{
    // 调用方契约：交换链销毁前 WaitIdle（后台缓冲不再被 GPU 引用）
    const VkDevice dev = m_owner->VulkanDeviceHandle();
    for (VkSemaphore s : renderFinished) {
        if (s != VK_NULL_HANDLE) {
            vkDestroySemaphore(dev, s, nullptr);
        }
    }
    if (imageAvailable != VK_NULL_HANDLE) {
        vkDestroySemaphore(dev, imageAvailable, nullptr);
    }
    if (swapchain != VK_NULL_HANDLE) {
        vkDestroySwapchainKHR(dev, swapchain, nullptr);
    }
    for (ZrImage image : m_backbufferImages) {
        void* payload = nullptr;
        if (m_owner->m_imageTable.TryGet(image, payload)) {
            delete static_cast<ZrVulkanImageObj*>(payload);
            m_owner->m_imageTable.Release(image);
        }
    }
    m_backbufferImages.clear();
}

uint32_t ZrVulkanSwapchain::AcquireNextImage()
{
    // TASK-B-R9-09：needsRecreate 粘性 → 本帧入口（Acquire 前）先尝试真重建
    // （简报"Begin 前检到 → 重建"的落点）。失败（如最小化零尺寸）→ 粘性保留 +
    // 台账计失败次数，本帧继续走既有失败路径（不提交不呈现），下一帧入口重试
    // ——每帧最多一次尝试，无自旋、无阻塞（不挂死）。
    if (m_owner->m_frameGate.NeedsRecreate()) {
        if (Recreate()) {
            std::printf("[zrhi][vulkan] swapchain RECREATED %ux%u -> %ux%u (serial=%u) — "
                        "rendering resumes\n",
                m_ledger.fromWidth, m_ledger.fromHeight, m_ledger.toWidth, m_ledger.toHeight,
                m_ledger.serial);
        } else if (m_ledger.failedAttempts == 1u || (m_ledger.failedAttempts % 300u) == 0u) {
            std::printf("[zrhi][vulkan] swapchain recreate attempt %u FAILED — sticky flag "
                        "kept, frames keep skipping (retry per frame entry)\n",
                m_ledger.failedAttempts);
        }
    }
    // → vkAcquireNextImageKHR：imageAvailable 信号量（等待发生在 Submit——VK 侧
    // 标准做法，对位 D3D12 per-buffer fence 的等待点差异）
    uint32_t index = 0u;
    const VkResult result = vkAcquireNextImageKHR(
        m_owner->VulkanDeviceHandle(), swapchain, UINT64_MAX, imageAvailable, VK_NULL_HANDLE,
        &index);
    // TASK-R10-03 A-5：VkResult 一律经唯一转译点分类，再交给帧闸决定本帧命运。
    const zr::VkOutcome outcome = zr::ClassifyVkResult(static_cast<int32_t>(result));
    m_owner->OnFrameAcquire(outcome);
    if (outcome != zr::VkOutcome::Success && outcome != zr::VkOutcome::Suboptimal) {
        // OUT_OF_DATE / SURFACE_LOST / DEVICE_LOST / 其它失败：
        // vkAcquireNextImageKHR **不会** signal imageAvailable，因此绝不能登记挂起
        // acquire（旧实现在这里照常登记 → 下一次 Submit 等一个永不 signal 的二进制
        // 信号量 → 队列永久卡死）。帧闸已把本帧的 Submit/Present 拦下，这里只返回
        // 0 让调用方继续记录（该帧不会被提交，也不会被呈现）。
        return 0u;
    }
    m_currentIndex = index;
    // 挂起状态交设备（Submit 消费：等 imageAvailable + 信号 renderFinished[index]）
    m_owner->SetPendingAcquire(imageAvailable, renderFinished[index], index);
    return index;
}

ZrImage ZrVulkanSwapchain::GetCurrentImage()
{
    assert(!m_backbufferImages.empty() && m_currentIndex < m_backbufferImages.size());
    if (m_currentIndex >= m_backbufferImages.size()) {
        return ZrImage{};
    }
    return m_backbufferImages[m_currentIndex];
}

void ZrVulkanSwapchain::Present()
{
    // → vkQueuePresentKHR（wait renderFinished[当前图]；须在 Submit 之后——
    // 契约：Present 等待的信号量由 Submit 信号）
    // TASK-R10-03 A-5/A-6：本帧没有成功的提交时**绝不呈现**——否则等待的
    // renderFinished 永远不会被 signal，队列永久挂起。
    if (!m_owner->ShouldPresentThisFrame()) {
        m_owner->OnFramePresentSkipped();
        return;
    }
    VkPresentInfoKHR presentInfo {};
    presentInfo.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    presentInfo.waitSemaphoreCount = 1u;
    presentInfo.pWaitSemaphores = &renderFinished[m_currentIndex];
    presentInfo.swapchainCount = 1u;
    presentInfo.pSwapchains = &swapchain;
    presentInfo.pImageIndices = &m_currentIndex;
    const VkResult result = vkQueuePresentKHR(m_owner->VulkanQueueHandle(), &presentInfo);
    // 统一处理：成功/次优/过期/失败都进帧闸（过期与次优 → 标记需重建）
    m_owner->OnFramePresent(zr::ClassifyVkResult(static_cast<int32_t>(result)));
}

// =============================================================================
// 命令表 / 句柄→接口 / 时间戳堆
// =============================================================================
ZrCommandList ZrVulkanDevice::CreateCommandList(ZrQueueType queueType)
{
    assert(queueType == ZrQueueType::Graphics || queueType == ZrQueueType::Compute
        || queueType == ZrQueueType::Transfer);
    (void)queueType; // 三域基座均为 graphics 队列表
    ZrVulkanCommandList* obj = new ZrVulkanCommandList(this);
    if (!obj->Init()) {
        delete obj;
        return ZrCommandList{};
    }
    return m_commandListTable.Acquire(obj);
}

IZrCommandList* ZrVulkanDevice::GetCommandList(ZrCommandList handle)
{
    return ResolveCommandList(handle);
}

IZrBuffer* ZrVulkanDevice::GetBuffer(ZrBuffer handle)
{
    return ResolveBuffer(handle); // ZrVulkanBufferObj 即 IZrBuffer 实现
}

uint32_t ZrVulkanDevice::GetBindlessIndex(ZrImageView view)
{
    ZrVulkanViewObj* obj = ResolveView(view);
    if (obj == nullptr) {
        return ZR_BINDLESS_INVALID;
    }
    // 仅 SRV/UAV 注册进全局大 set（RTV/DSV 为渲染附件，非 shader 可见）
    assert(obj->desc.viewType == ZrImageViewType::Srv
        || obj->desc.viewType == ZrImageViewType::Uav);
    if (obj->desc.viewType != ZrImageViewType::Srv
        && obj->desc.viewType != ZrImageViewType::Uav) {
        return ZR_BINDLESS_INVALID;
    }
    return obj->bindlessSlot;
}

IZrSwapchain* ZrVulkanDevice::GetSwapchain(ZrSwapchain handle)
{
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanSwapchain*>(payload);
}

ZrQueryPool ZrVulkanDevice::CreateTimestampQueryHeap(uint32_t queryCount)
{
    // → vkCreateQueryPool（TIMESTAMP 类型）
    if (queryCount == 0u) {
        return ZrQueryPool{};
    }
    ZrVulkanQueryPoolObj* obj = new ZrVulkanQueryPoolObj();
    obj->count = queryCount;
    VkQueryPoolCreateInfo poolInfo {};
    poolInfo.sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO;
    poolInfo.queryType = VK_QUERY_TYPE_TIMESTAMP;
    poolInfo.queryCount = queryCount;
    if (vkCreateQueryPool(m_device, &poolInfo, nullptr, &obj->pool) != VK_SUCCESS) {
        delete obj;
        return ZrQueryPool{};
    }
    // 建堆即复位（设备空闲契约：创建期无在途 GPU 工作）
    vkResetQueryPool(m_device, obj->pool, 0u, queryCount);
    return m_queryPoolTable.Acquire(obj);
}

uint32_t ZrVulkanDevice::ResolveTimestamps(
    ZrQueryPool heap, double* outMilliseconds, uint32_t count)
{
    // → vkGetQueryPoolResults + timestampPeriod 换算（"GPU 完成等待"契约以
    // vkDeviceWaitIdle 兑现——验证程序帧循环 WaitIdle 同构）
    ZrVulkanQueryPoolObj* obj = ResolveQueryPool(heap);
    assert(obj != nullptr && outMilliseconds != nullptr);
    if (obj == nullptr || outMilliseconds == nullptr) {
        return 0u;
    }
    const uint32_t n = count < obj->count ? count : obj->count;
    if (n == 0u) {
        return 0u;
    }
    vkDeviceWaitIdle(m_device);
    std::vector<uint64_t> ticks(n);
    const VkResult result = vkGetQueryPoolResults(m_device, obj->pool, 0u, n,
        sizeof(uint64_t) * n, ticks.data(), sizeof(uint64_t),
        VK_QUERY_RESULT_64_BIT);
    if (result != VK_SUCCESS) {
        return 0u;
    }
    // GPU ticks → 毫秒（timestampPeriod = 每 tick 纳秒数；绝对毫秒，间隔=两值之差）
    for (uint32_t i = 0; i < n; ++i) {
        outMilliseconds[i] = static_cast<double>(ticks[i])
            * m_timestampPeriodNs / 1000.0;
    }
    // 为下一轮使用复位（设备已 idle，安全）
    vkResetQueryPool(m_device, obj->pool, 0u, obj->count);
    return n;
}

// =============================================================================
// 销毁（→ vkDestroy* 家族）
// =============================================================================
void ZrVulkanDevice::Destroy(ZrBuffer handle)
{
    void* payload = nullptr;
    if (!m_bufferTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanBufferObj* obj = static_cast<ZrVulkanBufferObj*>(payload);
    if (obj->buffer != VK_NULL_HANDLE) {
        vkDestroyBuffer(m_device, obj->buffer, nullptr);
    }
    if (obj->memory != VK_NULL_HANDLE) {
        vkFreeMemory(m_device, obj->memory, nullptr);
    }
    delete obj;
    m_bufferTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrImage handle)
{
    void* payload = nullptr;
    if (!m_imageTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanImageObj* obj = static_cast<ZrVulkanImageObj*>(payload);
    assert(!obj->swapchainOwned); // 后台缓冲随 Destroy(ZrSwapchain) 整组销毁
    if (obj->memory != VK_NULL_HANDLE && !obj->inAliasHeap) {
        vkFreeMemory(m_device, obj->memory, nullptr);
    }
    if (obj->image != VK_NULL_HANDLE) {
        vkDestroyImage(m_device, obj->image, nullptr); // 别名堆落位图也销毁本体
    }
    delete obj;
    m_imageTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrImageView handle)
{
    void* payload = nullptr;
    if (!m_imageViewTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanViewObj* obj = static_cast<ZrVulkanViewObj*>(payload);
    if (obj->view != VK_NULL_HANDLE) {
        vkDestroyImageView(m_device, obj->view, nullptr);
    }
    if (obj->bindlessSlot != ZR_SLOT_INVALID) {
        m_bindlessSlots.Free(obj->bindlessSlot); // bindless 槽回收（free list 复用）
    }
    delete obj;
    m_imageViewTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrSampler handle)
{
    void* payload = nullptr;
    if (!m_samplerTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanSamplerObj* obj = static_cast<ZrVulkanSamplerObj*>(payload);
    vkDestroySampler(m_device, obj->sampler, nullptr);
    delete obj;
    m_samplerTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrSwapchain handle)
{
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanSwapchain* sc = static_cast<ZrVulkanSwapchain*>(payload);
    // TASK-B-R9-09：销毁路径的依赖面收口（与重建同策）——引用本链后台缓冲的
    // 视图先于链销毁（活视图挂死图像属 UB；stale 条目顺带剔除，登记表有界）
    DestroySwapchainDependentViews(*sc);
    delete sc; // 析构释放整组后台缓冲
    m_swapchainTable.Release(handle);
    // TASK-B-R9-11：链全销毁 → 回到离屏（detached）——提交不再受 acquire 闸约束
    if (m_swapchainTable.AliveCount() == 0u) {
        m_frameGate.SetSwapchainAttached(false);
    }
}

void ZrVulkanDevice::Destroy(ZrCommandList handle)
{
    void* payload = nullptr;
    if (!m_commandListTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrVulkanCommandList*>(payload);
    m_commandListTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrQueryPool handle)
{
    void* payload = nullptr;
    if (!m_queryPoolTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanQueryPoolObj* obj = static_cast<ZrVulkanQueryPoolObj*>(payload);
    vkDestroyQueryPool(m_device, obj->pool, nullptr);
    delete obj;
    m_queryPoolTable.Release(handle);
}

void ZrVulkanDevice::Destroy(ZrAliasHeap handle)
{
    void* payload = nullptr;
    if (!m_aliasHeapTable.TryGet(handle, payload)) {
        return;
    }
    ZrVulkanAliasHeapObj* obj = static_cast<ZrVulkanAliasHeapObj*>(payload);
    vkFreeMemory(m_device, obj->memory, nullptr);
    delete obj;
    m_aliasHeapTable.Release(handle);
}

// =============================================================================
// 队列与同步
// =============================================================================
IZrQueue* ZrVulkanDevice::GetQueue(ZrQueueType type)
{
    return m_queues[static_cast<uint32_t>(type)].get();
}

void ZrVulkanDevice::WaitIdle()
{
    // → vkDeviceWaitIdle
    vkDeviceWaitIdle(m_device);
}

uint32_t ZrVulkanDevice::GetDebugErrorCount()
{
    // 验证层 ERROR 级消息累计计数（读取不清空——与 D3D12 InfoQueue 排空语义的
    // 已知差异：VK 侧消息即到即计，计数只增，验证程序按"终值 0"断言）
    return m_validationErrorCount;
}

// ---- 帧闸驱动（TASK-R10-03 A-5/A-6）-----------------------------------------
// 三处入口（交换链 Acquire / 队列 Submit / 交换链 Present）都把 VkResult 分类结果
// 交给这里；日志只在**状态变化**时打，避免窗口最小化时每帧刷屏。
void ZrVulkanDevice::OnFrameAcquire(zr::VkOutcome outcome)
{
    m_frameGate.BeginFrame(outcome);
    if (outcome == m_lastAcquireOutcome) {
        return;
    }
    if (outcome == zr::VkOutcome::Success) {
        std::printf("[zrhi][vulkan] swapchain acquire recovered (needsRecreate=%d)\n",
            m_frameGate.NeedsRecreate() ? 1 : 0);
    } else if (outcome == zr::VkOutcome::Suboptimal) {
        std::printf("[zrhi][vulkan] swapchain SUBOPTIMAL — frame still submitted, swapchain "
                    "recreate flagged\n");
    } else {
        std::printf("[zrhi][vulkan] swapchain acquire FAILED (outcome=%d) — swapchain recreate "
                    "flagged, frame skipped (no submit / no present)\n",
            static_cast<int>(outcome));
    }
    m_lastAcquireOutcome = outcome;
}

void ZrVulkanDevice::OnFramePresent(zr::VkOutcome outcome)
{
    m_frameGate.OnPresent(outcome);
    if (outcome == zr::VkOutcome::Success) {
        return;
    }
    std::printf("[zrhi][vulkan] present result=%d (needsRecreate=%d, recreateRequests=%llu)\n",
        static_cast<int>(outcome), m_frameGate.NeedsRecreate() ? 1 : 0,
        static_cast<unsigned long long>(m_frameGate.RecreateRequests()));
}

void ZrVulkanDevice::OnFramePresentSkipped()
{
    ++m_presentSkipLogCount;
    if (m_presentSkipLogCount > 3u && (m_presentSkipLogCount % 300u) != 0u) {
        return;
    }
    std::printf("[zrhi][vulkan] present skipped (no successful submit this frame; "
                "skippedPresent=%llu) — queue would wait on an unsignalled semaphore\n",
        static_cast<unsigned long long>(m_frameGate.SkippedPresentFrames()));
}

// ---- 提交（IZrQueue::Submit 的实际落点；→ vkQueueSubmit + CL fence 信号）-----------
void ZrVulkanDevice::SubmitCommandList(ZrCommandList handle)
{
    ZrVulkanCommandList* cmd = ResolveCommandList(handle);
    assert(cmd != nullptr);
    if (cmd == nullptr) {
        return;
    }
    VkSubmitInfo submitInfo {};
    submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submitInfo.commandBufferCount = 1u;
    VkCommandBuffer buffer = cmd->NativeCmd();
    submitInfo.pCommandBuffers = &buffer;
    // 等待阶段掩码（函数级生存期：pWaitDstStageMask 是持久指针，块内局部会
    // 悬垂——曾有栈垃圾掩码触发验证层"含曲面细分/变换反馈阶段"误报的实证）
    const VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    // 挂起 acquire：等 imageAvailable（颜色输出阶段起点——呈现图内容消费点）+
    // 信号 renderFinished[当前图]（Present 等待）；离屏提交两者皆无
    if (m_pendingAcquire != VK_NULL_HANDLE) {
        submitInfo.waitSemaphoreCount = 1u;
        submitInfo.pWaitSemaphores = &m_pendingAcquire;
        submitInfo.pWaitDstStageMask = &waitStage;
        submitInfo.signalSemaphoreCount = 1u;
        submitInfo.pSignalSemaphores = &m_pendingPresentSignal;
    }
    // TASK-R10-03 A-5：acquire 失败/过期时本帧不提交——既不调 vkQueueSubmit，
    // 也不留下任何"挂起信号量"给下一次提交（那是永久卡死的入口）。
    if (!m_frameGate.CanSubmit()) {
        if (m_pendingAcquire != VK_NULL_HANDLE || m_pendingPresentSignal != VK_NULL_HANDLE) {
            std::printf("[zrhi][vulkan] submit skipped (acquire failed/out-of-date) — pending "
                        "semaphores dropped\n");
        }
        m_pendingAcquire = VK_NULL_HANDLE;
        m_pendingPresentSignal = VK_NULL_HANDLE;
        return;
    }
    // CL 私有 fence（Begin 时等待复位——一次性提交语义，≈ 帧槽 fence 的 D3D12 模式）
    const VkResult submitResult = vkQueueSubmit(m_queue, 1u, &submitInfo, cmd->m_submitFence);
    m_frameGate.OnSubmit(zr::ClassifyVkResult(static_cast<int32_t>(submitResult)));
    m_pendingAcquire = VK_NULL_HANDLE; // 握手消费
    m_pendingPresentSignal = VK_NULL_HANDLE;
    if (submitResult != VK_SUCCESS) {
        // TASK-R10-03 A-6：提交失败时 fence 永远不会 signal → **不能**置
        // m_pendingSubmit（否则下一帧 Begin 的 vkWaitForFences(UINT64_MAX) 永久阻塞），
        // 本帧也不再呈现（帧闸已拦下）。
        std::printf("[zrhi][vulkan] vkQueueSubmit FAILED (result=%d) — frame dropped, fence not "
                    "awaited\n",
            static_cast<int>(submitResult));
        return;
    }
    cmd->m_pendingSubmit = true;
}

// =============================================================================
// 原生扩展点 / 深度清屏
// =============================================================================
VkCommandBuffer ZrVulkanDevice::VulkanNativeCmdOf(ZrCommandList cmd) const
{
    ZrVulkanCommandList* obj = ResolveCommandList(cmd);
    return obj != nullptr ? obj->NativeCmd() : VK_NULL_HANDLE;
}

VkImage ZrVulkanDevice::VulkanImageOf(ZrImage image) const
{
    ZrVulkanImageObj* obj = ResolveImage(image);
    return obj != nullptr ? obj->image : VK_NULL_HANDLE;
}

VkBuffer ZrVulkanDevice::VulkanBufferOf(ZrBuffer buffer) const
{
    ZrVulkanBufferObj* obj = ResolveBuffer(buffer);
    return obj != nullptr ? obj->buffer : VK_NULL_HANDLE;
}

bool ZrVulkanDevice::ViewIsDepthSampling(ZrVulkanViewObj* view) const
{
    // 深度图采样视图判定（源图像格式为深度格式即真——与 CreateImageView 的
    // "aspect 归源图像"规则配套：D32→R32Float 孪生解释视图仍按深度图采样）
    if (view == nullptr) {
        return false;
    }
    const ZrVulkanImageObj* image = ResolveImage(view->desc.image);
    if (image == nullptr) {
        return false;
    }
    const VkFormat srcFormat = ZrVulkan_Format(image->desc.format);
    return srcFormat == VK_FORMAT_D32_SFLOAT || srcFormat == VK_FORMAT_D16_UNORM
        || srcFormat == VK_FORMAT_D24_UNORM_S8_UINT
        || srcFormat == VK_FORMAT_X8_D24_UNORM_PACK32
        || srcFormat == VK_FORMAT_D32_SFLOAT_S8_UINT;
}

void ZrVulkanDevice::RecordClearDepth(ZrCommandList cmdHandle, ZrImageView dsv, float depthValue)
{
    // 深度清屏扩展点（对位 D3D12 验证程序 ClearDepthStencilView 原生调用；RHI 基座
    // 尚无 ClearDepth 命令）。vkCmdClearDepthStencilImage 布局合法域约束 → 内部做
    // DS 附件布局 → TRANSFER_DST → 清屏 → 回落 的往返屏障（外部契约不变）
    ZrVulkanCommandList* cmd = ResolveCommandList(cmdHandle);
    ZrVulkanViewObj* view = ResolveView(dsv);
    ZrVulkanImageObj* image = view != nullptr ? ResolveImage(view->desc.image) : nullptr;
    assert(cmd != nullptr && image != nullptr && image->image != VK_NULL_HANDLE);
    if (cmd == nullptr || image == nullptr || image->image == VK_NULL_HANDLE) {
        return;
    }
    cmd->EndRenderingIfActive();

    const VkImageAspectFlags aspect = AspectForFormat(ZrVulkan_Format(image->desc.format));
    // 视图子资源窗口（TASK-B-R7-02）：清屏范围跟随 DSV 视图的 mip/层窗口——
    // CSM 级联阴影图（4 层数组图）每级联一个层 DSV，逐层清屏不得波及已绘层级
    // （对位 D3D12 ClearDepthStencilView 的视图范围语义；整资源视图窗口=全资源，
    // 与 B-R7-01 行为一致）。窗口计算与 CreateImageView 同式（REMAINING → clamp）
    const uint32_t clearMipBase = view->desc.subresources.baseMipLevel < image->desc.mipLevels
        ? view->desc.subresources.baseMipLevel
        : image->desc.mipLevels;
    const uint32_t clearMipCount
        = (view->desc.subresources.levelCount == ZR_REMAINING_MIP_LEVELS)
        ? (image->desc.mipLevels - clearMipBase)
        : view->desc.subresources.levelCount;
    const uint32_t clearLayerBase
        = view->desc.subresources.baseArrayLayer < image->desc.arrayLayers
        ? view->desc.subresources.baseArrayLayer
        : image->desc.arrayLayers;
    const uint32_t clearLayerCount
        = (view->desc.subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS)
        ? (image->desc.arrayLayers - clearLayerBase)
        : view->desc.subresources.layerCount;
    VkImageMemoryBarrier toTransfer {};
    toTransfer.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    toTransfer.oldLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    toTransfer.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    toTransfer.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toTransfer.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toTransfer.image = image->image;
    toTransfer.subresourceRange.aspectMask = aspect;
    toTransfer.subresourceRange.baseMipLevel = clearMipBase;
    toTransfer.subresourceRange.levelCount = clearMipCount;
    toTransfer.subresourceRange.baseArrayLayer = clearLayerBase;
    toTransfer.subresourceRange.layerCount = clearLayerCount;
    toTransfer.srcAccessMask = VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT;
    toTransfer.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    vkCmdPipelineBarrier(cmd->NativeCmd(), VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT, 0u, 0u, nullptr, 0u, nullptr, 1u, &toTransfer);

    VkClearDepthStencilValue clearValue {};
    clearValue.depth = depthValue;
    VkImageSubresourceRange range {};
    range.aspectMask = aspect;
    range.baseMipLevel = clearMipBase;
    range.levelCount = clearMipCount;
    range.baseArrayLayer = clearLayerBase;
    range.layerCount = clearLayerCount;
    vkCmdClearDepthStencilImage(cmd->NativeCmd(), image->image,
        VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clearValue, 1u, &range);

    VkImageMemoryBarrier back = toTransfer;
    back.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    back.newLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    back.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    back.dstAccessMask = VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT
        | VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT;
    vkCmdPipelineBarrier(cmd->NativeCmd(), VK_PIPELINE_STAGE_TRANSFER_BIT,
        VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT | VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT,
        0u, 0u, nullptr, 0u, nullptr, 1u, &back);
}

// =============================================================================
// 队列包装
// =============================================================================
void ZrVulkanQueue::Submit(ZrCommandList commandList)
{
    // → vkQueueSubmit（设备级落点：挂起 acquire/present 握手 + CL fence）
    m_owner->SubmitCommandList(commandList);
}

// =============================================================================
// 句柄载荷解析（stale 句柄返回 nullptr）
// =============================================================================
ZrVulkanImageObj* ZrVulkanDevice::ResolveImage(ZrImage handle) const
{
    void* payload = nullptr;
    if (!m_imageTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanImageObj*>(payload);
}

ZrVulkanBufferObj* ZrVulkanDevice::ResolveBuffer(ZrBuffer handle) const
{
    void* payload = nullptr;
    if (!m_bufferTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanBufferObj*>(payload);
}

ZrVulkanViewObj* ZrVulkanDevice::ResolveView(ZrImageView handle) const
{
    void* payload = nullptr;
    if (!m_imageViewTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanViewObj*>(payload);
}

ZrVulkanSamplerObj* ZrVulkanDevice::ResolveSampler(ZrSampler handle) const
{
    void* payload = nullptr;
    if (!m_samplerTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanSamplerObj*>(payload);
}

ZrVulkanQueryPoolObj* ZrVulkanDevice::ResolveQueryPool(ZrQueryPool handle) const
{
    void* payload = nullptr;
    if (!m_queryPoolTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanQueryPoolObj*>(payload);
}

ZrVulkanCommandList* ZrVulkanDevice::ResolveCommandList(ZrCommandList handle) const
{
    void* payload = nullptr;
    if (!m_commandListTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanCommandList*>(payload);
}

ZrVulkanShaderObj* ZrVulkanDevice::ResolveShader(ZrShader handle) const
{
    void* payload = nullptr;
    if (!m_shaderTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanShaderObj*>(payload);
}

ZrVulkanPsoObj* ZrVulkanDevice::ResolvePipelineState(ZrPipelineState handle) const
{
    void* payload = nullptr;
    if (!m_psoTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrVulkanPsoObj*>(payload);
}

// ZrVulkanBufferObj（IZrBuffer）——UPLOAD/READBACK 持久映射（HOST_COHERENT，创建期
// 一次 vkMapMemory；DEFAULT（DEVICE_LOCAL）返回 nullptr 契约）
void* ZrVulkanBufferObj::Map()
{
    return persistentMap; // 不可映射堆 = nullptr（契约）
}

void ZrVulkanBufferObj::Unmap()
{
    // 持久映射语义：Unmap 不解绑（≈ D3D12 UPLOAD 堆常驻映射；引用失效由调用方遵守）
}
