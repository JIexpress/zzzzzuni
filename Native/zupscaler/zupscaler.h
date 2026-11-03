// =============================================================================
// zupscaler.h —— ZRender 超分模块公共头（TASK-B-R6-01 建框架 / B-R6-02 扩 FSR4+DLSS）
//
// 组成：
//   - IUpscaler：后端抽象（能力矩阵 / Initialize / Evaluate / Resize / Shutdown）
//   - UpscalerChain：注册后端表 → 按设备能力自动选优（默认优先序 Dlss → Fsr4 →
//     Fsr3 → Fsr2 → Bilinear）→ 设置覆盖接口（SetOverride）
//   - BilinearUpscaler（CreateBilinearUpscaler）：自写基线回退——viewport 缩放
//     blit（D3D12 原生：全屏三角 + 线性采样，PSO/根签名自包含）
//   - CreateFsrUpscaler / CreateFsr4Upscaler：AMD FidelityFX SDK 运行时后端
//     （官方预编译 amd_fidelityfx_upscaler_dx12.dll，统一 FFX 2.0 API；
//     B-R6-02 起同一后端按 provider 版本族分派 3.1 / 4.1）
//   - CreateDlssStreamlineUpscaler：NVIDIA Streamline 骨架（B-R6-02；
//     本机无 N 卡 → 能力门控拒绝，链自动跳过）
//
// 能力矩阵（UpscalerBackendSupported，自动选优的 supported 谓词）：
//   Dlss    : caps.nvidiaHardware && caps.dlssRuntimePresent（N 卡 + Streamline
//             运行时；本机 AMD → 恒 false）
//   Fsr4    : caps.fsr4Available（FFX 运行时 provider 枚举命中 4.x 版本族；
//             780M 实测未命中——见 Docs/Logs/TASK-B-R6-02.md 受限项）
//   Fsr3    : caps.fsrRuntimePresent
//   Fsr2    : caps.fsrRuntimePresent（FSR 2 独立仓库已 404 下线；本任务
//             vendor 的统一运行时内含 FSR2 provider 档位，见 ThirdParty/ffx-fsr/
//             SOURCE.md——能力位与 Fsr3 同源，实现层同一后端）
//   Bilinear: 恒 true（永远可用的基线回退）
//
// C ABI 纪律：ZUP_GetVersion 为唯一 C 导出；C++ 接口仅限同源静态链接方
// （zrender.dll / 窗口验证程序 / 单测）使用，不跨 DLL ABI。跨 DLL 的设置入口
// 是 zplugin.h 的 ZR_Upscaler_* 三函数（B-R6-02）。
// =============================================================================
#pragma once

#include <cstdint>

extern "C" {

/// 超分模块版本（与 ZRender 整体版本同步，形如 "0.1.0"）。
const char* ZUP_GetVersion(void);

} // extern "C"

namespace zr {

// =============================================================================
// 后端枚举与设备能力档案
// =============================================================================

/// 超分后端（能力矩阵横轴；数值稳定可入日志/存档）
/// 注意：枚举值参与 C ABI 映射（zplugin.h 的 ZR_UPSCALER_PREF_* = 值 + 1，
/// 0 留给 Auto），只允许在末尾追加。
enum class UpscalerBackend : uint32_t {
    Bilinear = 0, // 自写基线（viewport 缩放 blit）
    Fsr2,         // AMD FSR 2 provider 档位
    Fsr3,         // AMD FSR 3.1 upscaler
    Fsr4,         // AMD FSR 4.1（RDNA3/4 离散卡；provider 运行时枚举门控）
    Dlss,         // NVIDIA DLSS（Streamline interposer；B-R6-02 骨架）
};

/// 后端名（"Bilinear"/"Fsr2"/"Fsr3"/"Fsr4"/"Dlss"；未知返回 "Unknown"）
const char* UpscalerBackendName(UpscalerBackend backend);

/// 设备能力档案（调用方从 DXGI 适配器 + 运行时探测填充；ZupProbeDeviceCaps
/// 提供免 D3D12 设备的保守探测实现）
struct UpscalerDeviceCaps {
    uint32_t vendorId = 0;           // PCI vendor id：0x1002/0x1022=AMD、0x10DE=NVIDIA、0x8086=Intel
    uint32_t deviceId = 0;           // PCI device id（代际细分用）
    bool fsrRuntimePresent = false;  // FSR 运行时可用（ffx dll 可加载 + 导出齐全）
    bool fsr4Available = false;      // FFX provider 枚举命中 4.x 版本族（真实能力，不猜代际）
    bool nvidiaHardware = false;     // NVIDIA 硬件（DLSS 前提；vendor==0x10DE）
    bool dlssRuntimePresent = false; // Streamline interposer + nvngx_dlss 可加载
};

/// 能力矩阵判定（默认表 supported 谓词；单测直接断言各代际 → 期望后端）
bool UpscalerBackendSupported(UpscalerBackend backend, const UpscalerDeviceCaps& caps);

/// 默认优先序（高→低）：Dlss → Fsr4 → Fsr3 → Fsr2 → Bilinear。
/// 按 capacity 写入 outBackends，返回写入数（capacity 不足截断）。
uint32_t UpscalerDefaultPriority(UpscalerBackend* outBackends, uint32_t capacity);

// =============================================================================
// FFX provider 版本枚举与选择（纯逻辑，可 mock 单测）
// =============================================================================

/// provider 版本条目（ffxQueryDescGetVersions 的 id/name 对；name 截断到 31 字节）
struct ZupProviderVersion {
    uint64_t versionId = 0;
    char name[32] = {};
};

/// 按后端选择 provider 版本族（纯函数；不触 DLL）：
///   Fsr4  : 仅接受 name 前缀 "4."（FSR 4.x ML 路径）
///   Fsr3  : 优先 "3.1"，回退 "2.3"（与 B-R6-01 行为一致）
///   Fsr2  : 优先 "2.3"
///   其余  : 恒 false（Bilinear/Dlss 不走 FFX provider）
/// 命中时写 outVersionId/outName（outNameCapacity 为字节数，含结尾 0）。
bool ZupSelectProviderVersion(UpscalerBackend backend, const ZupProviderVersion* providers,
    uint32_t count, uint64_t* outVersionId, char* outName, uint32_t outNameCapacity);

/// 枚举 FFX 运行时 provider 版本（nativeDevice 可为 null：FFX 对 null 设备
/// 返回与真实设备一致的保守列表）。返回写入数；运行时不可用返回 0（静默，
/// 失败点由 ZupFsrRuntimeProbe 的打印版覆盖）。
uint32_t ZupFsrEnumerateProviders(void* nativeDevice, ZupProviderVersion* out, uint32_t capacity);

/// FSR 4 可用性探测：枚举命中 4.x provider 即 true（780M/RDNA3 iGPU 实测 false，
/// 原因见 TASK-B-R6-02 报告：SDK v2.3.0 文档要求 7000/9000 系**离散** GPU）。
bool ZupFsr4Probe(void* nativeDevice);

// =============================================================================
// 初始化描述 / 帧参数（后端无关 POD）
// =============================================================================

/// 后端无关资源状态位（Evaluate 传入各资源进入 dispatch 时的真实状态；
/// FSR 后端映射到 FfxApiResourceState，Bilinear 忽略）
enum ZrUpscalerResourceState : uint32_t {
    ZR_UPSCALER_STATE_COMPUTE_READ = 1u << 0,     // 计算着色器可读（D3D12 NON_PIXEL_SHADER_RESOURCE）
    ZR_UPSCALER_STATE_PIXEL_READ = 1u << 1,       // 像素着色器可读（D3D12 PIXEL_SHADER_RESOURCE）
    ZR_UPSCALER_STATE_UNORDERED_ACCESS = 1u << 2, // UAV 可读写
};

/// 初始化描述（输入/输出尺寸、相机参数、原生设备句柄）
struct UpscalerInitDesc {
    uint32_t inputWidth = 0;    // 渲染分辨率宽（低分辨率输入）
    uint32_t inputHeight = 0;   // 渲染分辨率高
    uint32_t outputWidth = 0;   // 呈现分辨率宽（上采样输出）
    uint32_t outputHeight = 0;  // 呈现分辨率高
    float cameraFovY = 0.0f;    // 垂直视场角（弧度；FSR 质量参数）
    float cameraNear = 0.0f;    // 近平面
    float cameraFar = 0.0f;     // 远平面
    void* nativeDevice = nullptr; // D3D12：ID3D12Device*

    // ---- Bilinear 后端绘制所需（其余后端忽略）----
    uint32_t outputDxgiFormat = 0;      // DXGI_FORMAT 数值（输出 RTV/PSO 格式）
    const char* bilinearHlsl = nullptr; // 上采样着色器源码（调用方读入，SM5.0 可编译）
    uint32_t bilinearHlslSize = 0;      // 源码字节数
};

/// 单帧求值参数（资源以 D3D12 原生指针传入——本任务 D3D12 真机路径）
struct UpscalerFrameArgs {
    void* nativeCmdList = nullptr;      // D3D12：ID3D12GraphicsCommandList*（须处于已 Begin 记录态）
    void* inputColor = nullptr;         // 渲染分辨率颜色（ID3D12Resource*）
    void* inputDepth = nullptr;         // 渲染分辨率深度（D32Float）
    void* inputMotionVectors = nullptr; // 渲染分辨率运动矢量（RG16Float）
    void* output = nullptr;             // 呈现分辨率输出
    uint32_t inputColorState = 0;       // ZrUpscalerResourceState 位组合
    uint32_t inputDepthState = 0;       // （dispatch 进入时的真实状态——调用方先屏好障）
    uint32_t inputMvState = 0;
    uint32_t outputState = 0;
    float jitterX = 0.0f;               // 注入主视投影的亚像素 jitter（渲染分辨率像素单位）
    float jitterY = 0.0f;
    float motionVectorScaleX = 0.0f;    // MV 像素换算（见 ZupMotionVectorScale）
    float motionVectorScaleY = 0.0f;
    float frameTimeDeltaMs = 16.666f;   // 帧间隔（ms；FSR 时间域参数）
    bool resetHistory = false;          // true = 相机不连续/历史失效（首帧）
    float sharpness = 0.0f;             // 0..1 RCAS 锐化（FSR；Bilinear 忽略）

    // ---- DLSS 后端所需（其余后端忽略）----
    // sl::Constants 要求 view→clip 与 clip→prevClip（row_major，不含 jitter）；
    // cameraMatricesValid=false 时 DLSS 后端拒绝 Evaluate（骨架期显式失败）。
    bool cameraMatricesValid = false;
    float viewToClip[16] = {};     // row_major：相机视图 → 当前裁剪（不含 jitter）
    float clipToPrevClip[16] = {}; // row_major：当前裁剪 → 上帧裁剪
};

// =============================================================================
// MV Pass 布局契约（单一事实源）
// =============================================================================
// Native/shaders/motion_vectors.hlsl 的 CBufMotionVectors 与此结构一一对应
// （修改必须两侧同步；单测 test_upscaler.cpp 锁定尺寸）。
struct ZrUpscalerMotionVectorConstants {
    float mvpCur[16];  // row_major：当前帧 mvp（含本帧 jitter）
    float mvpPrev[16]; // row_major：上帧 mvp（含上帧 jitter）
};
static_assert(sizeof(ZrUpscalerMotionVectorConstants) == 128,
    "MV 常量块须为 128B（motion_vectors.hlsl CBufMotionVectors 镜像）");

/// MV 像素换算：MV Pass 输出 NDC 增量（cur-prev），FSR 需要渲染分辨率像素单位
/// 且 y 向下为正（D3D12 视口 y 向下）→ scale = (0.5*renderW, -0.5*renderH)
inline void ZupMotionVectorScale(uint32_t renderWidth, uint32_t renderHeight, float out[2])
{
    out[0] = 0.5f * static_cast<float>(renderWidth);
    out[1] = -0.5f * static_cast<float>(renderHeight);
}

/// FSR jitter 序列（与 FSR 官方 getJitterPhaseCount/halton 公式一致）：
///   phaseCount = ceil(8 * displayWidth / renderWidth)
///   offset     = halton(index+1, 2) - 0.5 / halton(index+1, 3) - 0.5（index 取模）
uint32_t ZupJitterPhaseCount(uint32_t renderWidth, uint32_t displayWidth);
void ZupJitterOffset(uint32_t index, uint32_t phaseCount, float* outX, float* outY);

// =============================================================================
// Resize 尺寸合法性（TASK-H-02 D2：纯逻辑，可单测）
// =============================================================================
/// 校验一次 Resize 的四个尺寸是否落在创建时上限之内（FSR 后端的上下文按
/// Initialize 的尺寸创建，输出资源按 maxOutput* 分配——**目标输出超过上限时
/// dispatch 会写到未分配内存**，必须在 Resize 阶段拒绝）：
///   - 任一维度为 0 → false
///   - input > maxInput / output > maxOutput → false
///   - output < input（下采样）→ false（后端为超分，非降采样器）
/// 通过返回 true。
bool ZupResizeWithinLimits(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
    uint32_t outputHeight, uint32_t maxInputWidth, uint32_t maxInputHeight,
    uint32_t maxOutputWidth, uint32_t maxOutputHeight);

// =============================================================================
// IUpscaler —— 后端抽象
// =============================================================================
class IUpscaler {
public:
    virtual ~IUpscaler() = default;

    virtual UpscalerBackend Backend() const = 0;
    /// 后端版本/供应商名（如 FFX provider 名；不可用时返回 "unavailable"）
    virtual const char* BackendVersion() const = 0;

    /// 初始化（失败返回 false，调用方回退下一候选后端）
    virtual bool Initialize(const UpscalerInitDesc& desc) = 0;
    /// 尺寸变更（FSR：允许 ≤ 创建时 max 尺寸的动态分辨率；Bilinear：直通）
    virtual bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight) = 0;
    /// 单帧求值（向 nativeCmdList 记录上采样命令；调用方保证各资源已处于
    /// args 中声明的状态）
    virtual bool Evaluate(const UpscalerFrameArgs& args) = 0;
    /// 关闭并释放后端资源（幂等）
    virtual void Shutdown() = 0;

    virtual bool IsInitialized() const = 0;
};

// =============================================================================
// 后端注册表与 UpscalerChain
// =============================================================================

using UpscalerSupportedFn = bool (*)(const UpscalerDeviceCaps&);
using UpscalerCreateFn = IUpscaler* (*)(); // 返回 nullptr = 运行时不可用（回退下一候选）

struct UpscalerBackendEntry {
    UpscalerBackend backend = UpscalerBackend::Bilinear;
    UpscalerSupportedFn supported = nullptr; // 能力矩阵谓词
    UpscalerCreateFn create = nullptr;       // 工厂（可为 null = 占位槽位）
};

/// 内置后端工厂
IUpscaler* CreateBilinearUpscaler();
IUpscaler* CreateFsrUpscaler();    // FSR 3.1 版本族；FFX 运行时缺失时 Initialize 失败（回退）
IUpscaler* CreateFsr4Upscaler();   // FSR 4.x 版本族；provider 未枚举到时 Initialize 失败并打印原因
IUpscaler* CreateDlssStreamlineUpscaler(); // DLSS 骨架；无 N 卡/无 Streamline 时返回 nullptr

/// FSR 运行时探测（ffx 组件 dll 可加载且五函数导出齐全；探测后释放，
/// 静默无日志——能力档案 fsrRuntimePresent 的探测入口）
bool ZupFsrRuntimeProbe();

/// Streamline 运行时探测（sl.interposer.dll + nvngx_dlss.dll 可加载；静默）
bool ZupDlssRuntimeProbe();
/// Streamline 版本串（编译期头版本；"Streamline <ver> headers" 或 "unavailable"）
const char* ZupDlssRuntimeVersion();

/// 免 D3D12 设备的保守能力探测（DXGI 首个非软件适配器 + 三条运行时探测）。
/// outCaps 为 null 时静默返回 false。
bool ZupProbeDeviceCaps(UpscalerDeviceCaps* outCaps);

/// 超分链：注册后端表 → 能力自动选优 → 生命周期状态机
/// 状态机：Uninitialized --SelectAndInitialize--> Initialized --Resize*-->
/// Initialized --Shutdown--> Uninitialized（Evaluate/Resize 仅 Initialized 有效）
class UpscalerChain {
public:
    UpscalerChain();
    ~UpscalerChain();

    UpscalerChain(const UpscalerChain&) = delete;
    UpscalerChain& operator=(const UpscalerChain&) = delete;

    /// 清空后端表（单测注入 mock 用；内置表清空后仅剩显式注册项）
    void ClearBackends();
    /// 注册/覆盖后端条目（同 backend 覆盖）
    void RegisterBackend(const UpscalerBackendEntry& entry);

    /// 设置覆盖：自动选优改为仅尝试该后端（不支持/工厂失败 → 明确失败，
    /// 不静默回退——可证伪优先）
    void SetOverride(UpscalerBackend backend);
    /// 清除覆盖（回到自动选优）
    void ClearOverride();

    /// 选优 + 初始化（状态机入口）。候选顺序：覆盖模式=仅覆盖项；自动模式=
    /// 注册表按 UpscalerDefaultPriority 序。首个 supported 且工厂产出且
    /// Initialize 成功者胜出。全部失败返回 false（IsInitialized 保持 false）。
    bool SelectAndInitialize(const UpscalerDeviceCaps& caps, const UpscalerInitDesc& desc);

    bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight);
    bool Evaluate(const UpscalerFrameArgs& args);
    void Shutdown();

    bool IsInitialized() const;
    UpscalerBackend ActiveBackend() const;    // 仅 IsInitialized() 时有意义
    const char* ActiveBackendVersion() const; // 后端版本名（证据行）

    /// 本次 SelectAndInitialize 实际尝试的候选数（选优过程可观测）
    uint32_t LastAttemptCount() const { return m_lastAttemptCount; }

private:
    static constexpr uint32_t kMaxBackends = 8;
    UpscalerBackendEntry m_backends[kMaxBackends] = {};
    uint32_t m_backendCount = 0;
    bool m_hasOverride = false;
    UpscalerBackend m_override = UpscalerBackend::Bilinear;
    IUpscaler* m_active = nullptr;
    uint32_t m_lastAttemptCount = 0;
};

} // namespace zr
