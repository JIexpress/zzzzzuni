// zrhi_vulkan_commandlist.cpp —— Vulkan 命令表实现（TASK-B-R7-01）
//
// 语义对应见 zrhi_vulkan_commandlist.h 头注。要点：
//   - ZrImageLayout → VkImageLayout 一律经 ZrVulkan_ImageLayout 显式 switch 查表
//   - ClearColorImage 的 ColorAttachmentOptimal 入参经内部往返屏障落入
//     vkCmdClearColorImage 的布局合法域（外部契约不变）
//   - 屏障/清屏/拷贝类命令前先 EndRendering（VK 屏障不得处于 rendering 实例内）
#include "zrhi/vulkan/zrhi_vulkan_commandlist.h"

#include "zrhi/vulkan/zrhi_vulkan_device.h"
#include "zrhi/vulkan/zrhi_vulkan_translate.h"

#include <cassert>
#include <cstdio>
#include <cstring>

namespace {
// 一次 ResourceBarrier 调用可展开的屏障数上限（与 D3D12 后端同源约定）
constexpr uint32_t ZR_VULKAN_MAX_BARRIERS = 128;
// v1 固定顶点布局跨度（Pos3f+Normal3f+Uv2f = 32B，与 D3D12 后端同源）
constexpr uint32_t ZR_VULKAN_GRAPHICS_VERTEX_STRIDE = 32u;

/// 图像格式 → aspect 掩码（深度/模板整体同步约定：D32/D16 → DEPTH，D24S8 →
/// DEPTH|STENCIL；颜色 → COLOR）
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
} // namespace

ZrVulkanCommandList::~ZrVulkanCommandList()
{
    if (m_descriptorPool != VK_NULL_HANDLE) {
        vkDestroyDescriptorPool(m_owner->VulkanDeviceHandle(), m_descriptorPool, nullptr);
    }
    if (m_submitFence != VK_NULL_HANDLE) {
        vkDestroyFence(m_owner->VulkanDeviceHandle(), m_submitFence, nullptr);
    }
    if (m_pool != VK_NULL_HANDLE) {
        vkDestroyCommandPool(m_owner->VulkanDeviceHandle(), m_pool, nullptr);
    }
}

bool ZrVulkanCommandList::Init()
{
    assert(m_owner != nullptr);
    const VkDevice dev = m_owner->VulkanDeviceHandle();

    // → vkAllocateCommandBuffers 的池：RESET_COMMAND_BUFFER_BIT 允许单表 Reset
    VkCommandPoolCreateInfo poolInfo {};
    poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    poolInfo.queueFamilyIndex = m_owner->GraphicsQueueFamily();
    if (vkCreateCommandPool(dev, &poolInfo, nullptr, &m_pool) != VK_SUCCESS) {
        return false;
    }
    VkCommandBufferAllocateInfo allocInfo {};
    allocInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    allocInfo.commandPool = m_pool;
    allocInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    allocInfo.commandBufferCount = 1u;
    if (vkAllocateCommandBuffers(dev, &allocInfo, &m_cmd) != VK_SUCCESS) {
        return false;
    }

    VkFenceCreateInfo fenceInfo {};
    fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    if (vkCreateFence(dev, &fenceInfo, nullptr, &m_submitFence) != VK_SUCCESS) {
        return false;
    }

    // CL 私有描述符池：maxSets=512 / uniform 2560 / 采样图 1024 / 采样器 1024（Begin 时
    // Reset——上次提交 fence 已等待完成，复用安全；快照模型每根绑定变化分配一个集合）。
    //
    // 容量推导（TASK-H-05 F4：必须自洽，否则"开到第 N 个 set 就失败"是隐性上界）：
    //   maxSets      = 512（单帧快照上限，每根绑定变化一个集合）
    //   每 set 上界   = rootCbvCount(≤5) × UNIFORM_BUFFER
    //                + srvTableCount(≤2) × (SAMPLED_IMAGE + SAMPLER)
    //   → UNIFORM_BUFFER : 512 × 5 = 2560
    //   → SAMPLED_IMAGE  : 512 × 2 = 1024
    //   → SAMPLER        : 512 × 2 = 1024
    // 早前 UNIFORM_BUFFER 只声明 2048（= 512 × 4），rootCbvCount=5 的 PSO（R5-03 五槽
    // pbr_r5）实际只能开到 409 个集合；虽不违反规范，但把"容量不足"伪装成"分配失败"。
    //
    // 池必须为 set0 布局实际用到的每一种类型显式预留：layout 用的是 SAMPLED_IMAGE +
    // SAMPLER（glslc -x hlsl 把 t_i/s_i 出成两个独立 UniformConstant，见
    // CreatePipelineState）。早先只声明 COMBINED_IMAGE_SAMPLER 属规范违规——多数驱动
    // 宽松放过（本机 AMD 实测未报错），但严格实现/validation 层会返回
    // VK_ERROR_OUT_OF_POOL_MEMORY，属于换机即炸的可移植性缺陷。
    VkDescriptorPoolSize sizes[3] = {};
    sizes[0].type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    sizes[0].descriptorCount = 2560u;
    sizes[1].type = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
    sizes[1].descriptorCount = 1024u;
    sizes[2].type = VK_DESCRIPTOR_TYPE_SAMPLER;
    sizes[2].descriptorCount = 1024u;
    VkDescriptorPoolCreateInfo poolCreateInfo {};
    poolCreateInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    // UPDATE_AFTER_BIND_POOL：set0 布局带 UPDATE_AFTER_BIND 绑定（根 CBV/SRV 每绘
    // 直绑语义，见 zrhi_vulkan_device.cpp CreatePipelineState）——池侧标志必须配套
    poolCreateInfo.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT
        | VK_DESCRIPTOR_POOL_CREATE_UPDATE_AFTER_BIND_BIT;
    poolCreateInfo.maxSets = 512u;
    poolCreateInfo.poolSizeCount = 3u;
    poolCreateInfo.pPoolSizes = sizes;
    if (vkCreateDescriptorPool(dev, &poolCreateInfo, nullptr, &m_descriptorPool)
        != VK_SUCCESS) {
        return false;
    }
    return true;
}

void ZrVulkanCommandList::Begin()
{
    assert(!m_recording);
    const VkDevice dev = m_owner->VulkanDeviceHandle();

    // 上次提交待等待：vkWaitForFences 后复位命令缓冲与描述符池（≈ 帧槽 fence
    // 等待的 D3D12 模式；验证程序帧循环 WaitIdle 语义的 VK 等价安全条件）
    if (m_pendingSubmit) {
        vkWaitForFences(dev, 1u, &m_submitFence, VK_TRUE, UINT64_MAX);
        vkResetFences(dev, 1u, &m_submitFence);
        vkResetCommandPool(dev, m_pool, 0u);
        vkResetDescriptorPool(dev, m_descriptorPool, 0u);
        m_pendingSubmit = false;
    }
    m_activeSet = VK_NULL_HANDLE;
    m_activePso = ZrPipelineState {};
    m_descriptorDirty = false;
    for (ZrBuffer& cb : m_cbSlots) {
        cb = ZrBuffer {};
    }
    for (ZrImageView& sv : m_srvSlots) {
        sv = ZrImageView {};
    }
    m_renderingActive = false;

    VkCommandBufferBeginInfo beginInfo {};
    beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vkBeginCommandBuffer(m_cmd, &beginInfo);
    m_recording = true;
}

void ZrVulkanCommandList::End()
{
    assert(m_recording);
    EndRenderingIfActive();
    vkEndCommandBuffer(m_cmd);
    m_recording = false;
}

void ZrVulkanCommandList::EmitBarriers(const ZrImageBarrier* imageBarriers,
    uint32_t imageBarrierCount, const ZrBufferBarrier* bufferBarriers,
    uint32_t bufferBarrierCount)
{
    assert((imageBarriers != nullptr) || (imageBarrierCount == 0u));
    assert((bufferBarriers != nullptr) || (bufferBarrierCount == 0u));
    if ((imageBarriers == nullptr || imageBarrierCount == 0u)
        && (bufferBarriers == nullptr || bufferBarrierCount == 0u)) {
        return;
    }

    // 屏障不得处于 rendering 实例内：先 vkCmdEndRendering（下次 SetRenderTarget
    // 重开；loadOp=LOAD 不丢内容——与 D3D12 屏障任意位置合法的差异点）
    EndRenderingIfActive();

    // 全局 src/dst 阶段与访问取所有屏障的并集（单次 vkCmdPipelineBarrier 提交；
    // VK 的执行域在屏障级而非 per-barrier——语义覆盖：逐屏障 stage 并集即逐条
    // 提交的串行等价，Vulkan 规范保证 execution dependency chain）
    VkImageMemoryBarrier imageBarriersVk[ZR_VULKAN_MAX_BARRIERS] = {};
    uint32_t imageCount = 0u;
    // B-5（TASK-R10-05）：超出单批上限被丢弃的条数。历史缺陷：静默截断——缺失
    // 同步（花屏/脏读）零信号。现在：丢弃必计数（GetDeviceStats）+ warning 一条。
    uint32_t imageDropped = 0u;
    VkPipelineStageFlags srcStages = VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT;
    VkPipelineStageFlags dstStages = VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT;

    for (uint32_t i = 0; i < imageBarrierCount; ++i) {
        if (imageCount >= ZR_VULKAN_MAX_BARRIERS) {
            ++imageDropped; // 单批上限：丢弃且必留痕（历史缺陷是静默 continue）
            continue;
        }
        const ZrImageBarrier& ib = imageBarriers[i];
        ZrVulkanImageObj* obj = m_owner->ResolveImage(ib.image);
        assert(obj != nullptr && obj->image != VK_NULL_HANDLE);
        if (obj == nullptr || obj->image == VK_NULL_HANDLE) {
            continue;
        }
        // 显式 switch 查表转译（translate 为唯一转译点）
        const VkImageLayout oldLayout = ZrVulkan_ImageLayout(ib.prevLayout);
        const VkImageLayout newLayout = ZrVulkan_ImageLayout(ib.newLayout);
        const VkImageAspectFlags aspect = AspectForFormat(ZrVulkan_Format(obj->desc.format));

        VkImageMemoryBarrier& b = imageBarriersVk[imageCount];
        b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
        b.oldLayout = oldLayout; // 1:1（oldLayout）
        b.newLayout = newLayout; // 1:1（newLayout）
        b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; // 所有权转移未启用
        b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        b.image = obj->image;
        b.subresourceRange.aspectMask = aspect;
        b.subresourceRange.baseMipLevel = ib.subresources.baseMipLevel;
        b.subresourceRange.levelCount = ib.subresources.levelCount; // REMAINING 值同源
        b.subresourceRange.baseArrayLayer = ib.subresources.baseArrayLayer;
        b.subresourceRange.layerCount = ib.subresources.layerCount;
        b.srcAccessMask = ZrVulkan_Access(ib.srcAccess);
        b.dstAccessMask = ZrVulkan_Access(ib.dstAccess);
        srcStages |= ZrVulkan_PipelineStage(ib.srcStage);
        dstStages |= ZrVulkan_PipelineStage(ib.dstStage);
        imageCount += 1u;
    }

    // 缓冲屏障（VK 原生；D3D12 后端无对应是其自身缺失——见该后端注释）
    VkBufferMemoryBarrier bufferBarriersVk[ZR_VULKAN_MAX_BARRIERS] = {};
    uint32_t bufferCount = 0u;
    uint32_t bufferDropped = 0u;
    for (uint32_t i = 0; i < bufferBarrierCount; ++i) {
        if (bufferCount >= ZR_VULKAN_MAX_BARRIERS) {
            ++bufferDropped;
            continue;
        }
        const ZrBufferBarrier& bb = bufferBarriers[i];
        ZrVulkanBufferObj* obj = m_owner->ResolveBuffer(bb.buffer);
        assert(obj != nullptr && obj->buffer != VK_NULL_HANDLE);
        if (obj == nullptr || obj->buffer == VK_NULL_HANDLE) {
            continue;
        }
        VkBufferMemoryBarrier& b = bufferBarriersVk[bufferCount];
        b.sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER;
        b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        b.buffer = obj->buffer;
        b.offset = bb.offset;
        b.size = bb.size; // ZR_WHOLE_SIZE == VK_WHOLE_SIZE（值同源约定）
        b.srcAccessMask = ZrVulkan_Access(bb.srcAccess);
        b.dstAccessMask = ZrVulkan_Access(bb.dstAccess);
        srcStages |= ZrVulkan_PipelineStage(bb.srcStage);
        dstStages |= ZrVulkan_PipelineStage(bb.dstStage);
        bufferCount += 1u;
    }

    // B-5（TASK-R10-05）：截断必须可观测——warning 一条 + 计入 GetDeviceStats
    // （barrierTruncationCount）。在"全被丢弃 → 早退"之前做，全丢也是丢弃。
    const uint32_t droppedTotal = imageDropped + bufferDropped;
    if (droppedTotal > 0u) {
        m_owner->NoteBarrierTruncation(droppedTotal);
        std::printf("[zrhi][vulkan] WARNING: ResourceBarrier truncated %u barrier(s) "
                    "(image %u emitted of %u, buffer %u emitted of %u, per-call max %u) — "
                    "missing synchronization possible; check "
                    "GetDeviceStats().barrierTruncationCount\n",
            droppedTotal, imageCount, imageBarrierCount, bufferCount, bufferBarrierCount,
            ZR_VULKAN_MAX_BARRIERS);
    }
    if (imageCount == 0u && bufferCount == 0u) {
        return;
    }
    // stage 掩码兜底：无显式阶段（None）时以 Top/Bottom 全域对齐（等价 D3D12
    // ExecuteCommandLists 边界隐式同步的最小 VK 表达）
    vkCmdPipelineBarrier(m_cmd, srcStages, dstStages, 0u, 0u, nullptr, bufferCount,
        bufferCount > 0u ? bufferBarriersVk : nullptr, imageCount,
        imageCount > 0u ? imageBarriersVk : nullptr);
}

void ZrVulkanCommandList::ResourceBarrier(const ZrImageBarrier* imageBarriers,
    uint32_t imageBarrierCount, const ZrBufferBarrier* bufferBarriers,
    uint32_t bufferBarrierCount)
{
    EmitBarriers(imageBarriers, imageBarrierCount, bufferBarriers, bufferBarrierCount);
}

void ZrVulkanCommandList::ResourceBarrierSplit(
    const ZrImageBarrier* imageBarriers, uint32_t count, bool beginOnly)
{
    // VK 无原生 split：begin 半边落一道普通屏障（写→读转换即刻完整生效）；
    // end 半边为 no-op——若同样落屏障，第二道会把已处于新布局的资源再次
    // "转换"（validation VUID-VkImageMemoryBarrier-oldLayout-01197 实证，
    // --shadows 级联 DSV 写 → 深度只读的 split 每帧双发）。正确性等价：
    // 单道全屏障的执行依赖（LATE_FRAGMENT_TESTS → FRAGMENT_SHADER）已覆盖
    // begin/end 两半的语义；D3D12 BEGIN_ONLY 的"提前开始缓存刷新"收益放弃。
    if (beginOnly) {
        EmitBarriers(imageBarriers, count, nullptr, 0u);
    }
}

void ZrVulkanCommandList::CopyBuffer(
    ZrBuffer src, ZrBuffer dst, uint64_t srcOffset, uint64_t dstOffset, uint64_t size)
{
    // → vkCmdCopyBuffer（单区域；缓冲无布局屏障需求）
    ZrVulkanBufferObj* srcObj = m_owner->ResolveBuffer(src);
    ZrVulkanBufferObj* dstObj = m_owner->ResolveBuffer(dst);
    assert(srcObj != nullptr && dstObj != nullptr && size > 0);
    if (srcObj == nullptr || dstObj == nullptr || size == 0) {
        return;
    }
    EndRenderingIfActive();
    VkBufferCopy region {};
    region.srcOffset = srcOffset;
    region.dstOffset = dstOffset;
    region.size = size;
    vkCmdCopyBuffer(m_cmd, srcObj->buffer, dstObj->buffer, 1u, &region);
}

void ZrVulkanCommandList::ClearColorImage(
    ZrImage image, ZrImageLayout layout, const float colorRGBA[4],
    const ZrSubresourceRange& subresources)
{
    // → vkCmdClearColorImage。layout 语义校验（合法域：General/ColorAttachmentOptimal/
    // TransferDstOptimal——前者为 VK 原生合法，后两者为 D3D12 清屏路径等价入参，
    // 内部往返屏障转译；zrhi_device.h 注释允许"后端清屏路径的等价布局"）
    assert(layout == ZrImageLayout::General || layout == ZrImageLayout::ColorAttachmentOptimal
        || layout == ZrImageLayout::TransferDstOptimal);
    // 基座：整资源清屏（子资源级清屏随视图/Pass 任务补全）
    assert(subresources.baseMipLevel == 0u && subresources.levelCount == ZR_REMAINING_MIP_LEVELS
        && subresources.baseArrayLayer == 0u
        && subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS);
    (void)subresources;

    ZrVulkanImageObj* obj = m_owner->ResolveImage(image);
    assert(obj != nullptr && obj->image != VK_NULL_HANDLE);
    if (obj == nullptr || obj->image == VK_NULL_HANDLE) {
        return;
    }

    const VkImageAspectFlags aspect
        = AspectForFormat(ZrVulkan_Format(obj->desc.format));
    assert(aspect == VK_IMAGE_ASPECT_COLOR_BIT); // 清屏仅颜色图（深度走 RecordClearDepth）

    VkImageMemoryBarrier toTransfer {};
    toTransfer.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    toTransfer.oldLayout = ZrVulkan_ImageLayout(layout);
    toTransfer.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
    toTransfer.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toTransfer.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toTransfer.image = obj->image;
    toTransfer.subresourceRange.aspectMask = aspect;
    toTransfer.subresourceRange.levelCount = VK_REMAINING_MIP_LEVELS;
    toTransfer.subresourceRange.layerCount = VK_REMAINING_ARRAY_LAYERS;
    toTransfer.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_WRITE_BIT;
    toTransfer.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    if (layout != ZrImageLayout::TransferDstOptimal) {
        // 附件/General → TRANSFER_DST 往返屏障（vkCmdClearColorImage 布局合法域约束）。
        // stage/access 匹配：COLOR_ATTACHMENT_WRITE 属 COLOR_ATTACHMENT_OUTPUT 阶段
        // （VK 逐位 stage-support 表校验，D3D12 无此约束）
        vkCmdPipelineBarrier(m_cmd, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT
                | VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VK_PIPELINE_STAGE_TRANSFER_BIT, 0u, 0u, nullptr, 0u, nullptr, 1u, &toTransfer);
    }

    VkClearColorValue clearValue {};
    std::memcpy(clearValue.float32, colorRGBA, sizeof(clearValue.float32));
    VkImageSubresourceRange range {};
    range.aspectMask = aspect;
    range.levelCount = VK_REMAINING_MIP_LEVELS;
    range.layerCount = VK_REMAINING_ARRAY_LAYERS;
    vkCmdClearColorImage(m_cmd, obj->image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clearValue,
        1u, &range);

    if (layout != ZrImageLayout::TransferDstOptimal) {
        // 回落屏障：外部布局契约（清屏后仍处入参 layout）——RG 屏障计划一致性前提
        VkImageMemoryBarrier back = toTransfer;
        back.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        back.newLayout = ZrVulkan_ImageLayout(layout);
        back.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        back.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT
            | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT
            | VK_ACCESS_SHADER_WRITE_BIT;
        vkCmdPipelineBarrier(m_cmd, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT
                | VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            0u, 0u, nullptr, 0u, nullptr, 1u, &back);
    }
}

void ZrVulkanCommandList::BeginPass(const ZrPassDesc&)
{
    // 基座占位（对位 D3D12）；真 Pass 语义由 SetRenderTarget 动态渲染落地
}

void ZrVulkanCommandList::EndPass()
{
    // 基座占位（同上）
}

void ZrVulkanCommandList::WriteTimestamp(ZrQueryPool heap, uint32_t queryIndex)
{
    // → vkCmdWriteTimestamp：BOTTOM_OF_PIPE（记录点所有前置工作完成后写入；
    // D3D12 EndQuery(TIMESTAMP) 对位）
    ZrVulkanQueryPoolObj* obj = m_owner->ResolveQueryPool(heap);
    assert(obj != nullptr && queryIndex < obj->count);
    if (obj == nullptr || queryIndex >= obj->count) {
        return;
    }
    EndRenderingIfActive();
    vkCmdWriteTimestamp(m_cmd, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, obj->pool, queryIndex);
}

// =============================================================================
// 图形绘制命令实现
// =============================================================================

// 调用契约与 D3D12 后端一致：SetPipelineState 先行，描述符槽位写入（根 CBV/SRV）
// 在绑定与绘制之间完成；无效句柄断言并跳过（Release 防御）。

void ZrVulkanCommandList::SetPipelineState(ZrPipelineState pso)
{
    // → vkCmdBindPipeline + 根绑定快照复位（描述符集在首个根绑定写入时重建——
    // VK 描述符集是不可变快照，与 D3D12 根参数直绑的语义差异点）
    ZrVulkanPsoObj* obj = m_owner->ResolvePipelineState(pso);
    assert(obj != nullptr && obj->pipeline != VK_NULL_HANDLE);
    if (obj == nullptr || obj->pipeline == VK_NULL_HANDLE) {
        return;
    }
    vkCmdBindPipeline(m_cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, obj->pipeline);
    m_activePso = pso;
    m_descriptorDirty = true; // 首次绘制前重建（含全空绑定的部分绑定快照）

    for (ZrBuffer& cb : m_cbSlots) {
        cb = ZrBuffer {};
    }
    for (ZrImageView& sv : m_srvSlots) {
        sv = ZrImageView {};
    }
}

void ZrVulkanCommandList::RebindDescriptorSet()
{
    // 根绑定快照重建：新分配 set0 → 全量写当前 CBV/SRV 表 → 重新绑定。
    // 为什么每变化全量重建：VK 的 vkUpdateDescriptorSets 改写已绑定集合只影响
    // "后续执行"（GPU 在 draw 执行点读描述符）——同帧多次改写同一集合会使
    // 之前的绘制全部读到最后一组绑定（真机实证：三个 cube 全画成右边的）。
    // 快照模型 = D3D12 根参数每绘直绑语义的忠实对位。
    ZrVulkanPsoObj* obj = m_owner->ResolvePipelineState(m_activePso);
    if (obj == nullptr || m_activePso.raw == ZR_HANDLE_INVALID) {
        return;
    }
    // 分配统一经设备（TASK-H-05 F1）：失败时设备累加 descriptorSetAllocFailureCount，
    // 上层经 IZrDevice::GetDeviceStats 可查——不再是"没人读的私有计数器 + NDEBUG 下
    // 被编译掉的 assert"。
    const VkDescriptorSet set = m_owner->AllocateRootDescriptorSet(m_descriptorPool, obj->setLayout);
    if (set == VK_NULL_HANDLE) {
        // Cannot rebuild the pool here: sets already recorded into this command buffer may
        // still be referenced by the GPU, and destroying the pool under them is UB. Skip
        // the rebind (the previous snapshot belongs to the previous root bindings, so this
        // frame draws with stale descriptors) and leave the failure counted on the device.
        assert(false && "RebindDescriptorSet: descriptor set allocate FAILED (pool exhausted)");
        return;
    }

    // 全量写当前根 CBV 槽（未设置的槽位跳过——PARTIALLY_BOUND 允许）
    uint32_t writeCount = 0u;
    VkWriteDescriptorSet writes[9] = {};
    VkDescriptorBufferInfo bufInfos[5] = {};
    VkDescriptorImageInfo imageInfos[4] = {}; // 每表槽 2 项：SAMPLED_IMAGE + SAMPLER
    for (uint32_t i = 0; i < obj->desc.rootCbvCount && i < 5u; ++i) {
        if (m_cbSlots[i].raw == ZR_HANDLE_INVALID) {
            continue;
        }
        ZrVulkanBufferObj* buf = m_owner->ResolveBuffer(m_cbSlots[i]);
        if (buf == nullptr || buf->buffer == VK_NULL_HANDLE) {
            continue;
        }
        bufInfos[writeCount].buffer = buf->buffer;
        bufInfos[writeCount].offset = 0u;
        bufInfos[writeCount].range = buf->desc.size;
        writes[writeCount].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        writes[writeCount].dstSet = set;
        writes[writeCount].dstBinding = i; // 根 CBV 槽 i → binding i
        writes[writeCount].dstArrayElement = 0u;
        writes[writeCount].descriptorCount = 1u;
        writes[writeCount].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
        writes[writeCount].pBufferInfo = &bufInfos[writeCount];
        writeCount += 1u;
    }
    // 全量写根 SRV 表槽（TASK-B-R7-02：SAMPLED_IMAGE(binding cbv+t) + SAMPLER
    // (binding cbv+M+t) 两描述符——与 glslc HLSL 的独立 t/s UniformConstant 对位；
    // 采样器 = 设备级 Point/Clamp 静态采样器，对位 D3D12 s_i 静态采样器）
    for (uint32_t t = 0; t < obj->desc.srvTableCount && t < 2u; ++t) {
        const uint32_t imageBinding = obj->desc.rootCbvCount + t;
        const uint32_t samplerBinding = obj->desc.rootCbvCount + obj->desc.srvTableCount + t;
        if (m_srvSlots[t].raw == ZR_HANDLE_INVALID) {
            continue;
        }
        ZrVulkanViewObj* view = m_owner->ResolveView(m_srvSlots[t]);
        if (view == nullptr || view->view == VK_NULL_HANDLE) {
            continue;
        }
        // 描述符布局须与绘制时资源实际布局一致：深度图采样（CSM 阴影图主 pass
        // 读）由 RG 声明为 DepthStencilReadOnlyOptimal；颜色图走 SHADER_READ_ONLY。
        // 布局错配 = validation error
        const VkImageLayout srvLayout
            = m_owner->ViewIsDepthSampling(view) ? VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL
                                                 : VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        imageInfos[t * 2u].sampler = VK_NULL_HANDLE;
        imageInfos[t * 2u].imageView = view->view;
        imageInfos[t * 2u].imageLayout = srvLayout;
        writes[writeCount].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        writes[writeCount].dstSet = set;
        writes[writeCount].dstBinding = imageBinding;
        writes[writeCount].dstArrayElement = 0u;
        writes[writeCount].descriptorCount = 1u;
        writes[writeCount].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
        writes[writeCount].pImageInfo = &imageInfos[t * 2u];
        writeCount += 1u;
        imageInfos[t * 2u + 1u].sampler = m_owner->PointClampSampler();
        imageInfos[t * 2u + 1u].imageView = VK_NULL_HANDLE;
        imageInfos[t * 2u + 1u].imageLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        writes[writeCount].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        writes[writeCount].dstSet = set;
        writes[writeCount].dstBinding = samplerBinding;
        writes[writeCount].dstArrayElement = 0u;
        writes[writeCount].descriptorCount = 1u;
        writes[writeCount].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLER;
        writes[writeCount].pImageInfo = &imageInfos[t * 2u + 1u];
        writeCount += 1u;
    }
    if (writeCount > 0u) {
        vkUpdateDescriptorSets(m_owner->VulkanDeviceHandle(), writeCount, writes, 0u, nullptr);
    }
    vkCmdBindDescriptorSets(m_cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, obj->pipelineLayout, 0u, 1u,
        &set, 0u, nullptr);
    m_activeSet = set;
    m_descriptorDirty = false;
}

void ZrVulkanCommandList::SetVertexBuffers(const ZrBuffer* buffers, uint32_t count)
{
    // → vkCmdBindVertexBuffers：stride 取 v1 固定顶点布局（32B，与 PSO 输入布局同源）
    assert(buffers != nullptr && count > 0u && count <= 4u);
    if (buffers == nullptr || count == 0u || count > 4u) {
        return;
    }
    VkBuffer vkBuffers[4] = {};
    VkDeviceSize offsets[4] = {};
    for (uint32_t i = 0; i < count; ++i) {
        ZrVulkanBufferObj* obj = m_owner->ResolveBuffer(buffers[i]);
        assert(obj != nullptr && obj->buffer != VK_NULL_HANDLE);
        if (obj == nullptr || obj->buffer == VK_NULL_HANDLE) {
            return;
        }
        vkBuffers[i] = obj->buffer;
    }
    vkCmdBindVertexBuffers(m_cmd, 0u, count, vkBuffers, offsets);
}

void ZrVulkanCommandList::SetIndexBuffer(ZrBuffer buffer)
{
    // → vkCmdBindIndexBuffer：v1 固定 R32_UINT
    ZrVulkanBufferObj* obj = m_owner->ResolveBuffer(buffer);
    assert(obj != nullptr && obj->buffer != VK_NULL_HANDLE);
    if (obj == nullptr || obj->buffer == VK_NULL_HANDLE) {
        return;
    }
    vkCmdBindIndexBuffer(m_cmd, obj->buffer, 0u, VK_INDEX_TYPE_UINT32);
}

void ZrVulkanCommandList::SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer)
{
    // 根 CBV 直绑的 VK 落地：根绑定快照模型（见 RebindDescriptorSet 注）——记录
    // 槽位 → 重建集合 → 重绑（对位 D3D12 SetGraphicsRootConstantBufferView 每绘直绑）
    ZrVulkanPsoObj* pso = m_owner->ResolvePipelineState(m_activePso);
    ZrVulkanBufferObj* obj = m_owner->ResolveBuffer(buffer);
    assert(pso != nullptr && obj != nullptr && obj->buffer != VK_NULL_HANDLE);
    assert(m_activePso.IsValid() && slot < pso->desc.rootCbvCount);
    if (pso == nullptr || obj == nullptr || !m_activePso.IsValid()
        || slot >= pso->desc.rootCbvCount || slot >= 5u) {
        return;
    }
    m_cbSlots[slot] = buffer;
    RebindDescriptorSet();
}

void ZrVulkanCommandList::SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view)
{
    // 根 SRV 描述符表的 VK 落地：根绑定快照模型（视图 + 设备级 Point/Clamp 静态
    // 采样器；对位 D3D12 SetGraphicsRootDescriptorTable + 静态采样器 s_i）
    ZrVulkanPsoObj* pso = m_owner->ResolvePipelineState(m_activePso);
    ZrVulkanViewObj* obj = m_owner->ResolveView(view);
    assert(pso != nullptr && obj != nullptr && obj->view != VK_NULL_HANDLE);
    assert(m_activePso.IsValid());
    assert(rootSlot >= pso->desc.rootCbvCount
        && rootSlot < pso->desc.rootCbvCount + pso->desc.srvTableCount);
    if (pso == nullptr || obj == nullptr || !m_activePso.IsValid()) {
        return;
    }
    const uint32_t srvIndex = rootSlot >= pso->desc.rootCbvCount
        ? rootSlot - pso->desc.rootCbvCount
        : 0u;
    if (srvIndex >= pso->desc.srvTableCount || srvIndex >= 2u) {
        return;
    }
    m_srvSlots[srvIndex] = view;
    RebindDescriptorSet();
}

void ZrVulkanCommandList::DrawIndexedInstanced(uint32_t indexCountPerInstance,
    uint32_t instanceCount, uint32_t startIndexLocation, int32_t baseVertexLocation,
    uint32_t startInstanceLocation)
{
    // → vkCmdDrawIndexed——参数一一对应，无转译
    assert(m_renderingActive); // 须处于 SetRenderTarget 开启的动态渲染实例内
    if (m_descriptorDirty) {
        RebindDescriptorSet(); // 无根绑定写入的首绘兜底（部分绑定快照）
    }
    vkCmdDrawIndexed(m_cmd, indexCountPerInstance, instanceCount, startIndexLocation,
        baseVertexLocation, startInstanceLocation);
}

void ZrVulkanCommandList::SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv)
{
    // 附件绑定 → vkCmdBeginRendering（动态渲染；对位 D3D12 OMSetRenderTargets）。
    // loadOp=LOAD/storeOp=STORE：清屏是显式 Clear 命令（与 D3D12 同语义）
    ZrVulkanViewObj* rtv = m_owner->ResolveView(colorRtv);
    assert((colorRtv.IsValid() && rtv != nullptr && rtv->desc.viewType == ZrImageViewType::Rtv)
        || !colorRtv.IsValid());
    if (colorRtv.IsValid()
        && (rtv == nullptr || rtv->desc.viewType != ZrImageViewType::Rtv)) {
        return;
    }
    ZrVulkanViewObj* dsv = nullptr;
    if (depthDsv.IsValid()) {
        dsv = m_owner->ResolveView(depthDsv);
        assert(dsv != nullptr && dsv->desc.viewType == ZrImageViewType::Dsv);
        if (dsv == nullptr || dsv->desc.viewType != ZrImageViewType::Dsv) {
            dsv = nullptr;
        }
    }

    EndRenderingIfActive();

    // renderArea/layerCount：取颜色目标（无颜色则深度目标）的"视图 mip 窗口"
    // 尺寸——VK 要求 renderArea.extent 与 layerCount 非 0（viewMask=0 时），且
    // 不得超过附件视图的有效范围（VUID-VkRenderingInfo-pNext-06079/06080：
    // --r5 Hi-Z 逐 mip RTV 实证，mip 视图 320..20px 而整图 640×480）。整图 mip0
    // 视图（既有全部模式）行为不变。
    uint32_t areaW = 0u;
    uint32_t areaH = 0u;
    ZrVulkanViewObj* areaView = rtv != nullptr ? rtv : dsv;
    if (areaView != nullptr) {
        ZrVulkanImageObj* areaImage = m_owner->ResolveImage(areaView->desc.image);
        if (areaImage != nullptr) {
            const uint32_t mipBase
                = areaView->desc.subresources.baseMipLevel < areaImage->desc.mipLevels
                ? areaView->desc.subresources.baseMipLevel
                : 0u;
            // mip 尺寸 = max(1, 整图 >> mipBase)（VK 规范 mip 链定义）
            areaW = areaImage->desc.width >> mipBase;
            areaH = areaImage->desc.height >> mipBase;
            if (areaW == 0u) {
                areaW = 1u;
            }
            if (areaH == 0u) {
                areaH = 1u;
            }
        }
    }
    assert(areaW > 0u && areaH > 0u);

    VkRenderingAttachmentInfo colorAttachment {};
    colorAttachment.sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO;
    colorAttachment.imageView = rtv != nullptr ? rtv->view : VK_NULL_HANDLE;
    colorAttachment.imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    colorAttachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    colorAttachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;

    VkRenderingAttachmentInfo depthAttachment {};
    depthAttachment.sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO;
    if (dsv != nullptr) {
        depthAttachment.imageView = dsv->view;
        depthAttachment.imageLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
        depthAttachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
        depthAttachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    }

    VkRenderingInfo renderingInfo {};
    renderingInfo.sType = VK_STRUCTURE_TYPE_RENDERING_INFO;
    renderingInfo.renderArea = { { 0, 0 }, { areaW, areaH } };
    renderingInfo.layerCount = 1u; // 单层 2D 目标（v1）
    renderingInfo.colorAttachmentCount = (rtv != nullptr) ? 1u : 0u;
    // depth-only（CSM 级联 pass）：无颜色附件时指针须为空（规范要求 count=0
    // 时 pColorAttachments 忽略；显式 nullptr 免验证层歧义）
    renderingInfo.pColorAttachments = (rtv != nullptr) ? &colorAttachment : nullptr;
    renderingInfo.pDepthAttachment = (dsv != nullptr) ? &depthAttachment : nullptr;
    vkCmdBeginRendering(m_cmd, &renderingInfo);
    m_renderingActive = true;
}

void ZrVulkanCommandList::SetViewportScissor(uint32_t width, uint32_t height)
{
    // → vkCmdSetViewport + vkCmdSetScissor。负高度视口（y=height, height=-height）
    // 把 D3D12 LH 屏幕空间（NDC y 上正）映射到 VK 帧缓冲坐标（y 下正），配合
    // frontFace=CW（B-R8-01 修正，见 device 侧光栅化注）精确复刻 D3D12 CW 前向
    // 判定——两后端像素输出与剔除集合对照的前提
    assert(width > 0u && height > 0u);
    if (width == 0u || height == 0u) {
        return;
    }
    const VkViewport viewport = { 0.0f, static_cast<float>(height), static_cast<float>(width),
        -static_cast<float>(height), 0.0f, 1.0f };
    const VkRect2D scissor = { { 0, 0 }, { width, height } };
    vkCmdSetViewport(m_cmd, 0u, 1u, &viewport);
    vkCmdSetScissor(m_cmd, 0u, 1u, &scissor);
}

void ZrVulkanCommandList::EndRenderingIfActive()
{
    if (m_renderingActive) {
        vkCmdEndRendering(m_cmd);
        m_renderingActive = false;
    }
}
