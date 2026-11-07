// =============================================================================
// rg_export_path.cpp —— rg.export 落盘路径限定实现（TASK-X-SEC-01）
// =============================================================================
#include "zrender/rg_export_path.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <filesystem>
#include <system_error>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace zrender {

namespace {

namespace fs = std::filesystem;

/// 规范化路径长度上限（远大于 MAX_PATH，足够容纳长路径产物，防异常输入）
constexpr size_t kMaxResolvedPathChars = 4096u;

char ToLowerAscii(char c)
{
    return (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
}

bool EqualsNoCase(const std::string& a, const std::string& b)
{
    if (a.size() != b.size()) {
        return false;
    }
    for (size_t i = 0; i < a.size(); ++i) {
        if (ToLowerAscii(a[i]) != ToLowerAscii(b[i])) {
            return false;
        }
    }
    return true;
}

std::vector<std::string> SplitComponents(const fs::path& path)
{
    std::vector<std::string> out;
    for (const fs::path& part : path) {
        out.push_back(part.u8string());
    }
    return out;
}

/// Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9；"CON.txt" 同样命中）
bool IsReservedDeviceName(const std::string& component)
{
    std::string stem = component;
    const size_t dot = stem.find('.');
    if (dot != std::string::npos) {
        stem.resize(dot);
    }
    std::string upper = stem;
    std::transform(upper.begin(), upper.end(), upper.begin(),
        [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL") {
        return true;
    }
    if (upper.size() == 4u) {
        const std::string prefix = upper.substr(0u, 3u);
        const char digit = upper[3];
        if ((prefix == "COM" || prefix == "LPT") && digit >= '1' && digit <= '9') {
            return true;
        }
    }
    return false;
}

/// 单分量合法性（分隔符由 path 迭代器处理；这里检查冒号/尾随点空格/设备名）
bool IsValidComponent(const std::string& component, std::string& reason)
{
    if (component == ".") {
        return true; // 规范化时消掉
    }
    if (component.find(':') != std::string::npos) {
        reason = "component '" + component + "' contains ':' (drive/ADS not allowed)";
        return false;
    }
    if (!component.empty()
        && (component.back() == '.' || component.back() == ' ')) {
        reason = "component '" + component + "' ends with '.' or ' ' (Windows strips it)";
        return false;
    }
    for (const char ch : component) {
        if (static_cast<unsigned char>(ch) < 0x20u) {
            reason = "component contains a control character";
            return false;
        }
    }
    if (component.size() > 255u) {
        reason = "component longer than 255 characters";
        return false;
    }
    if (IsReservedDeviceName(component)) {
        reason = "component '" + component + "' is a reserved device name";
        return false;
    }
    return true;
}

/// 规范化（weakly_canonical）；失败返回 false 并带原因
bool Canonicalize(const fs::path& path, fs::path& out, std::string& reason)
{
    std::error_code ec;
    out = fs::weakly_canonical(path, ec);
    if (ec || out.empty()) {
        reason = "weakly_canonical('" + path.u8string() + "') failed: "
            + (ec ? ec.message() : std::string("empty result"));
        return false;
    }
    return true;
}

/// root 是否为 path 的逐分量前缀（大小写不敏感；要求 path 严格更深）
bool IsComponentPrefix(const fs::path& root, const fs::path& path)
{
    const std::vector<std::string> rootParts = SplitComponents(root);
    const std::vector<std::string> pathParts = SplitComponents(path);
    if (rootParts.empty() || pathParts.size() <= rootParts.size()) {
        return false;
    }
    for (size_t i = 0; i < rootParts.size(); ++i) {
        if (!EqualsNoCase(rootParts[i], pathParts[i])) {
            return false;
        }
    }
    return true;
}

#ifdef _WIN32
bool IsReparsePoint(const fs::path& path)
{
    const DWORD attributes = ::GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES
        && (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0u;
}
#endif

/// 对原始路径（未跟随后目标）做根以下 reparse point 检查。
/// 只有当 rawRoot 是 rawBase 的逐分量前缀时才能对齐；否则跳过（规范化后
/// 的包含性判定仍然生效——不是静默放行）。
bool RejectReparseBelowRoot(const fs::path& rawBase, const fs::path& rawRoot,
    std::string& offending)
{
    const std::vector<std::string> baseParts = SplitComponents(rawBase);
    const std::vector<std::string> rootParts = SplitComponents(rawRoot);
    if (rootParts.empty() || baseParts.size() <= rootParts.size()) {
        return false;
    }
    for (size_t i = 0; i < rootParts.size(); ++i) {
        if (!EqualsNoCase(rootParts[i], baseParts[i])) {
            return false; // 文本前缀对不上：交给规范化后的包含性判定
        }
    }
    fs::path cursor = rawRoot;
    for (size_t i = rootParts.size(); i < baseParts.size(); ++i) {
        cursor /= baseParts[i];
        std::error_code ec;
        const fs::file_status status = fs::symlink_status(cursor, ec);
        bool reparse = false;
        if (!ec) {
            reparse = (status.type() == fs::file_type::symlink);
        }
#ifdef _WIN32
        // junction/mount point 在部分 STL 版本里不报 symlink，补一次 Win32 属性检查
        if (!reparse) {
            reparse = IsReparsePoint(cursor);
        }
#endif
        if (reparse) {
            offending = cursor.u8string();
            return true;
        }
        if (ec) {
            break; // 组件尚不存在：后续 creat_directories 负责，无需再查
        }
    }
    return false;
}

ZrRgExportPathResult Failure(ZrRgExportPathError error, const std::string& detail)
{
    ZrRgExportPathResult out;
    out.ok = false;
    out.error = error;
    out.code = ZrRgExportPathErrorCode(error);
    out.detail = detail;
    return out;
}

} // namespace

const char* ZrRgExportPathErrorCode(ZrRgExportPathError error)
{
    switch (error) {
    case ZrRgExportPathError::None: return "none";
    case ZrRgExportPathError::InvalidPath: return "path_invalid";
    case ZrRgExportPathError::Traversal: return "path_traversal";
    case ZrRgExportPathError::SymlinkEscape: return "path_symlink";
    case ZrRgExportPathError::OutsideRoot: return "path_outside_root";
    case ZrRgExportPathError::TooLong: return "path_too_long";
    case ZrRgExportPathError::RootUnavailable: return "path_root_unavailable";
    }
    return "path_invalid";
}

std::string ZrRgExportValidateLeafName(const std::string& name)
{
    if (name.empty()) {
        return "name is empty";
    }
    if (name == "." || name == "..") {
        return "name must not be '.' or '..'";
    }
    for (const char ch : name) {
        if (ch == '/' || ch == '\\' || ch == ':' || ch == '*' || ch == '?'
            || ch == '"' || ch == '<' || ch == '>' || ch == '|') {
            return "name contains a path separator or reserved character";
        }
        if (static_cast<unsigned char>(ch) < 0x20u) {
            return "name contains a control character";
        }
    }
    if (name.back() == '.' || name.back() == ' ') {
        return "name ends with '.' or ' '";
    }
    if (IsReservedDeviceName(name)) {
        return "name is a reserved device name";
    }
    return std::string();
}

ZrRgExportPathResult ZrResolveRgExportPath(const std::string& requested,
    const ZrRgExportRoots& roots)
{
    if (roots.allowed.empty() || roots.defaultRoot.empty()) {
        return Failure(ZrRgExportPathError::RootUnavailable,
            "no allowed export roots configured");
    }
    if (requested.find('\0') != std::string::npos) {
        return Failure(ZrRgExportPathError::InvalidPath, "path contains NUL byte");
    }
    if (requested.size() > kMaxResolvedPathChars) {
        return Failure(ZrRgExportPathError::TooLong, "requested path is too long");
    }

    fs::path raw;
    try {
        raw = fs::u8path(requested);
    } catch (const std::exception& ex) {
        return Failure(ZrRgExportPathError::InvalidPath,
            std::string("path is not valid UTF-8: ") + ex.what());
    }

    // ---- 1. 形态检查（`..` 一律拒绝，即使规范化后仍在根内）--------------------
    if (raw.has_root_name() && !raw.has_root_directory()) {
        return Failure(ZrRgExportPathError::InvalidPath,
            "drive-relative path ('C:file') is not allowed");
    }
    // UNC 根（\\server\share\...）不在导出场景内；weakly_canonical 解析不存在的
    // 网络共享可能阻塞数秒（本机 DoS），直接拒绝。
    if (raw.has_root_name()) {
        const std::wstring rootName = raw.root_name().wstring();
        if (rootName.size() >= 2u && rootName[0] == L'\\' && rootName[1] == L'\\') {
            return Failure(ZrRgExportPathError::InvalidPath,
                "UNC paths are not allowed");
        }
    }
    if (!raw.has_root_name() && raw.has_root_directory()) {
        return Failure(ZrRgExportPathError::InvalidPath,
            "root-relative path without a drive is not allowed");
    }
    for (const fs::path& part : raw.relative_path()) {
        const std::string component = part.u8string();
        if (component == "..") {
            return Failure(ZrRgExportPathError::Traversal,
                "'..' component is not allowed (use a path inside the export root)");
        }
        std::string reason;
        if (!IsValidComponent(component, reason)) {
            return Failure(ZrRgExportPathError::InvalidPath, reason);
        }
    }

    // ---- 2. 拼绝对路径（相对路径以 defaultRoot 为基准）------------------------
    fs::path rawDefaultRoot;
    try {
        rawDefaultRoot = fs::u8path(roots.defaultRoot);
    } catch (const std::exception&) {
        return Failure(ZrRgExportPathError::RootUnavailable,
            "default export root is not valid UTF-8");
    }
    if (!rawDefaultRoot.is_absolute() || !rawDefaultRoot.has_root_name()) {
        return Failure(ZrRgExportPathError::RootUnavailable,
            "default export root must be an absolute path");
    }

    fs::path rawBase;
    if (raw.empty()) {
        rawBase = rawDefaultRoot / fs::u8path("rg_export.json");
    } else if (raw.is_absolute()) {
        rawBase = raw;
    } else {
        rawBase = rawDefaultRoot / raw;
    }

    if (requested.size() + rawDefaultRoot.u8string().size() > kMaxResolvedPathChars) {
        return Failure(ZrRgExportPathError::TooLong, "resolved path is too long");
    }

    // ---- 3. 规范化 + 逐分量包含性判定 -----------------------------------------
    fs::path canonicalTarget;
    std::string reason;
    if (!Canonicalize(rawBase, canonicalTarget, reason)) {
        return Failure(ZrRgExportPathError::InvalidPath, reason);
    }
    if (canonicalTarget.u8string().size() > kMaxResolvedPathChars) {
        return Failure(ZrRgExportPathError::TooLong, "normalized path is too long");
    }

    bool insideAnyRoot = false;
    std::string rootDetail;
    for (const std::string& rootText : roots.allowed) {
        fs::path rawRoot;
        try {
            rawRoot = fs::u8path(rootText);
        } catch (const std::exception&) {
            continue;
        }
        fs::path canonicalRoot;
        std::string rootReason;
        if (!Canonicalize(rawRoot, canonicalRoot, rootReason)) {
            rootDetail += (rootDetail.empty() ? "" : "; ") + rootReason;
            continue;
        }
        // 先做根以下 reparse point 检查（原始路径文本前缀对齐时生效）：
        // junction 指向根外时，规范化会把目标解析到根外，若先判包含性就只能
        // 报 path_outside_root；这里先报更精确的 path_symlink。
        std::string offending;
        if (RejectReparseBelowRoot(rawBase, rawRoot, offending)) {
            return Failure(ZrRgExportPathError::SymlinkEscape,
                "reparse point (symlink/junction) under export root: '" + offending + "'");
        }
        if (!IsComponentPrefix(canonicalRoot, canonicalTarget)) {
            continue;
        }
        insideAnyRoot = true;
        break;
    }
    if (!insideAnyRoot) {
        std::string allowedText;
        for (const std::string& rootText : roots.allowed) {
            allowedText += (allowedText.empty() ? "" : ", ") + rootText;
        }
        std::string detail = "path resolves outside the allowed export roots ("
            + allowedText + "); resolved='" + canonicalTarget.u8string() + "'";
        if (!rootDetail.empty()) {
            detail += "; unusable roots: " + rootDetail;
        }
        return Failure(ZrRgExportPathError::OutsideRoot, detail);
    }

    ZrRgExportPathResult out;
    out.ok = true;
    out.path = canonicalTarget.u8string();
    out.error = ZrRgExportPathError::None;
    out.code = ZrRgExportPathErrorCode(ZrRgExportPathError::None);
    return out;
}

} // namespace zrender
