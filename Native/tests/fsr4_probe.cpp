// =============================================================================
// fsr4_probe.cpp —— FSR4 两机探针：真设备/null 对照可复核工具（TASK-B-PROBE-01）
//
// 回答一个问题：这台机器的 FSR 4.1 到底支不支持，证据是什么——第三人可复核、
// 第二台机器可复跑。同时是 B-FSR-01 修复的阳性对照工具：对端机 RX 9060 XT 上
// null=2 条 vs 真设备=4 条（含 4.1.2）的差异应能被本探针复现出来（B-FSR-01
// 主控裁决：阳性验证移交本探针，两机补验）。
//
// 步骤（与任务简报一一对应）：
//   a) ZR_Upscaler_QueryCaps 取完整 caps（含 fsr4ProbeMode 三态口径）
//   b) null 设备 provider 枚举（UPSCALER 效果族：条数+版本名列表）
//   c) 真设备 provider 枚举（DXGI 枚举适配器 → D3D12CreateDevice → 枚举，与
//      B-FSR-01 修法同源：feature level 12.0→11.1→11.0 取首个成功）
//   d) FG 族双设备对照（zr::FrameGenerator::QuerySupport——fg_ffx.h 现有导出
//      面，null 与真设备各一次；B-FSR-02 移交项）
//   e) 控制台对照表 + Tools/Probes/out/fsr4-<UTC时间戳>.json
//
// JSON 落盘口径（照 Tools/Probes/env-probe.js——Mimosa 复查 2026-11-14）：
//   文件名白名单 ^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$（天然不含路径分隔符与
//   连续点，杜绝 ../ 与分隔符注入）+ 最终路径必须以常量 out 根为前缀的边界
//   断言；不拼接任何未校验路径。拿不到的项一律 MISSING+原因，不填默认值。
//
// 两机可跑（运行时依赖仅同目录 FFX dll + 系统组件 d3d12/dxgi/VC 运行时）：
//   - CMake POST_BUILD 拷 4 个 FFX dll（upscaler/loader/framegeneration/ags）
//     到 exe 目录——仓库内构建后直接可跑；
//   - 对端机免仓库：拷 fsr4_probe.exe + 4 个 FFX dll 到同一目录运行（免仓库
//     模式下 JSON 落 exe 同目录 out/；跑法详见 Tools/Probes/README.md）。
//   - 输出目录解析：自 exe 目录向上找仓库根（标记 = Tools/Probes/README.md
//     登记表）→ Tools/Probes/out；找不到 → exe 同目录 out/（便携模式，如实
//     记录在 JSON 的 outMode 字段）。
//
// 退出码：0 = 探针完整跑完且 JSON 已落盘（FSR4 支持与否是数据不是失败——
//         负对照同样是有效结果）；1 = JSON 未写出（out 目录不可建/不可写）。
//
// 边界（约法三章）：探针不进 zrender.dll、不进 Unity 绑定、不改引擎行为；
// ZR_Upscaler_*/ZR_ApiVersion 只读消费。不进 zrender_test（探针是真机数据
// 采集工具，不是 doctest——验收/补验流程手动运行）。
// =============================================================================

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <d3d12.h>
#include <dxgi.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "zplugin/zplugin.h"
#include "zupscaler/fg_ffx.h"
#include "zupscaler/zupscaler.h"

namespace {

// ---- 小工具：宽字符 → UTF-8 / JSON 转义 / 十六进制 / 时间戳 --------------------

std::string Utf8FromWide(const wchar_t* wide)
{
    char utf8[256] = {};
    ::WideCharToMultiByte(CP_UTF8, 0, (wide != nullptr) ? wide : L"", -1, utf8,
        static_cast<int>(sizeof(utf8)), nullptr, nullptr);
    return std::string(utf8);
}

std::string JsonEscape(const std::string& in)
{
    std::string out;
    out.reserve(in.size() + 8);
    for (const char c : in) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (static_cast<unsigned char>(c) < 0x20u) {
                char buf[8] = {};
                std::snprintf(buf, sizeof(buf), "\\u%04X",
                    static_cast<unsigned>(static_cast<unsigned char>(c)));
                out += buf;
            } else {
                out += c;
            }
        }
    }
    return out;
}

std::string Hex32(const uint32_t v)
{
    char buf[16] = {};
    std::snprintf(buf, sizeof(buf), "0x%04X", static_cast<unsigned>(v));
    return std::string(buf);
}

std::string Hex64(const uint64_t v)
{
    char buf[32] = {};
    std::snprintf(buf, sizeof(buf), "0x%llX", static_cast<unsigned long long>(v));
    return std::string(buf);
}

std::string UtcIso()
{
    std::time_t now = std::time(nullptr);
    std::tm utc {};
    ::gmtime_s(&utc, &now);
    char buf[32] = {};
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return std::string(buf);
}

std::string UtcStampFile()
{
    // 与 env-probe.js 同口径：ISO 串的 ':' '.' → '-'，截 19 字符
    std::time_t now = std::time(nullptr);
    std::tm utc {};
    ::gmtime_s(&utc, &now);
    char buf[32] = {};
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H-%M-%S", &utc);
    return std::string(buf);
}

/// 等价于 env-probe.js 的 /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$/：仅字母/数字/
/// _/- 分段 + 单点分隔（≥1 个点），天然不含路径分隔符与连续点，杜绝 ../ 与
/// 分隔符注入；再对最终路径做 out 目录边界断言（Mimosa 复查口径）。
bool IsValidProbeFileName(const std::string& name)
{
    if (name.empty()) {
        return false;
    }
    bool inSegment = false;
    bool sawDot = false;
    for (const char c : name) {
        if (c == '.') {
            if (!inSegment) {
                return false; // 连续点/以点开头（含 ".."）→ 拒绝
            }
            sawDot = true;
            inSegment = false;
        } else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9') || c == '_' || c == '-') {
            inSegment = true;
        } else {
            return false; // 非白名单字符（含 / \ : 等）→ 拒绝
        }
    }
    return inSegment && sawDot;
}

const char* FgSupportName(const zr::FrameGenSupport s)
{
    switch (s) {
    case zr::FrameGenSupport::Unavailable: return "Unavailable";
    case zr::FrameGenSupport::NoProvider: return "NoProvider";
    case zr::FrameGenSupport::RejectedByRuntime: return "RejectedByRuntime";
    case zr::FrameGenSupport::Available: return "Available";
    }
    return "UNKNOWN";
}

bool NameStartsWithFourDot(const char* name)
{
    return (name != nullptr) && (std::strncmp(name, "4.", 2) == 0);
}

// ---- 数据采集：UPSCALER 族 provider 枚举 / FG 族 QuerySupport -----------------

struct ProviderEntry {
    uint64_t id = 0;
    std::string name;
    bool operator==(const ProviderEntry& other) const
    {
        return id == other.id && name == other.name;
    }
};

std::vector<ProviderEntry> EnumerateUpscalerProviders(void* device, uint32_t* outCount)
{
    zr::ZupProviderVersion providers[8] = {};
    const uint32_t count = zr::ZupFsrEnumerateProviders(device, providers, 8u);
    if (outCount != nullptr) {
        *outCount = count;
    }
    std::vector<ProviderEntry> list;
    list.reserve(count);
    for (uint32_t i = 0u; i < count; ++i) {
        list.push_back({providers[i].versionId, std::string(providers[i].name)});
    }
    return list;
}

struct FgResult {
    zr::FrameGenSupport support = zr::FrameGenSupport::Unavailable;
    uint32_t returnCode = 0;
    std::string providerName;
};

FgResult QueryFg(void* device)
{
    const zr::FrameGenProbe probe = zr::FrameGenerator::QuerySupport(device);
    FgResult result;
    result.support = probe.support;
    result.returnCode = probe.returnCode;
    result.providerName = std::string(probe.providerName);
    return result;
}

// ---- 输出目录解析（仓库根标记 = Tools/Probes/README.md 登记表）---------------

struct OutLocation {
    std::string mode;    // "repo-root" | "portable-exe-dir"
    std::string outDir;  // 常量 out 根（独立于文件名计算——边界断言基准）
    std::string exeDir;
};

OutLocation ResolveOutLocation()
{
    char exePathA[MAX_PATH] = {};
    ::GetModuleFileNameA(nullptr, exePathA, MAX_PATH);
    std::string exeDir(exePathA);
    const size_t slash = exeDir.find_last_of("\\/");
    if (slash != std::string::npos) {
        exeDir = exeDir.substr(0, slash);
    }

    std::string cursor = exeDir;
    for (int depth = 0; depth < 8; ++depth) {
        const std::string marker = cursor + "\\Tools\\Probes\\README.md";
        if (::GetFileAttributesA(marker.c_str()) != INVALID_FILE_ATTRIBUTES) {
            return {"repo-root", cursor + "\\Tools\\Probes\\out", exeDir};
        }
        const size_t parentSlash = cursor.find_last_of("\\/");
        if (parentSlash == std::string::npos || parentSlash < 2) {
            break; // 已到盘根
        }
        cursor = cursor.substr(0, parentSlash);
    }
    // 免仓库拷贝模式：out 目录落在 exe 同目录（便携档，随 exe 走）
    return {"portable-exe-dir", exeDir + "\\out", exeDir};
}

// ---- JSON 组装小件 ------------------------------------------------------------

std::string ProvidersJson(const std::vector<ProviderEntry>& list, const int indent)
{
    std::string pad(static_cast<size_t>(indent), ' ');
    if (list.empty()) {
        return pad + "\"count\": 0,\n" + pad + "\"versions\": []";
    }
    std::string out = pad + "\"count\": " + std::to_string(list.size()) + ",\n" + pad
        + "\"versions\": [\n";
    for (size_t i = 0; i < list.size(); ++i) {
        out += pad + "  { \"id\": \"" + Hex64(list[i].id) + "\", \"name\": \""
            + JsonEscape(list[i].name) + "\" }" + ((i + 1 < list.size()) ? "," : "") + "\n";
    }
    out += pad + "]";
    return out;
}

std::string FgJson(const FgResult& fg, const int indent)
{
    std::string pad(static_cast<size_t>(indent), ' ');
    return pad + "\"support\": \"" + FgSupportName(fg.support) + "\",\n" + pad
        + "\"supportValue\": " + std::to_string(static_cast<uint32_t>(fg.support)) + ",\n" + pad
        + "\"returnCode\": \"" + zr::ZupFgReturnCodeName(fg.returnCode) + "\",\n" + pad
        + "\"returnCodeValue\": " + std::to_string(fg.returnCode) + ",\n" + pad
        + "\"providerName\": \"" + JsonEscape(fg.providerName) + "\"";
}

std::string VersionsJoined(const std::vector<ProviderEntry>& list)
{
    std::string out;
    for (size_t i = 0; i < list.size(); ++i) {
        if (i > 0) {
            out += ", ";
        }
        out += list[i].name;
    }
    return out;
}

bool AnyFourDot(const std::vector<ProviderEntry>& list)
{
    for (const ProviderEntry& e : list) {
        if (NameStartsWithFourDot(e.name.c_str())) {
            return true;
        }
    }
    return false;
}

struct MissingItem {
    std::string name;
    std::string reason;
};

} // namespace

int main()
{
    const std::string probedAt = UtcIso();
    const std::string stampFile = UtcStampFile();

    std::printf("======================================================================\n");
    std::printf(" fsr4-probe  (TASK-B-PROBE-01)  %s\n", probedAt.c_str());
    std::printf(" 本机 FSR 4.1 真实能力：真设备 vs null 对照，FG 族双设备对照\n");
    std::printf("======================================================================\n");

    std::vector<MissingItem> missing;

    // ---- 输出目录（常量根，独立于文件名计算）--------------------------------
    const OutLocation out = ResolveOutLocation();

    // ---- 主机信息 -----------------------------------------------------------
    // GetComputerNameA 的 NetBIOS 名上限 15 字符 + NUL；缓冲加倍防御
    constexpr DWORD kHostNameBufChars = 64;
    char hostNameA[kHostNameBufChars] = {};
    DWORD hostNameLen = kHostNameBufChars;
    const bool hostNameOk = ::GetComputerNameA(hostNameA, &hostNameLen) != FALSE;

    char exePathA[MAX_PATH] = {};
    ::GetModuleFileNameA(nullptr, exePathA, MAX_PATH);
    const std::string exePath(exePathA);

    // FFX 运行时 dll 是否齐备（exe 同目录——两机可跑的物理前提，缺则如实记录）
    const char* kFfxDlls[4] = {"amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_ags_x64.dll"};
    bool ffxPresent[4] = {};
    int ffxCount = 0;
    for (size_t i = 0; i < 4u; ++i) {
        const std::string path = out.exeDir + "\\" + kFfxDlls[i];
        ffxPresent[i] = ::GetFileAttributesA(path.c_str()) != INVALID_FILE_ATTRIBUTES;
        if (ffxPresent[i]) {
            ++ffxCount;
        }
    }

    std::printf("host.computerName : %s\n", hostNameOk ? hostNameA : "(取不到)");
    std::printf("host.exe          : %s\n", exePath.c_str());
    std::printf("host.ffxDlls      : %d/4 next to exe (upscaler/loader/framegeneration/ags)\n",
        ffxCount);
    if (ffxCount < 4) {
        for (size_t i = 0; i < 4; ++i) {
            if (!ffxPresent[i]) {
                missing.push_back({std::string("host.ffxDll.") + kFfxDlls[i],
                    "exe 同目录缺少该 FFX 运行时 dll——fsrRuntimePresent 极可能为 0；"
                    "按 README 两机跑法补拷后重跑"});
            }
        }
    }
    std::printf("out.mode          : %s -> %s\n", out.mode.c_str(), out.outDir.c_str());

    // ---- (a) caps：ZR_Upscaler_QueryCaps（C ABI 只读消费）--------------------
    ZrUpscalerCaps caps {};
    caps.structSize = sizeof(ZrUpscalerCaps);
    const int capsCode = ZR_Upscaler_QueryCaps(&caps);
    if (capsCode != 0) {
        missing.push_back({"caps.query",
            "ZR_Upscaler_QueryCaps code=" + std::to_string(capsCode)
                + "（-1=NULL/-2=越界/-3=structSize 不符）——caps 按缺记录"});
    }

    // ---- (c) 真设备：DXGI 首个非软件适配器 → D3D12CreateDevice（B-FSR-01 同源）
    IDXGIFactory1* factory = nullptr;
    IDXGIAdapter1* hardwareAdapter = nullptr; // 引用保留待建备（下方 Release）
    unsigned int adapterCount = 0u;
    bool adapterFound = false;
    std::string adapterDesc;
    uint32_t adapterVendor = 0u;
    uint32_t adapterDevice = 0u;
    unsigned long long adapterMemMb = 0u;
    if (SUCCEEDED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && factory != nullptr) {
        for (unsigned int i = 0u;; ++i) {
            IDXGIAdapter1* adapter = nullptr;
            if (factory->EnumAdapters1(i, &adapter) == DXGI_ERROR_NOT_FOUND) {
                break;
            }
            if (adapter == nullptr) {
                continue;
            }
            ++adapterCount;
            DXGI_ADAPTER_DESC1 desc {};
            const bool usable = SUCCEEDED(adapter->GetDesc1(&desc))
                && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0u;
            if (usable && !adapterFound) {
                adapterFound = true;
                adapterVendor = desc.VendorId;
                adapterDevice = desc.DeviceId;
                adapterMemMb = static_cast<unsigned long long>(desc.DedicatedVideoMemory)
                    / (1024u * 1024u);
                adapterDesc = Utf8FromWide(desc.Description);
                hardwareAdapter = adapter; // 保留引用；设备创建后 Release
            } else {
                adapter->Release();
            }
        }
        factory->Release();
    }
    if (!adapterFound) {
        missing.push_back({"host.adapter",
            "DXGI 未枚举到非软件适配器（无硬件/CI 环境）——真设备枚举按缺记录"});
    }

    ID3D12Device* realDevice = nullptr;
    const char* featureLevelName = nullptr;
    if (hardwareAdapter != nullptr) {
        // feature level 从高到低取首个成功（与 B-FSR-01 修法/zrhi 建备同序）
        struct LevelEntry {
            D3D_FEATURE_LEVEL value;
            const char* name;
        };
        const LevelEntry levels[3] = {{D3D_FEATURE_LEVEL_12_0, "12.0"},
            {D3D_FEATURE_LEVEL_11_1, "11.1"}, {D3D_FEATURE_LEVEL_11_0, "11.0"}};
        for (const LevelEntry& lv : levels) {
            if (SUCCEEDED(
                    ::D3D12CreateDevice(hardwareAdapter, lv.value, IID_PPV_ARGS(&realDevice)))) {
                featureLevelName = lv.name;
                break;
            }
        }
        hardwareAdapter->Release();
    }
    if (realDevice == nullptr) {
        missing.push_back({"providers.realDevice",
            "D3D12CreateDevice 失败（无硬件/驱动异常）——真设备路径枚举按 MISSING 记录，"
            "null 枚举照常采集"});
    }

    std::printf("host.adapter      : %s (vendor=%s device=%s, dmem=%llu MB, "
                "adapters=%u)\n",
        adapterFound ? adapterDesc.c_str() : "(无)", Hex32(adapterVendor).c_str(),
        Hex32(adapterDevice).c_str(), adapterMemMb, adapterCount);

    // ---- (b)(c) provider 枚举两路（UPSCALER 效果族）--------------------------
    uint32_t nullCount = 0u;
    const std::vector<ProviderEntry> nullList = EnumerateUpscalerProviders(nullptr, &nullCount);
    uint32_t realCount = 0u;
    std::vector<ProviderEntry> realList;
    const bool realListValid = (realDevice != nullptr);
    if (realListValid) {
        realList = EnumerateUpscalerProviders(realDevice, &realCount);
    }

    // ---- (d) FG 族双设备对照（FRAMEGENERATION 效果族）------------------------
    const FgResult fgNull = QueryFg(nullptr);
    FgResult fgReal;
    const bool fgRealValid = realListValid;
    if (fgRealValid) {
        fgReal = QueryFg(realDevice);
    } else {
        missing.push_back({"fg.realDevice",
            "无真实 D3D12 设备——FG 族真设备对照按 MISSING 记录（null 口径只能当"
            "\"未检测\"，见 fg_ffx.h QuerySupport 注释）"});
    }
    if (realDevice != nullptr) {
        realDevice->Release();
    }

    // ---- 对照结论（观察形态，不做机器断言）-----------------------------------
    const bool countsEqual = realListValid && (nullCount == realCount);
    const bool listsEqual = realListValid && (nullList == realList);
    const bool fourDotInNull = AnyFourDot(nullList);
    const bool fourDotInReal = AnyFourDot(realList);
    const char* shape = realListValid ? (countsEqual ? "negative-control"
                                                     : "positive-control")
                                      : "INDETERMINATE (real-device enumeration unavailable)";
    // 自检（探针内部一致性）：probeMode 与设备路径一致；C ABI 的 fsr4Available
    // 与直接枚举的 4.x 命中互相印证（两条独立路径——缓存 C ABI vs 本进程直枚举）
    const unsigned int expectedProbeMode
        = (capsCode == 0 && caps.fsrRuntimePresent == 0u) ? 2u
        : realListValid                                    ? 0u
                                                           : 1u;
    const bool probeModeConsistent
        = capsCode == 0 && caps.fsr4ProbeMode == expectedProbeMode;
    const bool fsr4Checkable = realListValid && capsCode == 0;
    const bool fsr4MatchesRealList
        = fsr4Checkable ? ((caps.fsr4Available != 0u) == fourDotInReal) : false;
    const bool fgSupportKnown
        = static_cast<uint32_t>(fgNull.support) <= 3u
        && (!fgRealValid || static_cast<uint32_t>(fgReal.support) <= 3u);

    // ---- (e) 控制台对照表 ----------------------------------------------------
    std::printf("\n[caps] ZR_Upscaler_QueryCaps (C ABI, ApiVersion=%u, structSize=%u)\n",
        static_cast<unsigned>(ZR_ApiVersion()),
        static_cast<unsigned>(sizeof(ZrUpscalerCaps)));
    if (capsCode == 0) {
        std::printf("  code=0\n");
        std::printf("  vendorId=%s deviceId=%s\n", Hex32(caps.vendorId).c_str(),
            Hex32(caps.deviceId).c_str());
        std::printf("  fsrRuntimePresent=%u  fsr4Available=%u  fsr4ProbeMode=%u  "
                    "dlssAvailable=%u\n",
            static_cast<unsigned>(caps.fsrRuntimePresent),
            static_cast<unsigned>(caps.fsr4Available),
            static_cast<unsigned>(caps.fsr4ProbeMode),
            static_cast<unsigned>(caps.dlssAvailable));
        std::printf("  preferredBackend=%u  activeBackend=%u\n",
            static_cast<unsigned>(caps.preferredBackend),
            static_cast<unsigned>(caps.activeBackend));
        if (caps.fsr4ProbeMode == 0u) {
            std::printf("  probeMode=0 -> 真实设备探测（结论可信）\n");
        } else if (caps.fsr4ProbeMode == 1u) {
            std::printf("  probeMode=1 -> 保守回退（null 枚举，结果可能失真，按\"未检测\"处理）\n");
        } else {
            std::printf("  probeMode=%u -> 未探测（按\"未检测\"处理）\n",
                static_cast<unsigned>(caps.fsr4ProbeMode));
        }
    } else {
        std::printf("  code=%d -> caps 缺（见 missing）\n", capsCode);
    }

    std::printf("\n[provider 枚举 · UPSCALER 族]  (ZupFsrEnumerateProviders, capacity 8)\n");
    std::printf("  device=null  count=%u : %s\n", static_cast<unsigned>(nullCount),
        VersionsJoined(nullList).c_str());
    if (realListValid) {
        std::printf("  device=real  count=%u : %s\n", static_cast<unsigned>(realCount),
            VersionsJoined(realList).c_str());
    } else {
        std::printf("  device=real  MISSING（无真实设备，见 missing）\n");
    }
    if (realListValid) {
        std::printf("  对照: 条数 null%sreal (%u%s%u)；版本列表%s；4.x 命中 null=%s real=%s\n",
            countsEqual ? "==" : "!=", static_cast<unsigned>(nullCount),
            countsEqual ? "==" : "!=", static_cast<unsigned>(realCount),
            listsEqual ? "一致" : "不一致", fourDotInNull ? "有" : "无",
            fourDotInReal ? "有" : "无");
        std::printf("  形态: %s\n", shape);
        if (countsEqual && listsEqual && !fourDotInReal) {
            std::printf("  解读: 负对照成立——两路一致且无 4.x → \"FSR4 不支持\"是硬件"
                        "真相而非探测缺陷\n");
        } else if (fourDotInReal && realCount > nullCount) {
            std::printf("  解读: 阳性对照成立——null 截断(%u) vs 真设备完整(%u 含 4.x)"
                        "→ B-FSR-01 修复效果实证（null 口径会误报\"不支持\"）\n",
                static_cast<unsigned>(nullCount), static_cast<unsigned>(realCount));
        }
    }

    std::printf("\n[FG 族对照 · FRAMEGENERATION]  (zr::FrameGenerator::QuerySupport)\n");
    std::printf("  device=null  support=%u(%s) rc=%u(%s) provider='%s'\n",
        static_cast<unsigned>(fgNull.support), FgSupportName(fgNull.support),
        static_cast<unsigned>(fgNull.returnCode),
        zr::ZupFgReturnCodeName(fgNull.returnCode), fgNull.providerName.c_str());
    if (fgRealValid) {
        std::printf("  device=real  support=%u(%s) rc=%u(%s) provider='%s'\n",
            static_cast<unsigned>(fgReal.support), FgSupportName(fgReal.support),
            static_cast<unsigned>(fgReal.returnCode),
            zr::ZupFgReturnCodeName(fgReal.returnCode), fgReal.providerName.c_str());
        std::printf("  对照: 两路support%s一致；FG provider='%s'\n",
            (fgNull.support == fgReal.support) ? "" : "不",
            fgReal.providerName.c_str());
    } else {
        std::printf("  device=real  MISSING（无真实设备，见 missing）\n");
    }

    if (realListValid && !fourDotInReal) {
        std::printf("\n[阳性对照] MISSING —— 本机真设备枚举无 4.x provider（fsr4Available=%u）。\n",
            (capsCode == 0) ? static_cast<unsigned>(caps.fsr4Available) : 0u);
        std::printf("  阳性对照（null=2 vs real=4 含 4.1.2）需 RDNA4/RX 9000 级硬件；\n");
        std::printf("  对端机补验步骤见 Tools/Probes/README.md fsr4-probe 节"
                    "（B-FSR-01 裁决移交）。\n");
    }

    // ---- JSON 组装（文件名白名单 + out 边界断言，env-probe.js 口径）----------
    const std::string fileName = "fsr4-" + stampFile + ".json";
    if (!IsValidProbeFileName(fileName)) {
        std::fprintf(stderr, "fsr4-probe: 输出文件名不合白名单，拒绝写文件: %s\n",
            fileName.c_str());
        return 1;
    }
    const std::string outRoot = out.outDir + "\\";    // 常量根
    const std::string outFile = outRoot + fileName;   // basename 已过白名单
    if (outFile.compare(0, outRoot.size(), outRoot) != 0) {
        std::fprintf(stderr, "fsr4-probe: 输出路径越出探针 out 目录，拒绝写文件\n");
        return 1;
    }
    std::error_code fsEc;
    std::filesystem::create_directories(std::filesystem::path(out.outDir), fsEc);
    if (fsEc.value() != 0) {
        std::fprintf(stderr, "fsr4-probe: out 目录不可创建 (%s): %s\n", out.outDir.c_str(),
            fsEc.message().c_str());
        return 1;
    }

    std::string json;
    json += "{\n";
    json += "  \"probedAt\": \"" + probedAt + "\",\n";
    json += "  \"probe\": \"fsr4-probe\",\n";
    json += "  \"task\": \"TASK-B-PROBE-01\",\n";
    json += "  \"outMode\": \"" + out.mode + "\",\n";
    json += "  \"outFile\": \"" + JsonEscape(outFile) + "\",\n";
    json += "  \"host\": {\n";
    json += "    \"computerName\": \"" + std::string(hostNameOk ? hostNameA : "") + "\",\n";
    json += "    \"adapter\": \"" + JsonEscape(adapterFound ? adapterDesc : "") + "\",\n";
    json += "    \"adapterFound\": " + std::string(adapterFound ? "true" : "false") + ",\n";
    json += "    \"vendorId\": \"" + Hex32(adapterVendor) + "\",\n";
    json += "    \"deviceId\": \"" + Hex32(adapterDevice) + "\",\n";
    json += "    \"dedicatedVideoMemoryMB\": " + std::to_string(adapterMemMb) + ",\n";
    json += "    \"adapterCount\": " + std::to_string(adapterCount) + ",\n";
    json += "    \"realDeviceCreated\": " + std::string(realListValid ? "true" : "false")
        + ",\n";
    json += "    \"featureLevel\": \""
        + std::string(featureLevelName != nullptr ? featureLevelName : "") + "\",\n";
    json += "    \"exePath\": \"" + JsonEscape(exePath) + "\",\n";
    json += "    \"ffxDllsPresent\": " + std::to_string(ffxCount) + ",\n";
    json += "    \"ffxDllsNextToExe\": {\n";
    for (size_t i = 0; i < 4u; ++i) {
        json += "      \"" + std::string(kFfxDlls[i]) + "\": "
            + (ffxPresent[i] ? "true" : "false") + ((i < 3u) ? "," : "") + "\n";
    }
    json += "    }\n";
    json += "  },\n";
    json += "  \"engine\": {\n";
    json += "    \"apiVersion\": " + std::to_string(ZR_ApiVersion()) + ",\n";
    json += "    \"zrVersion\": \"" + JsonEscape(ZR_GetVersion()) + "\"\n";
    json += "  },\n";
    json += "  \"caps\": {\n";
    if (capsCode == 0) {
        json += "    \"source\": \"ZR_Upscaler_QueryCaps\",\n";
        json += "    \"code\": 0,\n";
        json += "    \"structSize\": " + std::to_string(sizeof(ZrUpscalerCaps)) + ",\n";
        json += "    \"vendorId\": \"" + Hex32(caps.vendorId) + "\",\n";
        json += "    \"deviceId\": \"" + Hex32(caps.deviceId) + "\",\n";
        json += "    \"fsrRuntimePresent\": "
            + std::to_string(static_cast<unsigned>(caps.fsrRuntimePresent)) + ",\n";
        json += "    \"fsr4Available\": "
            + std::to_string(static_cast<unsigned>(caps.fsr4Available)) + ",\n";
        json += "    \"fsr4ProbeMode\": "
            + std::to_string(static_cast<unsigned>(caps.fsr4ProbeMode)) + ",\n";
        json += "    \"probeModeMeaning\": \"0=real-device(trusted) "
                "1=null-fallback(may be wrong) 2=not-probed\",\n";
        json += "    \"dlssAvailable\": "
            + std::to_string(static_cast<unsigned>(caps.dlssAvailable)) + ",\n";
        json += "    \"preferredBackend\": "
            + std::to_string(static_cast<unsigned>(caps.preferredBackend)) + ",\n";
        json += "    \"activeBackend\": "
            + std::to_string(static_cast<unsigned>(caps.activeBackend)) + "\n";
    } else {
        json += "    \"source\": \"ZR_Upscaler_QueryCaps\",\n";
        json += "    \"error\": " + std::to_string(capsCode) + "\n";
    }
    json += "  },\n";
    json += "  \"providers\": {\n";
    json += "    \"family\": \"UPSCALER\",\n";
    json += "    \"source\": \"ZupFsrEnumerateProviders (capacity 8)\",\n";
    json += "    \"nullDevice\": {\n" + ProvidersJson(nullList, 6) + "\n    },\n";
    json += "    \"realDevice\": {\n";
    json += realListValid ? (ProvidersJson(realList, 6) + "\n")
                          : "      \"status\": \"MISSING\",\n"
                            "      \"reason\": \"D3D12CreateDevice failed\"\n";
    json += "    }\n";
    json += "  },\n";
    json += "  \"fg\": {\n";
    json += "    \"family\": \"FRAMEGENERATION\",\n";
    json += "    \"source\": \"zr::FrameGenerator::QuerySupport (fg_ffx.h surface)\",\n";
    json += "    \"nullDevice\": {\n" + FgJson(fgNull, 6) + "\n    },\n";
    json += "    \"realDevice\": {\n";
    json += fgRealValid ? (FgJson(fgReal, 6) + "\n")
                        : "      \"status\": \"MISSING\",\n"
                          "      \"reason\": \"no real D3D12 device\"\n";
    json += "    }\n";
    json += "  },\n";
    json += "  \"comparison\": {\n";
    json += "    \"upscalerNullCount\": " + std::to_string(nullCount) + ",\n";
    json += "    \"upscalerRealCount\": " + std::to_string(realCount) + ",\n";
    json += "    \"upscalerCountsEqual\": " + std::string(countsEqual ? "true" : "false")
        + ",\n";
    json += "    \"upscalerVersionListsEqual\": "
        + std::string(listsEqual ? "true" : "false") + ",\n";
    json += "    \"fourDotXInNullList\": " + std::string(fourDotInNull ? "true" : "false")
        + ",\n";
    json += "    \"fourDotXInRealList\": " + std::string(fourDotInReal ? "true" : "false")
        + ",\n";
    json += "    \"fgSupportEqual\": "
        + std::string((fgRealValid && fgNull.support == fgReal.support) ? "true" : "false")
        + ",\n";
    json += "    \"fsr4Available\": "
        + std::to_string((capsCode == 0) ? static_cast<unsigned>(caps.fsr4Available) : 0u)
        + ",\n";
    json += "    \"fsr4ProbeMode\": "
        + std::to_string((capsCode == 0) ? static_cast<unsigned>(caps.fsr4ProbeMode) : 2u)
        + ",\n";
    json += "    \"shape\": \"" + std::string(shape) + "\"\n";
    json += "  },\n";
    json += "  \"selfCheck\": {\n";
    json += "    \"probeModeConsistent\": "
        + std::string(probeModeConsistent ? "true" : "false") + ",\n";
    json += "    \"fsr4AvailableMatchesRealList\": "
        + std::string(fsr4MatchesRealList ? "true" : "false") + ",\n";
    json += "    \"fsr4Checkable\": " + std::string(fsr4Checkable ? "true" : "false")
        + ",\n";
    json += "    \"fgSupportValuesKnown\": " + std::string(fgSupportKnown ? "true" : "false")
        + "\n";
    json += "  },\n";
    json += "  \"positiveControl\": {\n";
    if (fourDotInReal) {
        json += "    \"status\": \"CAPTURED\",\n";
        json += "    \"evidence\": \"本机真设备枚举命中 4.x provider（见 providers.realDevice）"
                "——B-FSR-01 修复的阳性对照在此机器成立\"\n";
    } else {
        json += "    \"status\": \"MISSING\",\n";
        json += "    \"reason\": \"本机硬件/运行时组合无 FSR4 provider（真设备枚举无 4.x）。"
                "阳性对照（null=2 vs real=4 含 4.1.2）需 RDNA4/RX 9000 级硬件——"
                "按 B-FSR-01 主控裁决在对端机 RX 9060 XT 补验："
                "步骤见 Tools/Probes/README.md fsr4-probe 节，JSON 回传存档\"\n";
    }
    json += "  },\n";
    json += "  \"missing\": [\n";
    for (size_t i = 0; i < missing.size(); ++i) {
        json += "    { \"name\": \"" + JsonEscape(missing[i].name) + "\", \"reason\": \""
            + JsonEscape(missing[i].reason) + "\" }" + ((i + 1 < missing.size()) ? "," : "")
            + "\n";
    }
    json += "  ],\n";
    json += "  \"references\": [\n";
    json += "    { \"label\": \"B-FSR-01 本机负对照存档（780M，2026-11-14 01:17）\", "
            "\"value\": \"null=real=2 (3.1.5/2.3.4)，fsr4Available=0，probeMode=0\", "
            "\"source\": \"Docs/Logs/TASK-B-FSR-01.md §5\" },\n";
    json += "    { \"label\": \"阳性对照（RX 9060 XT，2026-11-13 实测）\", "
            "\"value\": \"null=2 vs 真设备=4 含 4.1.2\", "
            "\"source\": \"Docs/Planning/ZRender-改进计划-2026-11-13.md §3-P0\" }\n";
    json += "  ]\n";
    json += "}\n";

    std::ofstream file(outFile, std::ios::binary | std::ios::trunc);
    if (!file) {
        std::fprintf(stderr, "fsr4-probe: 无法打开输出文件: %s\n", outFile.c_str());
        return 1;
    }
    file << json;
    file.close();
    if (file.fail()) {
        std::fprintf(stderr, "fsr4-probe: 输出文件写入失败: %s\n", outFile.c_str());
        return 1;
    }

    std::printf("\n[missing] %zu 项（不填默认值，如实记录）\n", missing.size());
    for (const MissingItem& item : missing) {
        std::printf("  MISSING | %s | %s\n", item.name.c_str(), item.reason.c_str());
    }
    std::printf("\nJSON -> %s\n", outFile.c_str());
    std::printf("exit=0（JSON 已落盘；FSR4 支持与否是数据不是失败，"
                "对照表与 JSON 数字应一致）\n");
    return 0;
}
