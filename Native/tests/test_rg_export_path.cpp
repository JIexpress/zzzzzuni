// =============================================================================
// test_rg_export_path.cpp —— rg.export 落盘路径限定单测（TASK-X-SEC-01）
//
// 覆盖判据 V3 的纯逻辑面：
//   - 允许根内相对/绝对路径通过（含子目录）
//   - `..` 分量拒绝（即使规范化后仍在根内也拒绝）；绝对路径越界拒绝
//   - 目录前缀混淆（root 与 root_evil）拒绝——逐分量比较，不做字符串 startsWith
//   - 盘符相对（C:file）/ ADS（a:b）/ 保留设备名（CON.txt）拒绝
//   - 符号链接/junction 逃逸拒绝（先用空目录建 junction；无权限则跳过并在报告注明）
// 文件系统用例全部落在 %TEMP% 的一次性目录，用例结束 remove_all 清理。
// =============================================================================
#include "doctest/doctest.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <string>
#include <system_error>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <winioctl.h>
#endif

#include "zrender/rg_export_path.h"

namespace fs = std::filesystem;

namespace {

#ifdef _WIN32
// 用户态 SDK 不导出 REPARSE_DATA_BUFFER（在 ntddk/ntifs.h），这里按 Win32 公开
// 布局定义一个 mount point 视图：8 字节头（tag/len/reserved）+ 4 个 USHORT
// 偏移长度 + 路径缓冲区。
struct ReparseMountPointBuffer {
    ULONG ReparseTag;
    USHORT ReparseDataLength;
    USHORT Reserved;
    USHORT SubstituteNameOffset;
    USHORT SubstituteNameLength;
    USHORT PrintNameOffset;
    USHORT PrintNameLength;
    WCHAR PathBuffer[1];
};

/// 创建目录 junction（IO_REPARSE_TAG_MOUNT_POINT；不需要 SeCreateSymbolicLink 权限）
bool CreateJunction(const fs::path& linkPath, const fs::path& targetPath)
{
    if (!::CreateDirectoryW(linkPath.c_str(), nullptr)
        && ::GetLastError() != ERROR_ALREADY_EXISTS) {
        return false;
    }
    HANDLE handle = ::CreateFileW(linkPath.c_str(), GENERIC_WRITE, 0u, nullptr,
        OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return false;
    }

    const std::wstring substitute = L"\\??\\" + targetPath.native();
    const USHORT subBytes
        = static_cast<USHORT>(substitute.size() * sizeof(wchar_t));
    const USHORT printBytes
        = static_cast<USHORT>(targetPath.native().size() * sizeof(wchar_t));
    const wchar_t nul = L'\0';

    std::vector<unsigned char> buffer(MAXIMUM_REPARSE_DATA_BUFFER_SIZE, 0u);
    auto* reparse = reinterpret_cast<ReparseMountPointBuffer*>(buffer.data());
    reparse->ReparseTag = IO_REPARSE_TAG_MOUNT_POINT;
    reparse->Reserved = 0u;

    unsigned char* pathBuffer
        = reinterpret_cast<unsigned char*>(reparse->PathBuffer);
    std::memcpy(pathBuffer, substitute.data(), subBytes);
    std::memcpy(pathBuffer + subBytes, &nul, sizeof(nul));
    std::memcpy(pathBuffer + subBytes + sizeof(nul), targetPath.native().data(),
        printBytes);
    std::memcpy(pathBuffer + subBytes + sizeof(nul) + printBytes, &nul, sizeof(nul));

    reparse->SubstituteNameOffset = 0u;
    reparse->SubstituteNameLength = subBytes;
    reparse->PrintNameOffset = static_cast<USHORT>(subBytes + sizeof(nul));
    reparse->PrintNameLength = printBytes;
    // PathBuffer = 替换名 + NUL + 显示名 + NUL：两个终止符都要计入
    reparse->ReparseDataLength
        = static_cast<USHORT>(8u + subBytes + printBytes + 2u * sizeof(nul));

    DWORD returned = 0u;
    const BOOL ok = ::DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, reparse,
        static_cast<DWORD>(8u + reparse->ReparseDataLength), nullptr, 0u, &returned,
        nullptr);
    ::CloseHandle(handle);
    return ok != FALSE;
}
#endif

/// 一次性临时目录（%TEMP%/zr_rgpath_<pid>_<counter>），析构整棵删除
struct TempTree {
    fs::path root;
    bool ok = false;

    TempTree()
    {
        static std::atomic<uint64_t> counter { 0u };
        std::error_code ec;
        const fs::path base = fs::temp_directory_path(ec);
        if (ec || base.empty()) {
            return;
        }
        root = base
            / ("zr_rgpath_" + std::to_string(::GetCurrentProcessId()) + "_"
                + std::to_string(GetTickCount64()) + "_"
                + std::to_string(counter.fetch_add(1u)));
        fs::create_directories(root, ec);
        ok = !ec;
    }

    ~TempTree()
    {
        if (!ok) {
            return;
        }
        std::error_code ec;
        fs::remove_all(root, ec); // 测试目录（含 junction）整棵清理
    }

    TempTree(const TempTree&) = delete;
    TempTree& operator=(const TempTree&) = delete;
};

zrender::ZrRgExportRoots MakeRoots(const fs::path& allowedRoot)
{
    zrender::ZrRgExportRoots roots;
    roots.allowed.push_back(allowedRoot.u8string());
    roots.defaultRoot = allowedRoot.u8string();
    return roots;
}

} // namespace

// ---- 1. 允许根内正常可用 ------------------------------------------------------------
TEST_CASE("rg-export-path: relative and absolute paths inside the root are accepted")
{
    TempTree temp;
    REQUIRE(temp.ok);
    const fs::path allowed = temp.root / "export";
    const fs::path outside = temp.root / "outside";
    fs::create_directories(allowed);
    fs::create_directories(outside);
    const zrender::ZrRgExportRoots roots = MakeRoots(allowed);

    // 缺省路径
    zrender::ZrRgExportPathResult r = zrender::ZrResolveRgExportPath("", roots);
    REQUIRE(r.ok);
    CHECK(r.path.size() > 0u);
    CHECK(fs::path(fs::u8path(r.path)).filename() == fs::path("rg_export.json"));
    CHECK(fs::path(fs::u8path(r.path)).parent_path() == fs::weakly_canonical(allowed));

    // 相对文件名
    r = zrender::ZrResolveRgExportPath("graph.json", roots);
    REQUIRE(r.ok);
    CHECK(fs::path(fs::u8path(r.path)).parent_path() == fs::weakly_canonical(allowed));

    // 相对子目录（目录尚不存在也应通过——由调用方 create_directories）
    r = zrender::ZrResolveRgExportPath("sub/dir/graph.json", roots);
    REQUIRE(r.ok);
    CHECK(fs::path(fs::u8path(r.path)).filename() == fs::path("graph.json"));

    // 绝对路径且在根内
    const fs::path absoluteInside = allowed / "deep" / "graph.txt";
    r = zrender::ZrResolveRgExportPath(absoluteInside.u8string(), roots);
    REQUIRE(r.ok);
    CHECK(fs::path(fs::u8path(r.path)) == fs::weakly_canonical(allowed) / "deep"
            / "graph.txt");
}

// ---- 2. 穿越与越界 ----------------------------------------------------------------
TEST_CASE("rg-export-path: dot-dot components are rejected even when inside the root")
{
    TempTree temp;
    REQUIRE(temp.ok);
    const fs::path allowed = temp.root / "export";
    fs::create_directories(allowed);
    const zrender::ZrRgExportRoots roots = MakeRoots(allowed);

    const char* cases[] = {
        "../../windows/system32/evil.json",
        "..\\..\\windows\\system32\\evil.json",
        "a/../b.json",       // 规范化后仍在根内，但规则更严格：一律拒绝
        "sub/../../evil.json",
        "..",
    };
    for (const char* requested : cases) {
        const zrender::ZrRgExportPathResult r
            = zrender::ZrResolveRgExportPath(requested, roots);
        CAPTURE(requested);
        CHECK_FALSE(r.ok);
        CHECK(r.error == zrender::ZrRgExportPathError::Traversal);
        CHECK(r.code == "path_traversal");
        CHECK_FALSE(r.detail.empty());
    }
}

TEST_CASE("rg-export-path: absolute paths outside the root are rejected")
{
    TempTree temp;
    REQUIRE(temp.ok);
    const fs::path allowed = temp.root / "export";
    const fs::path sibling = temp.root / "export_evil"; // 字符串前缀相同、分量不同
    fs::create_directories(allowed);
    fs::create_directories(sibling);
    const zrender::ZrRgExportRoots roots = MakeRoots(allowed);

    const char* absolute[] = {
        "C:/Windows/System32/evil.json",
        "C:/Users/Public/evil.json",
    };
    for (const char* requested : absolute) {
        const zrender::ZrRgExportPathResult r
            = zrender::ZrResolveRgExportPath(requested, roots);
        CAPTURE(requested);
        CHECK_FALSE(r.ok);
        CHECK(r.error == zrender::ZrRgExportPathError::OutsideRoot);
        CHECK(r.code == "path_outside_root");
    }

    // root 与 root_evil：逐分量判定，不能被字符串前缀蒙混
    const zrender::ZrRgExportPathResult r = zrender::ZrResolveRgExportPath(
        (sibling / "evil.json").u8string(), roots);
    CHECK_FALSE(r.ok);
    CHECK(r.error == zrender::ZrRgExportPathError::OutsideRoot);
}

// ---- 3. 形态非法 ------------------------------------------------------------------
TEST_CASE("rg-export-path: drive-relative, ADS and device names are rejected")
{
    TempTree temp;
    REQUIRE(temp.ok);
    const fs::path allowed = temp.root / "export";
    fs::create_directories(allowed);
    const zrender::ZrRgExportRoots roots = MakeRoots(allowed);

    const char* invalid[] = {
        "C:relative.json",    // 盘符相对：解析基准依赖进程 CWD，不可控
        "graph.json:stream",  // NTFS ADS
        "CON",                // 保留设备名
        "con.txt",            // 设备名 + 扩展名
        "NUL.json",
        "trailing. ",         // Windows 会剥掉尾随点/空格
        "\\rootless\\x.json", // 无盘符的根相对路径
        "\\\\server\\share\\x.json", // UNC（规范化可能卡在网络等待上，直接拒绝）
    };
    for (const char* requested : invalid) {
        const zrender::ZrRgExportPathResult r
            = zrender::ZrResolveRgExportPath(requested, roots);
        CAPTURE(requested);
        CHECK_FALSE(r.ok);
        CHECK(r.error == zrender::ZrRgExportPathError::InvalidPath);
        CHECK(r.code == "path_invalid");
    }
}

TEST_CASE("rg-export-path: missing root configuration fails closed")
{
    zrender::ZrRgExportRoots empty;
    const zrender::ZrRgExportPathResult r
        = zrender::ZrResolveRgExportPath("graph.json", empty);
    CHECK_FALSE(r.ok);
    CHECK(r.error == zrender::ZrRgExportPathError::RootUnavailable);

    zrender::ZrRgExportRoots relativeDefault;
    relativeDefault.allowed.push_back("relative/root");
    relativeDefault.defaultRoot = "relative/root";
    const zrender::ZrRgExportPathResult r2
        = zrender::ZrResolveRgExportPath("graph.json", relativeDefault);
    CHECK_FALSE(r2.ok);
    CHECK(r2.error == zrender::ZrRgExportPathError::RootUnavailable);
}

// ---- 4. 符号链接 / junction 逃逸 ---------------------------------------------------
TEST_CASE("rg-export-path: reparse point under the root is rejected")
{
    TempTree temp;
    REQUIRE(temp.ok);
    const fs::path allowed = temp.root / "export";
    const fs::path outside = temp.root / "outside"; // 根外的真实目录
    fs::create_directories(allowed);
    fs::create_directories(outside);
    const zrender::ZrRgExportRoots roots = MakeRoots(allowed);

#ifdef _WIN32
    const fs::path link = allowed / "escape";
    if (!CreateJunction(link, outside)) {
        MESSAGE("junction creation unavailable (GetLastError="
            << ::GetLastError() << ") - reparse-point case skipped");
        return;
    }

    // junction 指向根外：即使规范化后解析到 C:\...\outside，也必须被拒绝
    const zrender::ZrRgExportPathResult r
        = zrender::ZrResolveRgExportPath("escape/evil.json", roots);
    CAPTURE(r.detail);
    CHECK_FALSE(r.ok);
    CHECK(r.error == zrender::ZrRgExportPathError::SymlinkEscape);
    CHECK(r.code == "path_symlink");

    // 绝对路径形式（词法上仍在根内）同样拒绝
    const zrender::ZrRgExportPathResult r2
        = zrender::ZrResolveRgExportPath((link / "evil.json").u8string(), roots);
    CHECK_FALSE(r2.ok);
    CHECK(r2.error == zrender::ZrRgExportPathError::SymlinkEscape);

    // 指向根内的 junction 也拒绝（导出目录不需要重解析点，规则简单可审计）
    const fs::path inner = allowed / "inner";
    fs::create_directories(inner);
    const fs::path innerLink = allowed / "inner_link";
    REQUIRE(CreateJunction(innerLink, inner));
    const zrender::ZrRgExportPathResult r3
        = zrender::ZrResolveRgExportPath("inner_link/ok.json", roots);
    CHECK_FALSE(r3.ok);
    CHECK(r3.error == zrender::ZrRgExportPathError::SymlinkEscape);
#else
    MESSAGE("reparse-point case is Windows-only");
#endif
}

// ---- 5. capture 名字（同一口径的文件名白名单）-------------------------------------
TEST_CASE("rg-export-path: capture leaf names reject separators and device names")
{
    CHECK(zrender::ZrRgExportValidateLeafName("sceneA").empty());
    CHECK(zrender::ZrRgExportValidateLeafName("scene-01_v2").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("..").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("../escape").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("a/b").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("a\\b").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("C:scene").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("CON").empty());
    CHECK_FALSE(zrender::ZrRgExportValidateLeafName("trail.").empty());
}
