// =============================================================================
// zrhi.h —— ZRender RHI 公共头（Vulkan 语义、多后端）
//
// TASK-B-R2-01：核心语义层经本公共头聚合暴露——
//   zrhi_types.h    枚举体系 + 格式 traits
//   zrhi_handles.h  强类型句柄 + 泛代句柄表
//   zrhi_barriers.h 屏障数据模型 + 只读判定
//   zrhi_device.h   IZrDevice / IZrCommandList / IZrQueue 纯虚接口
// 内部头不出模块目录（Docs/Standards/file-conventions.md §3）；
// 后端实现（D3D12 等）由后续任务填充。零 Vulkan 头依赖。
// =============================================================================
#pragma once

#include "zrhi/zrhi_barriers.h"
#include "zrhi/zrhi_device.h"
#include "zrhi/zrhi_handles.h"
#include "zrhi/zrhi_types.h"

#include <cstdint>

extern "C" {

/// RHI 模块版本（与 ZRender 整体版本同步，形如 "0.1.0"）。
const char* ZRHI_GetVersion(void);

} // extern "C"
