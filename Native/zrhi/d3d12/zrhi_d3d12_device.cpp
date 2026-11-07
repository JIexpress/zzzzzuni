// zrhi_d3d12_device.cpp —— D3D12 后端设备实现（TASK-B-R2-02）
//
// Vulkan 语义对应（Vulkan → 本实现）：
//   ZrD3D12Device::Create  ≈ vkCreateDevice（DEBUG layer ≈ validation layer）
//   CreateSwapchain        ≈ vkCreateSwapchainKHR（CreateSwapChainForHwnd，FLIP 3 缓冲）
//   WaitIdle               ≈ vkDeviceWaitIdle（fence Signal + SetEventOnCompletion 等待）
//   SubmitCommandList      ≈ vkQueueSubmit（ExecuteCommandLists + 帧槽 fence Signal）
//   GetDebugErrorCount     ≈ 验证层错误消息计数（InfoQueue Error 级以上，读取即排空）
#include "zrhi/d3d12/zrhi_d3d12_device.h"

#include "zrhi/d3d12/zrhi_d3d12_commandlist.h"
#include "zrhi/d3d12/zrhi_d3d12_translate.h"

#include <d3dcompiler.h> // TASK-B-R4-03：运行时 D3DCompile（链接 d3dcompiler.lib）

#include <cassert>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {
// ZrImageDesc → D3D12_RESOURCE_DESC（TASK-B-R3-02 提取；usage 位 → 资源 flags）
D3D12_RESOURCE_DESC BuildImageResourceDesc(const ZrImageDesc& desc)
{
    D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE;
    if (ZrHasAll(desc.usage, ZrImageUsage::ColorAttachment)) {
        flags |= D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    }
    if (ZrHasAll(desc.usage, ZrImageUsage::DepthStencilAttachment)) {
        flags |= D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
    }
    if (ZrHasAll(desc.usage, ZrImageUsage::Storage)) {
        flags |= D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    }
    D3D12_RESOURCE_DESC resourceDesc {};
    resourceDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    resourceDesc.Width = desc.width;
    resourceDesc.Height = desc.height;
    resourceDesc.DepthOrArraySize = static_cast<UINT16>(desc.arrayLayers);
    resourceDesc.MipLevels = static_cast<UINT16>(desc.mipLevels);
    resourceDesc.Format = ZrD3D12_Format(desc.format);
    resourceDesc.SampleDesc.Count = 1u;
    resourceDesc.Flags = flags;
    return resourceDesc;
}
} // namespace

// =============================================================================
// 创建入口
// =============================================================================
ZrD3D12Device* ZrD3D12CreateDevice() { return ZrD3D12Device::Create(); }

ZrD3D12Device* ZrD3D12CreateDeviceFrom(ID3D12Device* external)
{
    return ZrD3D12Device::CreateFrom(external);
}

void ZrD3D12DestroyDevice(ZrD3D12Device* device) { delete device; }

ZrD3D12Device* ZrD3D12Device::Create()
{
    // ---- DEBUG layer（可用则开启；≈ Vulkan validation layer）----------------------
    bool debugLayer = false;
    Microsoft::WRL::ComPtr<ID3D12Debug1> debug1;
    Microsoft::WRL::ComPtr<ID3D12Debug> debug0;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug1)))) {
        debug1->EnableDebugLayer(); // GPU Based Validation 不开（基座性能优先）
        debugLayer = true;
    } else if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug0)))) {
        debug0->EnableDebugLayer();
        debugLayer = true;
    }

    std::unique_ptr<ZrD3D12Device> d(new ZrD3D12Device());
    d->m_debugLayerEnabled = debugLayer;

    // ---- DXGI 工厂（调试层开启时附 DEBUG 标志）------------------------------------
    const UINT factoryFlags = debugLayer ? DXGI_CREATE_FACTORY_DEBUG : 0u;
    if (FAILED(CreateDXGIFactory2(factoryFlags, IID_PPV_ARGS(&d->m_factory)))) {
        return nullptr;
    }

    // ---- 适配器：首个非软件适配器（找不到则用默认适配器）------------------------------
    Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0; i < 8u; ++i) {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> candidate;
        if (d->m_factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        DXGI_ADAPTER_DESC1 adapterDesc {};
        candidate->GetDesc1(&adapterDesc);
        if ((adapterDesc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u) {
            adapter = candidate;
            break;
        }
    }

    // ---- 设备（12.0 → 11.1 → 11.0 逐级回退）----------------------------------------
    HRESULT hr = E_FAIL;
    const D3D_FEATURE_LEVEL levels[]
        = {D3D_FEATURE_LEVEL_12_0, D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    for (D3D_FEATURE_LEVEL level : levels) {
        hr = D3D12CreateDevice(adapter.Get(), level, IID_PPV_ARGS(&d->m_device));
        if (SUCCEEDED(hr)) {
            break;
        }
    }
    if (FAILED(hr)) {
        return nullptr;
    }

    // InfoQueue / 队列 / fence / 帧槽环 / 描述符堆 / 队列包装：与 CreateFrom 共用
    if (!d->InitializeRuntime(debugLayer)) {
        return nullptr;
    }
    return d.release();
}

// TASK-B-UNITY-01：包装外部设备（Unity 的 ID3D12Device）。
// 只做"设备已存在"之后的接线；创建期的所有选择（适配器、feature level、
// 调试层、DRED）都不在本库控制范围内 —— 见头文件 CreateFrom 注释。
ZrD3D12Device* ZrD3D12Device::CreateFrom(ID3D12Device* external)
{
    if (external == nullptr) {
        return nullptr;
    }
    std::unique_ptr<ZrD3D12Device> d(new ZrD3D12Device());
    d->m_device = external; // ComPtr::operator=(T*) 会 AddRef：宿主引用不受影响
    d->m_ownsDevice = false;

    // ---- DXGI 工厂：从设备反查（不新建工厂、不枚举适配器）--------------------------
    // 外部设备可能来自任意适配器；用 IDXGIDevice::GetAdapter 取其真实适配器，再取
    // 该适配器的父工厂，保证"交换链用的工厂"与"设备所属适配器"一致。
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgiDevice;
    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    Microsoft::WRL::ComPtr<IDXGIFactory4> factory;
    if (SUCCEEDED(d->m_device.As(&dxgiDevice)) && SUCCEEDED(dxgiDevice->GetAdapter(&adapter))
        && SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory)))) {
        d->m_factory = factory;
    }
    // 反查失败不致命：交换链路径会因 m_factory 为空而返回无效句柄，其余功能可用

    if (!d->InitializeRuntime(/*configureDebugSettings=*/false)) {
        return nullptr;
    }
    return d.release();
}

// Create / CreateFrom 共用的设备就绪初始化。与改造前 Create() 的尾段逐行等价
// （顺序、失败返回 nullptr 的语义均未变）。
bool ZrD3D12Device::InitializeRuntime(bool configureDebugSettings)
{
    // ---- InfoQueue：Error 级以上捕获（调试层可用时）----------------------------------
    // QI 成功 ⇔ 本进程的 D3D12 调试层已开启（Create 路径由本库开启、CreateFrom 路径
    // 由宿主开启）。仅 Create 路径改写 break 策略 —— 进程级调试策略属于宿主。
    if (SUCCEEDED(m_device.As(&m_infoQueue))) {
        m_debugLayerEnabled = true;
        if (configureDebugSettings) {
            m_infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_CORRUPTION, FALSE);
            m_infoQueue->SetBreakOnSeverity(D3D12_MESSAGE_SEVERITY_ERROR, FALSE);
        }
    } else {
        m_infoQueue.Reset();
    }

    // ---- DIRECT 命令队列（三域共用；异步队列后续任务）----------------------------------
    D3D12_COMMAND_QUEUE_DESC queueDesc {};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    if (FAILED(m_device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&m_queue)))) {
        return false;
    }

    // ---- WaitIdle fence + 共享等待事件 --------------------------------------------------
    if (FAILED(m_device->CreateFence(
            0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_waitFence)))) {
        return false;
    }
    m_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (m_fenceEvent == nullptr) {
        return false;
    }

    // ---- 帧槽环（分配器 + 提交完成 fence）-----------------------------------------------
    for (ZrD3D12FrameSlot& slot : m_frameSlots) {
        if (FAILED(m_device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator)))) {
            return false;
        }
        if (FAILED(m_device->CreateFence(
                0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&slot.fence)))) {
            return false;
        }
    }

    // ---- CreateBuffer(initData) 一次性上传链路（UPLOAD→DEFAULT→fence）-------------
    if (FAILED(m_device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&m_uploadAllocator)))
        || FAILED(m_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
               m_uploadAllocator.Get(), nullptr, IID_PPV_ARGS(&m_uploadList)))
        || FAILED(m_device->CreateFence(
               0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_uploadFence)))) {
        return false;
    }
    m_uploadList->Close(); // 创建即记录态：空记录关闭复位（用前 Reset）

    // ---- 设备级描述符堆（RTV/DSV/Sampler 非 shader 可见；bindless shader 可见）------
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc {};
    heapDesc.NumDescriptors = kDescriptorCapacity;
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    if (FAILED(m_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_rtvHeap)))) {
        return false;
    }
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
    if (FAILED(m_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_dsvHeap)))) {
        return false;
    }
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER;
    if (FAILED(m_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_samplerHeap)))) {
        return false;
    }
    // 全局 bindless 堆（CBV_SRV_UAV，shader 可见）：容量预分配 + 断言上限——
    // shader 可见堆不可按需增长重建（每帧绑定的堆基址须稳定），按需增长策略
    // （多堆 + 重绑）留待后续任务
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    heapDesc.NumDescriptors = kBindlessCapacity;
    heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(m_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&m_bindlessHeap)))) {
        return false;
    }
    m_rtvStride = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    m_dsvStride = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
    m_samplerStride = m_device->GetDescriptorHandleIncrementSize(
        D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER);
    m_bindlessStride = m_device->GetDescriptorHandleIncrementSize(
        D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

    // ---- 队列包装（Graphics/Compute/Transfer；基座共用 DIRECT 队列）---------------------
    for (uint32_t i = 0; i < 3u; ++i) {
        m_queues[i].reset(new ZrD3D12Queue(this, static_cast<ZrQueueType>(i)));
    }

    // ---- TASK-B-PSO-01：PSO 磁盘缓存（能力探测 + 加载；任何失败只是禁用缓存）---------
    // 放在设备全部就绪之后：库反序列化依赖设备；失败不影响 InitializeRuntime 的
    // 返回值（缓存是纯优化，兜底是直接创建）。
    m_psoCache.reset(new ZrD3D12PsoCache());
    m_psoCache->Initialize(m_device.Get());
    return true;
}

ZrPsoCacheStats ZrD3D12Device::GetPsoCacheStats() const
{
    return m_psoCache != nullptr ? m_psoCache->Stats() : ZrPsoCacheStats {};
}

void ZrD3D12Device::FlushPsoCache()
{
    if (m_psoCache != nullptr) {
        m_psoCache->Flush();
    }
}

ZrD3D12Device::~ZrD3D12Device()
{
    // TASK-B-PSO-01：先把本进程新建的 PSO 条目序列化落盘（原子替换），再走资源断言。
    // 缓存刷写失败不影响销毁流程（Flush 内部吞掉所有错误）。
    if (m_psoCache != nullptr) {
        m_psoCache->Flush();
    }
    // 调用方须先 WaitIdle 并 Destroy 全部资源（≈ Vulkan 设备销毁前资源归还次序）
    assert(m_imageTable.AliveCount() == 0u && m_bufferTable.AliveCount() == 0u
        && m_imageViewTable.AliveCount() == 0u && m_samplerTable.AliveCount() == 0u
        && m_queryPoolTable.AliveCount() == 0u && m_commandListTable.AliveCount() == 0u
        && m_swapchainTable.AliveCount() == 0u && m_aliasHeapTable.AliveCount() == 0u
        && m_shaderTable.AliveCount() == 0u && m_psoTable.AliveCount() == 0u);
    if (m_fenceEvent != nullptr) {
        CloseHandle(m_fenceEvent);
        m_fenceEvent = nullptr;
    }
}

// =============================================================================
// 创建：缓冲 / 图像
// =============================================================================
ZrBuffer ZrD3D12Device::CreateBuffer(const ZrBufferDesc& desc, const void* initData)
{
    if (desc.size == 0 || (desc.hostVisible && desc.readback)) {
        return ZrBuffer{};
    }
    // → vkCreateBuffer + 内存域选择（desc.hostVisible/readback → 堆类型）
    ZrD3D12BufferObj* obj = new ZrD3D12BufferObj();
    obj->desc = desc;

    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = desc.readback
        ? D3D12_HEAP_TYPE_READBACK
        : (desc.hostVisible ? D3D12_HEAP_TYPE_UPLOAD : D3D12_HEAP_TYPE_DEFAULT);
    D3D12_RESOURCE_DESC resourceDesc {};
    resourceDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    resourceDesc.Width = desc.size;
    resourceDesc.Height = 1u;
    resourceDesc.DepthOrArraySize = 1u;
    resourceDesc.MipLevels = 1u;
    resourceDesc.SampleDesc.Count = 1u;
    resourceDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    // READBACK 须以 COPY_DEST 创建（此后恒处该态）；UPLOAD/DEFAULT 以 GENERIC_READ
    // （UPLOAD 的合法全集；DEFAULT 缓冲经拷贝中转，队列边界自动衰减回 COMMON）
    const D3D12_RESOURCE_STATES initialState
        = desc.readback ? D3D12_RESOURCE_STATE_COPY_DEST : D3D12_RESOURCE_STATE_GENERIC_READ;
    if (FAILED(m_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &resourceDesc,
            initialState, nullptr, IID_PPV_ARGS(&obj->resource)))) {
        delete obj;
        return ZrBuffer{};
    }

    // ---- 初始数据上传：DEFAULT 堆经 UPLOAD 中转 + CopyBufferRegion + fence 等待 ----
    // （≈ Vulkan 的 staging buffer + vkCmdCopyBuffer + vkWaitForFences）
    if (initData != nullptr && !desc.hostVisible && !desc.readback) {
        Microsoft::WRL::ComPtr<ID3D12Resource> upload;
        D3D12_HEAP_PROPERTIES uploadHeap {};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
        if (FAILED(m_device->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE,
                &resourceDesc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                IID_PPV_ARGS(&upload)))) {
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        void* mapped = nullptr;
        if (FAILED(upload->Map(0, nullptr, &mapped)) || mapped == nullptr) {
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        std::memcpy(mapped, initData, static_cast<size_t>(desc.size));
        upload->Unmap(0, nullptr);

        m_uploadAllocator->Reset();
        if (FAILED(m_uploadList->Reset(m_uploadAllocator.Get(), nullptr))) {
            Destroy(m_bufferTable.Acquire(obj));
            return ZrBuffer{};
        }
        m_uploadList->CopyBufferRegion(
            obj->resource.Get(), 0, upload.Get(), 0, desc.size); // UPLOAD → DEFAULT
        m_uploadList->Close();
        ID3D12CommandList* lists[] = {m_uploadList.Get()};
        m_queue->ExecuteCommandLists(1, lists);
        m_uploadSignaled += 1u;
        m_queue->Signal(m_uploadFence.Get(), m_uploadSignaled); // fence 等待：上传完成后
        WaitForFenceValue(m_uploadFence.Get(), m_uploadSignaled); // 才允许调用方使用
    }
    return m_bufferTable.Acquire(obj);
}

ZrImage ZrD3D12Device::CreateImage(const ZrImageDesc& desc)
{
    assert(desc.width > 0u && desc.height > 0u && desc.mipLevels > 0u && desc.arrayLayers > 0u);
    assert(desc.samples == ZrSampleCount::One); // 多重采样后续任务支持
    if (desc.width == 0u || desc.height == 0u || desc.mipLevels == 0u || desc.arrayLayers == 0u
        || desc.samples != ZrSampleCount::One) {
        return ZrImage{};
    }

    ZrD3D12ImageObj* obj = new ZrD3D12ImageObj();
    obj->desc = desc;

    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    // initialLayout 仅语义记录：D3D12 提交资源恒以 COMMON 创建，首次屏障转入使用态
    const D3D12_RESOURCE_DESC resourceDesc = BuildImageResourceDesc(desc);
    if (FAILED(m_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE,
            &resourceDesc, D3D12_RESOURCE_STATE_COMMON, nullptr,
            IID_PPV_ARGS(&obj->resource)))) {
        delete obj;
        return ZrImage{};
    }
    return FinalizeImageCreation(obj, desc);
}

ZrImage ZrD3D12Device::CreateSharedImage(const ZrImageDesc& desc, void** outSharedHandle)
{
    if (outSharedHandle != nullptr) {
        *outSharedHandle = nullptr;
    }
    if (desc.width == 0u || desc.height == 0u || desc.mipLevels != 1u || desc.arrayLayers != 1u
        || desc.samples != ZrSampleCount::One) {
        // 共享交接只支持单 mip / 单层 / 单采样：Unity 的 CreateExternalTexture 亦然
        return ZrImage{};
    }

    ZrD3D12ImageObj* obj = new ZrD3D12ImageObj();
    obj->desc = desc;

    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    const D3D12_RESOURCE_DESC resourceDesc = BuildImageResourceDesc(desc);
    // SHARED 必须在**创建时**给出：事后 CreateSharedHandle 会返回 E_INVALIDARG
    // （堆不是共享堆），这正是"普通 CreateImage 无法跨 API 交接"的根因。
    if (FAILED(m_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED,
            &resourceDesc, D3D12_RESOURCE_STATE_COMMON, nullptr,
            IID_PPV_ARGS(&obj->resource)))) {
        delete obj;
        return ZrImage{};
    }

    // NT 句柄（D3D12 只接受 GENERIC_ALL）：由调用方 CloseHandle；与资源生命周期
    // 独立，资源 Release 后句柄仍有效（但那之后再 Open 就是悬空引用）。
    HANDLE shared = nullptr;
    if (outSharedHandle != nullptr) {
        if (FAILED(m_device->CreateSharedHandle(obj->resource.Get(), nullptr, GENERIC_ALL,
                nullptr, &shared))) {
            delete obj;
            return ZrImage{};
        }
        *outSharedHandle = shared;
    }
    return FinalizeImageCreation(obj, desc);
}

// =============================================================================
// TASK-B-R3-02：别名堆 + placed resource + 统计
//
// 语义对应（Vulkan → 本实现）：
//   - CreateAliasHeap      → vkAllocateMemory（D3D12：CreateHeap，DEFAULT 堆）
//   - GetImagePlacementSize→ vkGetImageMemoryRequirements（D3D12：
//                            GetResourceAllocationInfo——GetResourcePlacementInfo
//                            的等价对齐来源，SizeInBytes/Alignment 即落位要求）
//   - CreateImageInHeap    → vkCreateImage + vkBindImageMemory(heap, offset)
//                            （D3D12：CreatePlacedResource；初始态恒 COMMON）
//   - 每别名槽一堆模型：堆尺寸 = 槽内最大落位尺寸（64KB 向上取整），placed image
//     offset 恒 0——槽内串行复用（生命周期不重叠），天然无重叠
// =============================================================================
ZrAliasHeap ZrD3D12Device::CreateAliasHeap(uint64_t sizeBytes)
{
    assert(sizeBytes > 0u);
    if (sizeBytes == 0u) {
        return ZrAliasHeap{};
    }
    // D3D12 堆尺寸须 64KB 对齐（D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT）
    constexpr uint64_t k64KB = 64u * 1024u;
    const uint64_t alignedSize = (sizeBytes + k64KB - 1u) & ~(k64KB - 1u);

    D3D12_HEAP_DESC heapDesc {};
    heapDesc.SizeInBytes = alignedSize;
    heapDesc.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
    // 允许 RT/DS 纹理落位（别名复用目标多为颜色/深度临时贴图）：
    // D3D12_HEAP_FLAG_NONE = 不加任何 DENY 限制
    heapDesc.Flags = D3D12_HEAP_FLAG_NONE;

    ZrD3D12AliasHeapObj* obj = new ZrD3D12AliasHeapObj();
    obj->sizeBytes = alignedSize;
    if (FAILED(m_device->CreateHeap(&heapDesc, IID_PPV_ARGS(&obj->heap)))) {
        delete obj;
        return ZrAliasHeap{};
    }
    return m_aliasHeapTable.Acquire(obj);
}

uint64_t ZrD3D12Device::GetImagePlacementSize(const ZrImageDesc& desc)
{
    // → vkGetImageMemoryRequirements：落位尺寸与对齐由驱动给出（placed resource
    // 堆尺寸与偏移的权威来源；本模型 offset 恒 0，仅需尺寸）
    const D3D12_RESOURCE_DESC resourceDesc = BuildImageResourceDesc(desc);
    const D3D12_RESOURCE_ALLOCATION_INFO info
        = m_device->GetResourceAllocationInfo(0u, 1u, &resourceDesc);
    return info.SizeInBytes;
}

ZrImage ZrD3D12Device::CreateImageInHeap(const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset)
{
    assert(desc.width > 0u && desc.height > 0u && desc.mipLevels > 0u && desc.arrayLayers > 0u);
    assert(desc.samples == ZrSampleCount::One);
    void* payload = nullptr;
    if (desc.width == 0u || desc.height == 0u || desc.mipLevels == 0u || desc.arrayLayers == 0u
        || desc.samples != ZrSampleCount::One || !m_aliasHeapTable.TryGet(heap, payload)) {
        return ZrImage{};
    }
    ZrD3D12AliasHeapObj* heapObj = static_cast<ZrD3D12AliasHeapObj*>(payload);

    ZrD3D12ImageObj* obj = new ZrD3D12ImageObj();
    obj->desc = desc;
    // → vkCreateImage + vkBindImageMemory(heap, offset)：placed resource 与堆上
    // 其它 placed resource 真共享物理内存（别名复用的物理基础）。初始态恒 COMMON。
    // 对齐保证：offset=0 天然满足任意对齐；堆容量由调用方按
    // GetImagePlacementSize 的槽内最大值创建（每别名槽一堆模型，见类注）
    const D3D12_RESOURCE_DESC resourceDesc = BuildImageResourceDesc(desc);
    if (FAILED(m_device->CreatePlacedResource(heapObj->heap.Get(), offset,
            &resourceDesc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&obj->resource)))) {
        delete obj;
        return ZrImage{};
    }
    return FinalizeImageCreation(obj, desc);
}

ZrDeviceStats ZrD3D12Device::GetDeviceStats()
{
    ZrDeviceStats stats;
    stats.aliasHeapCount = m_aliasHeapTable.AliveCount();
    stats.imageCount = m_imageTable.AliveCount();
    stats.bufferCount = m_bufferTable.AliveCount();
    // TASK-H-02 D3：Present 失败状态（队列/交换链侧记录，此处上报）
    stats.presentFailureCount = m_presentStatus.failureCount;
    stats.lastPresentHresult = static_cast<int32_t>(m_presentStatus.lastFailureHresult);
    stats.deviceRemoved = m_presentStatus.deviceRemoved;
    // TASK-H-05 F1：描述符集分配失败计数是 Vulkan 命令表私有池的产物（D3D12 描述符
    // 堆走槽位分配器 ZR_SLOT_INVALID 路径，失败在 Create* 处即返回空句柄，无"池耗尽
    // 后静默跳过重绑"这一形态）→ 本后端恒 0，显式赋值而非依赖默认值，便于读者确认
    // 该字段在此后端"无来源"而不是"漏填"。
    stats.descriptorSetAllocFailureCount = 0u;
    return stats;
}

void ZrD3D12Device::Destroy(ZrAliasHeap handle)
{
    void* payload = nullptr;
    if (!m_aliasHeapTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12AliasHeapObj*>(payload);
    m_aliasHeapTable.Release(handle);
}

// 图像创建公共尾段（CreateImage / CreateImageInHeap 共用）：
// 格式能力探测 → RTV/UAV 清屏描述符 → 句柄表入册 → 初始布局落表
ZrImage ZrD3D12Device::FinalizeImageCreation(ZrD3D12ImageObj* obj, const ZrImageDesc& desc)
{
    const D3D12_RESOURCE_DESC resourceDesc = BuildImageResourceDesc(desc);

    // 清屏描述符（按格式能力：RTV 或 UAV 路径）。格式能力查询：
    // CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT)（D3D11 的 CheckFormatSupport
    // 在 D3D12 不存在，语义等价物即此）
    bool supportRtv = false;
    bool supportUav = false;
    D3D12_FEATURE_DATA_FORMAT_SUPPORT formatSupport {};
    formatSupport.Format = resourceDesc.Format;
    if (SUCCEEDED(m_device->CheckFeatureSupport(
            D3D12_FEATURE_FORMAT_SUPPORT, &formatSupport, sizeof(formatSupport)))) {
        supportRtv = (formatSupport.Support1 & D3D12_FORMAT_SUPPORT1_RENDER_TARGET) != 0u;
        supportUav = (formatSupport.Support1 & D3D12_FORMAT_SUPPORT1_TYPED_UNORDERED_ACCESS_VIEW)
            != 0u;
    }
    if (ZrHasAll(desc.usage, ZrImageUsage::ColorAttachment) && supportRtv) {
        // 槽位耗尽必须在此拦住：ZR_SLOT_INVALID 参与 slot*stride 会算出堆外地址，
        // CreateRenderTargetView 将直接写越界（Release 下 assert 不生效）。
        const uint32_t rtvSlot = m_rtvSlots.Allocate();
        if (rtvSlot == ZR_SLOT_INVALID) {
            assert(false && "FinalizeImageCreation: RTV descriptor heap exhausted");
            delete obj;
            return ZrImage{};
        }
        obj->rtv = RtvCpu(rtvSlot);
        obj->hasRtv = true;
        m_device->CreateRenderTargetView(obj->resource.Get(), nullptr, obj->rtv);
    }
    if (ZrHasAll(desc.usage, ZrImageUsage::Storage) && supportUav) {
        assert(desc.arrayLayers == 1u); // UAV 视图基座仅单层 2D
        const uint32_t uavSlot = m_bindlessSlots.Allocate(); // 全局 bindless 堆槽（GPU 句柄可派生）
        if (uavSlot == ZR_SLOT_INVALID) {
            assert(false && "FinalizeImageCreation: bindless descriptor heap exhausted");
            if (obj->hasRtv) {
                // 回滚已分配的 RTV 槽，避免创建失败时泄漏槽位
                m_rtvSlots.Free(static_cast<uint32_t>(
                    (obj->rtv.ptr - m_rtvHeap->GetCPUDescriptorHandleForHeapStart().ptr)
                    / m_rtvStride));
            }
            delete obj;
            return ZrImage{};
        }
        obj->uavSlot = uavSlot;
        obj->hasUav = true;
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc {};
        uavDesc.Format = resourceDesc.Format;
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        m_device->CreateUnorderedAccessView(
            obj->resource.Get(), nullptr, &uavDesc, BindlessCpu(obj->uavSlot));
    }
    // per-resource 状态表：记录初始布局（initialLayout；R3 屏障追踪的起点）
    const ZrImage handle = m_imageTable.Acquire(obj);
    NoteImageInitialLayout(handle, desc.initialLayout);
    return handle;
}

// =============================================================================
// 创建：图像视图 / 采样器 / 时间戳堆（TASK-B-R2-03）
// =============================================================================
ZrImageView ZrD3D12Device::CreateImageView(const ZrImageViewDesc& desc)
{
    ZrD3D12ImageObj* image = ResolveImage(desc.image);
    if (image == nullptr || image->resource == nullptr) {
        return ZrImageView{};
    }
    const ZrFormat viewFormat = desc.overrideFormat ? desc.format : image->desc.format;
    const DXGI_FORMAT dxgiFormat = ZrD3D12_Format(viewFormat);
    if (dxgiFormat == DXGI_FORMAT_UNKNOWN) {
        return ZrImageView{};
    }
    // mip 窗口（REMAINING 语义 → clamp 到资源实际范围）
    const uint32_t mipBase = desc.subresources.baseMipLevel < image->desc.mipLevels
        ? desc.subresources.baseMipLevel
        : image->desc.mipLevels;
    const uint32_t mipCount = (desc.subresources.levelCount == ZR_REMAINING_MIP_LEVELS)
        ? (image->desc.mipLevels - mipBase)
        : ((desc.subresources.baseMipLevel + desc.subresources.levelCount) < image->desc.mipLevels
                ? desc.subresources.levelCount
                : (image->desc.mipLevels - mipBase));

    ZrD3D12ViewObj* obj = new ZrD3D12ViewObj();
    obj->desc = desc;

    switch (desc.viewType) {
    case ZrImageViewType::Srv: {
        assert(ZrHasAll(image->desc.usage, ZrImageUsage::Sampled)); // TASK-B-R5-02 放宽：采样用途即可（含深度图）
        const uint32_t srvSlot = m_bindlessSlots.Allocate(); // 直接注册进全局 bindless 堆
        if (srvSlot == ZR_SLOT_INVALID) {
            assert(false && "CreateImageView(Srv): bindless descriptor heap exhausted");
            delete obj;
            return ZrImageView{};
        }
        obj->bindlessSlot = srvSlot;
        obj->cpu = BindlessCpu(obj->bindlessSlot);
        D3D12_SHADER_RESOURCE_VIEW_DESC srvDesc {};
        srvDesc.Format = dxgiFormat;
        srvDesc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        if (image->desc.arrayLayers > 1u) {
            // TASK-B-R5-02：数组图 → Texture2DArray 视图（层窗口取
            // subresources.baseArrayLayer/layerCount；REMAINING 语义与 mip 窗口一致）
            const uint32_t layerBase = desc.subresources.baseArrayLayer;
            const uint32_t layerCount
                = (desc.subresources.layerCount == ZR_REMAINING_ARRAY_LAYERS)
                ? (image->desc.arrayLayers - layerBase)
                : desc.subresources.layerCount;
            assert(layerBase < image->desc.arrayLayers && layerCount > 0u
                && layerBase + layerCount <= image->desc.arrayLayers);
            srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DARRAY;
            srvDesc.Texture2DArray.MostDetailedMip = mipBase;
            srvDesc.Texture2DArray.MipLevels = mipCount;
            srvDesc.Texture2DArray.FirstArraySlice = layerBase;
            srvDesc.Texture2DArray.ArraySize = layerCount;
            srvDesc.Texture2DArray.PlaneSlice = 0u;
        } else {
            srvDesc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srvDesc.Texture2D.MostDetailedMip = mipBase;
            srvDesc.Texture2D.MipLevels = mipCount;
        }
        m_device->CreateShaderResourceView(image->resource.Get(), &srvDesc, obj->cpu);
        break;
    }
    case ZrImageViewType::Uav: {
        assert(ZrHasAll(image->desc.usage, ZrImageUsage::Storage));
        const uint32_t uavSlot = m_bindlessSlots.Allocate();
        if (uavSlot == ZR_SLOT_INVALID) {
            assert(false && "CreateImageView(Uav): bindless descriptor heap exhausted");
            delete obj;
            return ZrImageView{};
        }
        obj->bindlessSlot = uavSlot;
        obj->cpu = BindlessCpu(obj->bindlessSlot);
        D3D12_UNORDERED_ACCESS_VIEW_DESC uavDesc {};
        uavDesc.Format = dxgiFormat;
        uavDesc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        uavDesc.Texture2D.MipSlice = mipBase;
        m_device->CreateUnorderedAccessView(image->resource.Get(), nullptr, &uavDesc, obj->cpu);
        break;
    }
    case ZrImageViewType::Rtv: {
        assert(ZrHasAll(image->desc.usage, ZrImageUsage::ColorAttachment));
        const uint32_t rtvSlot = m_rtvSlots.Allocate();
        if (rtvSlot == ZR_SLOT_INVALID) {
            assert(false && "CreateImageView(Rtv): RTV descriptor heap exhausted");
            delete obj;
            return ZrImageView{};
        }
        obj->cpu = RtvCpu(rtvSlot);
        D3D12_RENDER_TARGET_VIEW_DESC rtvDesc {};
        rtvDesc.Format = dxgiFormat;
        rtvDesc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
        rtvDesc.Texture2D.MipSlice = mipBase;
        m_device->CreateRenderTargetView(image->resource.Get(), &rtvDesc, obj->cpu);
        break;
    }
    case ZrImageViewType::Dsv: {
        assert(ZrHasAll(image->desc.usage, ZrImageUsage::DepthStencilAttachment));
        const uint32_t slot = m_dsvSlots.Allocate();
        if (slot == ZR_SLOT_INVALID) {
            assert(false && "CreateImageView(Dsv): DSV descriptor heap exhausted");
            delete obj;
            return ZrImageView{};
        }
        obj->cpu = m_dsvHeap->GetCPUDescriptorHandleForHeapStart();
        obj->cpu.ptr += static_cast<SIZE_T>(slot) * static_cast<SIZE_T>(m_dsvStride);
        D3D12_DEPTH_STENCIL_VIEW_DESC dsvDesc {};
        dsvDesc.Format = dxgiFormat;
        if (image->desc.arrayLayers > 1u) {
            // TASK-B-R5-02：数组图的层窗口 DSV（级联 c → 层 c；ArraySize 恒 1，
            // D3D12 逐层渲染经单层视图绑定）
            const uint32_t layerBase = desc.subresources.baseArrayLayer;
            assert(layerBase < image->desc.arrayLayers);
            dsvDesc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2DARRAY;
            dsvDesc.Texture2DArray.MipSlice = mipBase;
            dsvDesc.Texture2DArray.FirstArraySlice = layerBase;
            dsvDesc.Texture2DArray.ArraySize = 1u;
        } else {
            dsvDesc.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
            dsvDesc.Texture2D.MipSlice = mipBase;
        }
        m_device->CreateDepthStencilView(image->resource.Get(), &dsvDesc, obj->cpu);
        break;
    }
    default:
        assert(false && "CreateImageView: 非法 ZrImageViewType");
        delete obj;
        return ZrImageView{};
    }
    return m_imageViewTable.Acquire(obj);
}

ZrSampler ZrD3D12Device::CreateSampler(const ZrSamplerDesc& desc)
{
    // → vkCreateSampler（三字段过滤 → D3D12 打包 FILTER；寻址/LOD/各向异性映射）
    const uint32_t slot = m_samplerSlots.Allocate();
    if (slot == ZR_SLOT_INVALID) {
        assert(false && "CreateSampler: sampler descriptor heap exhausted");
        return ZrSampler{};
    }
    ZrD3D12SamplerObj* obj = new ZrD3D12SamplerObj();
    obj->desc = desc;
    obj->cpu = m_samplerHeap->GetCPUDescriptorHandleForHeapStart();
    obj->cpu.ptr += static_cast<SIZE_T>(slot) * static_cast<SIZE_T>(m_samplerStride);

    D3D12_SAMPLER_DESC samplerDesc {};
    samplerDesc.Filter = ZrD3D12_Filter(desc.magFilter, desc.minFilter, desc.mipFilter);
    samplerDesc.AddressU = ZrD3D12_AddressMode(desc.addressU);
    samplerDesc.AddressV = ZrD3D12_AddressMode(desc.addressV);
    samplerDesc.AddressW = ZrD3D12_AddressMode(desc.addressW);
    samplerDesc.MipLODBias = desc.mipLodBias;
    samplerDesc.MinLOD = desc.minLod;
    samplerDesc.MaxLOD = desc.maxLod;
    samplerDesc.MaxAnisotropy = desc.maxAnisotropy;
    samplerDesc.ComparisonFunc = D3D12_COMPARISON_FUNC_NONE;
    m_device->CreateSampler(&samplerDesc, obj->cpu);
    return m_samplerTable.Acquire(obj);
}

ZrQueryPool ZrD3D12Device::CreateTimestampQueryHeap(uint32_t queryCount)
{
    // → vkCreateQueryPool(VK_QUERY_TYPE_TIMESTAMP)
    if (queryCount == 0u) {
        return ZrQueryPool{};
    }
    ZrD3D12QueryPoolObj* obj = new ZrD3D12QueryPoolObj();
    obj->count = queryCount;

    D3D12_QUERY_HEAP_DESC heapDesc {};
    heapDesc.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
    heapDesc.Count = queryCount;
    if (FAILED(m_device->CreateQueryHeap(&heapDesc, IID_PPV_ARGS(&obj->heap)))) {
        delete obj;
        return ZrQueryPool{};
    }

    // Resolve 落点：READBACK 缓冲（n*8B）+ 专用一次性命令链路 + 完成 fence
    D3D12_HEAP_PROPERTIES readbackHeap {};
    readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
    D3D12_RESOURCE_DESC bufferDesc {};
    bufferDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bufferDesc.Width = static_cast<uint64_t>(queryCount) * 8u;
    bufferDesc.Height = 1u;
    bufferDesc.DepthOrArraySize = 1u;
    bufferDesc.MipLevels = 1u;
    bufferDesc.SampleDesc.Count = 1u;
    bufferDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    if (FAILED(m_device->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE,
            &bufferDesc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
            IID_PPV_ARGS(&obj->readback)))
        || FAILED(m_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
               IID_PPV_ARGS(&obj->resolveAllocator)))
        || FAILED(m_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
               obj->resolveAllocator.Get(), nullptr, IID_PPV_ARGS(&obj->resolveList)))
        || FAILED(m_device->CreateFence(
               0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&obj->resolveFence)))) {
        delete obj; // ComPtr 已创建的部分随析构释放
        return ZrQueryPool{};
    }
    obj->resolveList->Close(); // 空记录关闭复位（Resolve 前 Reset）
    return m_queryPoolTable.Acquire(obj);
}

uint32_t ZrD3D12Device::ResolveTimestamps(ZrQueryPool heap, double* outMilliseconds, uint32_t count)
{
    // → vkGetQueryPoolResults：GPU 完成等待 + Resolve 到 host 可读缓冲 + 频率换算
    ZrD3D12QueryPoolObj* obj = ResolveQueryPool(heap);
    if (obj == nullptr || outMilliseconds == nullptr || count == 0u) {
        return 0u;
    }
    const uint32_t n = count < obj->count ? count : obj->count;

    // 一次性命令链路：ResolveQueryData（堆 → READBACK 缓冲）
    obj->resolveAllocator->Reset();
    if (FAILED(obj->resolveList->Reset(obj->resolveAllocator.Get(), nullptr))) {
        return 0u;
    }
    obj->resolveList->ResolveQueryData(
        obj->heap.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0u, n, obj->readback.Get(), 0);
    obj->resolveList->Close();
    ID3D12CommandList* lists[] = {obj->resolveList.Get()};
    m_queue->ExecuteCommandLists(1, lists);
    obj->resolveSignaled += 1u;
    m_queue->Signal(obj->resolveFence.Get(), obj->resolveSignaled);
    WaitForFenceValue(obj->resolveFence.Get(), obj->resolveSignaled); // 完成后才可 Map

    UINT64 frequency = 0;
    if (FAILED(m_queue->GetTimestampFrequency(&frequency)) || frequency == 0u) {
        return 0u;
    }
    uint64_t* mapped = nullptr;
    if (FAILED(obj->readback->Map(0, nullptr, reinterpret_cast<void**>(&mapped)))
        || mapped == nullptr) {
        return 0u;
    }
    // GPU ticks → 毫秒（≈ Vulkan timestampPeriod 换算；绝对毫秒，间隔=两值之差）
    for (uint32_t i = 0; i < n; ++i) {
        outMilliseconds[i] = static_cast<double>(mapped[i]) * 1000.0
            / static_cast<double>(frequency);
    }
    obj->readback->Unmap(0, nullptr);
    return n;
}
// =============================================================================
// TASK-B-R4-03：着色器 / 图形 PSO / 运行时编译
// =============================================================================

// 根签名（TASK-B-R4-03 v1 固定布局；TASK-B-R5-01 增补 rootCbvCount 1..4 可变；
// TASK-B-R5-02 增补 srvTable：根 CBV 后追加 1 个 SRV 描述符表 + 静态采样器；
// TASK-B-R5-03 增补 rootCbvCount 上限 5（b4=GI 探针数组）+ srvTableCount 1/2
// （SSR 同时采样 Hi-Z 与场景颜色两张纹理））：
//   参数 i（i < rootCbvCount）：根 CBV b_i（root descriptor——
//           SetGraphicsRootConstantBufferView(slot=b_i) 直绑常量缓冲 GPU VA，
//           不经描述符堆；≈ Vulkan UNIFORM_BUFFER 的最直等价物）
//           可见性 ALL。v1 默认单槽 b0（first_draw.hlsl 的 CBufPerDraw 合一单槽）；
//           簇光照三槽：b0=逐绘常量 / b1=光源数组 / b2=簇数据（pbr_forward.hlsl）；
//           阴影四槽：b3=级联 lightVP+bias（pbr_shadow.hlsl）
//   参数 rootCbvCount + i（i < srv 表数）：SRV 描述符表 t_i space0（单描述符，
//           指向全局 bindless 堆内槽位；经 IZrCommandList::SetGraphicsBindlessSrv
//           绑定；≈ Vulkan 采样图描述符集绑定）
//   静态采样器 s_i（i < srv 表数）：Point/Clamp——CSM PCF 手动比较采样
//           （深度图 SRV 经 R32Float 解释，深度比较在 PS 手写而非 SampleCmp）
//   标志：ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT（PSO 带输入布局时必需）
// 序列化：D3D12SerializeRootSignature 1.0；DXC/SM6 升级时布局不变，升级到
//   versioned 1.1 走 D3D12SerializeVersionedRootSignature（消费方零改动）
static Microsoft::WRL::ComPtr<ID3D12RootSignature> CreateFirstDrawRootSignature(
    ID3D12Device* device, uint32_t cbvCount, uint32_t srvTableCount)
{
    // 上限 5 CBV + 2 描述符表（R5-03）；ranges/samplers 静态存储期——D3D12 序列化
    // 在本函数内同步完成，出栈前已拷贝
    D3D12_ROOT_PARAMETER params[7] = {};
    for (uint32_t i = 0; i < cbvCount; ++i) {
        params[i].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        params[i].Descriptor.ShaderRegister = i; // b0..b4
        params[i].Descriptor.RegisterSpace = 0u;
        params[i].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    }
    uint32_t paramCount = cbvCount;
    D3D12_DESCRIPTOR_RANGE srvRanges[2] = {};
    D3D12_STATIC_SAMPLER_DESC staticSamplers[2] = {};
    for (uint32_t t = 0; t < srvTableCount; ++t) {
        srvRanges[t].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRanges[t].NumDescriptors = 1u; // t_i space0：单张纹理（表 i 各自独立）
        srvRanges[t].BaseShaderRegister = t; // t0 / t1
        srvRanges[t].RegisterSpace = 0u;
        srvRanges[t].OffsetInDescriptorsFromTableStart = 0u;
        params[paramCount].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[paramCount].DescriptorTable.NumDescriptorRanges = 1u;
        params[paramCount].DescriptorTable.pDescriptorRanges = &srvRanges[t];
        params[paramCount].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        ++paramCount;

        staticSamplers[t].Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        staticSamplers[t].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[t].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[t].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        staticSamplers[t].MipLODBias = 0.0f;
        staticSamplers[t].MaxAnisotropy = 1u;
        staticSamplers[t].ComparisonFunc = D3D12_COMPARISON_FUNC_NONE;
        staticSamplers[t].BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_WHITE;
        staticSamplers[t].MinLOD = 0.0f;
        staticSamplers[t].MaxLOD = D3D12_FLOAT32_MAX;
        staticSamplers[t].ShaderRegister = t; // s0 / s1
        staticSamplers[t].RegisterSpace = 0u;
        staticSamplers[t].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    }

    D3D12_ROOT_SIGNATURE_DESC rsDesc {};
    rsDesc.NumParameters = paramCount;
    rsDesc.pParameters = params;
    rsDesc.NumStaticSamplers = srvTableCount;
    rsDesc.pStaticSamplers = srvTableCount > 0u ? staticSamplers : nullptr;
    rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

    Microsoft::WRL::ComPtr<ID3DBlob> serialized;
    Microsoft::WRL::ComPtr<ID3DBlob> error;
    if (FAILED(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1_0,
            &serialized, &error))) {
        if (error != nullptr) {
            std::fprintf(stderr, "[zrhi][d3d12] root signature serialize failed: %.*s\n",
                static_cast<int>(error->GetBufferSize()),
                static_cast<const char*>(error->GetBufferPointer()));
        }
        return nullptr;
    }
    Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
    if (FAILED(device->CreateRootSignature(0u, serialized->GetBufferPointer(),
            serialized->GetBufferSize(), IID_PPV_ARGS(&rootSignature)))) {
        return nullptr;
    }
    return rootSignature;
}

ZrShader ZrD3D12Device::CreateShader(const ZrShaderDesc& desc)
{
    // → vkCreateShaderModule：持有 DXBC 字节码拷贝（PSO 创建期被引用）
    if (desc.code == nullptr || desc.codeSize == 0u) {
        return ZrShader{};
    }
    ZrD3D12ShaderObj* obj = new ZrD3D12ShaderObj();
    obj->desc = desc;
    const uint8_t* bytes = static_cast<const uint8_t*>(desc.code);
    obj->bytecode.assign(bytes, bytes + desc.codeSize);
    return m_shaderTable.Acquire(obj);
}

ZrPipelineState ZrD3D12Device::CreatePipelineState(const ZrGraphicsPsoDesc& desc)
{
    // → vkCreateGraphicsPipelines + VkPipelineLayout：根签名（v1 固定布局）+
    // D3D12_GRAPHICS_PIPELINE_STATE_DESC（映射在 translate 纯函数，单一转译点）
    if (!ZrValidateGraphicsPsoDesc(desc)) {
        assert(false && "CreatePipelineState: ZrGraphicsPsoDesc 校验失败（缺 shader/"
                        "非法格式/深度状态矛盾）");
        return ZrPipelineState{};
    }
    ZrD3D12ShaderObj* vs = ResolveShader(desc.vertexShader);
    ZrD3D12ShaderObj* ps = nullptr;
    if (!desc.depthOnly) {
        ps = ResolveShader(desc.pixelShader); // depth-only 管线无 PS（校验已放行）
    }
    if (vs == nullptr || (!desc.depthOnly && ps == nullptr)) {
        return ZrPipelineState{}; // stale 句柄
    }
    assert(vs->desc.stage == ZrShaderStage::Vertex);
    assert(desc.depthOnly || ps->desc.stage == ZrShaderStage::Pixel);

    // 根 CBV 槽数（TASK-B-R5-01；R5-03 上限放宽到 5）：1..5；越界拒绝
    const uint32_t cbvCount = desc.rootCbvCount;
    if (cbvCount == 0u || cbvCount > 5u) {
        assert(false && "CreatePipelineState: rootCbvCount 须为 1..5");
        return ZrPipelineState{};
    }
    // SRV 描述符表数（TASK-B-R5-03）：srvTableCount 非 0 优先，否则回落 srvTable
    const uint32_t srvTableCount
        = desc.srvTableCount != 0u ? desc.srvTableCount : (desc.srvTable ? 1u : 0u);
    if (srvTableCount > 2u) {
        assert(false && "CreatePipelineState: srvTableCount 须为 0..2");
        return ZrPipelineState{};
    }

    ZrD3D12PsoObj* obj = new ZrD3D12PsoObj();
    obj->desc = desc;
    obj->rootSignature
        = CreateFirstDrawRootSignature(m_device.Get(), cbvCount, srvTableCount);
    if (obj->rootSignature == nullptr) {
        delete obj;
        return ZrPipelineState{};
    }

    D3D12_SHADER_BYTECODE vsBytecode { vs->bytecode.data(), vs->bytecode.size() };
    D3D12_SHADER_BYTECODE psBytecode = desc.depthOnly
        ? D3D12_SHADER_BYTECODE { nullptr, 0u } // depth-only：无 PS
        : D3D12_SHADER_BYTECODE { ps->bytecode.data(), ps->bytecode.size() };
    const D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc
        = ZrD3D12_GraphicsPsoDesc(desc, obj->rootSignature.Get(), vsBytecode, psBytecode);

    // TASK-B-PSO-01：优先 ID3D12PipelineLibrary（按 desc 稳定哈希命名；命中直接复用
    // 驱动缓存），库不可用/未命中/调用失败一律回退 ID3D12Device::CreateGraphicsPipelineState。
    // 缓存层内部保证不抛、不崩；这里只看最终 HRESULT。
    HRESULT psoHr = E_FAIL;
    if (m_psoCache != nullptr) {
        psoHr = m_psoCache->CreateGraphicsPipelineState(psoDesc, vs->bytecode.data(),
            vs->bytecode.size(), psBytecode.pShaderBytecode, psBytecode.BytecodeLength,
            cbvCount, srvTableCount, IID_PPV_ARGS(&obj->pipelineState));
    } else {
        psoHr = m_device->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&obj->pipelineState));
    }
    if (FAILED(psoHr)) {
        delete obj;
        return ZrPipelineState{};
    }
    return m_psoTable.Acquire(obj);
}

ZrShader ZrD3D12Device::CompileHlsl(
    ZrShaderStage stage, const char* source, size_t sourceBytes, const char* entryPoint)
{
    // 运行时着色器编译：D3DCompile（d3dcompiler_47；FXC → DXBC SM5.0）。
    // DXC/SM6 升级路径：替换为 dxc.exe 调用（/T vs_6_0|ps_6_0，-spirv 出
    // Vulkan 轨 SPIR-V），字节码契约（ZrShaderDesc.code/codeSize）不变。
    if (source == nullptr || sourceBytes == 0u || entryPoint == nullptr) {
        return ZrShader{};
    }
    const char* profile = nullptr;
    switch (stage) {
    case ZrShaderStage::Vertex: profile = "vs_5_0"; break;
    case ZrShaderStage::Pixel: profile = "ps_5_0"; break;
    default:
        assert(false && "CompileHlsl: 非法 ZrShaderStage（v1 仅 VS/PS）");
        return ZrShader{};
    }

    Microsoft::WRL::ComPtr<ID3DBlob> bytecode;
    Microsoft::WRL::ComPtr<ID3DBlob> errors;
    const HRESULT hr = D3DCompile(source, sourceBytes, nullptr, nullptr, nullptr, entryPoint,
        profile, D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0u, &bytecode,
        &errors);
    if (FAILED(hr) || bytecode == nullptr) {
        // 错误 HRESULT 带信息抛出：编译器错误文本（含源码行号）+ HRESULT 进 stderr
        std::fprintf(stderr, "[zrhi][d3d12] D3DCompile FAILED (hr=0x%08lX, profile=%s, "
                             "entry=%s)\n",
            static_cast<unsigned long>(hr), profile, entryPoint);
        if (errors != nullptr) {
            std::fprintf(stderr, "[zrhi][d3d12] compiler output:\n%.*s\n",
                static_cast<int>(errors->GetBufferSize()),
                static_cast<const char*>(errors->GetBufferPointer()));
        }
        return ZrShader{};
    }
    ZrShaderDesc desc {};
    desc.stage = stage;
    desc.code = bytecode->GetBufferPointer();
    desc.codeSize = bytecode->GetBufferSize();
    desc.entryPoint = entryPoint;
    return CreateShader(desc); // CreateShader 内部拷贝字节码，blob 出栈安全
}

// ---- ZrShader / ZrPipelineState 销毁（≈ vkDestroyShaderModule / vkDestroyPipeline）--
void ZrD3D12Device::Destroy(ZrShader handle)
{
    void* payload = nullptr;
    if (!m_shaderTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12ShaderObj*>(payload);
    m_shaderTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrPipelineState handle)
{
    void* payload = nullptr;
    if (!m_psoTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12PsoObj*>(payload); // ComPtr 释放 PSO+RootSignature
    m_psoTable.Release(handle);
}

// =============================================================================
// 创建：交换链（→ vkCreateSwapchainKHR）
// =============================================================================
ZrSwapchain ZrD3D12Device::CreateSwapchain(const ZrSwapchainDesc& desc)
{
    assert(desc.windowHandle != nullptr); // 无窗口场景请用离屏图像（--headless 路径）
    assert(desc.width > 0u && desc.height > 0u && desc.imageCount >= 2u);
    if (desc.windowHandle == nullptr || desc.width == 0u || desc.height == 0u
        || desc.imageCount < 2u) {
        return ZrSwapchain{};
    }
    // TASK-B-UNITY-01：CreateFrom 路径的工厂由外部设备反查得来，反查失败时为空
    // （宿主设备不来自 DXGI 适配器的极端情况）——此时交换链不可用，其余功能不受影响
    if (m_factory == nullptr) {
        assert(false && "CreateSwapchain: 无 DXGI 工厂（外部设备反查失败）");
        return ZrSwapchain{};
    }

    ZrD3D12Swapchain* sc = new ZrD3D12Swapchain(this);

    DXGI_SWAP_CHAIN_DESC1 swapDesc {};
    swapDesc.Width = desc.width;
    swapDesc.Height = desc.height;
    swapDesc.Format = ZrD3D12_Format(desc.format);
    swapDesc.Stereo = FALSE;
    swapDesc.SampleDesc.Count = 1u; // FLIP 模型不支持 MSAA 交换链
    swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapDesc.BufferCount = desc.imageCount; // 3 缓冲
    swapDesc.Scaling = DXGI_SCALING_STRETCH;
    swapDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; // FLIP 模型
    swapDesc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;

    // Immediate：允许撕裂需创建标志 + 适配器能力（不支持则降级普通 SyncInterval 0）
    bool tearingAllowed = false;
    if (desc.presentMode == ZrPresentMode::Immediate) {
        Microsoft::WRL::ComPtr<IDXGIFactory5> factory5;
        if (SUCCEEDED(m_factory.As(&factory5))) {
            BOOL allowTearing = FALSE;
            if (SUCCEEDED(factory5->CheckFeatureSupport(
                    DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allowTearing, sizeof(allowTearing)))
                && allowTearing == TRUE) {
                tearingAllowed = true;
                swapDesc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
            }
        }
    }

    Microsoft::WRL::ComPtr<IDXGISwapChain1> swapChain1;
    if (FAILED(m_factory->CreateSwapChainForHwnd(
            m_queue.Get(), static_cast<HWND>(desc.windowHandle), &swapDesc, nullptr, nullptr,
            &swapChain1))
        || FAILED(swapChain1.As(&sc->m_swapchain))) {
        delete sc;
        return ZrSwapchain{};
    }

    const ZrD3D12PresentParams presentParams = ZrD3D12_PresentParams(desc.presentMode);
    sc->m_presentMode = desc.presentMode;
    sc->m_syncInterval = presentParams.syncInterval;
    sc->m_presentFlags = (tearingAllowed || desc.presentMode != ZrPresentMode::Immediate)
        ? presentParams.presentFlags
        : 0u; // Immediate 但撕裂不支持 → 降级去掉撕裂标志

    sc->m_bufferCount = swapDesc.BufferCount;
    sc->m_backbufferImages.resize(swapDesc.BufferCount);
    sc->m_bufferFences.resize(swapDesc.BufferCount);

    // RTV 描述符堆：每交换链一组
    D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc {};
    rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvHeapDesc.NumDescriptors = sc->m_bufferCount;
    if (FAILED(m_device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&sc->m_rtvHeap)))) {
        delete sc;
        return ZrSwapchain{};
    }
    const UINT rtvStride = m_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

    // ---- 后台缓冲图像句柄化（≈ vkGetSwapchainImagesKHR）+ RTV + per-buffer fence ------
    for (UINT i = 0; i < sc->m_bufferCount; ++i) {
        Microsoft::WRL::ComPtr<ID3D12Resource> backbuffer;
        if (FAILED(sc->m_swapchain->GetBuffer(i, IID_PPV_ARGS(&backbuffer)))) {
            Destroy(m_swapchainTable.Acquire(sc));
            return ZrSwapchain{};
        }
        ZrD3D12ImageObj* obj = new ZrD3D12ImageObj();
        obj->resource = backbuffer;
        obj->desc.format = desc.format;
        obj->desc.width = desc.width;
        obj->desc.height = desc.height;
        obj->desc.usage = ZrImageUsage::ColorAttachment;
        obj->swapchainOwned = true;
        obj->rtv = sc->m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        obj->rtv.ptr += static_cast<SIZE_T>(i) * static_cast<SIZE_T>(rtvStride);
        obj->hasRtv = true;
        m_device->CreateRenderTargetView(obj->resource.Get(), nullptr, obj->rtv);
        sc->m_backbufferImages[i] = m_imageTable.Acquire(obj);
        // 后台缓冲初始态按 Undefined 记录（D3D12 COMMON 创建；≈ Vulkan 交换链图像
        // initialLayout=UNDEFINED，首次屏障进入使用态）
        NoteImageInitialLayout(sc->m_backbufferImages[i], ZrImageLayout::Undefined);

        if (FAILED(m_device->CreateFence(0, D3D12_FENCE_FLAG_NONE,
                IID_PPV_ARGS(&sc->m_bufferFences[i].fence)))) {
            Destroy(m_swapchainTable.Acquire(sc));
            return ZrSwapchain{};
        }
    }
    return m_swapchainTable.Acquire(sc);
}

// =============================================================================
// 创建：命令表 / 句柄→接口
// =============================================================================
ZrCommandList ZrD3D12Device::CreateCommandList(ZrQueueType queueType)
{
    assert(queueType == ZrQueueType::Graphics || queueType == ZrQueueType::Compute
        || queueType == ZrQueueType::Transfer);
    (void)queueType; // Release/NDEBUG 下 assert 消失；三域基座均为 DIRECT 列表
    // → vkAllocateCommandBuffers
    ZrD3D12CommandList* obj = new ZrD3D12CommandList(this);
    if (!obj->Init()) {
        delete obj;
        return ZrCommandList{};
    }
    return m_commandListTable.Acquire(obj);
}

IZrCommandList* ZrD3D12Device::GetCommandList(ZrCommandList handle)
{
    return ResolveCommandList(handle);
}

IZrBuffer* ZrD3D12Device::GetBuffer(ZrBuffer handle)
{
    return ResolveBuffer(handle); // ZrD3D12BufferObj 即 IZrBuffer 实现
}

uint32_t ZrD3D12Device::GetBindlessIndex(ZrImageView view)
{
    ZrD3D12ViewObj* obj = ResolveView(view);
    if (obj == nullptr) {
        return ZR_BINDLESS_INVALID;
    }
    // 仅 SRV/UAV 注册进全局堆（RTV/DSV 为渲染目标描述符，非 shader 可见）
    assert(obj->desc.viewType == ZrImageViewType::Srv || obj->desc.viewType == ZrImageViewType::Uav);
    if (obj->desc.viewType != ZrImageViewType::Srv && obj->desc.viewType != ZrImageViewType::Uav) {
        return ZR_BINDLESS_INVALID;
    }
    return obj->bindlessSlot; // 槽位索引即 bindless GPU 索引（堆起始 + slot*stride）
}

IZrSwapchain* ZrD3D12Device::GetSwapchain(ZrSwapchain handle)
{
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12Swapchain*>(payload);
}

// =============================================================================
// 销毁（≈ vkDestroy* 家族）
// =============================================================================
void ZrD3D12Device::Destroy(ZrBuffer handle)
{
    void* payload = nullptr;
    if (!m_bufferTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12BufferObj*>(payload);
    m_bufferTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrImage handle)
{
    void* payload = nullptr;
    if (!m_imageTable.TryGet(handle, payload)) {
        return;
    }
    ZrD3D12ImageObj* obj = static_cast<ZrD3D12ImageObj*>(payload);
    assert(!obj->swapchainOwned); // 后台缓冲随 Destroy(ZrSwapchain) 整组销毁
    if (obj->hasRtv) {
        // RTV 槽回收（后台缓冲的 RTV 在交换链私有堆，不走此路径）
        m_rtvSlots.Free(static_cast<uint32_t>(
            (obj->rtv.ptr - m_rtvHeap->GetCPUDescriptorHandleForHeapStart().ptr) / m_rtvStride));
    }
    if (obj->hasUav) {
        m_bindlessSlots.Free(obj->uavSlot); // UAV 清屏描述符槽回收进 free list
    }
    m_imageStates.Remove(handle.Index()); // 状态表解除跟踪
    delete obj;
    m_imageTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrImageView handle)
{
    void* payload = nullptr;
    if (!m_imageViewTable.TryGet(handle, payload)) {
        return;
    }
    ZrD3D12ViewObj* obj = static_cast<ZrD3D12ViewObj*>(payload);
    // bindless 槽回收（SRV/UAV 注册过的视图；销毁后槽位可复用）
    if (obj->bindlessSlot != ZR_SLOT_INVALID) {
        m_bindlessSlots.Free(obj->bindlessSlot);
    } else if (obj->desc.viewType == ZrImageViewType::Rtv) {
        m_rtvSlots.Free(static_cast<uint32_t>(
            (obj->cpu.ptr - m_rtvHeap->GetCPUDescriptorHandleForHeapStart().ptr)
            / m_rtvStride));
    } else if (obj->desc.viewType == ZrImageViewType::Dsv) {
        m_dsvSlots.Free(static_cast<uint32_t>(
            (obj->cpu.ptr - m_dsvHeap->GetCPUDescriptorHandleForHeapStart().ptr)
            / m_dsvStride));
    }
    delete obj;
    m_imageViewTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrSampler handle)
{
    void* payload = nullptr;
    if (!m_samplerTable.TryGet(handle, payload)) {
        return;
    }
    ZrD3D12SamplerObj* obj = static_cast<ZrD3D12SamplerObj*>(payload);
    m_samplerSlots.Free(static_cast<uint32_t>(
        (obj->cpu.ptr - m_samplerHeap->GetCPUDescriptorHandleForHeapStart().ptr)
        / m_samplerStride));
    delete obj;
    m_samplerTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrQueryPool handle)
{
    void* payload = nullptr;
    if (!m_queryPoolTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12QueryPoolObj*>(payload);
    m_queryPoolTable.Release(handle);
}

void ZrD3D12Device::Destroy(ZrSwapchain handle)
{
    void* payload = nullptr;
    if (!m_swapchainTable.TryGet(handle, payload)) {
        return;
    }
    ZrD3D12Swapchain* sc = static_cast<ZrD3D12Swapchain*>(payload);
    // 先释放后台缓冲句柄与载荷（RTV 随交换链私有堆消亡），再销毁交换链本体
    for (ZrImage image : sc->m_backbufferImages) {
        void* imagePayload = nullptr;
        if (m_imageTable.TryGet(image, imagePayload)) {
            m_imageStates.Remove(image.Index()); // 后台缓冲状态解除跟踪
            delete static_cast<ZrD3D12ImageObj*>(imagePayload);
            m_imageTable.Release(image);
        }
    }
    sc->m_backbufferImages.clear();
    m_swapchainTable.Release(handle);
    delete sc;
}

void ZrD3D12Device::Destroy(ZrCommandList handle)
{
    void* payload = nullptr;
    if (!m_commandListTable.TryGet(handle, payload)) {
        return;
    }
    delete static_cast<ZrD3D12CommandList*>(payload);
    m_commandListTable.Release(handle);
}

// =============================================================================
// 队列与同步
// =============================================================================
IZrQueue* ZrD3D12Device::GetQueue(ZrQueueType type)
{
    const uint32_t index = static_cast<uint32_t>(type);
    return (index < 3u) ? m_queues[index].get() : nullptr;
}

void ZrD3D12Device::WaitIdle()
{
    // → vkDeviceWaitIdle：fence 信号 + CPU 事件等待
    m_waitValue += 1u;
    m_queue->Signal(m_waitFence.Get(), m_waitValue);
    WaitForFenceValue(m_waitFence.Get(), m_waitValue);
}

uint32_t ZrD3D12Device::GetDebugErrorCount()
{
    if (!m_infoQueue) {
        return 0u; // 调试层降级：无 InfoQueue（调用方应另以 IsDebugLayerEnabled 识别）
    }
    uint32_t errors = 0u;
    const UINT64 stored = m_infoQueue->GetNumStoredMessages();
    for (UINT64 i = 0; i < stored; ++i) {
        SIZE_T length = 0;
        if (FAILED(m_infoQueue->GetMessage(i, nullptr, &length)) || length == 0) {
            continue;
        }
        std::vector<uint8_t> storage(length);
        D3D12_MESSAGE* message = reinterpret_cast<D3D12_MESSAGE*>(storage.data());
        if (FAILED(m_infoQueue->GetMessage(i, message, &length))) {
            continue;
        }
        // 数值越小越严重：Corruption(0) < Error(1) < Warning(2) —— “Error 级以上”
        if (message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) {
            errors += 1u;
            std::fprintf(stderr, "[zrhi][d3d12] InfoQueue id=%u severity=%u: %s\n",
                static_cast<unsigned>(message->ID),
                static_cast<unsigned>(message->Severity),
                message->pDescription != nullptr ? message->pDescription : "");
        }
    }
    m_infoQueue->ClearStoredMessages();
    return errors;
}

void ZrD3D12Device::SubmitCommandList(ZrCommandList handle)
{
    ZrD3D12CommandList* obj = ResolveCommandList(handle);
    assert(obj != nullptr && "Submit: 无效命令表句柄");
    if (obj == nullptr) {
        return;
    }
    // → vkQueueSubmit：ExecuteCommandLists 后向该命令表的帧槽 fence 信号
    ID3D12CommandList* lists[] = {obj->m_list.Get()};
    m_queue->ExecuteCommandLists(1, lists);
    ZrD3D12FrameSlot& slot = m_frameSlots[obj->m_slotIndex];
    slot.signaled += 1u;
    m_queue->Signal(slot.fence.Get(), slot.signaled);
}

// =============================================================================
// 句柄载荷解析 / 帧槽 / 描述符分配（模块内部）
// =============================================================================
ZrD3D12ImageObj* ZrD3D12Device::ResolveImage(ZrImage handle) const
{
    void* payload = nullptr;
    if (!m_imageTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12ImageObj*>(payload);
}

ZrD3D12ViewObj* ZrD3D12Device::ResolveView(ZrImageView handle) const
{
    void* payload = nullptr;
    if (!m_imageViewTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12ViewObj*>(payload);
}

ZrD3D12SamplerObj* ZrD3D12Device::ResolveSampler(ZrSampler handle) const
{
    void* payload = nullptr;
    if (!m_samplerTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12SamplerObj*>(payload);
}

ZrD3D12QueryPoolObj* ZrD3D12Device::ResolveQueryPool(ZrQueryPool handle) const
{
    void* payload = nullptr;
    if (!m_queryPoolTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12QueryPoolObj*>(payload);
}

ZrD3D12BufferObj* ZrD3D12Device::ResolveBuffer(ZrBuffer handle) const
{
    void* payload = nullptr;
    if (!m_bufferTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12BufferObj*>(payload);
}

ZrD3D12CommandList* ZrD3D12Device::ResolveCommandList(ZrCommandList handle) const
{
    void* payload = nullptr;
    if (!m_commandListTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12CommandList*>(payload);
}

ZrD3D12ShaderObj* ZrD3D12Device::ResolveShader(ZrShader handle) const
{
    void* payload = nullptr;
    if (!m_shaderTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12ShaderObj*>(payload);
}

ZrD3D12PsoObj* ZrD3D12Device::ResolvePipelineState(ZrPipelineState handle) const
{
    void* payload = nullptr;
    if (!m_psoTable.TryGet(handle, payload)) {
        return nullptr;
    }
    return static_cast<ZrD3D12PsoObj*>(payload);
}

ID3D12Resource* ZrD3D12Device::D3D12ResourceOf(ZrImage image) const
{
    ZrD3D12ImageObj* obj = ResolveImage(image);
    return obj != nullptr ? obj->resource.Get() : nullptr;
}

ID3D12Resource* ZrD3D12Device::D3D12ResourceOf(ZrBuffer buffer) const
{
    ZrD3D12BufferObj* obj = ResolveBuffer(buffer);
    return obj != nullptr ? obj->resource.Get() : nullptr;
}

ID3D12GraphicsCommandList* ZrD3D12Device::D3D12CommandListOf(ZrCommandList cmd) const
{
    ZrD3D12CommandList* obj = ResolveCommandList(cmd);
    return obj != nullptr ? obj->m_list.Get() : nullptr;
}

D3D12_CPU_DESCRIPTOR_HANDLE ZrD3D12Device::D3D12ViewCpuOf(ZrImageView view) const
{
    // TASK-B-R4-03 原生扩展点：DSV/RTV 视图 CPU 描述符（验证程序深度清屏等
    // 原生路径用）；无效句柄返回 {0}
    ZrD3D12ViewObj* obj = ResolveView(view);
    return obj != nullptr ? obj->cpu : D3D12_CPU_DESCRIPTOR_HANDLE { 0 };
}

ZrD3D12FrameSlot& ZrD3D12Device::BeginFrameSlot(uint32_t& outSlotIndex)
{
    const uint32_t slotIndex = m_nextFrameSlot % kFrameRingSize;
    m_nextFrameSlot += 1u;
    ZrD3D12FrameSlot& slot = m_frameSlots[slotIndex];
    // 分配器复用前提：该槽此前提交已全部完成（≈ Vulkan 命令池重置安全条件）
    WaitForFenceValue(slot.fence.Get(), slot.signaled);
    slot.allocator->Reset();
    outSlotIndex = slotIndex;
    return slot;
}

void ZrD3D12Device::WaitForFenceValue(ID3D12Fence* fence, uint64_t value)
{
    if (fence == nullptr || fence->GetCompletedValue() >= value) {
        return;
    }
    fence->SetEventOnCompletion(value, m_fenceEvent);
    WaitForSingleObject(m_fenceEvent, INFINITE);
}

D3D12_CPU_DESCRIPTOR_HANDLE ZrD3D12Device::BindlessCpu(uint32_t slot) const
{
    D3D12_CPU_DESCRIPTOR_HANDLE handle = m_bindlessHeap->GetCPUDescriptorHandleForHeapStart();
    handle.ptr += static_cast<SIZE_T>(slot) * static_cast<SIZE_T>(m_bindlessStride);
    return handle;
}

D3D12_GPU_DESCRIPTOR_HANDLE ZrD3D12Device::BindlessGpu(uint32_t slot) const
{
    D3D12_GPU_DESCRIPTOR_HANDLE handle = m_bindlessHeap->GetGPUDescriptorHandleForHeapStart();
    handle.ptr += static_cast<UINT64>(slot) * static_cast<UINT64>(m_bindlessStride);
    return handle;
}

D3D12_CPU_DESCRIPTOR_HANDLE ZrD3D12Device::RtvCpu(uint32_t slot) const
{
    D3D12_CPU_DESCRIPTOR_HANDLE handle = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();
    handle.ptr += static_cast<SIZE_T>(slot) * static_cast<SIZE_T>(m_rtvStride);
    return handle;
}

void ZrD3D12Device::NotePresentResult(long hresult) noexcept
{
    // 只在设备状态里累计（上层经 GetDeviceStats 查询）；库内不打印、不中止
    m_presentStatus.Note(hresult);
}

void ZrD3D12Device::NoteImageInitialLayout(ZrImage image, ZrImageLayout layout)
{
    m_imageStates.SetInitial(image.Index(), layout);
}

void ZrD3D12Device::TransitionImageLayout(ZrImage image, ZrImageLayout prev, ZrImageLayout next)
{
    // 记录转移后的当前布局；表内旧值应与屏障 prevLayout 一致（不一致即调用方
    // 布局跟踪错误——R3 自动屏障将以本表为 StateBefore 权威来源）
    const ZrImageLayout old = m_imageStates.Transition(image.Index(), next);
    assert(old == prev && "TransitionImageLayout: 屏障 prev 与状态表不符（布局跟踪失步）");
    (void)old;
    (void)prev; // Release/NDEBUG 下 assert 消失，显式标记参数仅校验用途
}

// ---- ZrD3D12BufferObj::IZrBuffer（UPLOAD/READBACK 持久映射；DEFAULT 不可映射）------
void* ZrD3D12BufferObj::Map()
{
    if (desc.hostVisible || desc.readback) {
        void* mapped = nullptr;
        // UPLOAD/READBACK 全范围持久映射（≈ vkMapMemory 的 persistent map 语义）
        if (SUCCEEDED(resource->Map(0, nullptr, &mapped))) {
            return mapped;
        }
        return nullptr;
    }
    return nullptr; // DEFAULT 堆：设备本地内存不可映射（经 UPLOAD/READBACK 中转）
}

void ZrD3D12BufferObj::Unmap()
{
    if (desc.hostVisible || desc.readback) {
        resource->Unmap(0, nullptr);
    }
}

// =============================================================================
// ZrD3D12Queue —— IZrQueue
// =============================================================================
void ZrD3D12Queue::Submit(ZrCommandList commandList)
{
    m_owner->SubmitCommandList(commandList);
}

// =============================================================================
// ZrD3D12Swapchain —— IZrSwapchain
// =============================================================================
ZrD3D12Swapchain::~ZrD3D12Swapchain()
{
    // 后台缓冲句柄/RTV 堆/per-buffer fence 经 ZrD3D12Device::Destroy(ZrSwapchain) 释放
}

uint32_t ZrD3D12Swapchain::AcquireNextImage()
{
    assert(m_swapchain != nullptr);
    // → vkAcquireNextImageKHR：取当前帧索引，等待该缓冲上一次 Present 的信号完成
    m_currentIndex = m_swapchain->GetCurrentBackBufferIndex();
    BufferFence& buffer = m_bufferFences[m_currentIndex];
    m_owner->WaitForFenceValue(buffer.fence.Get(), buffer.signaled);
    return m_currentIndex;
}

ZrImage ZrD3D12Swapchain::GetCurrentImage()
{
    return m_backbufferImages[m_currentIndex];
}

void ZrD3D12Swapchain::Present()
{
    // → vkQueuePresentKHR（PresentMode → SyncInterval/Flags 映射见 zrhi_d3d12_translate）
    // TASK-H-02 D3：返回值必须检查——DEVICE_REMOVED / WAS_STILL_DRAWING 等失败
    // 此前被整条丢弃；现记录到设备状态（GetDeviceStats 可查），不在库内打印/中止。
    const HRESULT hr = m_swapchain->Present(m_syncInterval, m_presentFlags);
    m_owner->NotePresentResult(hr);
    // 帧槽 signal 语义不变：它跟踪的是本缓冲的 GPU 工作完成，而非呈现是否被接受
    BufferFence& buffer = m_bufferFences[m_currentIndex];
    buffer.signaled += 1u;
    m_owner->m_queue->Signal(buffer.fence.Get(), buffer.signaled);
}
