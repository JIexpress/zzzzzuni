// =============================================================================
// upscaler_chain.cpp —— UpscalerChain 选优/状态机 + 能力矩阵 + jitter 序列
// （TASK-B-R6-01 建 / B-R6-02 扩 Dlss 与 Fsr4 真工厂）
//
// 选优语义：
//   - 自动模式：注册表按 UpscalerDefaultPriority（Dlss→Fsr4→Fsr3→Fsr2→Bilinear）
//     排序遍历；候选须 supported(caps) 且工厂产出非空且 Initialize 成功。
//   - 覆盖模式（SetOverride）：仅尝试覆盖项；失败即整体失败——不静默回退
//     （可证伪优先；--fsr off 即用覆盖 Bilinear 保证走基线）。
//   - LastAttemptCount 记录实际尝试的候选数（选优过程可观测/可测）。
//
// 状态机（见 zupscaler.h）：Uninitialized → Initialized → (Resize)* → Shutdown
//   违规序列（未初始化 Evaluate/Resize、已初始化重复 SelectAndInitialize）
//   一律返回 false 并保持状态不变。
//
// ZupJitterPhaseCount/ZupJitterOffset 与 FSR 官方实现公式一致
// （FidelityFX SDK fsr3upscaler：phaseCount = ceil(8·display/render)，
//   Halton(2,3) 素数基低差异序列减 0.5 居中）。
// =============================================================================
#include "zupscaler/zupscaler.h"

#include <cmath>

namespace zr {

const char* UpscalerBackendName(UpscalerBackend backend)
{
    switch (backend) {
    case UpscalerBackend::Bilinear:
        return "Bilinear";
    case UpscalerBackend::Fsr2:
        return "Fsr2";
    case UpscalerBackend::Fsr3:
        return "Fsr3";
    case UpscalerBackend::Fsr4:
        return "Fsr4";
    case UpscalerBackend::Dlss:
        return "Dlss";
    default:
        return "Unknown";
    }
}

// ---- 能力矩阵（各代际 → 期望后端的单一事实源；单测逐格断言） ----------------
bool UpscalerBackendSupported(UpscalerBackend backend, const UpscalerDeviceCaps& caps)
{
    switch (backend) {
    case UpscalerBackend::Dlss:
        // DLSS：NVIDIA 硬件 + Streamline 运行时（interposer + nvngx_dlss）
        return caps.nvidiaHardware && caps.dlssRuntimePresent;
    case UpscalerBackend::Fsr4:
        // FSR 4.x ML 路径：FFX provider 运行时枚举命中 4.x 版本族
        return caps.fsr4Available;
    case UpscalerBackend::Fsr3:
    case UpscalerBackend::Fsr2:
        // FSR 3.1 / FSR 2 provider：统一运行时可用即可（硬件无关）
        return caps.fsrRuntimePresent;
    case UpscalerBackend::Bilinear:
        return true; // 基线回退永远可用
    default:
        return false;
    }
}

uint32_t UpscalerDefaultPriority(UpscalerBackend* outBackends, uint32_t capacity)
{
    static const UpscalerBackend kOrder[] = {
        UpscalerBackend::Dlss,
        UpscalerBackend::Fsr4,
        UpscalerBackend::Fsr3,
        UpscalerBackend::Fsr2,
        UpscalerBackend::Bilinear,
    };
    const uint32_t total = static_cast<uint32_t>(sizeof(kOrder) / sizeof(kOrder[0]));
    const uint32_t n = (capacity < total) ? capacity : total;
    for (uint32_t i = 0; i < n; ++i) {
        outBackends[i] = kOrder[i];
    }
    return n;
}

// ---- jitter 序列（FSR 官方公式） ---------------------------------------------
uint32_t ZupJitterPhaseCount(uint32_t renderWidth, uint32_t displayWidth)
{
    if (renderWidth == 0u || displayWidth == 0u) {
        return 1u;
    }
    const float scale = static_cast<float>(displayWidth) / static_cast<float>(renderWidth);
    const float raw = std::ceil(8.0f * scale);
    const uint32_t count = static_cast<uint32_t>(raw);
    return (count == 0u) ? 1u : count;
}

namespace {

/// Halton 低差异序列（index 1 起；FSR 官方实现公式）
float Halton(uint32_t index, uint32_t base)
{
    float f = 1.0f;
    float r = 0.0f;
    while (index > 0u) {
        f = f / static_cast<float>(base);
        r = r + f * static_cast<float>(index % base);
        index = index / base;
    }
    return r;
}

} // namespace

void ZupJitterOffset(uint32_t index, uint32_t phaseCount, float* outX, float* outY)
{
    if (phaseCount == 0u) {
        phaseCount = 1u;
    }
    const uint32_t i = index % phaseCount;
    if (outX != nullptr) {
        *outX = Halton(i + 1u, 2u) - 0.5f;
    }
    if (outY != nullptr) {
        *outY = Halton(i + 1u, 3u) - 0.5f;
    }
}

namespace {

/// 逐后端 supported 适配（UpscalerSupportedFn 签名 = (caps)；backend 由条目携带）
bool SupportedDlss(const UpscalerDeviceCaps& caps)
{
    return UpscalerBackendSupported(UpscalerBackend::Dlss, caps);
}
bool SupportedFsr4(const UpscalerDeviceCaps& caps)
{
    return UpscalerBackendSupported(UpscalerBackend::Fsr4, caps);
}
bool SupportedFsr3(const UpscalerDeviceCaps& caps)
{
    return UpscalerBackendSupported(UpscalerBackend::Fsr3, caps);
}
bool SupportedFsr2(const UpscalerDeviceCaps& caps)
{
    return UpscalerBackendSupported(UpscalerBackend::Fsr2, caps);
}
bool SupportedBilinear(const UpscalerDeviceCaps& caps)
{
    return UpscalerBackendSupported(UpscalerBackend::Bilinear, caps);
}

} // namespace

// ---- UpscalerChain -----------------------------------------------------------
UpscalerChain::UpscalerChain()
{
    // 默认后端表（B-R6-02）：Dlss 骨架（无 N 卡 → 工厂返回 nullptr，能力矩阵先拒）
    // + Fsr4（provider 未枚举到时 Initialize 失败并打印原因）+ Fsr3/Fsr2 统一 FFX
    // 运行时 + Bilinear 基线。回退顺序即 UpscalerDefaultPriority。
    UpscalerBackendEntry dlss {};
    dlss.backend = UpscalerBackend::Dlss;
    dlss.supported = &SupportedDlss;
    dlss.create = &CreateDlssStreamlineUpscaler;
    RegisterBackend(dlss);

    UpscalerBackendEntry fsr4 {};
    fsr4.backend = UpscalerBackend::Fsr4;
    fsr4.supported = &SupportedFsr4;
    fsr4.create = &CreateFsr4Upscaler;
    RegisterBackend(fsr4);

    UpscalerBackendEntry fsr3 {};
    fsr3.backend = UpscalerBackend::Fsr3;
    fsr3.supported = &SupportedFsr3;
    fsr3.create = &CreateFsrUpscaler;
    RegisterBackend(fsr3);

    UpscalerBackendEntry fsr2 {};
    fsr2.backend = UpscalerBackend::Fsr2;
    fsr2.supported = &SupportedFsr2;
    fsr2.create = &CreateFsrUpscaler; // 同一统一运行时（provider 档位不同）
    RegisterBackend(fsr2);

    UpscalerBackendEntry bilinear {};
    bilinear.backend = UpscalerBackend::Bilinear;
    bilinear.supported = &SupportedBilinear;
    bilinear.create = &CreateBilinearUpscaler;
    RegisterBackend(bilinear);
}

UpscalerChain::~UpscalerChain()
{
    Shutdown();
}

void UpscalerChain::ClearBackends()
{
    m_backendCount = 0;
}

void UpscalerChain::RegisterBackend(const UpscalerBackendEntry& entry)
{
    for (uint32_t i = 0; i < m_backendCount; ++i) {
        if (m_backends[i].backend == entry.backend) {
            m_backends[i] = entry; // 同名覆盖
            return;
        }
    }
    if (m_backendCount < kMaxBackends) {
        m_backends[m_backendCount] = entry;
        m_backendCount += 1u;
    }
}

void UpscalerChain::SetOverride(UpscalerBackend backend)
{
    m_hasOverride = true;
    m_override = backend;
}

void UpscalerChain::ClearOverride()
{
    m_hasOverride = false;
}

bool UpscalerChain::SelectAndInitialize(
    const UpscalerDeviceCaps& caps, const UpscalerInitDesc& desc)
{
    if (m_active != nullptr) {
        return false; // 状态机：重复初始化须先 Shutdown
    }

    // 候选顺序：覆盖模式 = 仅覆盖项；自动模式 = 默认优先序过滤注册表
    UpscalerBackend order[kMaxBackends] = {};
    uint32_t orderCount = 0;
    if (m_hasOverride) {
        order[0] = m_override;
        orderCount = 1u;
    } else {
        UpscalerBackend priority[kMaxBackends] = {};
        const uint32_t priorityCount = UpscalerDefaultPriority(priority, kMaxBackends);
        for (uint32_t p = 0; p < priorityCount; ++p) {
            for (uint32_t b = 0; b < m_backendCount; ++b) {
                if (m_backends[b].backend == priority[p]) {
                    order[orderCount] = priority[p];
                    orderCount += 1u;
                    break;
                }
            }
        }
    }

    m_lastAttemptCount = 0;
    for (uint32_t i = 0; i < orderCount; ++i) {
        const UpscalerBackendEntry* entry = nullptr;
        for (uint32_t b = 0; b < m_backendCount; ++b) {
            if (m_backends[b].backend == order[i]) {
                entry = &m_backends[b];
                break;
            }
        }
        if (entry == nullptr) {
            continue; // 优先序中的后端未注册
        }
        if (entry->supported == nullptr || !entry->supported(caps)) {
            continue; // 能力矩阵拒绝（不算"尝试"——未被列为候选）
        }
        m_lastAttemptCount += 1u;
        IUpscaler* instance = (entry->create != nullptr) ? entry->create() : nullptr;
        if (instance == nullptr) {
            continue; // 工厂不可用（占位槽位/运行时缺失）→ 回退下一候选
        }
        if (!instance->Initialize(desc)) {
            delete instance;
            continue; // 初始化失败 → 回退下一候选
        }
        m_active = instance;
        return true;
    }
    return false; // 全部候选失败（报告层须打印——不得静默降级为"无超分"）
}

bool UpscalerChain::Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
    uint32_t outputHeight)
{
    if (m_active == nullptr) {
        return false; // 状态机：未初始化
    }
    return m_active->Resize(inputWidth, inputHeight, outputWidth, outputHeight);
}

bool UpscalerChain::Evaluate(const UpscalerFrameArgs& args)
{
    if (m_active == nullptr) {
        return false; // 状态机：未初始化
    }
    return m_active->Evaluate(args);
}

void UpscalerChain::Shutdown()
{
    if (m_active != nullptr) {
        m_active->Shutdown();
        delete m_active;
        m_active = nullptr;
    }
}

bool UpscalerChain::IsInitialized() const
{
    return m_active != nullptr;
}

UpscalerBackend UpscalerChain::ActiveBackend() const
{
    return (m_active != nullptr) ? m_active->Backend() : UpscalerBackend::Bilinear;
}

const char* UpscalerChain::ActiveBackendVersion() const
{
    return (m_active != nullptr) ? m_active->BackendVersion() : "unavailable";
}

} // namespace zr
