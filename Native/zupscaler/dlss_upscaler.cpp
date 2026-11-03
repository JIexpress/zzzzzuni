// =============================================================================
// dlss_upscaler.cpp —— NVIDIA Streamline / DLSS 骨架后端（TASK-B-R6-02）
//
// 定位：**受限项**。本机为 AMD Radeon 780M（RDNA3 iGPU），无 NVIDIA 硬件，
// 因此本后端只做三件事，且都可在无 N 卡环境验证：
//   1) 能力门控：ZupDlssRuntimeProbe（sl.interposer.dll + nvngx_dlss.dll 可加载）
//      + caps.nvidiaHardware（vendor==0x10DE）→ 不满足时工厂返回 nullptr，
//      UpscalerChain 直接跳过，不报错、不影响 FSR 路径。
//   2) 接口完整：DlssStreamlineUpscaler 实现 IUpscaler 五方法，内部按
//      Streamline 官方调用序列（slInit → slIsFeatureSupported → slGetFeatureFunction
//      → slSetConstants/slSetTagForFrame/slEvaluateFeature）封装 slDLSSFeature 路径。
//   3) 编译链接：头文件取自官方 tag v2.14.16（ThirdParty/streamline/SOURCE.md），
//      导入库由官方 ABI 声明本地生成（见 SOURCE.md"导入库"节）。
//
// 运行时加载策略：**不静态导入 sl.interposer.dll**。zrender.dll 会被 Unity 加载，
// 若静态导入一个本机不存在的 dll，进程加载期即失败——这与"绝不碰 Unity"的约束
// 冲突。因此本文件用 LoadLibrary + GetProcAddress 取官方 SL_API（C 链接，符号名
// 无修饰），函数指针类型直接复用官方头里的 PFun_* 定义（零重复声明）。
//
// 真机验证状态：**未验证**（需 RTX 50 系 + 安装 Streamline 运行时的机器补验）。
// 见 Docs/Logs/TASK-B-R6-02.md 的"受限项声明"。
// =============================================================================
#include "zupscaler/zupscaler.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>

#include <cstdio>
#include <cstring>
#include <new>

// ---- 第三方头（NVIDIA Streamline v2.14.16；/WX 豁免隔离段）-------------------
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4244 4245 4324 4456 4457 4458 4514 4623 4625 4626 4668 4820 5045)
#include "sl.h"
#include "sl_dlss.h"
#pragma warning(pop)

namespace zr {
namespace {

constexpr const char* kSlInterposerDll = "sl.interposer.dll";
constexpr const char* kSlDlssPluginDll = "nvngx_dlss.dll";

/// Streamline 函数表（LoadLibrary + GetProcAddress；C 链接符号无修饰）
struct SlApi {
    HMODULE module = nullptr;
    PFun_slInit* init = nullptr;
    PFun_slShutdown* shutdown = nullptr;
    PFun_slIsFeatureSupported* isFeatureSupported = nullptr;
    PFun_slGetFeatureFunction* getFeatureFunction = nullptr;
    PFun_slSetConstants* setConstants = nullptr;
    PFun_slSetTagForFrame* setTagForFrame = nullptr;
    PFun_slEvaluateFeature* evaluateFeature = nullptr;
    PFun_slGetNewFrameToken* getNewFrameToken = nullptr;

    bool Valid() const
    {
        return module != nullptr && init != nullptr && shutdown != nullptr
            && isFeatureSupported != nullptr && getFeatureFunction != nullptr
            && setConstants != nullptr && setTagForFrame != nullptr
            && evaluateFeature != nullptr && getNewFrameToken != nullptr;
    }

    void Release()
    {
        if (module != nullptr) {
            ::FreeLibrary(module);
            module = nullptr;
        }
        init = nullptr;
        shutdown = nullptr;
        isFeatureSupported = nullptr;
        getFeatureFunction = nullptr;
        setConstants = nullptr;
        setTagForFrame = nullptr;
        evaluateFeature = nullptr;
        getNewFrameToken = nullptr;
    }
};

template <typename T>
T* GetSlProc(HMODULE module, const char* name)
{
    return reinterpret_cast<T*>(::GetProcAddress(module, name));
}

/// 载入 sl.interposer.dll 并取齐九函数（quiet=false 时打印缺失项——不静默）
bool LoadSlApi(SlApi* out, bool quiet)
{
    if (out == nullptr) {
        return false;
    }
    out->Release();
    const HMODULE module = ::LoadLibraryA(kSlInterposerDll);
    if (module == nullptr) {
        if (!quiet) {
            std::printf("  dlss: LoadLibrary('%s') FAILED (GetLastError=%lu) -> DLSS unavailable\n",
                kSlInterposerDll, ::GetLastError());
        }
        return false;
    }
    out->module = module;
    out->init = GetSlProc<PFun_slInit>(module, "slInit");
    out->shutdown = GetSlProc<PFun_slShutdown>(module, "slShutdown");
    out->isFeatureSupported = GetSlProc<PFun_slIsFeatureSupported>(module, "slIsFeatureSupported");
    out->getFeatureFunction = GetSlProc<PFun_slGetFeatureFunction>(module, "slGetFeatureFunction");
    out->setConstants = GetSlProc<PFun_slSetConstants>(module, "slSetConstants");
    out->setTagForFrame = GetSlProc<PFun_slSetTagForFrame>(module, "slSetTagForFrame");
    out->evaluateFeature = GetSlProc<PFun_slEvaluateFeature>(module, "slEvaluateFeature");
    out->getNewFrameToken = GetSlProc<PFun_slGetNewFrameToken>(module, "slGetNewFrameToken");
    if (!out->Valid()) {
        if (!quiet) {
            std::printf("  dlss: '%s' missing required SL_API entry points\n", kSlInterposerDll);
        }
        out->Release();
        return false;
    }
    return true;
}

/// 把 Streamline Result 转成可读文本（证据行）
const char* SlResultName(sl::Result result)
{
    switch (result) {
    case sl::Result::eOk:
        return "eOk";
    case sl::Result::eErrorDriverOutOfDate:
        return "eErrorDriverOutOfDate";
    case sl::Result::eErrorOSOutOfDate:
        return "eErrorOSOutOfDate";
    case sl::Result::eErrorNoSupportedAdapterFound:
        return "eErrorNoSupportedAdapterFound";
    case sl::Result::eErrorAdapterNotSupported:
        return "eErrorAdapterNotSupported";
    case sl::Result::eErrorNoPlugins:
        return "eErrorNoPlugins";
    case sl::Result::eErrorFeatureNotSupported:
        return "eErrorFeatureNotSupported";
    case sl::Result::eErrorFeatureMissing:
        return "eErrorFeatureMissing";
    case sl::Result::eErrorMissingInputParameter:
        return "eErrorMissingInputParameter";
    default:
        return "eError(other)";
    }
}

/// DLSS / Streamline 后端（骨架：本机无 N 卡，Initialize 在能力门控处即返回 false）
class DlssStreamlineUpscaler final : public IUpscaler {
public:
    ~DlssStreamlineUpscaler() override { Shutdown(); }

    UpscalerBackend Backend() const override { return UpscalerBackend::Dlss; }

    const char* BackendVersion() const override { return ZupDlssRuntimeVersion(); }

    bool Initialize(const UpscalerInitDesc& desc) override
    {
        if (m_initialized) {
            Shutdown();
        }
        if (desc.nativeDevice == nullptr || desc.inputWidth == 0u || desc.inputHeight == 0u
            || desc.outputWidth == 0u || desc.outputHeight == 0u) {
            std::printf("  dlss: Initialize FAILED (invalid desc)\n");
            return false;
        }
        if (!LoadSlApi(&m_api, /*quiet=*/false)) {
            return false; // 失败点已打印（dll 缺失/导出不全）
        }

        // slInit：只加载 DLSS 一个 feature（骨架最小面）
        static const sl::Feature kFeatures[] = {sl::kFeatureDLSS};
        sl::Preferences pref {};
        pref.showConsole = false;
        pref.logLevel = sl::LogLevel::eDefault;
        pref.pathsToPlugins = nullptr;
        pref.numPathsToPlugins = 0u;
        pref.pathToLogsAndData = nullptr;
        pref.featuresToLoad = kFeatures;
        pref.numFeaturesToLoad = 1u;
        pref.engine = sl::EngineType::eCustom;
        pref.engineVersion = "zrender";
        pref.projectId = "zrender-native";
        pref.renderAPI = sl::RenderAPI::eD3D12;
        const sl::Result initResult = m_api.init(pref, sl::kSDKVersion);
        std::printf("  dlss: slInit rc=%s (%d)\n", SlResultName(initResult),
            static_cast<int>(initResult));
        if (initResult != sl::Result::eOk) {
            m_api.Release();
            return false;
        }
        m_slInitialized = true;

        // 适配器能力查询（LUID 来自真实 D3D12 设备；无 N 卡时这里必失败）
        auto* device = static_cast<ID3D12Device*>(desc.nativeDevice);
        const LUID luid = device->GetAdapterLuid();
        sl::AdapterInfo adapterInfo {};
        adapterInfo.deviceLUID = reinterpret_cast<uint8_t*>(const_cast<LUID*>(&luid));
        adapterInfo.deviceLUIDSizeInBytes = sizeof(LUID);
        const sl::Result support = m_api.isFeatureSupported(sl::kFeatureDLSS, adapterInfo);
        std::printf("  dlss: slIsFeatureSupported(DLSS) rc=%s (%d) adapterLuid=%08lX:%08lX\n",
            SlResultName(support), static_cast<int>(support),
            static_cast<unsigned long>(luid.HighPart), static_cast<unsigned long>(luid.LowPart));
        if (support != sl::Result::eOk) {
            std::printf("  dlss: adapter does not support DLSS (needs NVIDIA RTX) -> "
                        "backend unavailable, chain falls back\n");
            Shutdown();
            return false;
        }

        // feature 函数（slDLSSSetOptions / slDLSSGetOptimalSettings 经 slGetFeatureFunction 取）
        const sl::Result optRes = m_api.getFeatureFunction(sl::kFeatureDLSS,
            "slDLSSGetOptimalSettings", reinterpret_cast<void*&>(m_getOptimalSettings));
        const sl::Result setRes = m_api.getFeatureFunction(sl::kFeatureDLSS,
            "slDLSSSetOptions", reinterpret_cast<void*&>(m_setOptions));
        std::printf("  dlss: feature functions: getOptimalSettings=%s setOptions=%s\n",
            SlResultName(optRes), SlResultName(setRes));
        if (optRes != sl::Result::eOk || setRes != sl::Result::eOk) {
            Shutdown();
            return false;
        }

        m_inputWidth = desc.inputWidth;
        m_inputHeight = desc.inputHeight;
        m_outputWidth = desc.outputWidth;
        m_outputHeight = desc.outputHeight;
        m_cameraFovY = desc.cameraFovY;
        m_cameraNear = desc.cameraNear;
        m_cameraFar = desc.cameraFar;

        // DLSS 质量档 → 官方推荐渲染尺寸（与输入尺寸比对只作证据，不强制）
        sl::DLSSOptions options {};
        options.mode = sl::DLSSMode::eMaxQuality;
        options.outputWidth = desc.outputWidth;
        options.outputHeight = desc.outputHeight;
        options.preExposure = 1.0f;
        options.colorBuffersHDR = sl::Boolean::eFalse;
        sl::DLSSOptimalSettings optimal {};
        const sl::Result optimalRes = m_getOptimalSettings(options, optimal);
        std::printf("  dlss: optimal settings rc=%s render=%ux%u (requested %ux%u)\n",
            SlResultName(optimalRes), optimal.optimalRenderWidth, optimal.optimalRenderHeight,
            desc.inputWidth, desc.inputHeight);
        const sl::Result optionsRes = m_setOptions(sl::ViewportHandle(0u), options);
        std::printf("  dlss: slDLSSSetOptions rc=%s\n", SlResultName(optionsRes));
        if (optionsRes != sl::Result::eOk) {
            Shutdown();
            return false;
        }
        m_initialized = true;
        return true;
    }

    bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight) override
    {
        if (!m_initialized || m_setOptions == nullptr) {
            return false;
        }
        if (inputWidth == 0u || inputHeight == 0u || outputWidth == 0u || outputHeight == 0u) {
            return false;
        }
        m_inputWidth = inputWidth;
        m_inputHeight = inputHeight;
        m_outputWidth = outputWidth;
        m_outputHeight = outputHeight;
        sl::DLSSOptions options {};
        options.mode = sl::DLSSMode::eMaxQuality;
        options.outputWidth = outputWidth;
        options.outputHeight = outputHeight;
        options.preExposure = 1.0f;
        options.colorBuffersHDR = sl::Boolean::eFalse;
        return m_setOptions(sl::ViewportHandle(0u), options) == sl::Result::eOk;
    }

    /// 骨架 Evaluate：完整走一遍官方调用序列（constants → tags → evaluate）。
    /// 本机无 N 卡 → Initialize 已失败，本函数不会被调用（未真机验证）。
    bool Evaluate(const UpscalerFrameArgs& args) override
    {
        if (!m_initialized || !args.cameraMatricesValid || args.nativeCmdList == nullptr
            || args.inputColor == nullptr || args.inputDepth == nullptr
            || args.inputMotionVectors == nullptr || args.output == nullptr) {
            return false;
        }
        sl::FrameToken* token = nullptr;
        const sl::Result tokenRes = m_api.getNewFrameToken(token, &m_frameIndex);
        if (tokenRes != sl::Result::eOk || token == nullptr) {
            std::printf("  dlss: slGetNewFrameToken FAILED (%s)\n", SlResultName(tokenRes));
            return false;
        }

        sl::Constants constants {};
        std::memcpy(&constants.cameraViewToClip, args.viewToClip, sizeof(float) * 16u);
        std::memcpy(&constants.clipToPrevClip, args.clipToPrevClip, sizeof(float) * 16u);
        constants.jitterOffset = {args.jitterX, args.jitterY};
        constants.mvecScale = {args.motionVectorScaleX, args.motionVectorScaleY};
        constants.cameraNear = m_cameraNear;
        constants.cameraFar = m_cameraFar;
        constants.cameraFOV = m_cameraFovY;
        constants.cameraAspectRatio = static_cast<float>(m_inputWidth)
            / static_cast<float>((m_inputHeight != 0u) ? m_inputHeight : 1u);
        constants.depthInverted = sl::Boolean::eTrue;
        constants.cameraMotionIncluded = sl::Boolean::eTrue;
        constants.motionVectors3D = sl::Boolean::eFalse;
        constants.reset = args.resetHistory ? sl::Boolean::eTrue : sl::Boolean::eFalse;
        const sl::Result constantsRes
            = m_api.setConstants(constants, *token, sl::ViewportHandle(0u));
        if (constantsRes != sl::Result::eOk) {
            std::printf("  dlss: slSetConstants FAILED (%s)\n", SlResultName(constantsRes));
            return false;
        }

        // 资源标签（D3D12 资源指针直接透传；状态由调用方保证）
        sl::Resource color(sl::ResourceType::eTex2d, args.inputColor, 0u);
        sl::Resource depth(sl::ResourceType::eTex2d, args.inputDepth, 0u);
        sl::Resource mv(sl::ResourceType::eTex2d, args.inputMotionVectors, 0u);
        sl::Resource output(sl::ResourceType::eTex2d, args.output, 0u);
        sl::ResourceTag tags[4] = {
            sl::ResourceTag(&color, sl::kBufferTypeScalingInputColor,
                sl::ResourceLifecycle::eValidUntilEvaluate),
            sl::ResourceTag(&depth, sl::kBufferTypeDepth,
                sl::ResourceLifecycle::eValidUntilEvaluate),
            sl::ResourceTag(&mv, sl::kBufferTypeMotionVectors,
                sl::ResourceLifecycle::eValidUntilEvaluate),
            sl::ResourceTag(&output, sl::kBufferTypeScalingOutputColor,
                sl::ResourceLifecycle::eValidUntilEvaluate),
        };
        auto* cmdBuffer = static_cast<sl::CommandBuffer*>(args.nativeCmdList);
        const sl::Result tagRes = m_api.setTagForFrame(
            *token, sl::ViewportHandle(0u), tags, 4u, cmdBuffer);
        if (tagRes != sl::Result::eOk) {
            std::printf("  dlss: slSetTagForFrame FAILED (%s)\n", SlResultName(tagRes));
            return false;
        }
        const sl::Result evalRes
            = m_api.evaluateFeature(sl::kFeatureDLSS, *token, nullptr, 0u, cmdBuffer);
        if (evalRes != sl::Result::eOk) {
            std::printf("  dlss: slEvaluateFeature FAILED (%s)\n", SlResultName(evalRes));
            return false;
        }
        m_frameIndex += 1u;
        return true;
    }

    void Shutdown() override
    {
        if (m_slInitialized && m_api.shutdown != nullptr) {
            m_api.shutdown();
        }
        m_slInitialized = false;
        m_api.Release();
        m_initialized = false;
    }

    bool IsInitialized() const override { return m_initialized; }

private:
    SlApi m_api {};
    bool m_slInitialized = false;
    bool m_initialized = false;
    PFun_slDLSSGetOptimalSettings* m_getOptimalSettings = nullptr;
    PFun_slDLSSSetOptions* m_setOptions = nullptr;
    uint32_t m_frameIndex = 0u;
    uint32_t m_inputWidth = 0u;
    uint32_t m_inputHeight = 0u;
    uint32_t m_outputWidth = 0u;
    uint32_t m_outputHeight = 0u;
    float m_cameraFovY = 0.0f;
    float m_cameraNear = 0.0f;
    float m_cameraFar = 0.0f;
};

} // namespace

bool ZupDlssRuntimeProbe()
{
    const HMODULE interposer = ::LoadLibraryA(kSlInterposerDll);
    if (interposer == nullptr) {
        return false;
    }
    const HMODULE plugin = ::LoadLibraryA(kSlDlssPluginDll);
    if (plugin != nullptr) {
        ::FreeLibrary(plugin);
    }
    ::FreeLibrary(interposer);
    return plugin != nullptr;
}

const char* ZupDlssRuntimeVersion()
{
    static char s_version[64] = {};
    static bool s_ready = false;
    if (!s_ready) {
        std::snprintf(s_version, sizeof(s_version), "Streamline %d.%d.%d (headers, runtime %s)",
            SL_VERSION_MAJOR, SL_VERSION_MINOR, SL_VERSION_PATCH,
            ZupDlssRuntimeProbe() ? "present" : "absent");
        s_ready = true;
    }
    return s_version;
}

IUpscaler* CreateDlssStreamlineUpscaler()
{
    // 运行时缺失 → nullptr（Chain 直接跳过，不报错；能力矩阵通常已先拒）
    if (!ZupDlssRuntimeProbe()) {
        return nullptr;
    }
    return new (std::nothrow) DlssStreamlineUpscaler();
}

} // namespace zr
