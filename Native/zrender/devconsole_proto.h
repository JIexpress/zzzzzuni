// =============================================================================
// devconsole_proto.h —— 原生 DevConsole 协议纯逻辑（TASK-B-R9-16）
//
// 机制沿用 A-DEV-03（Unity 侧运行期 DevConsole）的契约，落到原生窗口程序：
//   - 传输：TCP 只绑 127.0.0.1（线程/Socket 层在窗口程序，本模块不碰 Socket）
//   - 协议：一行一条请求、一行一条 JSON 响应（'\n' 分隔，UTF-8），单行上限 4096B
//   - 白名单：硬编码表，未知命令返回结构化错误而不是崩溃
//
// 本模块只做**纯解析与响应组装**（无 IO/无全局状态），命令的执行（读设备统计、
// 触发 RenderDoc、写 RG 导出文件）由窗口程序在本帧循环内完成：
//   help                        -> 命令列表
//   hud                         -> 渲染统计（DrawCall/显存/帧率）
//   rg.export [path] [json|text]-> RG 编译产物导出（缺省路径 Native/build/renderdoc/）
//   renderdoc.capture [name]    -> RenderDoc 一键捕获（未挂接 -> 明确错误）
//   quit                        -> 请求窗口程序优雅退出
// 命名遵循 file-conventions.md §3；前缀 ZrDevConsole。
// =============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace zrender {

/// 单行请求长度上限（A-DEV-03 同口径 4KB；超限即结构化错误，防 OOM）
constexpr uint32_t kZrDevConsoleMaxLineBytes = 4096u;

/// 白名单命令（机器可读枚举；执行层按此分发）
enum class ZrDevConsoleCommand : uint32_t {
    None = 0,
    Help,
    Hud,
    RgExport,
    RenderDocCapture,
    Quit,
};

/// 解析结果（valid=false 时 error/detail 给结构化错误；不抛异常）
struct ZrDevConsoleRequest {
    bool valid = false;
    ZrDevConsoleCommand command = ZrDevConsoleCommand::None;
    std::vector<std::string> args;   // 已剥离命令名的参数（空串已过滤）
    std::string error;               // valid=false 时的错误码（snake_case）
    std::string detail;              // 人类可读补充（可空）
};

/// 解析一行请求：去 \r\n 与首尾空白 -> 按空白分词 -> 白名单 + 参数个数校验。
/// 空行 -> error="empty_line"；未知命令 -> error="unknown_command"；
/// 参数过多 -> error="bad_arguments"（detail 列出正确用法）。
/// 注意：不做路径存在性/格式值域检查（如 [json|text] 的取值）——那是执行层的
/// 职责（需要文件系统/导出器状态），本模块只管协议形状。
ZrDevConsoleRequest ZrDevConsoleParse(const std::string& line);

/// 白名单命令名表（help 响应用；顺序即枚举序）
std::vector<std::string> ZrDevConsoleCommandNames();

// =============================================================================
// 响应组装（单行 JSON；调用方对信封外的自由文本负责转义——这里统一做）
// =============================================================================

/// JSON 字符串转义（与 bench_json.h 的 zbench::JsonEscape 同规则）
std::string ZrDevConsoleJsonEscape(const std::string& s);

/// 成功信封：{"ok":true,<payload>}（payload 为不带花括号的 "key":value 串，可为空）
std::string ZrDevConsoleOk(const std::string& payloadFields);

/// 把 "{...}" 形态的单对象 JSON 并入 ok 信封（剥外壳后作 payload——
/// ZrHudFormatJson 等独立单对象生成器的接入点，保证响应整体仍是合法单行 JSON：
/// ZrDevConsoleOk(ZrDevConsoleMergeObject(objectJson))）。
/// 输入非 "{...}" 形态（找不到配对外壳）时返回空串（信封退化为 {"ok":true}）。
std::string ZrDevConsoleMergeObject(const std::string& objectJson);

/// 失败信封：{"ok":false,"error":"<code>","detail":"<human>"}
/// （detail 为空时省略该键——A-DEV-03 结构化错误同口径）
std::string ZrDevConsoleError(const std::string& code, const std::string& detail);

} // namespace zrender
