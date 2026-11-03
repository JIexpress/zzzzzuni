// =============================================================================
// zrender.h —— ZRender 渲染图模块公共头（手写 RenderGraph / pass 调度）
//
// TASK-B-R0-01：仅构建基座占位接口，真实实现由后续任务填充。
// 前缀约定：ZREN_（ZR_ 保留给 zplugin 的 dll 级 C ABI）。
// =============================================================================
#pragma once

#include <cstdint>

extern "C" {

/// 渲染图模块版本（与 ZRender 整体版本同步，形如 "0.1.0"）。
const char* ZREN_GetVersion(void);

} // extern "C"
