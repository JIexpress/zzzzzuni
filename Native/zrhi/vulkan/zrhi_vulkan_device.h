// =============================================================================
// zrhi_vulkan_device.h —— Vulkan 后端设备（TASK-B-R7-01）
//
// 与 zrhi 语义的对应关系（zrhi 抽象当年 1:1 对齐 VK，本实现是最薄兑现点）：
//   - ZrVulkanDevice       → VkDevice + vkCreate*/vkDestroy* 家族（本文件即真实 VK）
//   - GetQueue             → vkGetDeviceQueue（Graphics/Compute/Transfer 三域基座
//                            共用单一 graphics+compute+transfer 队列族）
//   - WaitIdle             → vkDeviceWaitIdle
//   - CreateSwapchain      → vkCreateSwapchainKHR（VK_KHR_win32_surface +
//                            VK_KHR_swapchain；PresentMode 映射见 translate，
//                            表面不支持时降级 Fifo——必支持兜底）
//   - GetDebugErrorCount   → validation layer debug messenger 的 ERROR 级消息计数
//
// 后端决策（写实现前先定的契约）：
//   - 动态渲染：目标设备要求 Vulkan 1.3（780M/RDNA3 支持），vkCmdBegin/EndRendering
//     为核心能力，零 RenderPass/Framebuffer 对象（简报"VK_KHR_dynamic_rendering
//     可用则免 RenderPass 对象"的兑现）；BeginPass/EndPass 保持基座占位语义，
//     SetRenderTarget 落地为 BeginRendering（D3D12 OMSetRenderTargets 的对位）
//   - bindless = descriptor indexing：全局 shader-visible 大 set（binding0 =
//     SAMPLED_IMAGE 数组 / binding1 = STORAGE_IMAGE 数组，各 kBindlessCapacity 槽），
//     UPDATE_AFTER_BIND + PARTIALLY_BOUND 特性启用；GetBindlessIndex 契约与 D3D12
//     完全一致（槽位即 GPU 索引，Destroy 回收 free list）
//   - 根 CBV 槽（rootCbvCount 1..5）→ set0 binding0..N-1 的 UNIFORM_BUFFER；
//     SRV 描述符表（srvTableCount 0..2）→ binding N..N+M-1 的
//     COMBINED_IMAGE_SAMPLER（静态 Point/Clamp 采样器设备级预建，对位 D3D12
//     静态采样器 s_i）；命令表侧经描述符集写入 + 绑定落位（见 commandlist）
//   - 顶点/索引：v1 固定布局与 D3D12 完全同源（stride 32B、R32_UINT 索引）
//   - 视口：D3D12 是 LH 屏幕空间（NDC y 上正），Vulkan 帧缓冲 y 下正——
//     SetViewportScissor 用负高度视口（y=height, height=-height）翻转，
//     与 frontFace=CW 组合精确复刻 D3D12 CW 前向判定（B-R8-01 修正：原 CCW
//     与 D3D12 反向——同一负视口变换下绕序相同，front 标定须同为 CW；
//     --r5 OutlinePass 反向壳首次暴露，见 device 侧光栅化注）
//   - 屏障：ZrImageBarrier 直接 1:1 展开 VkImageMemoryBarrier（布局/访问/阶段
//     三元组经 translate 显式查表）；split barrier 语义 VK 原生没有——begin/end
//     两半各自落一道普通 vkCmdPipelineBarrier（D3D12 BEGIN_ONLY 的"提前开始刷新"
//     收益放弃，正确性不受影响——差异已写注释）
//
// 已知坑（简报要求写进注释）：
//   - 交换链重建（resize）：TASK-B-R9-09 已补全——OUT_OF_DATE/SUBOPTIMAL 置帧闸
//     粘性标记，下一帧 Acquire 入口自动真重建（失败回粘性，不挂死）；
//     调用方缓存的"后台缓冲 RTV 视图"在重建后失效（句柄 stale，须按新句柄重建）
//   - VK_KHR_dynamic_rendering：Vulkan 1.3 核心化，设备创建即要求 apiVersion 1.3
//
// 头文件为 zrhi 模块内部头（不出 Native/zrhi/；验证程序例外使用——同 d3d12 后端）。
// 线程模型：单线程独占（同 zrhi 约定）。
// =============================================================================
#pragma once

// ---- Vulkan SDK 头（/W4 /WX 豁免隔离段，镜像 fsr_upscaler.cpp 的第三方头做法）----
// VK_USE_PLATFORM_WIN32_KHR：Win32 surface（VK_KHR_win32_surface）入口声明需要
#if !defined(VK_USE_PLATFORM_WIN32_KHR)
#define VK_USE_PLATFORM_WIN32_KHR
#endif
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

#include <cstdint>
#include <memory>
#include <vector>

#include "zrhi/zrhi_device.h"
#include "zrhi/d3d12/zrhi_d3d12_slots.h" // ZrSlotAllocator：纯逻辑，无 D3D12 依赖
#include "zrhi/vulkan/zrhi_vulkan_frame_gate.h" // VkResult 统一分类 + 每帧提交/呈现闸
#include "zrhi/vulkan/zrhi_vulkan_recreate.h" // TASK-B-R9-09：重建尺寸裁决 + 台账（纯逻辑）

class ZrVulkanCommandList;
class ZrVulkanDevice;
class ZrVulkanSwapchain;

/// 图像载荷（句柄表 payload；模块内部）。memory 为空 = 交换链后台缓冲
/// （VK_KHR_swapchain 所有权，随交换链整组销毁，勿单独 Destroy）
struct ZrVulkanImageObj {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE; // 每资源一分配（基座不做子分配）
    ZrImageDesc desc;
    bool swapchainOwned = false;
    bool inAliasHeap = false; // 别名堆落位（memory 为堆所有，Destroy 不 free）
};

/// 缓冲载荷（句柄表 payload；模块内部）。堆域按 desc.hostVisible/readback：
///   默认 → DEVICE_LOCAL（Map 返回 nullptr）；hostVisible → HOST_VISIBLE|COHERENT
///   （UPLOAD 语义）；readback → HOST_VISIBLE（拷贝目标专用）
struct ZrVulkanBufferObj final : public IZrBuffer {
    VkBuffer buffer = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;      // Map 用（创建期注入）
    void* persistentMap = nullptr;         // HOST_VISIBLE 持久映射（创建期一次 map）
    ZrBufferDesc desc;

    // ---- IZrBuffer（UPLOAD/READBACK 持久映射；DEFAULT 返回 nullptr）------------
    void* Map() override;
    void Unmap() override;
};

/// 图像视图载荷（句柄表 payload；模块内部）
/// SRV/UAV：写入全局 bindless 大 set（slot 即 GPU 索引，Destroy 时回收）；
/// RTV/DSV：独立 VkImageView（动态渲染附件/清屏扩展点用）
struct ZrVulkanViewObj {
    ZrImageViewDesc desc;
    VkImageView view = VK_NULL_HANDLE;    // RTV/DSV：附件视图；SRV/UAV 也建视图
    uint32_t bindlessSlot = ZR_SLOT_INVALID; // SRV/UAV：bindless 槽位（= GPU 索引）
};

/// 采样器载荷（句柄表 payload；模块内部）
struct ZrVulkanSamplerObj {
    ZrSamplerDesc desc;
    VkSampler sampler = VK_NULL_HANDLE;
};

/// 着色器载荷（句柄表 payload；模块内部）。bytecode 为 SPIR-V 字节码拷贝
/// （ZrShaderDesc 契约：Vulkan 侧 code 即 SPIR-V，glslc -x hlsl 产物）
struct ZrVulkanShaderObj {
    ZrShaderDesc desc;
    std::vector<uint8_t> bytecode;
    VkShaderModule module = VK_NULL_HANDLE; // vkCreateShaderModule 产物（PSO 引用）
};

/// 图形 PSO 载荷（句柄表 payload；模块内部）。
/// pipelineLayout：set0 = binding0..cbvCount-1 UNIFORM_BUFFER +
/// binding cbvCount..+srvTableCount-1 COMBINED_IMAGE_SAMPLER（见类注）
struct ZrVulkanPsoObj {
    VkPipelineLayout pipelineLayout = VK_NULL_HANDLE;
    VkPipeline pipeline = VK_NULL_HANDLE;
    VkDescriptorSetLayout setLayout = VK_NULL_HANDLE; // set0（CL 按此分配描述符集）
    ZrGraphicsPsoDesc desc; // 创建描述（诊断/断言用）
};

/// 时间戳查询堆载荷（句柄表 payload；模块内部）
struct ZrVulkanQueryPoolObj {
    VkQueryPool pool = VK_NULL_HANDLE;
    uint32_t count = 0;
};

/// 别名堆载荷（TASK-B-R3-02 对位；句柄表 payload；模块内部）。
/// 每别名槽一段 VkDeviceMemory（→ D3D12 ID3D12Heap），placed image 落位
/// vkBindImageMemory(memory, offset)，offset 恒 0——槽内串行复用天然无重叠
struct ZrVulkanAliasHeapObj {
    VkDeviceMemory memory = VK_NULL_HANDLE;
    uint64_t sizeBytes = 0;
};

// =============================================================================
// ZrVulkanQueue —— IZrQueue 实现（真实 VkQueue；Submit → vkQueueSubmit）
// 提交语义：等交换链 imageAvailable 信号量（有挂起 acquire 时）→ 信号
// renderFinished（有挂起呈现时）+ 每命令表 fence。与 IZrSwapchain 的握手经
// 设备侧挂起状态完成（提交/呈现契约：Acquire → 记录 → Submit → Present）
// =============================================================================
class ZrVulkanQueue final : public IZrQueue {
public:
    ZrVulkanQueue(ZrVulkanDevice* owner, ZrQueueType type)
        : m_owner(owner), m_type(type)
    {
    }

    ZrQueueType GetQueueType() const override { return m_type; }
    void Submit(ZrCommandList commandList) override;

private:
    ZrVulkanDevice* m_owner;
    ZrQueueType m_type;
};

// =============================================================================
// ZrVulkanSwapchain —— IZrSwapchain 实现（真实 VkSwapchainKHR）
//   - AcquireNextImage → vkAcquireNextImageKHR（imageAvailable 信号量；等待
//     发生在 Submit 而非 Acquire——VK 侧标准做法，对位 D3D12 per-buffer fence）
//   - Present          → vkQueuePresentKHR（wait renderFinished；PresentMode
//     表面不支持时创建期已降级 Fifo）
//   - Recreate         → TASK-B-R9-09：真重建（OUT_OF_DATE 恢复路径）。帧闸
//     needsRecreate 粘性 → 下一帧 Acquire 入口自动重建（失败回粘性不挂死）；
//     旧链视图/信号量销毁 → oldSwapchain 传参建新链 → 重枚举后台缓冲 →
//     台账推进 + 清粘性。依赖面（谁引用旧链图像）见 Recreate() 实现注。
// =============================================================================
class ZrVulkanSwapchain final : public IZrSwapchain {
public:
    explicit ZrVulkanSwapchain(ZrVulkanDevice* owner)
        : m_owner(owner)
    {
    }

    ~ZrVulkanSwapchain() override;

    uint32_t AcquireNextImage() override;
    ZrImage GetCurrentImage() override;
    void Present() override;

private:
    friend class ZrVulkanDevice;

    ZrVulkanDevice* m_owner;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    VkSemaphore imageAvailable = VK_NULL_HANDLE; // 单信号量（验证程序串行帧循环）
    std::vector<VkSemaphore> renderFinished;     // 每后台缓冲一个呈现等待信号量
    std::vector<ZrImage> m_backbufferImages;
    ZrPresentMode m_presentMode = ZrPresentMode::Fifo;
    uint32_t m_currentIndex = 0; // Acquire 返回的当前帧索引（Present 用）

    // ---- TASK-B-R9-09：真重建 --------------------------------------------------
    /// 真重建（OUT_OF_DATE 恢复路径）。成功 = true 并清除帧闸粘性标记；
    /// 失败（最小化零尺寸/能力查询失败/建链失败）= false，粘性保留，旧链尽量
    /// 保持完好（建链失败时旧链未销毁，下一帧入口可重试）。
    bool Recreate();

    ZrSwapchainDesc m_desc;              // 建链描述（重建按其格式/呈现模式/缓冲数）
    zr::SwapchainResizeLedger m_ledger;  // 尺寸台账（取证行 serial=N→M 的数据源）
};

// =============================================================================
// ZrVulkanDevice —— IZrDevice 实现（Vulkan 后端全量）
// =============================================================================
class ZrVulkanDevice final : public IZrDevice {
public:
    static constexpr uint32_t kBindlessCapacity = 8192; // 全局 bindless 大 set 每绑定槽数
        // （sampled 与 storage 各一 binding；D3D12 侧 16384 混编——VK 侧按上限裁半，
        //  契约不变：槽位即 GPU 索引）

    /// 创建设备（失败返回 nullptr）：validation layer 可用则开启（ERROR 消息计数）；
    /// 要求物理设备支持 Vulkan 1.3（动态渲染核心化前提）
    static ZrVulkanDevice* Create();

    ~ZrVulkanDevice() override;

    // ---- IZrDevice：创建 -------------------------------------------------------
    ZrBuffer CreateBuffer(const ZrBufferDesc& desc, const void* initData = nullptr) override;
    ZrImage CreateImage(const ZrImageDesc& desc) override;
    ZrImageView CreateImageView(const ZrImageViewDesc& desc) override;
    ZrSampler CreateSampler(const ZrSamplerDesc& desc) override;
    ZrShader CreateShader(const ZrShaderDesc& desc) override; // SPIR-V → VkShaderModule
    ZrPipelineState CreatePipelineState(const ZrGraphicsPsoDesc& desc) override;
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
    void WaitIdle() override;
    uint32_t GetDebugErrorCount() override;

    // ---- IZrDevice：别名堆 + placed resource + 统计 -------------------------------
    ZrAliasHeap CreateAliasHeap(uint64_t sizeBytes) override;
    uint64_t GetImagePlacementSize(const ZrImageDesc& desc) override;
    ZrImage CreateImageInHeap(const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset) override;
    ZrDeviceStats GetDeviceStats() override;
    void Destroy(ZrAliasHeap handle) override;

    // ---- 原生扩展点（模块内部与验证程序使用；同 d3d12 后端 D3D12*Of 家族）----------
    VkInstance VulkanInstance() const { return m_instance; }
    VkPhysicalDevice VulkanPhysicalDevice() const { return m_physicalDevice; }
    VkDevice VulkanDeviceHandle() const { return m_device; }
    VkQueue VulkanQueueHandle() const { return m_queue; }
    VkCommandBuffer VulkanNativeCmdOf(ZrCommandList cmd) const;
    VkImage VulkanImageOf(ZrImage image) const;
    VkBuffer VulkanBufferOf(ZrBuffer buffer) const;
    bool IsValidationEnabled() const { return m_validationEnabled; }
    uint32_t GraphicsQueueFamily() const { return m_queueFamily; }

    /// 验证层 ERROR 消息计数递增（debug 回调专用；公开入口避免友元噪音）
    void NoteValidationError() { m_validationErrorCount += 1u; }

    /// TASK-R10-05 B-5：屏障批次截断累计（命令表 EmitBarriers 写、GetDeviceStats 读）。
    /// 单次 ResourceBarrier 超过后端单批上限时，多出的屏障被丢弃 = 缺失同步
    /// （花屏/脏读）——计数 + warning 让"本帧同步不可信"可被上层判定。
    void NoteBarrierTruncation(uint32_t dropped) { m_barrierTruncations += dropped; }

    /// 提交命令表（IZrQueue::Submit 的实际落点；→ vkQueueSubmit + CL fence 信号）
    void SubmitCommandList(ZrCommandList handle);

    /// 深度清屏扩展点（对位验证程序 D3D12 路径的 ClearDepthStencilView 原生调用；
    /// RHI 基座尚无 ClearDepth 命令）。内部做 DS 附件布局 → TRANSFER_DST → 清屏 →
    /// 回落布局的往返屏障（vkCmdClearDepthStencilImage 的布局合法域约束）
    void RecordClearDepth(ZrCommandList cmd, ZrImageView dsv, float depthValue);

    /// 写入全局 bindless 大 set 的单槽（SRV=SAMPLED_IMAGE binding0 / UAV=STORAGE_IMAGE
    /// binding1；CreateImageView 内部调用；命令表 SetGraphicsBindlessSrv 亦经此改写）
    void WriteBindlessDescriptor(uint32_t slot, VkImageView view, bool storage);

    /// 分配一张根绑定快照用的描述符集（TASK-H-05 F1；命令表 RebindDescriptorSet 的唯一
    /// 分配点，单测亦经此驱动真实失败）。
    /// 失败（池耗尽/池碎片/布局不兼容 → 典型 VK_ERROR_OUT_OF_POOL_MEMORY）时累加设备级
    /// 失败计数并返回 VK_NULL_HANDLE——计数经 IZrDevice::GetDeviceStats 的
    /// descriptorSetAllocFailureCount 读出，故"画错"不再是零信号事件。
    /// 不在库内 printf/abort（与 D3D12 Present 失败同策：只记账，由上层判定）。
    VkDescriptorSet AllocateRootDescriptorSet(
        VkDescriptorPool pool, VkDescriptorSetLayout layout);

    /// 设备级 Point/Clamp 静态采样器（SRV 描述符表的固定采样器，对位 D3D12 s_i）
    VkSampler PointClampSampler() const { return m_pointClampSampler; }

    /// 视图是否为深度图采样视图（TASK-B-R7-02：SRV 描述符布局选择依据——
    /// 深度图（CSM 阴影图）绘制时处于 DEPTH_STENCIL_READ_ONLY，描述符布局
    /// 必须一致；非深度图走 SHADER_READ_ONLY）
    bool ViewIsDepthSampling(ZrVulkanViewObj* view) const;

    /// 挂起 acquire 状态（ZrVulkanSwapchain::AcquireNextImage 置位；Submit 消费）
    VkSemaphore PendingAcquireSemaphore() const { return m_pendingAcquire; }
    void SetPendingAcquire(VkSemaphore semaphore, VkSemaphore presentSignal, uint32_t imageIndex)
    {
        m_pendingAcquire = semaphore;
        m_pendingPresentSignal = presentSignal;
        m_pendingImageIndex = imageIndex;
    }
    uint32_t PendingImageIndex() const { return m_pendingImageIndex; }

    // ---- 帧闸（TASK-R10-03 A-5/A-6）-----------------------------------------
    // 交换链 Acquire / 队列 Submit / 交换链 Present 三处共同驱动同一台状态机：
    //   · acquire 失败/过期 → 本帧不提交、不呈现（不登记永不 signal 的信号量）
    //   · submit 失败       → 本帧不呈现（Present 会等一个永不 signal 的信号量）
    //   · 需重建标记是粘性的，直到交换链真的重建（OnSwapchainRecreated）
    void OnFrameAcquire(zr::VkOutcome outcome);
    void OnFramePresent(zr::VkOutcome outcome);
    void OnFramePresentSkipped();
    bool ShouldSubmitThisFrame() const { return m_frameGate.CanSubmit(); }
    bool ShouldPresentThisFrame() const { return m_frameGate.CanPresent(); }
    void OnSwapchainRecreated() { m_frameGate.OnSwapchainRecreated(); }
    const zr::FrameGate& FrameGateState() const { return m_frameGate; }

    // ---- 交换链真重建（TASK-B-R9-09）------------------------------------------
    /// 手动触发真重建（正常路径是帧闸粘性 → Acquire 入口自动重建；此入口供
    /// 验证程序/API 强制 oldSwapchain 重建路径用）。语义同 ZrVulkanSwapchain::Recreate：
    /// 成功清除粘性标记并返回 true；失败（最小化零尺寸等）粘性保留返回 false。
    bool RecreateSwapchain(ZrSwapchain handle);

    /// 交换链尺寸台账快照（取证行 `VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)`
    /// 的数据源；from/to 为最近一次成功重建的前后尺寸，serial 为成功重建次数，
    /// failedAttempts 为失败尝试累计）。句柄无效返回 false。
    bool GetSwapchainResizeInfo(ZrSwapchain handle, zr::SwapchainResizeLedger* out) const;

private:
    friend class ZrVulkanCommandList;
    friend class ZrVulkanSwapchain;

    ZrVulkanDevice() = default;

    // ---- 句柄载荷解析（stale 句柄返回 nullptr）------------------------------------
    ZrVulkanImageObj* ResolveImage(ZrImage handle) const;
    ZrVulkanBufferObj* ResolveBuffer(ZrBuffer handle) const;
    ZrVulkanViewObj* ResolveView(ZrImageView handle) const;
    ZrVulkanSamplerObj* ResolveSampler(ZrSampler handle) const;
    ZrVulkanQueryPoolObj* ResolveQueryPool(ZrQueryPool handle) const;
    ZrVulkanCommandList* ResolveCommandList(ZrCommandList handle) const;
    ZrVulkanShaderObj* ResolveShader(ZrShader handle) const;
    ZrVulkanPsoObj* ResolvePipelineState(ZrPipelineState handle) const;

    // ---- 内存分配（每资源一 VkDeviceMemory；基座不做子分配）------------------------
    bool AllocateMemory(VkMemoryRequirements reqs, VkMemoryPropertyFlags props,
        VkDeviceMemory* outMemory);

    // ---- 交换链依赖面收口（TASK-B-R9-09）----------------------------------------
    /// 销毁登记表（m_swapchainViews）中 desc.image 挂在该链后台缓冲上的视图
    /// （重建与销毁交换链共用；调用方缓存句柄 stale 化 = 安全失败）。同时剔除
    /// 登记表中已 stale 的条目（保证登记表有界）。
    void DestroySwapchainDependentViews(const ZrVulkanSwapchain& chain);

    // ---- 图像创建公共尾段（CreateImage / CreateImageInHeap 共用）-------------------
    ZrImage FinalizeImageCreation(ZrVulkanImageObj* obj, const ZrImageDesc& desc);

    bool m_validationEnabled = false;
    uint32_t m_validationErrorCount = 0;

    // 描述符集分配失败累计（TASK-H-05 F1）：由 AllocateRootDescriptorSet 写，
    // GetDeviceStats 读——单一写入点 + 单一读出点，不允许再有"只增没人读"的计数器。
    uint32_t m_descriptorSetAllocFailures = 0;

    // 屏障批次截断累计（TASK-R10-05 B-5）：由命令表 EmitBarriers 写、GetDeviceStats 读
    uint32_t m_barrierTruncations = 0;

    // bindless 生效容量（TASK-R10-05 B-4）：Create 时按设备四个 update-after-bind
    // 上限收缩（ZrVulkanBindlessCapacityFor）；默认 = 名义容量（上限足够时不变）。
    uint32_t m_bindlessCapacity = kBindlessCapacity;

    VkInstance m_instance = VK_NULL_HANDLE;
    VkDebugUtilsMessengerEXT m_debugMessenger = VK_NULL_HANDLE;
    VkSurfaceKHR m_surface = VK_NULL_HANDLE; // Win32 surface（按需创建，供交换链枚举）
    VkPhysicalDevice m_physicalDevice = VK_NULL_HANDLE;
    VkDevice m_device = VK_NULL_HANDLE;
    VkQueue m_queue = VK_NULL_HANDLE; // graphics+compute+transfer 单队列
    uint32_t m_queueFamily = 0;
    double m_timestampPeriodNs = 1.0;

    // TASK-B-R9-09：引用交换链后台缓冲图像的视图登记表（依赖面收口的检索结构——
    // 句柄表无遍历接口，CreateImageView 对 swapchainOwned 图像的视图登记于此；
    // 重建/销毁交换链时按"desc.image ∈ 该链后台缓冲"匹配销毁）
    std::vector<ZrImageView> m_swapchainViews;

    // CreateBuffer(initData) 一次性上传链路（staging 中转 → vkCmdCopyBuffer → fence）
    VkCommandPool m_uploadPool = VK_NULL_HANDLE;
    VkCommandBuffer m_uploadCmd = VK_NULL_HANDLE;
    VkFence m_uploadFence = VK_NULL_HANDLE;

    // 全局 bindless 大 set（descriptor indexing；见类注）
    VkDescriptorPool m_bindlessPool = VK_NULL_HANDLE;
    VkDescriptorSetLayout m_bindlessLayout = VK_NULL_HANDLE;
    VkDescriptorSet m_bindlessSet = VK_NULL_HANDLE;
    ZrSlotAllocator m_bindlessSlots { kBindlessCapacity };
    VkSampler m_pointClampSampler = VK_NULL_HANDLE;

    std::unique_ptr<ZrVulkanQueue> m_queues[3]; // Graphics/Compute/Transfer 包装

    // 帧闸（VkResult 统一处理的落点；见 zrhi_vulkan_frame_gate.h）
    zr::FrameGate m_frameGate;
    zr::VkOutcome m_lastAcquireOutcome = zr::VkOutcome::Success;
    uint32_t m_presentSkipLogCount = 0u; // "跳过呈现"日志限流计数

    // 挂起 acquire（交换链 → 提交握手；见 PendingAcquire 注）
    VkSemaphore m_pendingAcquire = VK_NULL_HANDLE;
    VkSemaphore m_pendingPresentSignal = VK_NULL_HANDLE; // renderFinished[当前图]
    uint32_t m_pendingImageIndex = 0;

    // 句柄表与载荷所有权
    ZrHandleTable<ZrBuffer> m_bufferTable;
    ZrHandleTable<ZrImage> m_imageTable;
    ZrHandleTable<ZrImageView> m_imageViewTable;
    ZrHandleTable<ZrSampler> m_samplerTable;
    ZrHandleTable<ZrQueryPool> m_queryPoolTable;
    ZrHandleTable<ZrCommandList> m_commandListTable;
    ZrHandleTable<ZrSwapchain> m_swapchainTable;
    ZrHandleTable<ZrAliasHeap> m_aliasHeapTable;
    ZrHandleTable<ZrShader> m_shaderTable;
    ZrHandleTable<ZrPipelineState> m_psoTable;
};

/// 创建/销毁 Vulkan 后端设备（验证程序入口；失败返回 nullptr）
ZrVulkanDevice* ZrVulkanCreateDevice();
void ZrVulkanDestroyDevice(ZrVulkanDevice* device);
