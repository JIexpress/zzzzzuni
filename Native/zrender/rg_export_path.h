// =============================================================================
// rg_export_path.h —— rg.export 落盘路径限定（TASK-X-SEC-01）
//
// 背景：`rg.export [path]` 原先把客户端给的 path 直接交给 ofstream，等于给本机
// 任意进程一个"任意写文件"原语。本模块把它改成**白名单根 + 规范化后判定**：
//   1. 只允许写到显式声明的根目录之下（exe 同目录 rg_export / LOCALAPPDATA /
//      --rg-export-dir 显式根；由调用方组装 allowed）；
//   2. 任何含 `..` 分量的请求一律拒绝（即使规范化后仍在根内——规则更严格、
//      更好审计）；
//   3. 先用 weakly_canonical 解析（跟随符号链接/junction），再做**逐分量**
//      前缀判定，防 `C:\root2` 冒充 `C:\root`；
//   4. 对根以下的原始分量再做一次 reparse point 检查（符号链接/junction 即使
//      规范化结果仍落在根内也拒绝——导出目录不需要重解析点）。
//
// 纯逻辑（std::filesystem + 字符串），不写盘；错误码供 DevConsole JSON 信封与
// 日志复用，绝不静默失败。
// 命名遵循 file-conventions.md §3；前缀 ZrRgExport。
// =============================================================================
#pragma once

#include <string>
#include <vector>

namespace zrender {

/// 路径判定错误（None = 通过）
enum class ZrRgExportPathError : uint32_t {
    None = 0,
    InvalidPath,    // 非法字符/设备名/ADS/目录本身
    Traversal,      // 含 `..` 分量
    SymlinkEscape,  // 根以下存在重解析点（符号链接/junction）
    OutsideRoot,    // 规范化后不在任何允许根下
    TooLong,        // 路径超长
    RootUnavailable // 未配置可用的根（调用方配置错误）
};

/// 错误码字符串（snake_case，直接进 JSON error 字段）
const char* ZrRgExportPathErrorCode(ZrRgExportPathError error);

/// 判定结果
struct ZrRgExportPathResult {
    bool ok = false;
    std::string path;  // ok=true：规范化后的绝对路径（UTF-8）
    ZrRgExportPathError error = ZrRgExportPathError::None;
    std::string code;  // ok=false：错误码（= ZrRgExportPathErrorCode(error)）
    std::string detail; // 人类可读补充（含命中的根/原因）
};

/// 允许根集合
struct ZrRgExportRoots {
    std::vector<std::string> allowed; // 绝对路径（UTF-8）；顺序即提示顺序
    std::string defaultRoot;          // 省略路径的落点 + 相对路径的基准
};

/// 解析并校验导出路径：
///   - requested 为空 -> defaultRoot/rg_export.json
///   - requested 为相对 -> defaultRoot/requested
///   - requested 为绝对 -> 必须落在 allowed 的某一根下
/// 失败时 result.ok=false 且 error/code/detail 均被填写。
ZrRgExportPathResult ZrResolveRgExportPath(const std::string& requested,
    const ZrRgExportRoots& roots);

/// 供调用方对 `renderdoc.capture [name]` 之类的"纯文件名"参数做同一口径校验：
/// 不允许分隔符/盘符/`..`/设备名，返回空串表示合法（detail 写原因）。
std::string ZrRgExportValidateLeafName(const std::string& name);

} // namespace zrender
