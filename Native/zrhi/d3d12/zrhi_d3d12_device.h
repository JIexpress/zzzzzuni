// =============================================================================
// zrhi_d3d12_device.h —— D3D12 后端设备（TASK-B-R2-02）
//
// 与 Vulkan 语义的对应关系（Vulkan → 本实现）：
//   - ZrD3D12Device        → VkDevice + vkCreate*/vkDestroy* 家族
//   - GetQueue             → vkGetDeviceQueue（三域基座均映射 DIRECT 队列，
//                            异步 Compute/Copy 队列由后续任务补全）
//   - WaitIdle             → vkDeviceWaitIdle（fence 信号 + CPU 事件等待）
//   - CreateSwapchain      → vkCreateSwapchainKHR（DXGI CreateSwapChainForHwnd，
//                            FLIP 模型 3 缓冲；Acquire/Present 见 IZrSwapchain）
//   - Present              → vkQueuePresentKHR（IDXGISwapChain::Present）
//   - DEBUG layer          → Vulkan validation layer 等价物（可用则开启，附
//                            InfoQueue：Error 级以上记入 stderr，计数可查询）
//   - 帧槽环               → 命令分配器 + 提交 fence（vkAllocateCommandBuffers
//                            的池化复用等价物；Reset 前等待 GPU 完成）
//
// TASK-B-R2-03 增补（资源体系 + bindless + GPU 时间戳）：
//   - 描述符堆管理：RTV/DSV/Sampler（非 shader 可见）+ CBV_SRV_UAV（shader 可见
//     全局 bindless 堆，容量 16384 预分配 + 断言上限）——槽位经 ZrSlotAllocator
//     free-list 分配/回收（纯类可单测）
//   - Buffer：DEFAULT（+UPLOAD 中转初始上传/fence 等待）/ UPLOAD / READBACK 堆；
//     Map/Unmap（IZrBuffer）
//   - ImageView（SRV/UAV/RTV/DSV）+ Sampler + GetBindlessIndex（槽位索引即
//     bindless GPU 索引；Destroy 视图时槽回收）
//   - per-resource 状态表（ZrResourceStateTable）：图像初始布局落表 + 命令表
//     屏障时转移记录（R3 自动屏障铺垫）
//   - 时间戳：TIMESTAMP 查询堆 + ResolveQueryData→READBACK + GetTimestampFrequency
//     换算 ms
//
// 头文件为 zrhi 模块内部头（不出 Native/zrhi/；验证程序例外使用）。
// 线程模型：单线程独占（同 zrhi 约定）。
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
#include <dxgi1_5.h>
#include <wrl/client.h>

#include <cstdint>
#include <memory>
#include <vector>

#include "zrhi/zrhi_device.h"

#include "zrhi/d3d12/zrhi_d3d12_slots.h"
#include "zrhi/d3d12/zrhi_d3d12_translate.h" // TASK-H-02 D3：ZrD3D12PresentStatus
#include "zrhi/d3d12/zrhi_d3d12_pso_cache.h" // TASK-B-PSO-01：PSO 磁盘缓存

class ZrD3D12CommandList;
class ZrD3D12Device;
class ZrD3D12Swapchain;

/// 图像载荷（句柄表 payload；模块内部）
struct ZrD3D12ImageObj {
    Microsoft::WRL::ComPtr<ID3D12Resource> resource;
    ZrImageDesc desc;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv {};
    bool hasRtv = false; // RTV 清屏路径（≈ Vulkan 以 COLOR_ATTACHMENT_OPTIMAL 清屏）
    uint32_t uavSlot = ZR_SLOT_INVALID; // bindless 堆槽（UAV 清屏路径描述符）
    bool hasUav = false; // UAV 清屏路径（≈ vkCmdClearColorImage 走 storage 图像）
    bool swapchainOwned = false; // 交换链后台缓冲（随交换链销毁，勿单独 Destroy）
};

/// 缓冲载荷（句柄表 payload；模块内部）。堆类型由 desc.hostVisible/readback 决定：
///   默认 → DEFAULT（设备本地；Map 返回 nullptr）
///   hostVisible → UPLOAD（host 可写中转堆；→ HOST_VISIBLE 内存）
///   readback → READBACK（host 可读；→ HOST_VISIBLE 内存，拷贝目标专用）
struct ZrD3D12BufferObj final : public IZrBuffer {
    Microsoft::WRL::ComPtr<ID3D12Resource> resource;
    ZrBufferDesc desc;

    // ---- IZrBuffer（UPLOAD/READBACK 持久映射；DEFAULT 返回 nullptr）------------
    void* Map() override;
    void Unmap() override;
};

/// 图像视图载荷（句柄表 payload；模块内部）
/// SRV/UAV：描述符直接写进全局 bindless 堆（slot 即 GPU 索引，Destroy 时回收）；
/// RTV/DSV：描述符写进对应非 shader 可见堆（bindlessSlot 恒无效）。
struct ZrD3D12ViewObj {
    ZrImageViewDesc desc;
    D3D12_CPU_DESCRIPTOR_HANDLE cpu {};
    uint32_t bindlessSlot = ZR_SLOT_INVALID; // SRV/UAV：bindless 槽位（= GPU 索引）
};

/// 采样器载荷（句柄表 payload；模块内部；非 shader 可见 sampler 堆）
struct ZrD3D12SamplerObj {
    ZrSamplerDesc desc;
    D3D12_CPU_DESCRIPTOR_HANDLE cpu {};
};

/// 着色器载荷（TASK-B-R4-03；句柄表 payload；模块内部）。
/// bytecode 为 DXBC 字节码拷贝（运行时 D3DCompile 产物或调用方直传）
struct ZrD3D12ShaderObj {
    ZrShaderDesc desc;
    std::vector<uint8_t> bytecode; // DXBC（D3D12_SHADER_BYTECODE 的底层存储）
};

/// 图形 PSO 载荷（TASK-B-R4-03；句柄表 payload；模块内部）。
/// rootSignature 为 v1 固定布局（单一根 CBV b0，见 CreatePipelineState 实现）；
/// pipelineState 为 D3D12_GRAPHICS_PIPELINE_STATE_DESC 映射产物
struct ZrD3D12PsoObj {
    Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
    Microsoft::WRL::ComPtr<ID3D12PipelineState> pipelineState;
    ZrGraphicsPsoDesc desc; // 创建描述（诊断/断言用）
};

/// 时间戳查询堆载荷（句柄表 payload；模块内部）
struct ZrD3D12QueryPoolObj {
    Microsoft::WRL::ComPtr<ID3D12QueryHeap> heap;     // TIMESTAMP 类型
    Microsoft::WRL::ComPtr<ID3D12Resource> readback;  // n*8B READBACK（Resolve 落点）
    Microsoft::WRL::ComPtr<ID3D12CommandAllocator> resolveAllocator; // Resolve 专用一次性链路
    Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> resolveList;
    Microsoft::WRL::ComPtr<ID3D12Fence> resolveFence;
    uint64_t resolveSignaled = 0;
    uint32_t count = 0;
};

/// 别名堆载荷（TASK-B-R3-02；句柄表 payload；模块内部）。
/// RenderGraph 别名池的物理载体：placed resource 全部落位于同一 ID3D12Heap 上，
/// 槽位（生命周期不重叠的 Transient 贴图）串行复用即"真内存复用"。
/// 模型约定：每别名槽一个堆，堆尺寸 = 槽内最大 GetResourceAllocationInfo
/// （GetResourcePlacementInfo 语义等价物），placed image 落位 offset 恒 0——
/// 槽内任一时刻至多一个资源存在，天然无重叠，规避子偏移对齐复杂度。
struct ZrD3D12AliasHeapObj {
    Microsoft::WRL::ComPtr<ID3D12Heap> heap; // DEFAULT 堆类型
    uint64_t sizeBytes = 0;                  // 64KB 向上取整后的实际堆尺寸
};

/// 帧槽：命令分配器 + 提交完成 fence（分配器 Reset 的安全前提）
struct ZrD3D12FrameSlot {
    Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
    Microsoft::WRL::ComPtr<ID3D12Fence> fence;
    uint64_t signaled = 0; // 最近一次提交信号值
};

// =============================================================================
// ZrD3D12Queue —— IZrQueue 实现（→ VkQueue；Submit → vkQueueSubmit）
// 基座：Graphics/Compute/Transfer 三域共用 DIRECT 队列（异步队列后续任务）
// =============================================================================
class ZrD3D12Queue final : public IZrQueue {
public:
    ZrD3D12Queue(ZrD3D12Device* owner, ZrQueueType type)
        : m_owner(owner), m_type(type)
    {
    }

    ZrQueueType GetQueueType() const override { return m_type; }
    void Submit(ZrCommandList commandList) override;

private:
    ZrD3D12Device* m_owner;
    ZrQueueType m_type;
};

// =============================================================================
// ZrD3D12Swapchain —— IZrSwapchain 实现（→ VkSwapchainKHR）
//   - AcquireNextImage → vkAcquireNextImageKHR：GetCurrentBackBufferIndex 取索引
//     + 该后台缓冲的 per-buffer fence 等待（阻塞至其上一次呈现的 GPU 工作完成）
//   - Present → vkQueuePresentKHR：按 PresentMode 映射参数呈现，并向前台缓冲
//     的 fence 信号（下次 Acquire 同一缓冲时等待）
// =============================================================================
class ZrD3D12Swapchain final : public IZrSwapchain {
public:
    explicit ZrD3D12Swapchain(ZrD3D12Device* owner)
        : m_owner(owner)
    {
    }

    ~ZrD3D12Swapchain() override;

    uint32_t AcquireNextImage() override;
    ZrImage GetCurrentImage() override;
    void Present() override;

private:
    friend class ZrD3D12Device;

    struct BufferFence {
        Microsoft::WRL::ComPtr<ID3D12Fence> fence;
        uint64_t signaled = 0; // 该缓冲最近一次 Present 后的信号值
    };

    ZrD3D12Device* m_owner;
    Microsoft::WRL::ComPtr<IDXGISwapChain3> m_swapchain; // 3：GetCurrentBackBufferIndex
    uint32_t m_bufferCount = 0;
    ZrPresentMode m_presentMode = ZrPresentMode::Fifo;
    UINT m_syncInterval = 1;
    UINT m_presentFlags = 0;
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_rtvHeap; // RTV 堆：每交换链一组
    std::vector<ZrImage> m_backbufferImages;
    std::vector<BufferFence> m_bufferFences;
    uint32_t m_currentIndex = 0; // 当前帧索引跟踪（≈ swapchain image index）
};

// =============================================================================
// ZrD3D12Device —— IZrDevice 实现（D3D12 基座子集）
// 实现范围（B-R2-03）：CreateBuffer(initData)/CreateImage/CreateImageView/
// CreateSampler/CreateTimestampQueryHeap/CreateSwapchain/CreateCommandList/
// GetBuffer/GetBindlessIndex/ResolveTimestamps/GetQueue/WaitIdle/
// GetDebugErrorCount + 全部 Destroy。
// TASK-B-R4-03 增补：CreateShader（DXBC 持有）/CreatePipelineState（v1 固定布局
// 根签名 + D3D12_GRAPHICS_PIPELINE_STATE_DESC）/CompileHlsl（运行时 D3DCompile）/
// Destroy(ZrShader|ZrPipelineState)。
// =============================================================================
class ZrD3D12Device final : public IZrDevice {
public:
    static constexpr uint32_t kFrameRingSize = 8;        // 帧槽环（≥ 交换链缓冲数）
    static constexpr uint32_t kDescriptorCapacity = 128; // 设备级 RTV/DSV/Sampler 堆容量
    static constexpr uint32_t kBindlessCapacity = 16384; // 全局 shader 可见 CBV_SRV_UAV 堆
                                                          // （预分配 + 断言上限；按需增长
                                                          //   重建策略留待后续任务）

    /// 创建设备（失败返回 nullptr）：DEBUG layer 可用则开启并挂 InfoQueue
    static ZrD3D12Device* Create();

    /// 反查图像创建时的 desc（true=成功填充）。
    ///
    /// 用途：RG 的 Imported 资源原本只带句柄、不带尺寸/格式，rg.export 只能输出占位值
    /// （历史现象：depth 显示成 RGBA8Unorm 1x1，真实是 D32Float W×H）。诊断/导出路径
    /// 用这个方法拿到真实 desc 再交给 RgResourceDesc::ImportImage。
    bool GetImageDesc(ZrImage image, ZrImageDesc* out) const;

    /// TASK-B-UNITY-01：包装**外部创建**的 D3D12 设备（失败返回 nullptr）。
    ///
    /// 与 Create 的差异（其余初始化路径完全共用 InitializeRuntime）：
    ///   - 不调用 D3D12CreateDevice，不启用 DEBUG layer（二者都必须在设备创建
    ///     **之前**完成，事后无法补救）；
    ///   - 保留对 external 的强引用（ComPtr AddRef），不接管其生命周期：
    ///     调用方仍须在销毁本对象后自行 Release 宿主设备；
    ///   - DXGI 工厂从设备反查（IDXGIDevice::GetAdapter → 适配器父工厂），
    ///     而非新建工厂 + 枚举适配器，避免选到与外部设备不一致的适配器。
    ///
    /// 已知限制（不能靠包装达成，详见 .cpp 同名实现注释）：
    ///   - 调试层状态由宿主决定：宿主未开启时本设备无 InfoQueue（GetDebugErrorCount 恒 0）；
    ///   - 驱动/适配器选择、feature level 选择、DRED 等创建期参数完全由宿主决定；
    ///   - 若外部设备并非来自 DXGI 适配器（极少见），m_factory 为空 →
    ///     CreateSwapchain 直接返回无效句柄（其余功能不受影响）。
    static ZrD3D12Device* CreateFrom(ID3D12Device* external);

    ~ZrD3D12Device() override;

    // ---- IZrDevice：创建 -------------------------------------------------------
    ZrBuffer CreateBuffer(const ZrBufferDesc& desc, const void* initData = nullptr) override;
    ZrImage CreateImage(const ZrImageDesc& desc) override;

    /// TASK-B-UNITY-02：创建**可跨进程/跨 API 共享**的图像（→ 原生窗口到 Unity 的
    /// 纹理交接）。与 CreateImage 的唯一差异是堆标志与句柄：
    ///   - D3D12_HEAP_FLAG_SHARED（否则 CreateSharedHandle 直接失败）
    ///   - 创建即 ID3D12Device::CreateSharedHandle(GENERIC_ALL) 导出 NT 句柄，
    ///     写入 *outSharedHandle（调用方负责 CloseHandle；失败写 0）
    /// 其余（格式能力探测、RTV 描述符、句柄表入册、初始布局）与 CreateImage 共用。
    /// 失败返回无效句柄且 *outSharedHandle = 0。
    ZrImage CreateSharedImage(const ZrImageDesc& desc, void** outSharedHandle);
    ZrImageView CreateImageView(const ZrImageViewDesc& desc) override;
    ZrSampler CreateSampler(const ZrSamplerDesc& desc) override;
    ZrShader CreateShader(const ZrShaderDesc& desc) override; // TASK-B-R4-03 落地
    ZrPipelineState CreatePipelineState(const ZrGraphicsPsoDesc& desc) override; // R4-03 落地
    ZrSwapchain CreateSwapchain(const ZrSwapchainDesc& desc) override;
    ZrCommandList CreateCommandList(ZrQueueType queueType) override;
    ZrQueryPool CreateTimestampQueryHeap(uint32_t queryCount) override;
    IZrCommandList* GetCommandList(ZrCommandList handle) override;
    IZrSwapchain* GetSwapchain(ZrSwapchain handle) override;
    IZrBuffer* GetBuffer(ZrBuffer handle) override;
    uint32_t GetBindlessIndex(ZrImageView view) override;
    uint32_t ResolveTimestamps(ZrQueryPool heap, double* outMilliseconds, uint32_t count) override;

    // ---- IZrDevice：销毁 -------------------------------------------------------
    void Destroy(ZrBuffer handle) override;
    void Destroy(ZrImage handle) override;
    void Destroy(ZrImageView handle) override;
    void Destroy(ZrSampler handle) override;
    void Destroy(ZrShader handle) override;
    void Destroy(ZrPipelineState handle) override;
    void Destroy(ZrSwapchain handle) override;
    void Destroy(ZrCommandList handle) override;
    void Destroy(ZrQueryPool handle) override;

    // ---- IZrDevice：队列与同步 --------------------------------------------------
    IZrQueue* GetQueue(ZrQueueType type) override;
    void WaitIdle() override; // → vkDeviceWaitIdle
    uint32_t GetDebugErrorCount() override; // 排空 InfoQueue 并返回 Error 级以上计数

    // ---- IZrDevice：TASK-B-R3-02 别名堆 + placed resource + 统计 -------------------
    ZrAliasHeap CreateAliasHeap(uint64_t sizeBytes) override;
    uint64_t GetImagePlacementSize(const ZrImageDesc& desc) override;
    ZrImage CreateImageInHeap(const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset) override;
    ZrDeviceStats GetDeviceStats() override;
    void Destroy(ZrAliasHeap handle) override;

    // ---- 原生扩展点（模块内部与验证程序使用）--------------------------------------
    ID3D12Device* D3D12Device() const { return m_device.Get(); }
    ID3D12CommandQueue* D3D12Queue() const { return m_queue.Get(); }
    ID3D12Resource* D3D12ResourceOf(ZrImage image) const;
    /// TASK-B-UNITY-02：缓冲的原生资源（中心像素回读等原生拷贝路径使用；无效句柄返回
    /// nullptr）。与图像重载并列，避免调用方为了拿 CopyTextureRegion 的落点去重建资源。
    ID3D12Resource* D3D12ResourceOf(ZrBuffer buffer) const;
    ID3D12GraphicsCommandList* D3D12CommandListOf(ZrCommandList cmd) const;
    /// 视图的 CPU 描述符句柄（TASK-B-R4-03：验证程序深度清屏等原生路径使用；
    /// 无效句柄返回 {0}）
    D3D12_CPU_DESCRIPTOR_HANDLE D3D12ViewCpuOf(ZrImageView view) const;
    /// 调试层可用性。Create 路径 = 本库成功开启（并挂上 InfoQueue）；
    /// CreateFrom 路径 = 宿主设备暴露出 ID3D12InfoQueue（即宿主已开启调试层）。
    bool IsDebugLayerEnabled() const { return m_debugLayerEnabled; }
    /// TASK-B-UNITY-01：设备是否由本对象创建（Create=true / CreateFrom=false）。
    /// 诊断用：销毁时只有自建设备会被真正释放（外部设备仅解引用）。
    bool OwnsDevice() const { return m_ownsDevice; }

    // ---- TASK-B-PSO-01：PSO 磁盘缓存（ID3D12PipelineLibrary）-----------------------
    /// 缓存统计（命中/未命中/加载耗时/序列化字节；验证程序与日志共用）
    ZrPsoCacheStats GetPsoCacheStats() const;
    /// 缓存是否真正生效（false = 能力不支持/被环境变量禁用/已降级到直接创建）
    bool IsPsoCacheActive() const { return m_psoCache != nullptr && m_psoCache->IsActive(); }
    /// 显式刷写（析构时自动调用；测试可提前调用，幂等）
    void FlushPsoCache();

    // ---- TASK-B-R4-03：运行时着色器编译（D3D12 后端扩展，非 IZrDevice 接口）-----
    // HLSL 源码 → DXBC：内部 D3DCompile（FXC，SM5.0）。DXC/SM6 升级路径：改用
    // dxc.exe（/T vs_6_0 /T ps_6_0，-spirv 可出 Vulkan 轨 SPIR-V）——本接口的
    // ZrShaderDesc{code,codeSize} 字节码契约不变，仅编译器与 target profile 替换，
    // 消费方（CreatePipelineState）零改动。
    // 失败返回无效句柄；编译器错误信息（带行号）打印到 stderr——错误 HRESULT
    // 带信息输出（句柄+返回码约定，异常不跨 ABI）。
    ZrShader CompileHlsl(ZrShaderStage stage, const char* source, size_t sourceBytes,
        const char* entryPoint);

    /// 提交命令表（IZrQueue::Submit 的实际落点；→ vkQueueSubmit + fence 信号）
    void SubmitCommandList(ZrCommandList handle);

private:
    friend class ZrD3D12CommandList;
    friend class ZrD3D12Swapchain;

    ZrD3D12Device() = default;

    // ---- TASK-B-UNITY-01：设备就绪后的公共初始化（Create / CreateFrom 共用）------
    // 前提：m_device 已就位。负责 InfoQueue 挂接、DIRECT 队列、fence 与等待事件、
    // 帧槽环、上传链路、设备级描述符堆、队列包装。
    //   configureDebugSettings：true = 本库开启了调试层（Create 路径），可设置
    //   break 策略；false = 外部设备（CreateFrom），只读调试消息、不改宿主策略。
    // 返回 false 表示初始化失败（对象须丢弃）。
    bool InitializeRuntime(bool configureDebugSettings);

    // ---- 句柄载荷解析（stale 句柄返回 nullptr）------------------------------------
    ZrD3D12ImageObj* ResolveImage(ZrImage handle) const;
    ZrD3D12BufferObj* ResolveBuffer(ZrBuffer handle) const;
    ZrD3D12ViewObj* ResolveView(ZrImageView handle) const;
    ZrD3D12SamplerObj* ResolveSampler(ZrSampler handle) const;
    ZrD3D12QueryPoolObj* ResolveQueryPool(ZrQueryPool handle) const;
    ZrD3D12CommandList* ResolveCommandList(ZrCommandList handle) const;
    ZrD3D12ShaderObj* ResolveShader(ZrShader handle) const;   // TASK-B-R4-03
    ZrD3D12PsoObj* ResolvePipelineState(ZrPipelineState handle) const; // TASK-B-R4-03

    // ---- 帧槽环与 fence 等待 -----------------------------------------------------
    ZrD3D12FrameSlot& BeginFrameSlot(uint32_t& outSlotIndex);
    ID3D12DescriptorHeap* BindlessDescriptorHeap() const { return m_bindlessHeap.Get(); }
    void WaitForFenceValue(ID3D12Fence* fence, uint64_t value);

    // ---- 描述符堆槽位（free-list 分配/回收；分配器语义单测见 zrhi_d3d12_slots）------
    D3D12_CPU_DESCRIPTOR_HANDLE BindlessCpu(uint32_t slot) const;
    D3D12_GPU_DESCRIPTOR_HANDLE BindlessGpu(uint32_t slot) const;
    D3D12_CPU_DESCRIPTOR_HANDLE RtvCpu(uint32_t slot) const;

    // ---- 图像布局跟踪（R3 自动屏障铺垫）--------------------------------------------
    void NoteImageInitialLayout(ZrImage image, ZrImageLayout layout);
    void TransitionImageLayout(ZrImage image, ZrImageLayout prev, ZrImageLayout next);

    // ---- Present 结果记录（TASK-H-02 D3；ZrD3D12Swapchain::Present 调用）------------
    void NotePresentResult(long hresult) noexcept;

    // ---- TASK-B-R3-02：图像创建公共尾段 ---------------------------------------------
    // 资源对象已就位（Commit 或 Placed）后：格式能力探测 → RTV/UAV 清屏描述符 →
    // 句柄表入册 → 初始布局落表。CreateImage 与 CreateImageInHeap 共用。
    ZrImage FinalizeImageCreation(ZrD3D12ImageObj* obj, const ZrImageDesc& desc);

    bool m_debugLayerEnabled = false;
    bool m_ownsDevice = true; // TASK-B-UNITY-01：false = m_device 由外部传入（仅引用）

    Microsoft::WRL::ComPtr<IDXGIFactory4> m_factory;
    Microsoft::WRL::ComPtr<ID3D12Device> m_device;
    std::unique_ptr<ZrD3D12PsoCache> m_psoCache; // TASK-B-PSO-01：PSO 磁盘缓存
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> m_queue; // DIRECT（三域共用）
    Microsoft::WRL::ComPtr<ID3D12InfoQueue> m_infoQueue; // null = 调试层降级

    std::unique_ptr<ZrD3D12Queue> m_queues[3]; // Graphics/Compute/Transfer 包装

    // WaitIdle fence 与共享事件
    Microsoft::WRL::ComPtr<ID3D12Fence> m_waitFence;
    uint64_t m_waitValue = 0;
    HANDLE m_fenceEvent = nullptr;

    // CreateBuffer(initData) 一次性上传链路（UPLOAD 中转 → CopyBufferRegion → fence）
    Microsoft::WRL::ComPtr<ID3D12CommandAllocator> m_uploadAllocator;
    Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> m_uploadList;
    Microsoft::WRL::ComPtr<ID3D12Fence> m_uploadFence;
    uint64_t m_uploadSignaled = 0;

    // 帧槽环
    ZrD3D12FrameSlot m_frameSlots[kFrameRingSize];
    uint32_t m_nextFrameSlot = 0;

    // 设备级描述符堆（RTV/DSV/Sampler：非 shader 可见；bindless：shader 可见）
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_rtvHeap;
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_dsvHeap;
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_samplerHeap;
    Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> m_bindlessHeap; // CBV_SRV_UAV（全局）
    ZrSlotAllocator m_rtvSlots { kDescriptorCapacity };
    ZrSlotAllocator m_dsvSlots { kDescriptorCapacity };
    ZrSlotAllocator m_samplerSlots { kDescriptorCapacity };
    ZrSlotAllocator m_bindlessSlots { kBindlessCapacity };
    uint32_t m_rtvStride = 0;
    uint32_t m_dsvStride = 0;
    uint32_t m_samplerStride = 0;
    uint32_t m_bindlessStride = 0;

    // 句柄表与载荷所有权
    ZrHandleTable<ZrBuffer> m_bufferTable;
    ZrHandleTable<ZrImage> m_imageTable;
    ZrHandleTable<ZrImageView> m_imageViewTable;
    ZrHandleTable<ZrSampler> m_samplerTable;
    ZrHandleTable<ZrQueryPool> m_queryPoolTable;
    ZrHandleTable<ZrCommandList> m_commandListTable;
    ZrHandleTable<ZrSwapchain> m_swapchainTable;
    ZrHandleTable<ZrAliasHeap> m_aliasHeapTable; // TASK-B-R3-02
    ZrHandleTable<ZrShader> m_shaderTable;       // TASK-B-R4-03
    ZrHandleTable<ZrPipelineState> m_psoTable;   // TASK-B-R4-03

    // per-resource 图像布局状态表（key = ZrImage::Index()；纯类可单测）
    ZrResourceStateTable m_imageStates;

    // Present 结果状态（TASK-H-02 D3：失败不静默，经 GetDeviceStats 上报）
    ZrD3D12PresentStatus m_presentStatus;
};

/// 创建/销毁 D3D12 后端设备（验证程序入口；失败返回 nullptr）
ZrD3D12Device* ZrD3D12CreateDevice();
/// TASK-B-UNITY-01：包装外部设备（ZrD3D12Device::CreateFrom 的自由函数入口；
/// 销毁同样走 ZrD3D12DestroyDevice）
ZrD3D12Device* ZrD3D12CreateDeviceFrom(ID3D12Device* external);
void ZrD3D12DestroyDevice(ZrD3D12Device* device);
