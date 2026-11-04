// =============================================================================
// zkernel_abi.h —— DataKernel World 句柄式 C ABI（TASK-B-R9-17）
//
// 定位：zkernel.h 的 C++ 类接口（ZkWorld + 模板）不跨 DLL ABI；本头是唯一允许
// 托管侧（C# P/Invoke）消费的 DataKernel 通道，追加进 zrender.dll 的导出表
// （Native/zplugin/zrender.def）。纯 C 头（stdint.h 自包含），C/C++ 双可包含。
//
// 设计要点（简报 B-R9-17）：
//   1. World 句柄：内部句柄表 + 代际（gen<<32 | slot），最多 8 世界；0 恒无效。
//      销毁后槽位代际 +1，旧句柄（含进程内残留指针的整数拷贝）被拒。
//   2. Copy 语义：组件读写走 memcpy，原始行指针不出 ABI（ZK_SetComponent/
//      ZK_GetComponent）；size 必须与注册字节数精确一致，否则拒绝。
//   3. 游标式批量迭代（关键设计：无回调跨 ABI——规避托管 delegate 生命周期）：
//      ZK_EachBatchBegin 取得调用方持有的 POD 游标，ZK_EachBatchNext 每调用
//      交付一个 chunk 的各列起始指针 + 行数，返回 0 结束。游标是纯数据结构，
//      原生侧不持有任何游标状态（无表、无泄漏、无清理）。
//      游标失效判定：Next 比对 Begin 时快照的"布局版本号"，任何布局变更
//      （Spawn/Destroy/Add/Remove/Compact）都会使未完成的游标被拒（ERR_STATE）。
//   4. 错误码体系沿用 R10-03 风格：0 = 成功，负值 = 失败（ZK_ABI_ERR_*）。
//   5. 线程模型：沿用 zkernel 单线程约定——全部 ZK_* 只允许主线程调用；
//      游标只在主线程推进，且推进期间不得并发修改本世界（见各函数 doc）。
//
// 布局镜像约定：ZkEntityAbi / ZkWorldStatsAbi 与 zkernel.h 的 ZkEntity /
// ZkWorldStats 布局逐字段一致（zkernel_abi.cpp 内 static_assert 锁定）；
// 托管侧（ZFrame.Render.Bindings）按本头布局直接封送。
// =============================================================================
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// ---- 错误码（负值，0 = 成功；沿用 zplugin.h 的 R10-03 风格）------------------
#define ZK_ABI_OK 0          // 成功
#define ZK_ABI_ERR_NULL (-1) // 必填出参/数据指针为空
#define ZK_ABI_ERR_HANDLE (-2)        // World 句柄无效（未创建/已销毁/代际失配）
#define ZK_ABI_ERR_RANGE (-3)         // compId 越界（0..127）或组件未注册
#define ZK_ABI_ERR_ARGS (-4)          // 参数非法（size 不匹配/对齐非 2 的幂/游标损坏）
#define ZK_ABI_ERR_STATE (-5)         // 状态拒绝（重复注册/实体缺组件/句柄失效/游标作废）
#define ZK_ABI_ERR_FULL (-6)          // World 句柄表已满（8 个）
#define ZK_ABI_ERR_NOMEM (-7)         // 原生分配失败（异常不跨 ABI，折算为错误码）

/// World 句柄：uint64_t = (代际 << 32) | 槽位下标；代际自 1 起 → 0 恒无效。
typedef uint64_t ZkWorldHandle;

/// 实体代际句柄（与 zkernel.h 的 ZkEntity 布局一致：index + generation）。
typedef struct ZkEntityAbi {
    uint32_t index;      // 实体槽位索引（销毁后复用）
    uint32_t generation; // 代际：销毁 +1，旧句柄因代际不匹配被拒
} ZkEntityAbi;

/// 统计快照（zkernel.h 的 ZkWorldStats 三计数字段 + EntityCount/ChunkCount，
/// 简报要求 ABI 统计含实体数/chunk 数/迁移计数，故为 ABI 扩充五字段版）。
typedef struct ZkWorldStatsAbi {
    uint64_t entityCount; // 存活实体数
    uint64_t chunkCount;  // chunk 总数（全部 archetype 合计）
    uint64_t migrations;  // 成功迁移次数（Add/Remove 及批量；不含 Spawn/Destroy）
    uint64_t rowsMoved;   // 迁移路径物理搬行次数
    uint64_t bytesMoved;  // 迁移路径 memcpy/memset 字节总数
} ZkWorldStatsAbi;

/// 游标状态机取值（ZkEachCursor.state）。
#define ZK_CURSOR_NEW 0    // 已创建未推进（与 ACTIVE 等价，首次 Next 从头扫描）
#define ZK_CURSOR_ACTIVE 1 // 推进中
#define ZK_CURSOR_DONE 2   // 已耗尽（后续 Next 幂等返回 0）

/// 批量迭代游标（调用方持有的 POD；48 字节定宽，托管侧按布局封送）。
/// 原生侧不登记游标——Begin 只初始化字段，Next 只读写字段；调用方不得手改
/// （Next 做防御性校验，损坏返回 ZK_ABI_ERR_ARGS）。重复 Begin 复用同一结构体
/// 合法（整体覆盖）。
typedef struct ZkEachCursor {
    uint64_t layoutVersion;      // Begin 时的世界布局版本（Next 比对，变更即作废）
    uint64_t withMask[2];        // 查询掩码快照（w[0] = compId 0..63，w[1] = 64..127）
    uint32_t archIndex;          // 下一个待消费的 archetype 下标
    uint32_t chunkIndex;         // 当前 archetype 内已消费到的 chunk 下标
    uint32_t rowsLeftArch;       // 当前 archetype 剩余未交付行数
    uint32_t rowsDelivered;      // 累计已交付行数（耗尽时 = 查询匹配总行数）
    uint32_t state;              // ZK_CURSOR_*（勿手改）
    uint32_t reserved;           // 保留对齐（恒 0）
} ZkEachCursor;

// ---- World 生命周期 ---------------------------------------------------------

/// 创建 World 并登记进句柄表。成功返回 ZK_ABI_OK 并写 *outWorld；
/// outWorld 为空返回 ZK_ABI_ERR_NULL；表满（8 个未销毁 World）返回
/// ZK_ABI_ERR_FULL；内部分配失败返回 ZK_ABI_ERR_NOMEM。
int ZK_WorldCreate(ZkWorldHandle* outWorld);

/// 销毁 World（含全部实体与 chunk 存储）并使句柄失效（槽位代际 +1）。
/// 句柄无效返回 ZK_ABI_ERR_HANDLE；成功返回 ZK_ABI_OK。
int ZK_WorldDestroy(ZkWorldHandle world);

// ---- 组件注册 ---------------------------------------------------------------

/// 注册组件：compId 由托管侧显式分配（0..127），sizeBytes > 0，alignBytes 为
/// 2 的幂。重复注册返回 ZK_ABI_ERR_STATE；id 越界/未注册组件列相关参数非法
/// 返回 ZK_ABI_ERR_RANGE / ZK_ABI_ERR_ARGS。
int ZK_RegisterComponent(ZkWorldHandle world, uint32_t compId, uint32_t sizeBytes,
    uint32_t alignBytes);

// ---- 实体 -------------------------------------------------------------------

/// 按掩码生成实体（组件内存未初始化，用 ZK_SetComponent 写入）。
/// maskLo = compId 0..63 的位，maskHi = compId 64..127 的位。
/// 掩码含未注册组件返回 ZK_ABI_ERR_RANGE；outEntity 为空返回 ZK_ABI_ERR_NULL。
int ZK_Spawn(ZkWorldHandle world, uint64_t maskLo, uint64_t maskHi, ZkEntityAbi* outEntity);

/// 销毁实体（swap-back 收行 + 代际 +1）。句柄失效/已销毁返回 ZK_ABI_ERR_STATE。
int ZK_Destroy(ZkWorldHandle world, ZkEntityAbi entity);

// ---- 组件读写（Copy 语义：指针不出 ABI）--------------------------------------

/// 写组件（Copy 语义）：把 data[0..sizeBytes) memcpy 进实体行内该组件列。
/// data 为空返回 ZK_ABI_ERR_NULL；sizeBytes 与注册字节数不一致返回
/// ZK_ABI_ERR_ARGS；实体未持有该组件返回 ZK_ABI_ERR_STATE（写不隐含加）。
int ZK_SetComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId,
    const void* data, uint32_t sizeBytes);

/// 读组件（Copy 语义）：把实体行内该组件列 memcpy 进 outData[0..sizeBytes)。
/// 实体未持有该组件时 *outOk = 0 且 outData 不被写入（返回 ZK_ABI_OK）；
/// 持有时 *outOk = 1 并拷贝。sizeBytes 一律按注册字节数精确校验。
int ZK_GetComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId, void* outData,
    uint32_t sizeBytes, uint32_t* outOk);

/// 运行时增组件（包 ZkWorld::AddComponent）：initData 为空 = 零初始化新列
/// （合法取值，不算 ZK_ABI_ERR_NULL）。已持有该组件返回 ZK_ABI_ERR_STATE。
int ZK_AddComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId,
    const void* initData);

/// 运行时删组件（包 ZkWorld::RemoveComponent）：被删列数据丢弃。
/// 未持有该组件返回 ZK_ABI_ERR_STATE。
int ZK_RemoveComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId);

// ---- 游标式批量迭代（无回调跨 ABI）-------------------------------------------
//
// 用法（与 zkernel.h Each 批回调语义逐 chunk 一致：archetype 向量序 → chunk 序，
// 每批 n = min(剩余行, chunk 容量)，列指针 = with 升序 compId 位序第 j 列起始）：
//   ZkEachCursor cur;
//   ZK_EachBatchBegin(h, maskLo, maskHi, &cur);
//   void* cols[8];
//   uint32_t cumulative, chunkRows;
//   while (ZK_EachBatchNext(h, &cur, cols, 8, &cumulative, &chunkRows) == 1) {
//       // cols[j][0..chunkRows) 为本 chunk 各列行数据（SoA，行步长 = 组件字节数）
//   }

/// 初始化游标。掩码原样快照（未注册位只是匹配不到 archetype，不算错误；
/// 空掩码 = 遍历全部 archetype）。outCursor 为空返回 ZK_ABI_ERR_NULL。
int ZK_EachBatchBegin(ZkWorldHandle world, uint64_t maskLo, uint64_t maskHi,
    ZkEachCursor* outCursor);

/// 推进游标并交付下一个 chunk。返回 1 = 交付一批（见 outChunkRowCount）；
/// 返回 0 = 迭代结束（幂等：再次调用仍返回 0，*outRowCount = 累计总行数）；
/// 负值 = ZK_ABI_ERR_*（游标保持可重入前的状态，调用方应停止迭代）。
/// 出参约定：*outChunkRowCount = 本 chunk 可读行数；*outRowCount = 累计已交付
/// 行数（含本批，耗尽时等于查询匹配总行数——跨 chunk 推进断言用）。
/// colPtrs 出参定长数组（调用方分配，容量 maxCols >= 查询位列数，否则
/// ZK_ABI_ERR_ARGS）；查询无组件位（空掩码）时允许 colPtrs 为空。
/// 约定：两次 Next 之间不得 Spawn/Destroy/Add/Remove/Compact（会作废游标，
/// 下次 Next 返回 ZK_ABI_ERR_STATE）；组件列内容可用 ZK_SetComponent 原地改。
int ZK_EachBatchNext(ZkWorldHandle world, ZkEachCursor* cursor, void** colPtrs,
    uint32_t maxCols, uint32_t* outRowCount, uint32_t* outChunkRowCount);

// ---- 观测 -------------------------------------------------------------------

/// 统计快照（存活实体数/chunk 总数/迁移三计数，见 ZkWorldStatsAbi）。
/// outStats 为空返回 ZK_ABI_ERR_NULL。
int ZK_WorldStats(ZkWorldHandle world, ZkWorldStatsAbi* outStats);

#ifdef __cplusplus
} // extern "C"
#endif
