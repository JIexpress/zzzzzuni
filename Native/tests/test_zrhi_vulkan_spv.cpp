// =============================================================================
// test_zrhi_vulkan_spv.cpp —— --r5 SPIR-V 清单完整性（TASK-B-R8-01）
//
// 断言 glslc 构建产物（Native/build/bin/shaders_spv/）覆盖 --r5 全部 8 个着色器
// 文件 / 16 个入口点：每个产物存在、非空、以 SPIR-V magic（0x07230203，
// little-endian 字节序 03 02 23 07）开头、入口点名字串在场。清单表与
// Native/CMakeLists.txt 的 glslc 步骤逐条互为镜像（修改任一侧须同步）：
//   - 产物名 <stem>_vs.spv / <stem>_fs.spv（hiz 例外：<stem>_copy_fs/_min_fs）
//   - texBase = rootCbvCount、sampBase = rootCbvCount + srvTableCount
//     （zrhi_vulkan_device.cpp CreatePipelineState 的 set0 布局对位式——PSO 签名
//     改了而 CMake 绑定基没跟上时此处先红）
//
// 仅做文件级检查（无 GPU/无 vulkan.h 依赖；exe 目录推导用 Win32
// GetModuleFileNameA，不拉 vulkan.h）；ZR_HAS_VULKAN 缺失（CI 无 SDK）
// 时用例体为空、TU 仍合法——与 test_zrhi_vulkan_translate 同策。
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h> // GetModuleFileNameA —— SPIR-V 候选目录的 exe 推导（TASK-G-65）
#endif

#if defined(ZR_HAS_VULKAN)

namespace {

/// 清单条目：一个 SPIR-V 产物 = 一个着色器入口（hiz 的两个 PS 各占一行）
struct SpvManifestEntry {
    const char* file;         // glslc 产物文件名（shaders_spv/ 相对）
    const char* hlsl;         // 同源 HLSL 源（两编译器消费，禁 fork）
    const char* entry;        // 入口点（glslc -fentry-point / OpEntryPoint 名）
    uint32_t rootCbvCount;    // PSO 签名 N（set0 binding0..N-1 = UNIFORM_BUFFER）
    uint32_t srvTableCount;   // PSO 签名 M（SAMPLED_IMAGE / SAMPLER 段各 M）
    uint32_t texBase;         // CMake -ftexture-binding-base（= N）
    uint32_t sampBase;        // CMake -fsampler-binding-base（= N+M）
};

/// 8 着色器文件 / 16 入口点 —— 与 CMakeLists.txt glslc 步骤逐条对位
/// （texBase/sampBase 逐条抄自 CMake 旗标；对位式由下面的用例断言复核）
constexpr SpvManifestEntry kSpvManifest[] = {
    // 1. pbr_r5.hlsl（N=5 M=1：t0→5 s0→6）
    { "pbr_r5_vs.spv", "pbr_r5.hlsl", "VsMain", 5u, 1u, 5u, 6u },
    { "pbr_r5_fs.spv", "pbr_r5.hlsl", "PsMain", 5u, 1u, 5u, 6u },
    // 2. toon.hlsl（N=1 M=0：仅 b0；无纹理，基值无效果——按签名式填 N / N+M）
    { "toon_vs.spv", "toon.hlsl", "VsMain", 1u, 0u, 1u, 1u },
    { "toon_fs.spv", "toon.hlsl", "PsMain", 1u, 0u, 1u, 1u },
    // 3. toon_outline.hlsl（N=1 M=0）
    { "toon_outline_vs.spv", "toon_outline.hlsl", "VsMain", 1u, 0u, 1u, 1u },
    { "toon_outline_fs.spv", "toon_outline.hlsl", "PsMain", 1u, 0u, 1u, 1u },
    // 4. shadow_depth.hlsl（N=2 M=0：depth-only 无 PS）
    { "shadow_depth_vs.spv", "shadow_depth.hlsl", "VsMain", 2u, 0u, 2u, 2u },
    // 5. hiz.hlsl（N=1 M=1：t0→1 s0→2；单 VS 供两个 PS 入口共用）
    { "hiz_vs.spv", "hiz.hlsl", "VsMain", 1u, 1u, 1u, 2u },
    { "hiz_copy_fs.spv", "hiz.hlsl", "HiZCopyPsMain", 1u, 1u, 1u, 2u },
    { "hiz_min_fs.spv", "hiz.hlsl", "HiZMinPsMain", 1u, 1u, 1u, 2u },
    // 6. ssr_trace.hlsl（N=1 M=2：t0→1 t1→2 s0→3 s1→4）
    { "ssr_trace_vs.spv", "ssr_trace.hlsl", "VsMain", 1u, 2u, 1u, 3u },
    { "ssr_trace_fs.spv", "ssr_trace.hlsl", "PsMain", 1u, 2u, 1u, 3u },
    // 7. ssr_blur.hlsl（N=1 M=1）
    { "ssr_blur_vs.spv", "ssr_blur.hlsl", "VsMain", 1u, 1u, 1u, 2u },
    { "ssr_blur_fs.spv", "ssr_blur.hlsl", "PsMain", 1u, 1u, 1u, 2u },
    // 8. composite.hlsl（N=1 M=2）
    { "composite_vs.spv", "composite.hlsl", "VsMain", 1u, 2u, 1u, 3u },
    { "composite_fs.spv", "composite.hlsl", "PsMain", 1u, 2u, 1u, 3u },
};

constexpr uint32_t kSpvEntryCount
    = sizeof(kSpvManifest) / sizeof(kSpvManifest[0]); // 16 入口点
constexpr uint32_t kSpvMagic = 0x07230203u;          // SPIR-V little-endian magic

/// 可执行文件所在目录（含尾部分隔符）；无法取得时返回空串。
/// TASK-G-65：测试二进制位于 Native/build/bin/<Config>/zrender_test.exe，其
/// `../shaders_spv/` 即 Native/build/bin/shaders_spv/ —— 于是"任意 CWD 直跑
/// exe"（IDE test runner / 双击 exe / 自建 CI）不再产生 SPIR-V 误报。
std::string ExeDirectory()
{
#if defined(_WIN32)
    char buf[MAX_PATH * 4] = { 0 };
    const DWORD cap = static_cast<DWORD>(sizeof(buf));
    const DWORD n = ::GetModuleFileNameA(nullptr, buf, cap);
    if (n == 0u || n >= cap) {
        return std::string();
    }
    const std::string full(buf, static_cast<size_t>(n));
    const std::string::size_type slash = full.find_last_of("\\/");
    if (slash == std::string::npos) {
        return std::string();
    }
    return full.substr(0u, slash + 1u); // 保留尾部分隔符
#else
    return std::string(); // 非 Windows 构建：只用下面的相对候选（回退）
#endif
}

/// 候选目录（按优先级）：exe 目录推导优先（CWD 无关），随后回退到历史相对
/// 候选——与窗口程序 FindSpirvPaths 同序：仓库根 / Native/build 相对 /
/// build 相对（对应 CWD = 仓库根 / Native/ / Native/build/bin）。
std::vector<std::string> SpvCandidateDirs()
{
    std::vector<std::string> dirs;
    const std::string exeDir = ExeDirectory();
    if (!exeDir.empty()) {
        dirs.push_back(exeDir + "../shaders_spv/");
        dirs.push_back(exeDir + "shaders_spv/");
    }
    dirs.push_back("Native/build/bin/shaders_spv/");
    dirs.push_back("build/bin/shaders_spv/");
    dirs.push_back("shaders_spv/");
    return dirs;
}

/// 在候选目录中定位 shaders_spv/（顺序见 SpvCandidateDirs）
std::string LocateSpvFile(const char* file)
{
    for (const std::string& dir : SpvCandidateDirs()) {
        const std::string candidate = dir + file;
        std::ifstream f(candidate, std::ios::binary | std::ios::ate);
        if (f) {
            return candidate;
        }
    }
    return std::string();
}

} // namespace

TEST_CASE("ZrVulkan --r5 SPIR-V manifest: 8 shaders / 16 entry points complete and valid")
{
    // 清单形状锁定：16 入口点，且覆盖 8 个不同 hlsl 源（修改清单时两断言同步改）
    CHECK(kSpvEntryCount == 16u);
    uint32_t distinctHlsl = 0u;
    for (uint32_t i = 0; i < kSpvEntryCount; ++i) {
        bool first = true;
        for (uint32_t j = 0; j < i; ++j) {
            if (std::strcmp(kSpvManifest[i].hlsl, kSpvManifest[j].hlsl) == 0) {
                first = false;
                break;
            }
        }
        distinctHlsl += first ? 1u : 0u;
    }
    CHECK(distinctHlsl == 8u);

    // 候选目录拼成可读文本，供失败信息一次性列出（TASK-G-65）
    std::string triedDirs;
    for (const std::string& dir : SpvCandidateDirs()) {
        if (!triedDirs.empty()) {
            triedDirs += " | ";
        }
        triedDirs += dir;
    }

    // 每条：文件在场、非空、SPIR-V magic、入口点名字串在场
    for (const SpvManifestEntry& e : kSpvManifest) {
        const std::string path = LocateSpvFile(e.file);
        // TASK-G-65：先构造 std::string 再交给 doctest。原来的
        // `"SPIR-V missing: " << e.file` 从未进入 MessageBuilder——两个操作数都是
        // 指针，C++ 只能做 bool 折叠/移位，实际打印 `SPIR-V missing: 1`，文件名丢失。
        // 断言条件 `!path.empty()` 与 REQUIRE 级别一字未动。
        const std::string missing = std::string("SPIR-V missing: ") + e.file
            + " (build vulkan_shaders target first); tried: " + triedDirs;
        REQUIRE_MESSAGE(!path.empty(), missing);
        std::ifstream f(path, std::ios::binary | std::ios::ate);
        REQUIRE(f);
        const std::streamoff size = f.tellg();
        CHECK(size > 20); // 5 字头部 + 至少一条指令（非空产物）
        f.seekg(0, std::ios::beg);
        std::vector<char> bytes(static_cast<size_t>(size));
        REQUIRE(f.read(bytes.data(), size));
        uint32_t magic = 0u;
        std::memcpy(&magic, bytes.data(), sizeof(magic));
        CHECK(magic == kSpvMagic);
        const bool entryPresent = std::search(bytes.begin(), bytes.end(), e.entry,
            e.entry + std::strlen(e.entry)) != bytes.end();
        CHECK_MESSAGE(entryPresent, "OpEntryPoint name '" << e.entry << "' not found in "
                                                         << e.file);
    }
}

TEST_CASE("ZrVulkan --r5 SPIR-V manifest mirrors PSO signatures / binding bases")
{
    // 绑定基对位式：texBase=N、sampBase=N+M（CMake 旗标与 VK set0 布局同式；
    // N/M 来自各 PSO 创建点的 rootCbvCount/srvTableCount，上界同源约束）
    for (const SpvManifestEntry& e : kSpvManifest) {
        CHECK(e.rootCbvCount >= 1u);
        CHECK(e.rootCbvCount <= 5u);
        CHECK(e.srvTableCount <= 2u);
        CHECK(e.texBase == e.rootCbvCount);
        CHECK(e.sampBase == e.rootCbvCount + e.srvTableCount);
        if (e.srvTableCount > 0u) {
            // 最宽 PSO（pbr_r5 N=5 M=1）的 sampler binding = N+M = 6 < 9
            // （set0 布局 bindings[9] 上界；N+2M ≤ 9 同式）
            CHECK(e.rootCbvCount + 2u * e.srvTableCount <= 9u);
        }
    }
    // 无 fork 抽查：带纹理的 5 个 hlsl 均为单一源文件（D3D12 FXC 与 VK glslc 同源
    // 消费；fork 会在此表现为清单出现 xxx_vk.hlsl 类条目——清单只收 8 个 hlsl 名）
    const char* textured[] = { "pbr_r5.hlsl", "hiz.hlsl", "ssr_trace.hlsl",
        "ssr_blur.hlsl", "composite.hlsl" };
    for (const char* hlsl : textured) {
        bool found = false;
        for (const SpvManifestEntry& e : kSpvManifest) {
            if (std::strcmp(e.hlsl, hlsl) == 0) {
                found = true;
                break;
            }
        }
        CHECK(found);
    }
}

#endif // ZR_HAS_VULKAN
