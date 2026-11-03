// zrhi_d3d12_commandlist.cpp —— D3D12 命令表实现（TASK-B-R2-02）
//
// Vulkan 语义对应见 zrhi_d3d12_commandlist.h 头注。要点：
//   - ZrImageLayout → D3D12_RESOURCE_STATES 一律经 ZrD3D12_ResourceStates 显式
//     switch 查表（两套枚举序不同，严禁隐式数值转换——本文件不出现任何
//     static_cast<D3D12_RESOURCE_STATES>(layout)）
//   - 缓冲屏障：D3D12 缓冲无资源状态（恒 COMMON 语义），ExecuteCommandLists
//     边界即隐式同步点，故 ZrBufferBarrier 基座下不产生任何 D3D12 屏障
//     （≈ Vulkan 的 buffer memory barrier 由队列边界覆盖）
#include "zrhi/d3d12/zrhi_d3d12_commandlist.h"

#include "zrhi/d3d12/zrhi_d3d12_device.h"
#include "zrhi/d3d12/zrhi_d3d12_translate.h"

#include <cassert>

// 一次 ResourceBarrier 调用可展开的转换数上限（子资源 range 展开后）
constexpr uint32_t ZR_D3D12_MAX_BARRIERS = 128;

bool ZrD3D12CommandList::Init()
{
    assert(m_owner != nullptr);
    // CreateCommandList 创建即处于记录态：绑定一次性临时分配器，立即 Close 复位，
    // 后续 Begin 经设备帧槽环正式分配（≈ vkResetCommandBuffer 后可重复 Begin）
    Microsoft::WRL::ComPtr<ID3D12CommandAllocator> bootAllocator;
    if (FAILED(m_owner->D3D12Device()->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&bootAllocator)))) {
        return false;
    }
    if (FAILED(m_owner->D3D12Device()->CreateCommandList(0,
            D3D12_COMMAND_LIST_TYPE_DIRECT, bootAllocator.Get(), nullptr,
            IID_PPV_ARGS(&m_list)))) {
        return false;
    }
    m_list->Close(); // 空记录关闭，交还“可 Reset”状态
    m_recording = false;
    m_closed = true;
    return true;
}

void ZrD3D12CommandList::Begin()
{
    assert(!m_recording);
    // → vkBeginCommandBuffer：帧槽分配器 + 命令表 Reset + 绑定全局 bindless 堆
    uint32_t slotIndex = 0;
    ZrD3D12FrameSlot& slot = m_owner->BeginFrameSlot(slotIndex);
    m_slotIndex = slotIndex;
    m_list->Reset(slot.allocator.Get(), nullptr);
    ID3D12DescriptorHeap* heaps[] = {m_owner->BindlessDescriptorHeap()};
    m_list->SetDescriptorHeaps(1u, heaps);
    m_recording = true;
    m_closed = false;
}

void ZrD3D12CommandList::End()
{
    assert(m_recording);
    m_list->Close(); // → vkEndCommandBuffer
    m_recording = false;
    m_closed = true;
}

void ZrD3D12CommandList::ResourceBarrier(const ZrImageBarrier* imageBarriers,
    uint32_t imageBarrierCount, const ZrBufferBarrier* bufferBarriers,
    uint32_t bufferBarrierCount)
{
    // 缓冲屏障：D3D12 无缓冲资源状态，ExecuteCommandLists 边界隐式同步（见头注）
    (void)bufferBarriers;
    (void)bufferBarrierCount;
    // → vkCmdPipelineBarrier：批量转换一次性提交（普通屏障 flag=NONE）
    EmitImageBarriers(imageBarriers, imageBarrierCount, D3D12_RESOURCE_BARRIER_FLAG_NONE);
}

void ZrD3D12CommandList::ResourceBarrierSplit(
    const ZrImageBarrier* imageBarriers, uint32_t count, bool beginOnly)
{
    // TASK-B-R3-02：split barrier 半边——BEGIN_ONLY（写 Pass 结束即发起）/ END_ONLY
    // （读 Pass 前补可见性）。flag 映射在 translate（唯一转译点），屏障内容/状态
    // 转译与普通屏障同一路径
    EmitImageBarriers(imageBarriers, count, ZrD3D12_SplitBarrierFlag(beginOnly));
}

// 屏障展开公共实现：布局经 ZrD3D12_ResourceStates 显式查表、子资源 range 展开、
// flags 区分普通/BEGIN_ONLY/END_ONLY（split barrier 的 D3D12 落地点）
void ZrD3D12CommandList::EmitImageBarriers(
    const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
    D3D12_RESOURCE_BARRIER_FLAGS flags)
{
    assert((imageBarriers != nullptr) || (imageBarrierCount == 0u));
    if (imageBarriers == nullptr || imageBarrierCount == 0u) {
        return;
    }

    D3D12_RESOURCE_BARRIER barriers[ZR_D3D12_MAX_BARRIERS] = {};
    uint32_t count = 0u;

    for (uint32_t i = 0; i < imageBarrierCount; ++i) {
        const ZrImageBarrier& ib = imageBarriers[i];
        ZrD3D12ImageObj* obj = m_owner->ResolveImage(ib.image);
        assert(obj != nullptr && obj->resource != nullptr);
        if (obj == nullptr || obj->resource == nullptr) {
            continue;
        }

        // 显式 switch 查表转译（zrhi_d3d12_translate.cpp 为唯一转译点）
        const D3D12_RESOURCE_STATES stateBefore = ZrD3D12_ResourceStates(ib.prevLayout);
        const D3D12_RESOURCE_STATES stateAfter = ZrD3D12_ResourceStates(ib.newLayout);
        if (stateBefore == stateAfter) {
            // 布局未变：D3D12 转换屏障（含 split 半边）要求前后状态相异；纯访问/
            // 阶段域同步（UAV 屏障等）由后续任务按需补全。RG 的 split 计划只在
            // layout 变化时产出（PlanBarriers），此处恒有前后态相异
            continue;
        }

        // 设备端 per-resource 状态表转移记录（屏障生效后表内即当前布局）
        m_owner->TransitionImageLayout(ib.image, ib.prevLayout, ib.newLayout);

        // 子资源 range 展开（REMAINING 语义 -> clamp 到资源实际范围）
        const uint32_t mipLevels = obj->desc.mipLevels;
        const uint32_t arrayLayers = obj->desc.arrayLayers;
        const uint32_t mipBegin = ib.subresources.baseMipLevel < mipLevels
            ? ib.subresources.baseMipLevel
            : mipLevels;
        const uint32_t mipEnd = (ib.subresources.levelCount == ZR_REMAINING_MIP_LEVELS)
            ? mipLevels
            : ((ib.subresources.baseMipLevel + ib.subresources.levelCount) < mipLevels
                    ? (ib.subresources.baseMipLevel + ib.subresources.levelCount)
                    : mipLevels);
        const uint32_t layerBegin = ib.subresources.baseArrayLayer < arrayLayers
            ? ib.subresources.baseArrayLayer
            : arrayLayers;
        const uint32_t layerEnd = (ib.subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS)
            ? arrayLayers
            : ((ib.subresources.baseArrayLayer + ib.subresources.layerCount) < arrayLayers
                    ? (ib.subresources.baseArrayLayer + ib.subresources.layerCount)
                    : arrayLayers);

        const bool wholeResource = (mipBegin == 0u) && (mipEnd == mipLevels)
            && (layerBegin == 0u) && (layerEnd == arrayLayers);

        auto emit = [&](UINT subresource) {
            assert(count < ZR_D3D12_MAX_BARRIERS);
            if (count >= ZR_D3D12_MAX_BARRIERS) {
                return;
            }
            D3D12_RESOURCE_BARRIER& b = barriers[count];
            b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b.Flags = flags;
            b.Transition.pResource = obj->resource.Get();
            b.Transition.StateBefore = stateBefore;
            b.Transition.StateAfter = stateAfter;
            b.Transition.Subresource = subresource;
            count += 1u;
        };

        if (wholeResource) {
            emit(D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES);
            continue;
        }
        for (uint32_t layer = layerBegin; layer < layerEnd; ++layer) {
            for (uint32_t mip = mipBegin; mip < mipEnd; ++mip) {
                // 子资源索引（平面 0）：mip + layer * mipLevels（d3dx12.h 的
                // D3D12CalcSubresource 同式，SDK 无该内联函数故手写）
                emit(mip + layer * mipLevels);
            }
        }
    }

    if (count > 0u) {
        m_list->ResourceBarrier(count, barriers);
    }
}

void ZrD3D12CommandList::CopyBuffer(
    ZrBuffer src, ZrBuffer dst, uint64_t srcOffset, uint64_t dstOffset, uint64_t size)
{
    // → vkCmdCopyBuffer（单区域；缓冲无状态屏障需求）
    ZrD3D12BufferObj* srcObj = m_owner->ResolveBuffer(src);
    ZrD3D12BufferObj* dstObj = m_owner->ResolveBuffer(dst);
    assert(srcObj != nullptr && dstObj != nullptr && size > 0);
    if (srcObj == nullptr || dstObj == nullptr || size == 0) {
        return;
    }
    m_list->CopyBufferRegion(
        dstObj->resource.Get(), dstOffset, srcObj->resource.Get(), srcOffset, size);
}

void ZrD3D12CommandList::ClearColorImage(
    ZrImage image, ZrImageLayout layout, const float colorRGBA[4],
    const ZrSubresourceRange& subresources)
{
    // → vkCmdClearColorImage。layout 仅做语义校验：D3D12 清屏无布局参数，
    // 资源状态由前置 ResourceBarrier 保证（RTV 路径须处 RENDER_TARGET，
    // UAV 路径须处 UNORDERED_ACCESS）
    assert(layout == ZrImageLayout::General || layout == ZrImageLayout::ColorAttachmentOptimal
        || layout == ZrImageLayout::TransferDstOptimal);
    // 基座：整资源清屏（子资源级清屏随视图/Pass 任务补全）
    assert(subresources.baseMipLevel == 0u && subresources.levelCount == ZR_REMAINING_MIP_LEVELS
        && subresources.baseArrayLayer == 0u
        && subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS);
    (void)layout; // Release/NDEBUG 下 assert 消失，显式标记参数仅校验用途
    (void)subresources;

    ZrD3D12ImageObj* obj = m_owner->ResolveImage(image);
    assert(obj != nullptr && obj->resource != nullptr);
    if (obj == nullptr || obj->resource == nullptr) {
        return;
    }
    if (obj->hasRtv) {
        // RTV 清屏路径（交换链后台缓冲/颜色附件）
        m_list->ClearRenderTargetView(obj->rtv, colorRGBA, 0u, nullptr);
    } else if (obj->hasUav) {
        // UAV 清屏路径（storage 图像；GPU 描述符在 Begin 绑定的全局 bindless 堆中）
        m_list->ClearUnorderedAccessViewFloat(m_owner->BindlessGpu(obj->uavSlot),
            m_owner->BindlessCpu(obj->uavSlot), obj->resource.Get(), colorRGBA, 0u, nullptr);
    } else {
        assert(false && "ClearColorImage: 图像无 RTV/UAV 清屏能力");
    }
}

void ZrD3D12CommandList::BeginPass(const ZrPassDesc&)
{
    // 基座占位：渲染 Pass（动态渲染）由管线任务补全；不记录命令
}

void ZrD3D12CommandList::EndPass()
{
    // 基座占位（同上）
}

void ZrD3D12CommandList::WriteTimestamp(ZrQueryPool heap, uint32_t queryIndex)
{
    // → vkCmdWriteTimestamp：EndQuery 在记录点把 GPU 时钟 tick 写入查询堆
    ZrD3D12QueryPoolObj* obj = m_owner->ResolveQueryPool(heap);
    assert(obj != nullptr && queryIndex < obj->count);
    if (obj == nullptr || queryIndex >= obj->count) {
        return;
    }
    m_list->EndQuery(obj->heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, queryIndex);
}

// =============================================================================
// TASK-B-R4-03：图形绘制命令实现
// =============================================================================

// SetPipelineState/SetVertexBuffers 等的调用契约：PSO 已绑定（根签名随之）——
// 顺序由上层（RG DrawPass 回调）保证；无效句柄记录断言并跳过（Release 防御）。

void ZrD3D12CommandList::SetPipelineState(ZrPipelineState pso)
{
    // → vkCmdBindPipeline：根签名 + PSO + 固定拓扑（v1 三角形列表）一次绑定。
    // D3D12 拓扑是命令表状态（IASetPrimitiveTopology）而非 PSO 字段，随绑定下发
    ZrD3D12PsoObj* obj = m_owner->ResolvePipelineState(pso);
    assert(obj != nullptr && obj->pipelineState != nullptr && obj->rootSignature != nullptr);
    if (obj == nullptr || obj->pipelineState == nullptr) {
        return;
    }
    m_list->SetGraphicsRootSignature(obj->rootSignature.Get());
    m_list->SetPipelineState(obj->pipelineState.Get());
    m_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
}

void ZrD3D12CommandList::SetVertexBuffers(const ZrBuffer* buffers, uint32_t count)
{
    // → vkCmdBindVertexBuffers：stride 取 v1 固定顶点布局（Pos3f+Normal3f+Uv2f
    // = 32B，与 PSO 输入布局同源约定 ZR_D3D12_GRAPHICS_VERTEX_STRIDE）
    assert(buffers != nullptr && count > 0u && count <= 4u);
    if (buffers == nullptr || count == 0u || count > 4u) {
        return;
    }
    D3D12_VERTEX_BUFFER_VIEW views[4] = {};
    for (uint32_t i = 0; i < count; ++i) {
        ZrD3D12BufferObj* obj = m_owner->ResolveBuffer(buffers[i]);
        assert(obj != nullptr && obj->resource != nullptr);
        if (obj == nullptr || obj->resource == nullptr) {
            return;
        }
        views[i].BufferLocation = obj->resource->GetGPUVirtualAddress();
        views[i].SizeInBytes = static_cast<UINT>(obj->desc.size);
        views[i].StrideInBytes = ZR_D3D12_GRAPHICS_VERTEX_STRIDE;
    }
    m_list->IASetVertexBuffers(0u, count, views);
}

void ZrD3D12CommandList::SetIndexBuffer(ZrBuffer buffer)
{
    // → vkCmdBindIndexBuffer：v1 固定 R32_UINT
    ZrD3D12BufferObj* obj = m_owner->ResolveBuffer(buffer);
    assert(obj != nullptr && obj->resource != nullptr);
    if (obj == nullptr || obj->resource == nullptr) {
        return;
    }
    D3D12_INDEX_BUFFER_VIEW view {};
    view.BufferLocation = obj->resource->GetGPUVirtualAddress();
    view.SizeInBytes = static_cast<UINT>(obj->desc.size);
    view.Format = DXGI_FORMAT_R32_UINT;
    m_list->IASetIndexBuffer(&view);
}

void ZrD3D12CommandList::SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer)
{
    // 根 CBV 直绑：SetGraphicsRootConstantBufferView（slot 即根参数序号；v1 单槽 b0，
    // TASK-B-R5-01 起支持 rootCbvCount=1..4 的多槽布局——b0=逐绘/b1=光源/b2=簇）。
    // 缓冲须为 hostVisible（UPLOAD 堆）且大小 ≥ 256B 对齐要求由调用方保证
    ZrD3D12BufferObj* obj = m_owner->ResolveBuffer(buffer);
    assert(obj != nullptr && obj->resource != nullptr);
    if (obj == nullptr || obj->resource == nullptr) {
        return;
    }
    m_list->SetGraphicsRootConstantBufferView(
        slot, obj->resource->GetGPUVirtualAddress());
}

void ZrD3D12CommandList::DrawIndexedInstanced(uint32_t indexCountPerInstance,
    uint32_t instanceCount, uint32_t startIndexLocation, int32_t baseVertexLocation,
    uint32_t startInstanceLocation)
{
    // → vkCmdDrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset,
    // firstInstance)——参数一一对应，无转译
    m_list->DrawIndexedInstanced(indexCountPerInstance, instanceCount, startIndexLocation,
        baseVertexLocation, startInstanceLocation);
}

void ZrD3D12CommandList::SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv)
{
    // 附件绑定：OMSetRenderTargets（→ 动态渲染 VkRenderingInfo 的颜色+深度附件）
    // TASK-B-R5-02：colorRtv 无效 = 无颜色目标（depth-only Pass：仅深度附件）
    ZrD3D12ViewObj* rtv = m_owner->ResolveView(colorRtv);
    assert((colorRtv.IsValid() && rtv != nullptr && rtv->desc.viewType == ZrImageViewType::Rtv)
        || !colorRtv.IsValid());
    if (colorRtv.IsValid()
        && (rtv == nullptr || rtv->desc.viewType != ZrImageViewType::Rtv)) {
        return;
    }
    D3D12_CPU_DESCRIPTOR_HANDLE depthHandle { 0 };
    D3D12_CPU_DESCRIPTOR_HANDLE* depthPtr = nullptr;
    if (depthDsv.IsValid()) {
        ZrD3D12ViewObj* dsv = m_owner->ResolveView(depthDsv);
        assert(dsv != nullptr && dsv->desc.viewType == ZrImageViewType::Dsv);
        if (dsv != nullptr && dsv->desc.viewType == ZrImageViewType::Dsv) {
            depthHandle = dsv->cpu;
            depthPtr = &depthHandle;
        }
    }
    if (colorRtv.IsValid()) {
        m_list->OMSetRenderTargets(1u, &rtv->cpu, FALSE, depthPtr);
    } else {
        m_list->OMSetRenderTargets(0u, nullptr, FALSE, depthPtr);
    }
}

void ZrD3D12CommandList::SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view)
{
    // TASK-B-R5-02：根 SRV 描述符表绑定（PSO 须以 srvTable=true 创建）——
    // 解析视图的全局 bindless 堆槽位 → SetGraphicsRootDescriptorTable 指向该槽
    // （≈ Vulkan 绑定采样图描述符集；堆本身已在 Begin() 设为 shader-visible）
    ZrD3D12ViewObj* obj = m_owner->ResolveView(view);
    assert(obj != nullptr && obj->bindlessSlot != ZR_SLOT_INVALID);
    if (obj == nullptr || obj->bindlessSlot == ZR_SLOT_INVALID) {
        return;
    }
    m_list->SetGraphicsRootDescriptorTable(rootSlot, m_owner->BindlessGpu(obj->bindlessSlot));
}

void ZrD3D12CommandList::SetViewportScissor(uint32_t width, uint32_t height)
{
    // → vkCmdSetViewport + vkCmdSetScissor：左上 (0,0) 全区域，深度 0..1
    assert(width > 0u && height > 0u);
    if (width == 0u || height == 0u) {
        return;
    }
    const D3D12_VIEWPORT viewport = { 0.0f, 0.0f, static_cast<FLOAT>(width),
        static_cast<FLOAT>(height), 0.0f, 1.0f };
    const D3D12_RECT scissor = { 0, 0, static_cast<LONG>(width), static_cast<LONG>(height) };
    m_list->RSSetViewports(1u, &viewport);
    m_list->RSSetScissorRects(1u, &scissor);
}
