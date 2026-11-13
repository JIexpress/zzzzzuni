// =============================================================================
// fg_ffx.h —— ZRender 帧生成模块公共头（TASK-B-R8-02）
//
// 定位（与 UpscalerChain 的关系——重要）：帧生成（FrameGeneration，FG）是**独立
// 呈现层组件**，不进 IUpscaler/UpscalerChain：
//   - 上采样（FSR/DLSS）发生在帧内的 pass 链中（渲染分辨率 → 呈现分辨率），
//     属 UpscalerChain 管辖（zupscaler.h）；
//   - FG 发生在 present 层：SDK 代理交换链接管呈现时序，每个真帧插 1 帧
//     （present 回调逐帧合成+计数）。两者正交可叠加（先上采样后插帧），但
//     本模块只负责后者。
//
// 后端：AMD FidelityFX SDK v2.3.0 预编译 amd_fidelityfx_framegeneration_dx12.dll
// （vendor 记录见 ThirdParty/ffx-fsr/SOURCE.md "TASK-B-R8-02 追加"节），运行时
// LoadLibrary（无导入库），与 fsr_upscaler.cpp 同一集成模式。
//
// 集成契约（官方 frame-interpolation-api.md / frame-interpolation-swap-chain.md，
// tag v2.3.0）：
//   1. FG 效果上下文：ffxCreateContextDescFrameGeneration → pNext
//      ffxCreateContextDescFrameGenerationVersion（FFX_FRAMEGENERATION_VERSION）
//      → pNext ffxCreateBackendDX12Desc；
//   2. FG 交换链上下文：ffxCreateContextDescFrameGenerationSwapChainForHwndDX12
//      → pNext ffxCreateContextDescFrameGenerationSwapChainVersionDX12
//      （FFX_FRAMEGENERATION_SWAPCHAIN_DX12_VERSION）——返回包装 IDXGISwapChain4，
//      应用所有 Present 走包装交换链；
//   3. 每帧：Configure（frameID 严格 +1）→ PrepareV2 派发（深度+MV+相机）→
//      Present（交换链回调 frameGenerationCallback 派发插帧工作 + presentCallback
//      合成 UI/计数 isGeneratedFrame）；
//   4. 关停顺序：Configure(frameGenerationEnabled=false) → 销毁 FG 上下文 →
//      销毁交换链上下文 → CloseHandle(frame latency waitable)。
//
// 能力门控：查询以"provider 枚举 + ffxCreateContext 返回码"为事实——SDK 拒绝时
// 如实报告错误码，不伪装成功。**不要按硬件代际预先判定**：TASK-B-R9-01 实测
// AMD Radeon 780M（RDNA3 核显）在本机完全可用——`--fg` 输出
// `support=3 rc=0 provider='3.1.6'`、`real=60 gen=60`；本行原注释称"核显预期被拒"
// 与实测矛盾，已按实证纠正（仅注释，逻辑未改）。
//
// C ABI 纪律：本模块无 C 导出（窗口验证程序/单测静态链接使用），跨 DLL 的 FG
// 入口留待 zplugin 任务。接口以 void* 携带原生指针（ID3D12Device*、HWND、
// IDXGIFactory*、ID3D12CommandQueue*、IDXGISwapChain4*），D3D12 资源状态以数值
// 传递——公共头不依赖 d3d12.h/ffx 头。
// =============================================================================
#pragma once

#include <atomic>
#include <cstdint>

namespace zr {

// =============================================================================
// 能力探测（纯数据 + 纯分类函数）
// =============================================================================

/// FG 能力结论（ffx 运行时的真实回答，不猜硬件代际）
enum class FrameGenSupport : uint32_t {
    Unavailable = 0,       // FG dll 不可加载 / 导出不齐（运行时缺席）
    NoProvider = 1,        // dll 可加载但 provider 枚举为空（该设备无 FG provider）
    RejectedByRuntime = 2, // ffxCreateContext 明确拒绝（returnCode 为确切口）
    Available = 3,         // 上下文创建成功
};

/// 能力探测结果（证据行直出：support + 确切错误码 + provider 名）
struct FrameGenProbe {
    FrameGenSupport support = FrameGenSupport::Unavailable;
    uint32_t returnCode = 0;    // ffxReturnCode_t（0 = 未适用/OK）
    char providerName[32] = {}; // 枚举到的首个 FG provider 名；未枚举到为空串
};

/// provider 枚举数 + 创建返回码 → 结论（纯函数；QuerySupport/Create 共用口径）：
///   枚举 0 → NoProvider；rc != OK → RejectedByRuntime；否则 Available
FrameGenSupport ZupFgClassifySupport(uint32_t providerCount, uint32_t createReturnCode);

/// ffx 返回码 → 稳定名（"OK"/"NO_PROVIDER"/"ERROR_PARAMETER"/…；未知返回
/// "UNKNOWN_<rc>"）。证据行/单测共用。
const char* ZupFgReturnCodeName(uint32_t rc);

// =============================================================================
// 纯函数（单测锁定；语义与 AMD 官方实现对齐）
// =============================================================================

/// FfxApiResourceState → D3D12_RESOURCE_STATES（数值；与官方 backend/dx12/
/// ffx_dx12.cpp ffxGetDX12StateFromResourceState 对齐；未知值 → COMMON）
uint32_t ZupFgMapResourceState(uint32_t ffxState);

/// 创建描述校验（纯函数）：display 与 render 任一维 0 → false；
/// render > display → false（FG 契约：输入色为 displaySize，本集成不放大）；
/// backBufferFfxFormat == 0（UNKNOWN）→ false。
bool ZupFgValidateCreateDesc(uint32_t displayWidth, uint32_t displayHeight,
    uint32_t renderWidth, uint32_t renderHeight, uint32_t backBufferFfxFormat);

/// 帧计数翻倍判定（纯函数；"real=N gen=M" 证据行口径）：
///   real == 0 → false；generated ∈ [real-1, real] → true（每真帧恰 1 插帧，
///   尾帧 pacing 差 1 容忍）。
bool ZupFgFrameCountsDoubled(uint64_t realPresents, uint64_t generatedPresents);

// =============================================================================
// FrameGenerator —— FG 封装（Create / Configure / Present 包装 / Shutdown）
// =============================================================================

/// FG 效果上下文创建描述（FFX 头契约的 POD 载体；格式为 FfxApiSurfaceFormat 数值）
struct FrameGenCreateDesc {
    uint32_t displayWidth = 0;         // 呈现分辨率（= 交换链尺寸）
    uint32_t displayHeight = 0;
    uint32_t renderWidth = 0;          // 渲染分辨率（本集成 ≤ display）
    uint32_t renderHeight = 0;
    uint32_t backBufferFfxFormat = 0;  // FfxApiSurfaceFormat 数值（B8G8R8A8_UNORM 等）
    bool displayResolutionMv = true;   // MV 为呈现分辨率（本集成为真）
    // F2（TASK-B-R9-03）：默认**false**。MV 是否含当帧 jitter 抖动——只有调用方
    // 用"含 jitter 的 mvp"算 MV 时才置 true（窗口程序 --fg 显式置 true，行为不变）；
    // Unity/URP 的 MV 用 _NonJitteredViewProjMatrix 算（不含抖动），必须置 false，
    // 否则 FFX 会反解一份并不存在的抖动（每帧 0.5px 级重投影误差）。
    bool jitterCancellation = false;
    // 输入深度是否为 reversed-Z（1=近）。Unity 本机 usesReversedZBuffer=True →
    // Unity 路径置 true；窗口程序的标准 LH 投影保持 false（行为不变）。
    bool depthInverted = false;
    bool debugChecking = true;         // FFX_FRAMEGENERATION_ENABLE_DEBUG_CHECKING
    void* nativeDevice = nullptr;      // ID3D12Device*
};

/// PrepareV2 派发参数（ffxDispatchDescFrameGenerationPrepareV2 的 POD 载体；
/// 资源以原生指针传入，状态为 FfxApiResourceState 位组合）
struct FrameGenPrepareArgs {
    void* nativeCmdList = nullptr;                 // ID3D12GraphicsCommandList*（已 Begin）
    void* nativeDepth = nullptr;                   // ID3D12Resource*（渲染分辨率）
    void* nativeMotionVectors = nullptr;           // ID3D12Resource*（渲染分辨率）
    uint32_t depthState = 0;                       // FfxApiResourceState 位组合
    uint32_t mvState = 0;
    float jitterX = 0.0f;                          // 当前帧 jitter（渲染分辨率亚像素）
    float jitterY = 0.0f;
    float motionVectorScaleX = 0.0f;               // MV 像素换算（ZupMotionVectorScale 同口径）
    float motionVectorScaleY = 0.0f;
    float frameTimeDeltaMs = 16.666f;
    float cameraFovY = 0.0f;
    float cameraNear = 0.0f;
    float cameraFar = 0.0f;
    bool reset = false;                            // 相机不连续/历史失效
    uint64_t frameId = 0;                          // 与 ConfigureFrame 的 frameId 一致（严格 +1）
    float cameraPosition[3] = {0.0f, 0.0f, 0.0f};  // PrepareV2 必填相机位姿
    float cameraUp[3] = {0.0f, 1.0f, 0.0f};
    float cameraRight[3] = {1.0f, 0.0f, 0.0f};
    float cameraForward[3] = {0.0f, 0.0f, 1.0f};
};

/// present 回调的计数出口（FG 交换链对每个呈现帧调用一次；isGeneratedFrame
/// 区分真帧/插帧——帧计数翻倍证据的直接来源）
using FgPresentCountFn = void (*)(bool isGeneratedFrame, void* userCtx);

/// present 回调的旁路观察者（TASK-B-R9-03）：SDK 合成完成后（output 处于
/// COPY_DEST、内容已是本帧最终画面）回调，供调用方在读回资源上做像素校验。
/// 观察者**可以**在该命令表上再记录命令（须把 output 归还到 COPY_DEST 状态）。
/// 运行在 SDK 的 present 线程；必须短小、不得阻塞、不得抛异常。nullptr = 零开销。
using FgPresentObserveFn = void (*)(bool isGeneratedFrame, void* nativeCmdList,
    void* outputResource, void* userCtx);

class FrameGenerator {
public:
    FrameGenerator();
    ~FrameGenerator();
    FrameGenerator(const FrameGenerator&) = delete;
    FrameGenerator& operator=(const FrameGenerator&) = delete;

    /// 能力探测：加载 FG dll → ffxQuery 枚举 FRAMEGENERATION provider。device 可为
    /// null（无硬件回退），但 FFX 对 null 设备的枚举返回截断列表——同族行为已在
    /// UPSCALER 族实测证伪"null 与真机一致"（RX 9060 XT 2026-11-13：null=2 vs 真设备
    /// =4 含 4.1.2，见 TASK-B-FSR-01）；FRAMEGENERATION 族未做双设备对照，null 口径
    /// 的结论只能当"未检测"。探测后即释放；静默（verbose 证据行由窗口程序负责）。
    static FrameGenProbe QuerySupport(void* nativeDevice);

    /// 创建 FG 效果上下文（desc 链：FrameGeneration → Version → BackendDX12）。
    /// 失败打印确切错误码并保持未创建态。
    bool Create(const FrameGenCreateDesc& desc);

    /// 创建 FG 代理交换链（ForHwnd 路径：FG dll 自建底层交换链并包装）。
    /// hwnd/dxgiFactory/gameQueue 为原生句柄（HWND/IDXGIFactory*/ID3D12CommandQueue*）。
    /// 成功时 *outWrappedSwapchain = 包装后的 IDXGISwapChain4*（应用唯一 Present 入口）、
    /// *outFrameLatencyWaitable = GetFrameLatencyWaitableObject 句柄（帧起步等待；
    /// Shutdown 按官方契约统一 CloseHandle）。
    ///
    /// A-3（TASK-R10-05，本仓对 R10-02 审计 A-3 的修复点；位置经审计验收更正到
    /// fg_ffx.cpp——简报原写的 zfg_probe_wrap.cpp 无此代码）：`requestedDesc1` /
    /// `requestedFullscreenDesc1` 以 const void* 携带宿主原始 desc（DXGI_SWAP_CHAIN_
    /// DESC1* / DXGI_SWAP_CHAIN_FULLSCREEN_DESC*），非空时交换链创建 desc 以宿主值
    /// 为基底（BufferUsage/SwapEffect/Scaling/AlphaMode/SampleDesc 全保留），仅强制
    /// FG 必需位 FRAME_LATENCY_WAITABLE_OBJECT；Width/Height/Format/BufferCount 仍
    /// 以显式参数为准（调用方已校验）。空指针 = 与历史版本逐位一致（窗口程序 --fg
    /// 不受影响）。公共头保持 dxgi-free：类型经 void* 隐藏，解释权在 fg_ffx.cpp。
    bool CreateFgSwapchainForHwnd(void* hwnd, void* dxgiFactory, void* gameQueue,
        uint32_t width, uint32_t height, uint32_t dxgiFormat, uint32_t bufferCount,
        void** outWrappedSwapchain, void** outFrameLatencyWaitable,
        const void* requestedDesc1 = nullptr, const void* requestedFullscreenDesc1 = nullptr);

    /// 每帧配置（官方契约：每帧调用；frameId 严格 +1，否则 FG 内部状态复位）。
    /// presentCallback 由本类静态实现承担（合成拷贝 + 计数转发 cb），
    /// frameGenerationCallback 直转发 ffxDispatch（官方推荐回调路径）。
    bool ConfigureFrame(void* wrappedSwapchain, uint64_t frameId, bool enabled,
        FgPresentCountFn cb, void* cbUserCtx);

    /// PrepareV2 派发（在调用方已 Begin 的命令表上记录深度/MV 准备命令；须在
    /// Submit 前、Present 前调用）。
    bool DispatchPrepare(const FrameGenPrepareArgs& args);

    /// 像素校验旁路（见 FgPresentObserveFn；nullptr 关闭）。TASK-B-R9-03。
    void SetPresentObserver(FgPresentObserveFn fn, void* userCtx);

    /// Present 包装：经代理交换链呈现（真帧+插帧；FG 关闭时退化为普通 present
    /// + 一次内部合成拷贝）。返回 false = Present HRESULT 失败。
    bool PresentFrame(void* wrappedSwapchain, uint32_t syncInterval);

    /// 等待代理交换链的 pacing/插帧 GPU 工作落盘（ffxDispatchDescFrameGeneration
    /// SwapChainWaitForPresentsDX12；统计读数前调用）。
    void WaitForPresents();

    /// 关停（幂等）：Configure(enabled=false) → 销毁 FG 上下文 → 销毁交换链
    /// 上下文 → CloseHandle(waitable)。顺序为官方 Shutdown 契约（防
    /// OBJECT_DELETED_WHILE_STILL_IN_USE）。
    void Shutdown();

    bool IsCreated() const { return m_fgContext != nullptr; }
    bool HasSwapchainContext() const { return m_swapchainContext != nullptr; }

    /// 最后一次 ffx 调用的返回码（诊断）
    uint32_t LastReturnCode() const { return m_lastRc; }

    /// 逐帧统计（present 回调计数；原子——回调在 SDK pacing 线程触发）
    uint64_t RealPresents() const;
    uint64_t GeneratedPresents() const;

private:
    friend FrameGenerator* CreateFrameGeneratorForTests(const void*);
    void* m_fgContext = nullptr;        // ffxContext（FG 效果）
    void* m_swapchainContext = nullptr; // ffxContext（FG 代理交换链）
    void* m_runtime = nullptr;          // FgRuntime*（fg_ffx.cpp 内部；mock 注入时为
                                        //   module==nullptr 的同型结构）
    void* m_shared = nullptr;           // FgFrameShared*（FFX 蹦床 ↔ 计数器/出口桥；
                                        //   fg_ffx.cpp 内部类型，Create 时建、析构释放）
    void* m_wrappedSwapchain = nullptr; // 代理交换链（Present 入口；生命周期归上下文）
    void* m_waitable = nullptr;         // frame latency waitable（Shutdown CloseHandle）
    uint32_t m_displayWidth = 0;        // Create 时锁定的呈现分辨率（generationRect）
    uint32_t m_displayHeight = 0;
    uint32_t m_renderWidth = 0;         // Create 时锁定的渲染分辨率（PrepareV2 renderSize）
    uint32_t m_renderHeight = 0;
    uint32_t m_lastRc = 0;
    std::atomic<uint64_t> m_realPresents{0}; // 回调在 SDK pacing 线程触发 → 原子
    std::atomic<uint64_t> m_genPresents{0};
    uint64_t m_frameId = 0;
};

/// 仅单测注入：以给定 ffx 五函数表（const ffxFunctions*，ThirdParty 头类型经
/// void* 隐藏）构造 FrameGenerator——Create/Configure/Dispatch/Present 全部走
/// mock 表，Present 的交换链调用被记账替代。真机路径（QuerySupport/Create）
/// 不经此入口。返回堆实例；用完 delete（Shutdown 幂等已由析构兜底）。
FrameGenerator* CreateFrameGeneratorForTests(const void* ffxFuncsTable);

} // namespace zr
