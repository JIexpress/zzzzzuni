// =============================================================================
// test_zfg_dxgi_exports.cpp —— dxgi 代理的"导出参数个数表"静态用例（TASK-R10-03 A-1）
//
// R10-02 A-1：`zfg_dxgi_proxy.cpp` 曾断言"20 个导出均 ≤4 参"并用 4 参通用型转发，
// 而 DXGID3D10CreateDevice 是 7 参 → 第 5–7 参取未初始化栈槽 → 真实实现把
// ID3D10Device* 写到野地址。这类缺陷在 dumpbin 门禁（只比导出名/序号）与真机跑
// （Unity 不调这些导出）里都没有任何断言面，所以必须由**表驱动静态用例**兜住。
//
// 本用例断言四件事：
//   1. 每个导出的**声明形参个数**（= 转发覆盖）等于表里的 declaredArity —— 截断即红；
//      声明与定义不一致时 .cpp 直接编译不过（extern "C" 类型不匹配），双保险。
//   2. declaredArity ≥ paramCount（真实参数个数）——"转发是否覆盖"的判据。
//   3. paramCount ≤ kForwardSlots（8）——新增 >8 参导出必须扩窗口，否则红。
//   4. zfg_dxgi.def 里的每个 dxgi 导出都在表里（新增导出漏配即红），且数量一致。
// =============================================================================
#include "doctest/doctest.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "zfgprobe/zfg_dxgi_exports.h"

namespace {

/// 函数指针的形参个数（Win64 上 WINAPI/__stdcall 与默认约定同型，故只需一个特化）
template <typename T>
struct Arity;

template <typename R, typename... A>
struct Arity<R (*)(A...)>
{
    static constexpr size_t value = sizeof...(A);
};

template <typename Fn>
constexpr size_t ArityOf(Fn)
{
    return Arity<Fn>::value;
}

/// 取"声明的函数指针类型"的 arity（**不求值上下文**）。
///
/// 为什么不直接写 `&Name`：真身定义在 zfg_dxgi.dll 目标（zfg_dxgi_proxy.cpp）里，
/// 不参与 zrender_test 链接——直接取地址就是 LNK2019。而且绝不能把该 TU 挪进
/// 测试目标：那会让 zrhi 里 `::CreateDXGIFactory*` 的调用在本测试进程内解析到
/// 代理转发函数（行为劫持）。`decltype(&Name)` 只取类型、不 odr-use 函数；
/// "声明 arity == 定义 arity" 由 zfg_dxgi_proxy.cpp 的编译锁死（extern "C"
/// 类型不匹配即编译错误），两层合起来才是完整判据。
#define ZFG_DECLARED_ARITY(fn) ArityOf(static_cast<decltype(&(fn))>(nullptr))

/// 导出名 → 该导出**声明**的形参个数（与 zfg_dxgi_exports.h 的声明一一对应）
size_t DeclaredArityOf(const char* name)
{
    if (std::strcmp(name, "ApplyCompatResolutionQuirking") == 0) {
        return ZFG_DECLARED_ARITY(ApplyCompatResolutionQuirking);
    }
    if (std::strcmp(name, "CompatString") == 0) {
        return ZFG_DECLARED_ARITY(CompatString);
    }
    if (std::strcmp(name, "CompatValue") == 0) {
        return ZFG_DECLARED_ARITY(CompatValue);
    }
    if (std::strcmp(name, "CreateDXGIFactory") == 0) {
        return ZFG_DECLARED_ARITY(CreateDXGIFactory);
    }
    if (std::strcmp(name, "CreateDXGIFactory1") == 0) {
        return ZFG_DECLARED_ARITY(CreateDXGIFactory1);
    }
    if (std::strcmp(name, "CreateDXGIFactory2") == 0) {
        return ZFG_DECLARED_ARITY(CreateDXGIFactory2);
    }
    if (std::strcmp(name, "DXGID3D10CreateDevice") == 0) {
        return ZFG_DECLARED_ARITY(DXGID3D10CreateDevice);
    }
    if (std::strcmp(name, "DXGID3D10CreateLayeredDevice") == 0) {
        return ZFG_DECLARED_ARITY(DXGID3D10CreateLayeredDevice);
    }
    if (std::strcmp(name, "DXGID3D10GetLayeredDeviceSize") == 0) {
        return ZFG_DECLARED_ARITY(DXGID3D10GetLayeredDeviceSize);
    }
    if (std::strcmp(name, "DXGID3D10RegisterLayers") == 0) {
        return ZFG_DECLARED_ARITY(DXGID3D10RegisterLayers);
    }
    if (std::strcmp(name, "DXGIDeclareAdapterRemovalSupport") == 0) {
        return ZFG_DECLARED_ARITY(DXGIDeclareAdapterRemovalSupport);
    }
    if (std::strcmp(name, "DXGIDisableVBlankVirtualization") == 0) {
        return ZFG_DECLARED_ARITY(DXGIDisableVBlankVirtualization);
    }
    if (std::strcmp(name, "DXGIDumpJournal") == 0) {
        return ZFG_DECLARED_ARITY(DXGIDumpJournal);
    }
    if (std::strcmp(name, "DXGIGetDebugInterface1") == 0) {
        return ZFG_DECLARED_ARITY(DXGIGetDebugInterface1);
    }
    if (std::strcmp(name, "DXGIReportAdapterConfiguration") == 0) {
        return ZFG_DECLARED_ARITY(DXGIReportAdapterConfiguration);
    }
    if (std::strcmp(name, "PIXBeginCapture") == 0) {
        return ZFG_DECLARED_ARITY(PIXBeginCapture);
    }
    if (std::strcmp(name, "PIXEndCapture") == 0) {
        return ZFG_DECLARED_ARITY(PIXEndCapture);
    }
    if (std::strcmp(name, "PIXGetCaptureState") == 0) {
        return ZFG_DECLARED_ARITY(PIXGetCaptureState);
    }
    if (std::strcmp(name, "SetAppCompatStringPointer") == 0) {
        return ZFG_DECLARED_ARITY(SetAppCompatStringPointer);
    }
    if (std::strcmp(name, "UpdateHMDEmulationStatus") == 0) {
        return ZFG_DECLARED_ARITY(UpdateHMDEmulationStatus);
    }
    return static_cast<size_t>(-1);
}

/// .def 文件定位（ctest 与手工都从仓库根跑；再兜几层相对路径）
bool ReadDefExports(std::vector<std::string>* names, std::string* usedPath)
{
    if (names == nullptr) {
        return false;
    }
    const char* candidates[] = {
        "Native/zfgprobe/zfg_dxgi.def",
        "../Native/zfgprobe/zfg_dxgi.def",
        "../../Native/zfgprobe/zfg_dxgi.def",
        "zfg_dxgi.def",
    };
    FILE* f = nullptr;
    for (const char* path : candidates) {
        if (fopen_s(&f, path, "rb") == 0 && f != nullptr) {
            if (usedPath != nullptr) {
                *usedPath = path;
            }
            break;
        }
    }
    if (f == nullptr) {
        return false;
    }
    char line[512] = {};
    bool inExports = false;
    while (fgets(line, static_cast<int>(sizeof(line)), f) != nullptr) {
        // 去掉行尾换行
        size_t len = std::strlen(line);
        while (len > 0u && (line[len - 1u] == '\n' || line[len - 1u] == '\r')) {
            line[--len] = '\0';
        }
        // 整行注释 / 空行
        const char* p = line;
        while (*p == ' ' || *p == '\t') {
            ++p;
        }
        if (*p == '\0' || *p == ';') {
            continue;
        }
        if (_stricmp(p, "EXPORTS") == 0) {
            inExports = true;
            continue;
        }
        if (!inExports) {
            continue;
        }
        // 取第一个 token（导出名）；跳过本代理私有的 ZFG_* 辅助入口
        char name[128] = {};
        size_t i = 0u;
        while (p[i] != '\0' && p[i] != ' ' && p[i] != '\t' && i + 1u < sizeof(name)) {
            name[i] = p[i];
            ++i;
        }
        name[i] = '\0';
        if (name[0] == '\0' || std::strncmp(name, "ZFG_", 4u) == 0) {
            continue;
        }
        names->emplace_back(name);
    }
    fclose(f);
    return true;
}

} // namespace

TEST_CASE("zfg dxgi exports: 声明形参个数与表一致（截断即红）")
{
    size_t checked = 0u;
    for (size_t i = 0u; i < zfg::kDxgiExportCount; ++i) {
        const zfg::DxgiExportSpec& spec = zfg::kDxgiExports[i];
        CAPTURE(spec.name);
        const size_t declared = DeclaredArityOf(spec.name);
        CHECK(declared != static_cast<size_t>(-1)); // 表里的名字必须有对应声明
        CHECK(declared == spec.declaredArity);
        ++checked;
    }
    CHECK(checked == zfg::kDxgiExportCount);
}

TEST_CASE("zfg dxgi exports: 声明形参个数覆盖真实参数个数")
{
    for (size_t i = 0u; i < zfg::kDxgiExportCount; ++i) {
        const zfg::DxgiExportSpec& spec = zfg::kDxgiExports[i];
        CAPTURE(spec.name);
        CAPTURE(static_cast<int>(spec.paramCount));
        CAPTURE(static_cast<int>(spec.declaredArity));
        CHECK(spec.declaredArity >= spec.paramCount);      // 转发覆盖
        CHECK(spec.paramCount <= zfg::kForwardSlots);      // 不超过转发窗口
        CHECK(spec.declaredArity <= zfg::kForwardSlots);
        // 0 参导出允许 0 参声明；其余必须有形参
        const bool aritySane = (spec.declaredArity > 0u) || (spec.paramCount == 0u);
        CHECK(aritySane);
    }
}

TEST_CASE("zfg dxgi exports: 回归钉 —— DXGID3D10* 不再按 4 参截断")
{
    // R10-02 A-1：这三条曾是 4 参声明转发 7/6/7 参导出
    const size_t createDevice = DeclaredArityOf("DXGID3D10CreateDevice");
    const size_t layered = DeclaredArityOf("DXGID3D10CreateLayeredDevice");
    const size_t layeredSize = DeclaredArityOf("DXGID3D10GetLayeredDeviceSize");
    CHECK(createDevice >= 7u);
    CHECK(layered >= 6u);
    CHECK(layeredSize >= 7u);
    // 旧值 4 必须已经不成立
    CHECK(createDevice != 4u);
    CHECK(layered != 4u);
    CHECK(layeredSize != 4u);

    // 表里的"真实参数个数"与公开原型一致（7 / 6 / 7）
    size_t idxCreateDevice = static_cast<size_t>(-1);
    size_t idxLayered = static_cast<size_t>(-1);
    size_t idxLayeredSize = static_cast<size_t>(-1);
    for (size_t i = 0u; i < zfg::kDxgiExportCount; ++i) {
        const char* n = zfg::kDxgiExports[i].name;
        if (std::strcmp(n, "DXGID3D10CreateDevice") == 0) {
            idxCreateDevice = i;
        } else if (std::strcmp(n, "DXGID3D10CreateLayeredDevice") == 0) {
            idxLayered = i;
        } else if (std::strcmp(n, "DXGID3D10GetLayeredDeviceSize") == 0) {
            idxLayeredSize = i;
        }
    }
    REQUIRE(idxCreateDevice != static_cast<size_t>(-1));
    REQUIRE(idxLayered != static_cast<size_t>(-1));
    REQUIRE(idxLayeredSize != static_cast<size_t>(-1));
    CHECK(zfg::kDxgiExports[idxCreateDevice].paramCount == 7u);
    CHECK(zfg::kDxgiExports[idxLayered].paramCount == 6u);
    CHECK(zfg::kDxgiExports[idxLayeredSize].paramCount == 7u);
}

TEST_CASE("zfg dxgi exports: 表项下标枚举与表顺序一致")
{
    CHECK(zfg::kIdxApplyCompatResolutionQuirking == 0u);
    CHECK(std::strcmp(zfg::kDxgiExports[zfg::kIdxApplyCompatResolutionQuirking].name,
              "ApplyCompatResolutionQuirking")
        == 0);
    CHECK(std::strcmp(zfg::kDxgiExports[zfg::kIdxCreateDXGIFactory2].name, "CreateDXGIFactory2")
        == 0);
    CHECK(std::strcmp(zfg::kDxgiExports[zfg::kIdxDXGID3D10CreateDevice].name,
              "DXGID3D10CreateDevice")
        == 0);
    CHECK(std::strcmp(zfg::kDxgiExports[zfg::kIdxPIXGetCaptureState].name, "PIXGetCaptureState")
        == 0);
    CHECK(std::strcmp(zfg::kDxgiExports[zfg::kIdxUpdateHMDEmulationStatus].name,
              "UpdateHMDEmulationStatus")
        == 0);
    CHECK(zfg::kDxgiExportCount == 20u);
}

TEST_CASE("zfg dxgi exports: zfg_dxgi.def 的每个导出都在参数个数表里")
{
    std::vector<std::string> defNames;
    std::string usedPath;
    const bool ok = ReadDefExports(&defNames, &usedPath);
    // doctest 的 INFO 走 operator<<（写 `INFO(a, b)` 会被逗号运算符吃掉第二个操作数）
    INFO("def path: " << usedPath);
    REQUIRE(ok); // 找不到 .def = 用例前提不成立（ctest 的工作目录是仓库根）
    CHECK(defNames.size() == zfg::kDxgiExportCount);

    size_t matched = 0u;
    for (const std::string& name : defNames) {
        CAPTURE(name);
        bool found = false;
        for (size_t i = 0u; i < zfg::kDxgiExportCount; ++i) {
            if (name == zfg::kDxgiExports[i].name) {
                found = true;
                break;
            }
        }
        CHECK(found); // 新增导出漏配参数个数表 → 红
        if (found) {
            ++matched;
        }
    }
    CHECK(matched == zfg::kDxgiExportCount);
}
