// =============================================================================
// zrhi_d3d12_commandlist.h —— D3D12 命令表（TASK-B-R2-02）
//
// 与 Vulkan 语义的对应关系（Vulkan → 本实现）：
//   - Begin/End           → vkBeginCommandBuffer / vkEndCommandBuffer
//     （Begin 从设备帧槽环取分配器：等待该槽提交完成 fence 后 Reset——
//      ≈ Vulkan 命令池重置 + 命令缓冲二次分配的安全条件）
//   - ResourceBarrier(批量) → vkCmdPipelineBarrier（图像屏障：布局经
//     zrhi_d3d12_translate 显式 switch 查表转译；缓冲屏障：D3D12 无对应，
//     ExecuteCommandLists 边界隐式同步，见 .cpp 注释）
//   - CopyBuffer          → vkCmdCopyBuffer（单区域 CopyBufferRegion）
//   - ClearColorImage     → vkCmdClearColorImage（RTV 路径：ClearRenderTargetView；
//     UAV 路径：ClearUnorderedAccessViewFloat——按图像创建时的描述符能力分派）
//   - BeginPass/EndPass   → 占位（动态渲染由管线任务补全）
//   - WriteTimestamp      → vkCmdWriteTimestamp（EndQuery(TIMESTAMP)；TASK-B-R2-03）
// TASK-B-R2-03：ResourceBarrier 同时驱动设备端 per-resource 状态表转移记录
// （屏障生效后表内当前布局即 StateBefore 权威来源，供 R3 自动屏障）。
// =============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <wrl/client.h>

#include "zrhi/zrhi_device.h"

class ZrD3D12Device;

class ZrD3D12CommandList final : public IZrCommandList {
public:
    explicit ZrD3D12CommandList(ZrD3D12Device* owner)
        : m_owner(owner)
    {
    }

    ~ZrD3D12CommandList() override = default;

    /// 创建底层图形命令表（一次性绑定临时分配器后立即 Close 复位；失败 false）
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
    void WriteTimestamp(ZrQueryPool heap, uint32_t queryIndex) override; // → vkCmdWriteTimestamp

    // ---- TASK-B-R4-03：图形绘制命令（→ vkCmdBindPipeline / vkCmdBindVertexBuffers /
    //      vkCmdBindIndexBuffer / 根常量绑定 / vkCmdDrawIndexed / 附件绑定 / 视口）----
    void SetPipelineState(ZrPipelineState pso) override;
    void SetVertexBuffers(const ZrBuffer* buffers, uint32_t count) override;
    void SetIndexBuffer(ZrBuffer buffer) override;
    void SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer) override;
    void SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view) override; // TASK-B-R5-02
    void DrawIndexedInstanced(uint32_t indexCountPerInstance, uint32_t instanceCount,
        uint32_t startIndexLocation, int32_t baseVertexLocation,
        uint32_t startInstanceLocation) override;
    void SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv) override;
    void SetViewportScissor(uint32_t width, uint32_t height) override;

private:
    friend class ZrD3D12Device;

    // 屏障展开公共实现（普通与 split 半边共用；flags=NONE/BEGIN_ONLY/END_ONLY）
    void EmitImageBarriers(const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
        D3D12_RESOURCE_BARRIER_FLAGS flags);

    ZrD3D12Device* m_owner;
    Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> m_list;
    uint32_t m_slotIndex = 0; // 最近一次 Begin 占用的帧槽（Submit 后向其 fence 信号）
    bool m_recording = false;
    bool m_closed = false;
};
