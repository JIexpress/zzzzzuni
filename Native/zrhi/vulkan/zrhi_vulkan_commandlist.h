// =============================================================================
// zrhi_vulkan_commandlist.h —— Vulkan 命令表（TASK-B-R7-01）
//
// 与 zrhi 语义的对应关系（→ 真实 vkCmd*）：
//   - Begin/End           → vkBeginCommandBuffer / vkEndCommandBuffer
//     （Begin 先等本表上次提交 fence 完成后 Reset 命令缓冲与描述符池——
//      ≈ vkResetCommandPool 的安全条件；一次性提交语义）
//   - ResourceBarrier(批量) → 单次 vkCmdPipelineBarrier（图像屏障 1:1 展开
//     VkImageMemoryBarrier，布局/访问/阶段经 translate 显式查表；缓冲屏障
//     VkBufferMemoryBarrier——VK 原生支持，D3D12 侧无对应是 D3D12 缺失而非冗余）
//   - CopyBuffer          → vkCmdCopyBuffer（单区域）
//   - ClearColorImage     → vkCmdClearColorImage。布局合法域约束：VK 只接受
//     GENERAL/TRANSFER_DST——ColorAttachmentOptimal 入参（D3D12 RTV 清屏等价
//     布局，zrhi_device.h 注释允许）时内部做 附件布局→TRANSFER_DST→清屏→回落
//     的往返屏障，外部布局契约（清屏后仍处入参 layout）不变，RG 屏障计划一致
//   - BeginPass/EndPass   → 占位（对位 D3D12 基座；真 Pass 语义由 SetRenderTarget
//     动态渲染落地）
//   - SetRenderTarget     → vkCmdBeginRendering（动态渲染；loadOp=LOAD——清屏
//     是显式 ClearColorImage/ClearDepth 命令，与 D3D12 OMSetRenderTargets 对位）。
//     再次调用或 End 前遇屏障类命令时自动 vkCmdEndRendering（屏障不得处于
//     rendering 实例内——VK 侧约束，D3D12 无此限制）
//   - SetPipelineState    → vkCmdBindPipeline + 分配/绑定 set0 描述符集
//     （CL 私有描述符池，Begin 时 Reset——上次提交已完成，复用安全）
//   - SetGraphicsConstantBuffer → vkUpdateDescriptorSets 写 UNIFORM_BUFFER 槽
//     （集合已绑定未提交，记录期改写合法；对位 D3D12 根 CBV 直绑）
//   - SetGraphicsBindlessSrv   → 写 COMBINED_IMAGE_SAMPLER 槽（视图 + 设备级
//     Point/Clamp 静态采样器；对位 D3D12 根 SRV 表 + 静态采样器）
//   - SetViewportScissor  → vkCmdSetViewport（负高度视口 y=height/height=-height，
//     复刻 D3D12 LH 屏幕空间）+ vkCmdSetScissor
//   - WriteTimestamp      → vkCmdWriteTimestamp（BOTTOM_OF_PIPE；D3D12 EndQuery 对位）
//
// split barrier：VK 无原生 split——begin/end 两半各落一道普通屏障（正确性等价，
// D3D12 BEGIN_ONLY 的提前刷新收益放弃；见设备头注）
// =============================================================================
#pragma once

#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include <vulkan/vulkan.h>
#pragma warning(pop)

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "zrhi/zrhi_device.h"

class ZrVulkanDevice;

class ZrVulkanCommandList final : public IZrCommandList {
public:
    explicit ZrVulkanCommandList(ZrVulkanDevice* owner)
        : m_owner(owner)
    {
    }

    ~ZrVulkanCommandList() override;

    /// 创建命令池/命令缓冲/提交 fence/描述符池（失败 false）
    bool Init();

    // ---- IZrCommandList ---------------------------------------------------------
    void Begin() override;
    void End() override;
    void ResourceBarrier(const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
        const ZrBufferBarrier* bufferBarriers, uint32_t bufferBarrierCount) override;
    void ResourceBarrierSplit(
        const ZrImageBarrier* imageBarriers, uint32_t count, bool beginOnly) override;
    void CopyBuffer(ZrBuffer src, ZrBuffer dst, uint64_t srcOffset, uint64_t dstOffset,
        uint64_t size) override;
    void ClearColorImage(ZrImage image, ZrImageLayout layout, const float colorRGBA[4],
        const ZrSubresourceRange& subresources) override;
    void BeginPass(const ZrPassDesc& pass) override; // 基座占位（未记录任何命令）
    void EndPass() override;                          // 基座占位
    void WriteTimestamp(ZrQueryPool heap, uint32_t queryIndex) override;

    // ---- 图形绘制命令 -------------------------------------------------------------
    void SetPipelineState(ZrPipelineState pso) override;
    void SetVertexBuffers(const ZrBuffer* buffers, uint32_t count) override;
    void SetIndexBuffer(ZrBuffer buffer) override;
    void SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer) override;
    void SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view) override;
    void DrawIndexedInstanced(uint32_t indexCountPerInstance, uint32_t instanceCount,
        uint32_t startIndexLocation, int32_t baseVertexLocation,
        uint32_t startInstanceLocation) override;
    void SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv) override;
    void SetViewportScissor(uint32_t width, uint32_t height) override;

    VkCommandBuffer NativeCmd() const { return m_cmd; }

private:
    friend class ZrVulkanDevice;

    // 屏障展开公共实现（普通与 split 半边共用；split 仅语义注释差异——VK 无原生 flag）
    void EmitBarriers(const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
        const ZrBufferBarrier* bufferBarriers, uint32_t bufferBarrierCount);

    // 离开动态渲染实例（屏障/清屏/拷贝/时间戳/End 前必须；未处于渲染态为空操作）
    void EndRenderingIfActive();

    // 重建并绑定 set0（根绑定快照；SetGraphicsConstantBuffer/Srv 变化后调用）
    void RebindDescriptorSet();

    ZrVulkanDevice* m_owner;
    VkCommandPool m_pool = VK_NULL_HANDLE;
    VkCommandBuffer m_cmd = VK_NULL_HANDLE;
    VkFence m_submitFence = VK_NULL_HANDLE;
    bool m_pendingSubmit = false; // 上次提交待等待（Begin 时消费）

    // set0 描述符集（CL 私有池；根绑定的"每绘快照"——VK 描述符集是不可变快照，
    // 与 D3D12 根参数直绑的语义差异：SetGraphicsConstantBuffer/Srv 每次调用都
    // 分配新集合并全量重绑当前根绑定组合，保证逐绘常量独立，见 .cpp 注）
    VkDescriptorPool m_descriptorPool = VK_NULL_HANDLE;
    VkDescriptorSet m_activeSet = VK_NULL_HANDLE;
    ZrPipelineState m_activePso {};   // 当前绑定 PSO（描述符槽位布局来源）
    ZrBuffer m_cbSlots[5] = {};       // 根 CBV 槽位跟踪（Set* 调用间累积）
    ZrImageView m_srvSlots[2] = {};   // 根 SRV 表槽位跟踪
    bool m_descriptorDirty = false;   // 根绑定变化后是否已重建集合

    bool m_renderingActive = false; // 动态渲染实例进行中（EndRenderingIfActive 用）
    bool m_recording = false;

    // 描述符集分配失败计数**不在此处**（TASK-H-05 F1）：池耗尽是设备级事实，命令表
    // 只把失败交给 ZrVulkanDevice::AllocateRootDescriptorSet 累计，由
    // IZrDevice::GetDeviceStats().descriptorSetAllocFailureCount 读出。命令表私有的
    // 计数器在 H-03 验收里被判为"只有声明与自增、全仓无读取方"——保留它就是保留
    // 一个假的可观测性。
};
