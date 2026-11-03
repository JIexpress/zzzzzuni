// =============================================================================
// rg_mock_device.h —— RenderGraph 单测用 Mock 设备/命令表（TASK-B-R3-01）
//
// 实现 IZrDevice/IZrCommandList 最小子集：不依赖 D3D12，句柄为自增占位值；
// MockCommandList 记录全部调用序列（屏障批量调用列表/顺序），供测试断言。
// TASK-B-R3-02 增补：别名堆/placed image/资源统计 Mock + split 屏障半边记录
//（执行器 ExecuteAliased 设备无关，本 Mock 即其单测后端）。
// =============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "zrhi/zrhi_device.h"

/// 一次批量屏障调用的快照
struct MockBarrierCall {
    std::vector<ZrImageBarrier> images;
    std::vector<ZrBufferBarrier> buffers;
};

/// 一次 split 屏障半边提交的快照（TASK-B-R3-02 执行器单测）
struct MockSplitCall {
    std::vector<ZrImageBarrier> images;
    bool beginOnly = false;
};

/// 别名堆/placed image 的 Mock 记录（TASK-B-R3-02）
struct MockPlacedImage {
    ZrImageDesc desc;
    ZrAliasHeap heap;
    uint64_t offset = 0;
};

class MockCommandList : public IZrCommandList {
public:
    void Begin() override { ++beginCount; calls.push_back("Begin"); }
    void End() override { ++endCount; calls.push_back("End"); }

    void ResourceBarrier(const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
        const ZrBufferBarrier* bufferBarriers, uint32_t bufferBarrierCount) override
    {
        MockBarrierCall call;
        for (uint32_t i = 0; i < imageBarrierCount; ++i) {
            call.images.push_back(imageBarriers[i]);
        }
        for (uint32_t i = 0; i < bufferBarrierCount; ++i) {
            call.buffers.push_back(bufferBarriers[i]);
        }
        barrierCalls.push_back(call);
        calls.push_back("ResourceBarrier");
    }

    void ResourceBarrierSplit(
        const ZrImageBarrier* imageBarriers, uint32_t count, bool beginOnly) override
    {
        MockSplitCall call;
        call.beginOnly = beginOnly;
        for (uint32_t i = 0; i < count; ++i) {
            call.images.push_back(imageBarriers[i]);
        }
        splitCalls.push_back(call);
        calls.push_back(beginOnly ? "ResourceBarrierSplitBegin" : "ResourceBarrierSplitEnd");
    }

    void CopyBuffer(ZrBuffer, ZrBuffer, uint64_t, uint64_t, uint64_t) override
    {
        calls.push_back("CopyBuffer");
    }

    void ClearColorImage(ZrImage, ZrImageLayout, const float[4],
        const ZrSubresourceRange&) override
    {
        calls.push_back("ClearColorImage");
    }

    void BeginPass(const ZrPassDesc&) override { calls.push_back("BeginPass"); }
    void EndPass() override { calls.push_back("EndPass"); }
    void WriteTimestamp(ZrQueryPool, uint32_t) override
    {
        calls.push_back("WriteTimestamp");
    }

    // ---- TASK-B-R4-03：图形绘制命令记录（供命令序列断言）-----------------------
    void SetPipelineState(ZrPipelineState pso) override
    {
        boundPso = pso;
        calls.push_back("SetPipelineState");
    }
    void SetVertexBuffers(const ZrBuffer* buffers, uint32_t count) override
    {
        MockVertexBufferCall call;
        for (uint32_t i = 0; i < count; ++i) {
            call.buffers.push_back(buffers[i]);
        }
        vertexBufferCalls.push_back(call);
        calls.push_back("SetVertexBuffers");
    }
    void SetIndexBuffer(ZrBuffer buffer) override
    {
        boundIndexBuffer = buffer;
        calls.push_back("SetIndexBuffer");
    }
    void SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer) override
    {
        constantBufferCalls.push_back(MockConstantBufferCall { slot, buffer });
        calls.push_back("SetGraphicsConstantBuffer");
    }
    void SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view) override
    {
        bindlessSrvRootSlots.push_back(rootSlot);
        bindlessSrvViews.push_back(view);
        calls.push_back("SetGraphicsBindlessSrv");
    }
    void DrawIndexedInstanced(uint32_t indexCountPerInstance, uint32_t instanceCount,
        uint32_t startIndexLocation, int32_t baseVertexLocation,
        uint32_t startInstanceLocation) override
    {
        MockDrawCall call;
        call.indexCountPerInstance = indexCountPerInstance;
        call.instanceCount = instanceCount;
        call.startIndexLocation = startIndexLocation;
        call.baseVertexLocation = baseVertexLocation;
        call.startInstanceLocation = startInstanceLocation;
        drawCalls.push_back(call);
        calls.push_back("DrawIndexedInstanced");
    }
    void SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv) override
    {
        boundColorRtv = colorRtv;
        boundDepthDsv = depthDsv;
        calls.push_back("SetRenderTarget");
    }
    void SetViewportScissor(uint32_t width, uint32_t height) override
    {
        viewportWidth = width;
        viewportHeight = height;
        calls.push_back("SetViewportScissor");
    }

    uint32_t beginCount = 0;
    uint32_t endCount = 0;
    std::vector<MockBarrierCall> barrierCalls; // 屏障批量调用序列（按调用顺序）
    std::vector<MockSplitCall> splitCalls;     // split 半边提交序列（按调用顺序）
    std::vector<std::string> calls;            // 全部命令调用序列

    // TASK-B-R4-03：绘制命令结构化记录
    ZrPipelineState boundPso {};
    ZrImageView boundColorRtv {};
    ZrImageView boundDepthDsv {};
    ZrBuffer boundIndexBuffer {};
    uint32_t viewportWidth = 0;
    uint32_t viewportHeight = 0;
    struct MockVertexBufferCall {
        std::vector<ZrBuffer> buffers;
    };
    struct MockConstantBufferCall {
        uint32_t slot;
        ZrBuffer buffer;
    };
    struct MockDrawCall {
        uint32_t indexCountPerInstance = 0;
        uint32_t instanceCount = 0;
        uint32_t startIndexLocation = 0;
        int32_t baseVertexLocation = 0;
        uint32_t startInstanceLocation = 0;
    };
    std::vector<MockVertexBufferCall> vertexBufferCalls;
    std::vector<MockConstantBufferCall> constantBufferCalls;
    std::vector<MockDrawCall> drawCalls;
    // TASK-B-R5-02：根 SRV 描述符表绑定（rootSlot + 视图）
    std::vector<uint32_t> bindlessSrvRootSlots;
    std::vector<ZrImageView> bindlessSrvViews;
};

class MockDevice : public IZrDevice {
public:
    ZrBuffer CreateBuffer(const ZrBufferDesc&, const void* = nullptr) override
    {
        ++aliveBuffers;
        return ZrBuffer::FromRaw(++m_nextHandle);
    }
    ZrImage CreateImage(const ZrImageDesc&) override
    {
        ++aliveImages;
        return ZrImage::FromRaw(++m_nextHandle);
    }
    ZrImageView CreateImageView(const ZrImageViewDesc&) override
    {
        return ZrImageView::FromRaw(++m_nextHandle);
    }
    ZrSampler CreateSampler(const ZrSamplerDesc&) override
    {
        return ZrSampler::FromRaw(++m_nextHandle);
    }
    ZrShader CreateShader(const ZrShaderDesc& desc) override
    {
        // 契约对齐真后端：code/size 缺失拒绝（TASK-B-R4-03）
        if (desc.code == nullptr || desc.codeSize == 0u) {
            return ZrShader {};
        }
        return ZrShader::FromRaw(++m_nextHandle);
    }
    ZrPipelineState CreatePipelineState(const ZrGraphicsPsoDesc& desc) override
    {
        // 契约对齐真后端：ZrValidateGraphicsPsoDesc 拒绝非法描述（缺 shader 等）
        if (!ZrValidateGraphicsPsoDesc(desc)) {
            return ZrPipelineState {};
        }
        return ZrPipelineState::FromRaw(++m_nextHandle);
    }
    ZrSwapchain CreateSwapchain(const ZrSwapchainDesc&) override
    {
        return ZrSwapchain::FromRaw(++m_nextHandle);
    }
    ZrCommandList CreateCommandList(ZrQueueType) override
    {
        return ZrCommandList::FromRaw(++m_nextHandle);
    }
    ZrQueryPool CreateTimestampQueryHeap(uint32_t) override
    {
        return ZrQueryPool::FromRaw(++m_nextHandle);
    }

    IZrCommandList* GetCommandList(ZrCommandList) override { return &commandList; }
    IZrSwapchain* GetSwapchain(ZrSwapchain) override { return nullptr; }
    IZrBuffer* GetBuffer(ZrBuffer) override { return nullptr; }
    uint32_t GetBindlessIndex(ZrImageView) override { return ZR_BINDLESS_INVALID; }
    uint32_t ResolveTimestamps(ZrQueryPool, double*, uint32_t) override { return 0u; }

    void Destroy(ZrBuffer) override
    {
        if (aliveBuffers > 0u) {
            --aliveBuffers;
        }
    }
    void Destroy(ZrImage) override
    {
        if (aliveImages > 0u) {
            --aliveImages;
        }
    }
    void Destroy(ZrImageView) override {}
    void Destroy(ZrSampler) override {}
    void Destroy(ZrShader) override {}
    void Destroy(ZrPipelineState) override {}
    void Destroy(ZrSwapchain) override {}
    void Destroy(ZrCommandList) override {}
    void Destroy(ZrQueryPool) override {}

    IZrQueue* GetQueue(ZrQueueType) override { return nullptr; }
    void WaitIdle() override {}
    uint32_t GetDebugErrorCount() override { return 0u; }

    // ---- TASK-B-R3-02：别名堆 + placed image + 统计（Mock 语义）----------------
    ZrAliasHeap CreateAliasHeap(uint64_t sizeBytes) override
    {
        ++aliasHeapsCreated;
        ++aliveAliasHeaps;
        lastHeapSize = sizeBytes;
        return ZrAliasHeap::FromRaw(++m_nextHandle);
    }
    uint64_t GetImagePlacementSize(const ZrImageDesc& desc) override
    {
        // 4B/px 估算（与断言无关紧要，只要 > 0 且随尺寸单调）
        return uint64_t(desc.width) * uint64_t(desc.height) * 4ull;
    }
    ZrImage CreateImageInHeap(const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset) override
    {
        MockPlacedImage record;
        record.desc = desc;
        record.heap = heap;
        record.offset = offset;
        placedImages.push_back(record);
        ++aliveImages;
        return ZrImage::FromRaw(++m_nextHandle);
    }
    ZrDeviceStats GetDeviceStats() override
    {
        ZrDeviceStats stats;
        stats.aliasHeapCount = aliveAliasHeaps;
        stats.imageCount = aliveImages;
        stats.bufferCount = aliveBuffers;
        return stats;
    }
    void Destroy(ZrAliasHeap) override
    {
        if (aliveAliasHeaps > 0u) {
            --aliveAliasHeaps;
        }
    }

    uint32_t aliasHeapsCreated = 0;   // 累计建堆数
    uint32_t aliveAliasHeaps = 0;     // 存活堆数
    uint32_t aliveImages = 0;         // 存活图像数（Create/CreateInHeap 增、Destroy 减）
    uint32_t aliveBuffers = 0;        // 存活缓冲数
    uint64_t lastHeapSize = 0;        // 最近一次建堆请求尺寸
    std::vector<MockPlacedImage> placedImages; // placed image 落位记录（堆/偏移/描述）

    MockCommandList commandList; // 单命令表（GetCommandList 恒返回它）

private:
    uint32_t m_nextHandle = 0; // 自增占位句柄（raw 1..，全部形式有效）
};
