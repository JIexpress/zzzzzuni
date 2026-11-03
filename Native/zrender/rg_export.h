// =============================================================================
// rg_export.h —— RenderGraph 编译产物导出（TASK-B-R9-16，方案 §5.7「RG 导出」）
//
// 给排查「哪个 pass 黑屏/性能」提供数据：把 RgGraph::Compile() 的产物——
// pass 列表（执行序 + 被剔除者）、依赖边、屏障批次（逐 pass）、split barrier
// 计划、别名池槽计划、Transient 生命周期——导出为机器可读 JSON 或人类可读
// 文本。数据全部来自 rg_render_graph.h 的既有编译产物模型（B-R3-01），本模块
// 零新增状态：两个导出函数都是**纯函数**（同一编译产物两次导出逐字节一致）。
//
// 消费方：窗口程序 DevConsole 命令 `rg.export [path] [json|text]`（写盘）；
// 单测 test_rg_export.cpp 直接断言生成文本。
// 命名遵循 file-conventions.md §3；前缀 Rg。
// =============================================================================
#pragma once

#include <string>

#include "zrender/rg_render_graph.h"
#include "zrhi/zrhi_types.h"

namespace zrender {

/// 当前导出格式版本（结构变更时递增；消费方按版本号解析）
constexpr uint32_t kRgExportVersion = 1u;

/// 图像布局名（undefined/color/ds_write/...；非法值返回 "unknown"）
const char* RgImageLayoutName(ZrImageLayout layout);

/// 访问位掩码名（"shader_read|color_attachment_write"；None 返回 "none"）
std::string RgAccessBitsName(ZrAccessBits access);

/// 管线阶段位掩码名（同上口径）
std::string RgPipelineStageName(ZrPipelineStageBits stage);

/// 格式名（"RGBA8Unorm" 等，与 zrhi_types.h 枚举字面量一致；Count/越界返回 "unknown"）
const char* RgFormatName(ZrFormat format);

/// 导出为单文档 JSON（UTF-8，紧凑无缩进；不保证跨行——非 DevConsole 单行协议，
/// 落盘为文件，可多行）。graph 未编译（Error() != None）时仍输出骨架：
/// error 字段带错误码、各集合为空——导出失败状态本身也是排查数据。
std::string RgExportJson(const RgGraph& graph);

/// 导出为人类可读文本（含资源甘特式的 lifetime/slot 区间行）。
/// 同 graph 输入下与 JSON 一一对应（同源数据，两种视图）。
std::string RgExportText(const RgGraph& graph);

} // namespace zrender
