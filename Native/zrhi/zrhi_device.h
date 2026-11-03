// =============================================================================
// zrhi_device.h —— 设备与命令表抽象接口（TASK-B-R2-01）
//
// 纯虚接口，D3D12/Vulkan 后端按子集逐步填充。
// TASK-B-R2-02 增补（D3D12 基座所需，均为加法扩展）：
//   - ZrSwapchainDesc::windowHandle（→ VkSurfaceKHR / Win32 HWND）
//   - IZrQueue::Submit（→ vkQueueSubmit）
//   - IZrSwapchain（→ vkAcquireNextImageKHR / vkQueuePresentKHR / vkGetSwapchainImagesKHR）
//   - IZrDevice::GetSwapchain / GetDebugErrorCount（→ 验证层消息计数）
// 与 Vulkan 的概念映射全部以注释标注，不引入任何 Vulkan 头依赖：
//   - IZrDevice       → VkDevice（Create*/Destroy* → vkCreate*/vkDestroy*）
//   - IZrCommandList  → VkCommandBuffer（Begin/End/ResourceBarrier/... → vkCmd*）
//   - IZrQueue        → VkQueue（Submit 模型由后端任务补全，此处占位）
//   - 各 Desc POD     → 对应 Vk*CreateInfo（仅保留本阶段所需子集）
//
// TASK-B-R2-03 增补（资源体系 + bindless + GPU 时间戳，均为加法扩展）：
//   - ZrBufferDesc::hostVisible/readback + IZrBuffer（Map/Unmap；UPLOAD/READBACK 用途）
//   - ZrImageViewDesc::viewType（SRV|UAV|RTV|DSV）与格式覆盖开关
//   - IZrDevice::CreateBuffer(desc, initData) / CreateTimestampQueryHeap / ResolveTimestamps
//   - IZrDevice::GetBindlessIndex（SRV/UAV 视图注册全局 shader-visible 堆，槽位回收）
//   - IZrCommandList::WriteTimestamp（→ vkCmdWriteTimestamp）
//
// 错误处理：Vulkan 风格句柄+返回码——Create 失败返回 raw==ZR_HANDLE_INVALID
// 的句柄；不跨 ABI 抛异常。
// =============================================================================
#pragma once

#include <cstddef> // offsetof（ZrDeviceStats 布局护栏）

#include "zrhi/zrhi_barriers.h"
#include "zrhi/zrhi_handles.h"
#include "zrhi/zrhi_types.h"

// =============================================================================
// 描述结构（POD；→ 对应 Vk*CreateInfo 子集）
// =============================================================================

/// 缓冲描述（→ VkBufferCreateInfo；省略 sharingMode——单设备独占假设）
struct ZrBufferDesc {
    uint64_t size = 0;
    ZrBufferUsage usage = ZrBufferUsage::None;
    // 内存域（→ VkMemoryPropertyFlags 子集；D3D12：堆类型选择）：
    //   默认（双 false）= 设备本地 DEFAULT 堆（Map 不可用，经拷贝中转上传/回读）
    //   hostVisible      = UPLOAD 中转堆（host 可写；→ HOST_VISIBLE|HOST_COHERENT）
    //   readback         = READBACK 堆（host 可读；拷贝目标专用；→ HOST_VISIBLE）
    bool hostVisible = false;
    bool readback = false;
};

/// 图像视图类别。Vulkan 无独立视图类型枚举（由 image usage + descriptor 类型共同
/// 决定），此处显式化；对应 VkDescriptorType/附件语义：
///   Srv → VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE（着色器资源）
///   Uav → VK_DESCRIPTOR_TYPE_STORAGE_IMAGE（无序读写）
///   Rtv → 颜色附件（VkRenderingInfo::pColorAttachments）
///   Dsv → 深度模板附件（VkRenderingInfo::pDepthAttachment）
enum class ZrImageViewType : uint32_t {
    Srv,
    Uav,
    Rtv,
    Dsv,
};

/// bindless 无效槽位哨兵（GetBindlessIndex 失败/非 SRV-UAV 视图）
constexpr uint32_t ZR_BINDLESS_INVALID = 0xFFFFFFFFu;

/// 图像描述（→ VkImageCreateInfo；2D 子集）
struct ZrImageDesc {
    ZrFormat format = ZrFormat::RGBA8Unorm;
    uint32_t width = 1;
    uint32_t height = 1;
    uint32_t arrayLayers = 1;
    uint32_t mipLevels = 1;
    ZrSampleCount samples = ZrSampleCount::One;
    ZrImageUsage usage = ZrImageUsage::None;
    ZrImageLayout initialLayout = ZrImageLayout::Undefined; // → VkImageCreateInfo::initialLayout
};

/// 图像视图描述（→ VkImageViewCreateInfo；viewType 为视图类别 2D 子集）
struct ZrImageViewDesc {
    ZrImage image{};
    ZrImageViewType viewType = ZrImageViewType::Srv;
    ZrFormat format = ZrFormat::RGBA8Unorm;  // 与源图像兼容（解释性覆盖）
    bool overrideFormat = false;             // true 时以 format 覆盖源图像格式
    ZrSubresourceRange subresources;         // mip/层窗口（viewType 语义下取 mip 窗口）
};

/// 采样器描述（→ VkSamplerCreateInfo）
struct ZrSamplerDesc {
    ZrFilter magFilter = ZrFilter::Linear;
    ZrFilter minFilter = ZrFilter::Linear;
    ZrFilter mipFilter = ZrFilter::Linear; // mipmap 过滤（Vulkan:mipmapMode）
    ZrSamplerAddressMode addressU = ZrSamplerAddressMode::ClampToEdge;
    ZrSamplerAddressMode addressV = ZrSamplerAddressMode::ClampToEdge;
    ZrSamplerAddressMode addressW = ZrSamplerAddressMode::ClampToEdge;
    float mipLodBias = 0.0f;
    float minLod = 0.0f;
    float maxLod = 3.402823466e+38f; // → VK_LOD_CLAMP_NONE
    uint32_t maxAnisotropy = 1;      // 1 = 关闭各向异性
};

/// 着色器描述（→ VkShaderModuleCreateInfo；TASK-B-R4-03 增补 stage 字段）。
/// code 语义按后端：Vulkan 为 SPIR-V 字节码；D3D12 后端为 DXBC（可经
/// ZrD3D12Device::CompileHlsl 运行时编译产出，见 zrhi_d3d12_device.h）
struct ZrShaderDesc {
    ZrShaderStage stage = ZrShaderStage::Vertex; // VS|PS（v1 图形两阶段）
    const void* code = nullptr;                  // → pCode
    uint64_t codeSize = 0;                       // 字节数（SPIR-V 须为 4 的倍数）
    const char* entryPoint = "main";             // → pName
};

/// 图形 PSO 描述（TASK-B-R4-03；取代 R2-01 的占位 ZrPipelineStateDesc——
/// → VkGraphicsPipelineCreateInfo + VkPipelineLayout 的子集）。
/// 顶点布局 v1 固定语义版：Pos3f+Normal3f+Uv2f 紧凑交错（POSITION/NORMAL/
/// TEXCOORD0，stride 32B）——不暴露输入布局字段，后续任务再开放。
/// 根签名 v1 固定布局：单一根 CBV b0（CBufPerDraw：row_major float4x4 mvp +
/// float4 lightDir + float4 lightColor = 96B 常量；合一单槽方案，D3D12 侧经
/// SetGraphicsRootConstantBufferView 直绑 GPU VA），由后端在
/// CreatePipelineState 内创建（布局说明见 zrhi_d3d12_device.cpp）。
struct ZrGraphicsPsoDesc {
    ZrShader vertexShader {};                  // 必须有效（stage 须为 Vertex）
    ZrShader pixelShader {};                   // 必须有效（stage 须为 Pixel）
    ZrFormat rtvFormat = ZrFormat::BGRA8Unorm; // 颜色目标格式（须 isColor）
    ZrFormat dsvFormat = ZrFormat::Count;      // 深度格式；Count 哨兵 = 无深度附件
    ZrBlendMode blend = ZrBlendMode::Opaque;   // v1 仅 Opaque 预设
    bool depthTest = false;                    // 深度测试（须 dsvFormat 有效）
    bool depthWrite = false;                   // 深度写入（蕴含 depthTest）
    ZrCullMode cullMode = ZrCullMode::Back;    // 背面剔除
    /// 根 CBV 槽数（TASK-B-R5-01 增补；1..5，默认 1 = v1 单槽 b0 不变）：
    /// b0..bN-1 逐槽直绑（SetGraphicsConstantBuffer 的 slot 即根参数序号）。
    /// 簇光照三槽：b0=逐绘常量 / b1=光源数组 / b2=簇 start/count+平坦索引；
    /// 阴影四槽：b3=级联 lightVP+bias；R5-03 五槽：b4=GI 探针数组（pbr_r5.hlsl）
    uint32_t rootCbvCount = 1u;
    /// TASK-B-R5-02 增补（CSM 阴影 depth-only Pass）：
    /// true = 深度-only 管线——忽略 pixelShader/rtvFormat（无 PS、无颜色目标，
    /// NumRenderTargets=0），仅 VS 光栅化写深度；要求 depthTest+depthWrite 均
    /// true 且 dsvFormat 有效（校验见 ZrValidateGraphicsPsoDesc）
    bool depthOnly = false;
    /// TASK-B-R5-02 增补（CSM 阴影 PCF 采样）：
    /// true = 根签名在根 CBV 之后追加 1 个 SRV 描述符表（t0 space0，单描述符，
    /// 指向全局 bindless 堆；经 IZrCommandList::SetGraphicsBindlessSrv 绑定
    /// 槽位）+ 静态采样器 s0（Point/Clamp）。根参数总数 = rootCbvCount + 1。
    bool srvTable = false;
    /// TASK-B-R5-03 增补（SSR 需同时采样 Hi-Z 与场景颜色两张纹理）：
    /// SRV 描述符表数（0 = 关；1 = 仅 t0；2 = t0+t1）。语义优先于 srvTable：
    /// 非 0 时按本字段生效（srvTable=true 且本字段 0 → 按 1 处理，向后兼容）。
    /// 表 i（i < 表数）绑 t_i space0 单描述符 + 静态采样器 s_i（Point/Clamp）；
    /// 经 SetGraphicsBindlessSrv(rootCbvCount + i, view) 绑定。根参数总数
    /// = rootCbvCount + 表数。
    uint32_t srvTableCount = 0;
};

/// 图形 PSO 描述纯逻辑校验（接口层，无后端依赖——mock 单测覆盖；后端
/// CreatePipelineState 入口同样以此拒绝非法描述）：
///   - 缺任一着色器 → 拒绝
///   - rtvFormat 非 isColor（含 Count/深度格式）→ 拒绝
///   - dsvFormat 非 Count 时必须 hasDepth，且"只写不测"矛盾组合拒绝
///   - dsvFormat 为 Count 时开深度测试/写入 → 拒绝
constexpr bool ZrValidateGraphicsPsoDesc(const ZrGraphicsPsoDesc& desc)
{
    if (desc.depthOnly) {
        // TASK-B-R5-02：depth-only 管线——无 PS/无颜色目标；深度测试+写入必须
        // 开启且带有效深度格式（渲染深度-only Pass 的全部前提）
        if (desc.dsvFormat == ZrFormat::Count || !ZrGetFormatTraits(desc.dsvFormat).hasDepth) {
            return false; // 深度-only 必须有深度附件
        }
        return desc.depthTest && desc.depthWrite;
    }
    if (!desc.vertexShader.IsValid() || !desc.pixelShader.IsValid()) {
        return false; // 缺 shader 拒绝
    }
    if (desc.rtvFormat == ZrFormat::Count || !ZrGetFormatTraits(desc.rtvFormat).isColor) {
        return false; // 颜色目标格式非法（Count 哨兵显式拒绝，防 traits 越界查表）
    }
    if (desc.dsvFormat != ZrFormat::Count) {
        if (!ZrGetFormatTraits(desc.dsvFormat).hasDepth) {
            return false; // 深度附件格式必须含深度分量
        }
        if (desc.depthWrite && !desc.depthTest) {
            return false; // 只写不测矛盾
        }
    } else if (desc.depthTest || desc.depthWrite) {
        return false; // 无深度附件却开深度状态
    }
    return true;
}

/// 交换链描述（→ VkSwapchainCreateInfoKHR）
struct ZrSwapchainDesc {
    ZrFormat format = ZrFormat::BGRA8Unorm;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t imageCount = 3; // → minImageCount
    ZrPresentMode presentMode = ZrPresentMode::Fifo;
    void* windowHandle = nullptr; // → surface（Win32: HWND；null = 无呈现窗口，创建失败）
};

/// 渲染 Pass 描述（占位形状；→ 动态渲染 VkRenderingInfo / VkRenderPassBeginInfo）
struct ZrPassDesc {
    static constexpr uint32_t kMaxColorTargets = 8;

    ZrImageView colorTargets[kMaxColorTargets] = {};
    ZrImageLayout colorLayouts[kMaxColorTargets] = {};
    uint32_t colorTargetCount = 0;
    ZrImageView depthTarget = {};
    ZrImageLayout depthLayout = ZrImageLayout::DepthStencilReadOnlyOptimal;
    bool hasDepth = false;
};

/// 设备资源统计（TASK-B-R3-02：别名堆/资源存活计数，供 RG 别名复用实证）
struct ZrDeviceStats {
    uint32_t aliasHeapCount = 0; // 存活别名堆数（→ VkDeviceMemory 绑定计数）
    uint32_t imageCount = 0;     // 存活图像数（含交换链后台缓冲）
    uint32_t bufferCount = 0;    // 存活缓冲数

    // ---- TASK-H-02 D3：Present 结果可观测（D3D12 后端填充；其余后端保持默认）----
    // Present 的 HRESULT 不再被静默丢弃：失败累计 + 最近失败码 + 设备移除粘滞位。
    // 上层（窗口程序/引擎）据此判断是否需要重建设备。
    uint32_t presentFailureCount = 0; // IZrSwapchain::Present 失败次数
    int32_t lastPresentHresult = 0;   // 最近一次失败的 HRESULT（0 = 从未失败）
    bool deviceRemoved = false;       // 曾观察到 DEVICE_REMOVED/RESET/DRIVER_INTERNAL_ERROR

    // ---- TASK-H-05 F1：描述符集分配失败可观测（Vulkan 后端填充；D3D12 侧恒 0）----
    // Vulkan 命令表的 set0 分配失败（池耗尽 → 本帧跳过重绑 → 画面错误）此前只有
    // 一个没人读的私有计数器 + Release 下被 NDEBUG 编译掉的 assert。现由设备
    // （ZrVulkanDevice::AllocateRootDescriptorSet）累加、经 GetDeviceStats 上报：
    // 窗口程序/引擎读 >0 即可判定「本帧渲染不可信」（库内不打印、不中止）。
    uint32_t descriptorSetAllocFailureCount = 0; // 累计 vkAllocateDescriptorSets 失败次数

    // ---- TASK-R10-05 B-5：屏障批次截断可观测（Vulkan 后端填充；D3D12 侧恒 0）----
    // 单次 ResourceBarrier 的批次超过后端单批上限（VK 侧 128）时，多出的屏障被
    // 丢弃 = 缺失同步（花屏/脏读且极难排查）。历史缺陷：静默截断、无日志无计数。
    // 现由 ZrVulkanDevice::NoteBarrierTruncation 累加（库内同时打一条 warning），
    // 窗口程序/引擎读 >0 即可判定「本帧同步不可信」。
    uint32_t barrierTruncationCount = 0; // 累计被截断丢弃的屏障条数（image+buffer）
};

// ---- ABI 布局护栏（TASK-H-05 F1）-------------------------------------------------
// 本结构与别名堆/资源计数一起被后端按「默认构造 + 逐字段赋值」消费（全仓无位置
// 聚合初始化），故尾部追加字段不破坏既有偏移；但"追加"这件事必须被钉住：任何
// 在中间插字段/改字段类型/改顺序的改动都会让下面的断言编译期失败，逼迫改动人
// 明确承认布局变更（并同步更新本块数值与实际 sizeof）。
// 数值来源：TASK-H-05 施工时以编译器诊断实测（改动前 sizeof=24，追加 F1 字段后 28；
// TASK-R10-05 B-5 追加 barrierTruncationCount 后 32）。
static_assert(sizeof(ZrDeviceStats) == 32,
    "ZrDeviceStats 布局变更：字段只在尾部追加，且须同步更新本护栏与实际 sizeof");
static_assert(offsetof(ZrDeviceStats, aliasHeapCount) == 0, "ZrDeviceStats::aliasHeapCount 偏移变更");
static_assert(offsetof(ZrDeviceStats, imageCount) == 4, "ZrDeviceStats::imageCount 偏移变更");
static_assert(offsetof(ZrDeviceStats, bufferCount) == 8, "ZrDeviceStats::bufferCount 偏移变更");
static_assert(offsetof(ZrDeviceStats, presentFailureCount) == 12,
    "ZrDeviceStats::presentFailureCount 偏移变更");
static_assert(offsetof(ZrDeviceStats, lastPresentHresult) == 16,
    "ZrDeviceStats::lastPresentHresult 偏移变更");
static_assert(offsetof(ZrDeviceStats, deviceRemoved) == 20, "ZrDeviceStats::deviceRemoved 偏移变更");
static_assert(offsetof(ZrDeviceStats, descriptorSetAllocFailureCount) == 24,
    "ZrDeviceStats::descriptorSetAllocFailureCount 必须是尾部追加（偏移 24）");
static_assert(offsetof(ZrDeviceStats, barrierTruncationCount) == 28,
    "ZrDeviceStats::barrierTruncationCount 必须是尾部追加（偏移 28）");

// =============================================================================
// IZrBuffer —— 缓冲映射接口（TASK-B-R2-03）
// UPLOAD（hostVisible）缓冲：Map 返回可写内存（→ vkMapMemory 持久映射语义）；
// READBACK 缓冲：Map 返回可读内存（拷贝完成 + WaitIdle 后读取）；
// DEFAULT 堆缓冲：Map 返回 nullptr（设备本地内存不可直接映射，经中转堆拷贝）。
// =============================================================================
class IZrBuffer {
public:
    virtual ~IZrBuffer() = default;

    /// 持久映射（UPLOAD/READBACK；DEFAULT 返回 nullptr）
    virtual void* Map() = 0;

    /// 解除映射（Map 配对；引用由此失效）
    virtual void Unmap() = 0;
};

// =============================================================================
// IZrQueue —— 命令队列（→ VkQueue）
// TASK-B-R2-02：Submit 落地（→ vkQueueSubmit，单命令表/单次提交语义）
// =============================================================================
class IZrCommandList; // 前置声明（IZrDevice::GetCommandList 引用）

class IZrQueue {
public:
    virtual ~IZrQueue() = default;

    /// 队列域（后端据此映射队列族：→ vkGetDeviceQueue 的 familyIndex）
    virtual ZrQueueType GetQueueType() const = 0;

    /// 提交命令表（→ vkQueueSubmit；基座为单命令表单次提交，完成等待走
    /// IZrDevice::WaitIdle，fence/semaphore 细粒度同步由后续任务补全）
    virtual void Submit(ZrCommandList commandList) = 0;
};

// =============================================================================
// IZrSwapchain —— 交换链（→ VkSwapchainKHR；TASK-B-R2-02）
// Acquire/Present 语义对齐 Vulkan（当前帧索引跟踪）：
//   - AcquireNextImage → vkAcquireNextImageKHR（阻塞至该后台缓冲上一次呈现完成）
//   - GetCurrentImage  → vkGetSwapchainImagesKHR[index]
//   - Present          → vkQueuePresentKHR（PresentMode 决定垂直同步/撕裂行为）
// =============================================================================
class IZrSwapchain {
public:
    virtual ~IZrSwapchain() = default;

    /// 取下一张后台缓冲的索引（0..imageCount-1）
    virtual uint32_t AcquireNextImage() = 0;

    /// 当前后台缓冲的图像句柄（Acquire 之后、Present 之前有效）
    virtual ZrImage GetCurrentImage() = 0;

    /// 呈现当前后台缓冲（须在当帧命令表 Submit 之后调用）
    virtual void Present() = 0;
};

// =============================================================================
// IZrDevice —— 抽象设备（→ VkDevice + vkCreate*/vkDestroy* 家族）
// =============================================================================
class IZrDevice {
public:
    virtual ~IZrDevice() = default;

    // ---- 创建（失败返回 raw==ZR_HANDLE_INVALID 的句柄）------------------------
    virtual ZrBuffer CreateBuffer(const ZrBufferDesc& desc, const void* initData = nullptr) = 0; // → vkCreateBuffer + vkCmdCopyBuffer 初始上传
    virtual ZrImage CreateImage(const ZrImageDesc& desc) = 0;               // → vkCreateImage
    virtual ZrImageView CreateImageView(const ZrImageViewDesc& desc) = 0;   // → vkCreateImageView
    virtual ZrSampler CreateSampler(const ZrSamplerDesc& desc) = 0;         // → vkCreateSampler
    virtual ZrShader CreateShader(const ZrShaderDesc& desc) = 0;            // → vkCreateShaderModule
    virtual ZrPipelineState CreatePipelineState(
        const ZrGraphicsPsoDesc& desc) = 0;                                 // → vkCreateGraphicsPipelines
    virtual ZrSwapchain CreateSwapchain(const ZrSwapchainDesc& desc) = 0;   // → vkCreateSwapchainKHR
    virtual ZrCommandList CreateCommandList(ZrQueueType queueType) = 0;     // → vkAllocateCommandBuffers
    virtual ZrQueryPool CreateTimestampQueryHeap(uint32_t queryCount) = 0;  // → vkCreateQueryPool(TIMESTAMP)

    /// 句柄→命令表接口（命令表经句柄统一生命周期管理；后端解析泛代表）
    virtual IZrCommandList* GetCommandList(ZrCommandList handle) = 0;

    /// 句柄→交换链接口（TASK-B-R2-02；生命周期随 Destroy(ZrSwapchain)）
    virtual IZrSwapchain* GetSwapchain(ZrSwapchain handle) = 0;

    /// 句柄→缓冲映射接口（TASK-B-R2-03；生命周期随 Destroy(ZrBuffer)）
    virtual IZrBuffer* GetBuffer(ZrBuffer handle) = 0;

    /// SRV/UAV 视图注册进全局 shader-visible 描述符堆，返回 bindless GPU 槽位索引
    /// （→ VK_DESCRIPTOR_INDEXING / descriptor heap 索引；RTV/DSV 视图或无效句柄
    /// 返回 ZR_BINDLESS_INVALID；Destroy(ZrImageView) 时槽位回收进 free list）
    virtual uint32_t GetBindlessIndex(ZrImageView view) = 0;

    /// 解析时间戳堆（TASK-B-R2-03）：GPU 完成等待 + Resolve 到 host 缓冲，按 GPU
    /// 时间戳频率换算为毫秒写入 outMilliseconds[0..count)，返回实际写入数
    /// （→ vkGetQueryPoolResults + VkPhysicalDeviceLimits::timestampPeriod）
    virtual uint32_t ResolveTimestamps(
        ZrQueryPool heap, double* outMilliseconds, uint32_t count) = 0;

    // ---- 销毁（强类型重载防跨类别误传；→ vkDestroy* / vkFreeCommandBuffers）--
    virtual void Destroy(ZrBuffer handle) = 0;         // → vkDestroyBuffer
    virtual void Destroy(ZrImage handle) = 0;          // → vkDestroyImage
    virtual void Destroy(ZrImageView handle) = 0;      // → vkDestroyImageView
    virtual void Destroy(ZrSampler handle) = 0;        // → vkDestroySampler
    virtual void Destroy(ZrShader handle) = 0;         // → vkDestroyShaderModule
    virtual void Destroy(ZrPipelineState handle) = 0;  // → vkDestroyPipeline(+Layout)
    virtual void Destroy(ZrSwapchain handle) = 0;      // → vkDestroySwapchainKHR
    virtual void Destroy(ZrCommandList handle) = 0;    // → vkFreeCommandBuffers
    virtual void Destroy(ZrQueryPool handle) = 0;      // → vkDestroyQueryPool

    // ---- 队列与同步 ------------------------------------------------------------
    virtual IZrQueue* GetQueue(ZrQueueType type) = 0; // → vkGetDeviceQueue
    virtual void WaitIdle() = 0;                      // → vkDeviceWaitIdle

    /// 调试层累计错误计数（TASK-B-R2-02；→ Vulkan validation layer 错误消息。
    /// D3D12：InfoQueue 中 Severity ≤ Error（含 Corruption）的消息数；读取即
    /// 排空缓存并打印。调试层不可用时返回 0（降级模式，调用方应另行确认））
    virtual uint32_t GetDebugErrorCount() = 0;

    // ---- TASK-B-R3-02：别名堆 + placed resource（RenderGraph 真内存复用）----------
    // 语义 ≈ Vulkan 的 VkDeviceMemory 子分配（alias heap ≈ device memory，
    //  placed image ≈ vkBindImageMemory 到偏移）。D3D12：ID3D12Heap +
    //  CreatePlacedResource。简化模型：每别名槽一个堆，槽内串行复用天然无重叠。

    /// 创建别名堆（→ vkAllocateMemory；D3D12：CreateHeap，DEFAULT 堆类型）。
    /// sizeBytes 为该堆可容纳的最大 placed resource 尺寸（调用方经
    /// GetImagePlacementSize 查询并取槽内最大值；后端按需要对齐向上取整）
    virtual ZrAliasHeap CreateAliasHeap(uint64_t sizeBytes) = 0;

    /// 查询图像描述的落位所需尺寸与对齐（→ vkGetImageMemoryRequirements；
    /// D3D12：GetResourceAllocationInfo().SizeInBytes——placed resource 偏移
    /// 与堆尺寸都必须满足该对齐要求，本模型以每槽一堆 + offset=0 规避子偏移）
    virtual uint64_t GetImagePlacementSize(const ZrImageDesc& desc) = 0;

    /// 在别名堆内创建 placed 图像（→ vkCreateImage + vkBindImageMemory(heap,offset)；
    /// D3D12：CreatePlacedResource，初始态恒 COMMON）。堆须 ≥
    /// GetImagePlacementSize(desc)，offset 按对齐要求由调用方保证（每槽一堆时恒 0）
    virtual ZrImage CreateImageInHeap(
        const ZrImageDesc& desc, ZrAliasHeap heap, uint64_t offset) = 0;

    /// 设备资源统计（别名复用实证：堆数/资源存活数）
    virtual ZrDeviceStats GetDeviceStats() = 0;

    // ---- TASK-B-R3-02：别名堆销毁（→ vkFreeMemory / ID3D12Heap Release）----------
    virtual void Destroy(ZrAliasHeap handle) = 0;
};

// =============================================================================
// IZrCommandList —— 命令表（→ VkCommandBuffer + vkCmd* 记录族）
// =============================================================================
class IZrCommandList {
public:
    virtual ~IZrCommandList() = default;

    /// 开始记录（→ vkBeginCommandBuffer；一次性提交语义）
    virtual void Begin() = 0;

    /// 结束记录（→ vkEndCommandBuffer）
    virtual void End() = 0;

    /// 批量资源屏障：一次调用提交全部图像/缓冲屏障（→ vkCmdPipelineBarrier，
    /// src/dst access+stage 与布局转换均内嵌于各屏障 POD）
    virtual void ResourceBarrier(const ZrImageBarrier* imageBarriers, uint32_t imageBarrierCount,
        const ZrBufferBarrier* bufferBarriers, uint32_t bufferBarrierCount) = 0;

    /// split barrier 半边提交（TASK-B-R3-02；D3D12 原生语义）：
    ///   beginOnly=true  → D3D12_RESOURCE_BARRIER_FLAG_BEGIN_ONLY（写 Pass 结束后
    ///     立即发起，GPU 可提前开始刷新缓存——等待发生在 end 边界而非 begin 边界）
    ///   beginOnly=false → D3D12_RESOURCE_BARRIER_FLAG_END_ONLY（读 Pass 前补可见性）
    /// Vulkan 对应：vkCmdPipelineBarrier 无法拆分，本条为 D3D12 侧映射点
    virtual void ResourceBarrierSplit(
        const ZrImageBarrier* imageBarriers, uint32_t count, bool beginOnly) = 0;

    /// 缓冲拷贝（单区域；→ vkCmdCopyBuffer 的单个 VkBufferCopy{srcOffset,dstOffset,size}）
    virtual void CopyBuffer(ZrBuffer src, ZrBuffer dst, uint64_t srcOffset, uint64_t dstOffset,
        uint64_t size) = 0;

    /// 清空颜色图像（→ vkCmdClearColorImage；layout 须为 General/TransferDstOptimal
    /// 或后端清屏路径的等价布局——如 D3D12 RTV 清屏的 ColorAttachmentOptimal），
    /// range 指定整资源或子资源
    virtual void ClearColorImage(ZrImage image, ZrImageLayout layout, const float colorRGBA[4],
        const ZrSubresourceRange& subresources) = 0;

    /// 开始渲染 Pass（占位声明；→ 动态渲染 vkCmdBeginRendering / VkRenderPassBeginInfo）
    virtual void BeginPass(const ZrPassDesc& pass) = 0;

    /// 结束渲染 Pass（占位声明；→ vkCmdEndRendering / vkCmdEndRenderPass）
    virtual void EndPass() = 0;

    /// 写 GPU 时间戳（→ vkCmdWriteTimestamp；TIMESTAMP_TOP 等价——EndQuery 在
    /// 记录点写入；须处于 Begin..End 记录态，index < CreateTimestampQueryHeap(n)）
    virtual void WriteTimestamp(ZrQueryPool heap, uint32_t queryIndex) = 0;

    // ---- TASK-B-R4-03：图形绘制命令（接口层加法扩展）---------------------------
    /// 绑定图形 PSO（同时绑定其根签名；v1 图元拓扑固定为三角形列表——
    /// → vkCmdBindPipeline + 拓扑预设；D3D12：SetGraphicsRootSignature +
    /// SetPipelineState + IASetPrimitiveTopology(TRIANGLELIST)）
    virtual void SetPipelineState(ZrPipelineState pso) = 0;

    /// 绑定顶点缓冲（槽 0 起；v1 固定顶点布局 stride=32B 由 PSO 侧约定——
    /// → vkCmdBindVertexBuffers；D3D12：IASetVertexBuffers）
    virtual void SetVertexBuffers(const ZrBuffer* buffers, uint32_t count) = 0;

    /// 绑定索引缓冲（v1 固定 R32Uint——→ vkCmdBindIndexBuffer；D3D12：
    /// IASetIndexBuffer）
    virtual void SetIndexBuffer(ZrBuffer buffer) = 0;

    /// 绑定图形根常量缓冲（slot = 根签名 CBV 槽位；v1 固定布局仅 b0 → slot 0——
    /// D3D12：SetGraphicsRootConstantBufferView 直绑 GPU VA；Vulkan 对应
    /// vkCmdBindDescriptorSets 的 UNIFORM_BUFFER 槽位）
    virtual void SetGraphicsConstantBuffer(uint32_t slot, ZrBuffer buffer) = 0;

    /// 绑定图形根 SRV 描述符表（TASK-B-R5-02 增补；PSO 须以 srvTable=true 创建，
    /// rootSlot = 描述符表根参数序号（= rootCbvCount）——D3D12：
    /// SetGraphicsRootDescriptorTable 指向全局 bindless 堆内该视图槽位；
    /// Vulkan 对应采样图描述符集绑定）
    virtual void SetGraphicsBindlessSrv(uint32_t rootSlot, ZrImageView view) = 0;

    /// 索引化实例绘制（→ vkCmdDrawIndexed(indexCountPerInstance, instanceCount,
    /// startIndexLocation, baseVertexLocation, startInstanceLocation)）
    virtual void DrawIndexedInstanced(uint32_t indexCountPerInstance, uint32_t instanceCount,
        uint32_t startIndexLocation, int32_t baseVertexLocation,
        uint32_t startInstanceLocation) = 0;

    /// 绑定渲染目标（colorRtv 须为 RTV 视图；depthDsv 须为 DSV 视图，传无效句柄
    /// = 无深度附件——→ 动态渲染 VkRenderingInfo 的附件绑定；D3D12：
    /// OMSetRenderTargets）
    virtual void SetRenderTarget(ZrImageView colorRtv, ZrImageView depthDsv) = 0;

    /// 设置视口与剪裁矩形（左上 (0,0) 起 width×height 全区域，深度 0..1——
    /// → vkCmdSetViewport + vkCmdSetScissor；D3D12：RSSetViewports + RSSetScissorRects）
    virtual void SetViewportScissor(uint32_t width, uint32_t height) = 0;
};
