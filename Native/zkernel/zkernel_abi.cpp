// zkernel_abi.cpp —— DataKernel World 句柄式 C ABI 实现（TASK-B-R9-17）
//
// 结构：静态句柄表（8 槽 + 代际）包装 ZkWorld；全部出参走调用方缓冲（Copy 语义，
// 原始行指针不出 ABI）；游标是调用方持有的 POD（本文件不登记游标，无泄漏面）。
// chunk 扫描逻辑收口在 ZkWorld::ScanChunk（zkernel.cpp），ABI 层只做句柄/参数
// 校验与游标推进编排，不复制 archetype 内部结构知识。
//
// 校验顺序约定（各函数一致，测试按此断言）：
//   1. 出参/数据指针 NULL        → ZK_ABI_ERR_NULL（AddComponent 的 initData 除外：
//                                  空指针 = 零初始化，是 ZkWorld::AddComponent 的合法语义）
//   2. World 句柄解析            → ZK_ABI_ERR_HANDLE
//   3. compId 范围 / 注册        → ZK_ABI_ERR_RANGE
//   4. size/对齐/游标完整性      → ZK_ABI_ERR_ARGS
//   5. 实体/组件状态拒绝         → ZK_ABI_ERR_STATE
//
// 异常纪律：异常不跨 ABI。可能分配的四条路径（WorldCreate/Spawn/AddComponent/
// RemoveComponent 会 new/vector 扩容）逐函数 try/catch 折算为 ZK_ABI_ERR_NOMEM；
// 其余函数全链路无分配（memcpy/查表/句柄表操作），天然不抛。
//
// 线程模型：沿用 zkernel 单线程约定（句柄表与 World 全部只允许主线程触碰，
// 不加锁——与 zplugin.h 的 zmesh 句柄表同一口径）；游标只在主线程推进。

#include "zkernel/zkernel_abi.h"

#include <cstddef> // offsetof
#include <cstring> // memcpy / memset
#include <new>     // std::nothrow

#include "zkernel/zkernel.h"

// =============================================================================
// 布局镜像护栏：ABI POD 与 zkernel.h 的 C++ POD 必须逐字段一致（C# 侧按本 ABI
// 头布局封送，两侧任何一侧改动都必须同步）。
// =============================================================================
static_assert(sizeof(ZkEntityAbi) == sizeof(ZkEntity), "ZkEntityAbi layout drift");
static_assert(offsetof(ZkEntityAbi, index) == offsetof(ZkEntity, index), "ZkEntityAbi.index drift");
static_assert(offsetof(ZkEntityAbi, generation) == offsetof(ZkEntity, generation),
    "ZkEntityAbi.generation drift");

// ZkWorldStatsAbi 与 ZkWorldStats **不是**同布局镜像：ABI 版按简报要求扩充了
// entityCount/chunkCount 两字段（迁移三计数经 ZK_WorldStats 逐字段搬运，不靠
// 内存布局对齐），故此处不做布局断言。

namespace {

constexpr uint32_t ZK_ABI_MAX_WORLDS = 8; // 简报约定：最多 8 世界
constexpr uint32_t ZK_ABI_MAX_COMPONENTS = 128;

/// 静态句柄表：槽位 + 代际。generation 0 = 空槽哨兵（创建时置 1 起，销毁时 +1
/// 绕开 0），因此句柄 0 恒无效（未初始化的调用方缓冲不会误命中）。
struct WorldSlot {
    ZkWorld* world;
    uint32_t generation;
};
WorldSlot s_worlds[ZK_ABI_MAX_WORLDS] = {};

constexpr uint32_t ZK_HANDLE_SLOT_MASK = 0xFFFFFFFFu;

/// 句柄 → World（含代际/范围校验）；无效返回 nullptr。
ZkWorld* ResolveWorld(ZkWorldHandle handle)
{
    const uint32_t slot = static_cast<uint32_t>(handle & ZK_HANDLE_SLOT_MASK);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slot >= ZK_ABI_MAX_WORLDS || generation == 0u) {
        return nullptr;
    }
    WorldSlot& s = s_worlds[slot];
    if (s.world == nullptr || s.generation != generation) {
        return nullptr; // 未创建 / 已销毁（代际失配）
    }
    return s.world;
}

/// ABI 实体句柄 → C++ 实体句柄（布局已 static_assert 锁定，按字段搬运）。
inline ZkEntity ToEntity(const ZkEntityAbi& e)
{
    ZkEntity r;
    r.index = e.index;
    r.generation = e.generation;
    return r;
}

/// 掩码双字 → Mask128（w[2]/w[3] 恒 0；ABI 只暴露组件 0..127）。
inline Mask128 ToMask(uint64_t maskLo, uint64_t maskHi)
{
    Mask128 m{};
    m.w[0] = maskLo;
    m.w[1] = maskHi;
    return m;
}

} // namespace

extern "C" {

// =============================================================================
// World 生命周期
// =============================================================================
int ZK_WorldCreate(ZkWorldHandle* outWorld)
{
    if (outWorld == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    try {
        for (uint32_t i = 0; i < ZK_ABI_MAX_WORLDS; ++i) {
            WorldSlot& s = s_worlds[i];
            if (s.world != nullptr) {
                continue;
            }
            ZkWorld* w = new (std::nothrow) ZkWorld(); // 构造含 vector 分配
            if (w == nullptr) {
                return ZK_ABI_ERR_NOMEM;
            }
            uint32_t generation = s.generation + 1u;
            if (generation == 0u) {
                generation = 1u; // 绕开 0 哨兵
            }
            s.world = w;
            s.generation = generation;
            *outWorld
                = (static_cast<ZkWorldHandle>(generation) << 32) | static_cast<ZkWorldHandle>(i);
            return ZK_ABI_OK;
        }
        return ZK_ABI_ERR_FULL; // 8 世界未销毁
    } catch (...) {
        return ZK_ABI_ERR_NOMEM; // 异常不跨 ABI
    }
}

int ZK_WorldDestroy(ZkWorldHandle world)
{
    const uint32_t slot = static_cast<uint32_t>(world & ZK_HANDLE_SLOT_MASK);
    const uint32_t generation = static_cast<uint32_t>(world >> 32);
    if (slot >= ZK_ABI_MAX_WORLDS || generation == 0u) {
        return ZK_ABI_ERR_HANDLE;
    }
    WorldSlot& s = s_worlds[slot];
    if (s.world == nullptr || s.generation != generation) {
        return ZK_ABI_ERR_HANDLE;
    }
    delete s.world; // ~Impl 释放全部 chunk（_aligned_free）；析构不抛
    s.world = nullptr;
    s.generation = (s.generation + 1u == 0u) ? 1u : s.generation + 1u;
    return ZK_ABI_OK;
}

// =============================================================================
// 组件注册
// =============================================================================
int ZK_RegisterComponent(ZkWorldHandle world, uint32_t compId, uint32_t sizeBytes,
    uint32_t alignBytes)
{
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (compId >= ZK_ABI_MAX_COMPONENTS) {
        return ZK_ABI_ERR_RANGE;
    }
    if (sizeBytes == 0u || alignBytes == 0u || (alignBytes & (alignBytes - 1u)) != 0u) {
        return ZK_ABI_ERR_ARGS; // size 必须 > 0；align 必须为 2 的幂
    }
    if (w->IsComponentRegistered(compId)) {
        return ZK_ABI_ERR_STATE; // 重复注册
    }
    return w->RegisterComponent(compId, sizeBytes, alignBytes) ? ZK_ABI_OK : ZK_ABI_ERR_STATE;
}

// =============================================================================
// 实体
// =============================================================================
int ZK_Spawn(ZkWorldHandle world, uint64_t maskLo, uint64_t maskHi, ZkEntityAbi* outEntity)
{
    if (outEntity == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    try {
        // ZkWorld::Spawn 因掩码含未注册组件返回 ZK_INVALID_ENTITY（chunk 扩容在
        // try 内——AddChunk 分配失败折算 NOMEM）。
        const ZkEntity e = w->Spawn(ToMask(maskLo, maskHi));
        if (e.index == ZK_INVALID_ENTITY.index && e.generation == ZK_INVALID_ENTITY.generation) {
            return ZK_ABI_ERR_RANGE; // 未注册组件位
        }
        outEntity->index = e.index;
        outEntity->generation = e.generation;
        return ZK_ABI_OK;
    } catch (...) {
        return ZK_ABI_ERR_NOMEM;
    }
}

int ZK_Destroy(ZkWorldHandle world, ZkEntityAbi entity)
{
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    return w->Destroy(ToEntity(entity)) ? ZK_ABI_OK : ZK_ABI_ERR_STATE;
}

// =============================================================================
// 组件读写（Copy 语义）
// =============================================================================
int ZK_SetComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId, const void* data,
    uint32_t sizeBytes)
{
    if (data == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (compId >= ZK_ABI_MAX_COMPONENTS || !w->IsComponentRegistered(compId)) {
        return ZK_ABI_ERR_RANGE;
    }
    if (sizeBytes != w->ComponentSize(compId)) {
        return ZK_ABI_ERR_ARGS; // 坏 size 拒绝（精确匹配注册字节数）
    }
    if (!w->HasComponent(ToEntity(entity), compId)) {
        return ZK_ABI_ERR_STATE; // 句柄失效 / 实体不持有该组件（写不隐含加）
    }
    void* row = w->GetComponent(ToEntity(entity), compId);
    if (row == nullptr) {
        return ZK_ABI_ERR_STATE; // 不可达（HasComponent 已过），防御性兜底
    }
    std::memcpy(row, data, sizeBytes);
    return ZK_ABI_OK;
}

int ZK_GetComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId, void* outData,
    uint32_t sizeBytes, uint32_t* outOk)
{
    if (outData == nullptr || outOk == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (compId >= ZK_ABI_MAX_COMPONENTS || !w->IsComponentRegistered(compId)) {
        return ZK_ABI_ERR_RANGE;
    }
    if (sizeBytes != w->ComponentSize(compId)) {
        return ZK_ABI_ERR_ARGS; // 坏 size 拒绝
    }
    if (!w->HasComponent(ToEntity(entity), compId)) {
        *outOk = 0u; // 实体失效 / 不持有该组件：不是错误，调用方按 ok 分流
        return ZK_ABI_OK;
    }
    const void* row = w->GetComponent(ToEntity(entity), compId);
    if (row == nullptr) {
        return ZK_ABI_ERR_STATE; // 不可达（HasComponent 已过），防御性兜底
    }
    std::memcpy(outData, row, sizeBytes);
    *outOk = 1u;
    return ZK_ABI_OK;
}

int ZK_AddComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId,
    const void* initData)
{
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (compId >= ZK_ABI_MAX_COMPONENTS || !w->IsComponentRegistered(compId)) {
        return ZK_ABI_ERR_RANGE;
    }
    if (!w->IsAlive(ToEntity(entity))) {
        return ZK_ABI_ERR_STATE;
    }
    if (w->HasComponent(ToEntity(entity), compId)) {
        return ZK_ABI_ERR_STATE; // 已持有（显式拒绝，不做幂等语义）
    }
    try {
        return w->AddComponent(ToEntity(entity), compId, initData) ? ZK_ABI_OK : ZK_ABI_ERR_STATE;
    } catch (...) {
        return ZK_ABI_ERR_NOMEM; // 迁移路径的 archetype 创建 / chunk 扩容
    }
}

int ZK_RemoveComponent(ZkWorldHandle world, ZkEntityAbi entity, uint32_t compId)
{
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (compId >= ZK_ABI_MAX_COMPONENTS || !w->IsComponentRegistered(compId)) {
        return ZK_ABI_ERR_RANGE;
    }
    if (!w->HasComponent(ToEntity(entity), compId)) {
        return ZK_ABI_ERR_STATE; // 句柄失效 / 未持有该组件
    }
    try {
        return w->RemoveComponent(ToEntity(entity), compId) ? ZK_ABI_OK : ZK_ABI_ERR_STATE;
    } catch (...) {
        return ZK_ABI_ERR_NOMEM;
    }
}

// =============================================================================
// 游标式批量迭代（无回调跨 ABI）
// =============================================================================
int ZK_EachBatchBegin(ZkWorldHandle world, uint64_t maskLo, uint64_t maskHi,
    ZkEachCursor* outCursor)
{
    if (outCursor == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    ZkEachCursor c;
    std::memset(&c, 0, sizeof(c));
    c.layoutVersion = w->LayoutVersion(); // 快照：Next 比对，变更即作废
    c.withMask[0] = maskLo;
    c.withMask[1] = maskHi;
    c.archIndex = 0;    // 从第一个 archetype 起扫描
    c.chunkIndex = 0;
    c.rowsLeftArch = 0; // 0 = 无挂起 archetype（首次 Next 触发扫描）
    c.rowsDelivered = 0;
    c.state = ZK_CURSOR_ACTIVE;
    *outCursor = c;
    return ZK_ABI_OK;
}

int ZK_EachBatchNext(ZkWorldHandle world, ZkEachCursor* cursor, void** colPtrs, uint32_t maxCols,
    uint32_t* outRowCount, uint32_t* outChunkRowCount)
{
    if (cursor == nullptr || outRowCount == nullptr || outChunkRowCount == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    if (cursor->state == ZK_CURSOR_DONE) {
        *outRowCount = cursor->rowsDelivered; // 幂等耗尽：总行数照报
        *outChunkRowCount = 0u;
        return 0;
    }
    if (cursor->state != ZK_CURSOR_NEW && cursor->state != ZK_CURSOR_ACTIVE) {
        return ZK_ABI_ERR_ARGS; // state 损坏（调用方改写游标）
    }
    if (cursor->layoutVersion != w->LayoutVersion()) {
        cursor->state = ZK_CURSOR_DONE; // 布局已变：作废（后续调用幂等返回 0）
        return ZK_ABI_ERR_STATE;
    }
    if (cursor->archIndex > w->ArchetypeCount()) {
        return ZK_ABI_ERR_ARGS; // 游标损坏（越出 archetype 表）
    }

    const Mask128 with = ToMask(cursor->withMask[0], cursor->withMask[1]);
    uint32_t withBits[ZK_ABI_MAX_COMPONENTS];
    const uint32_t withCount = ZkListBits(with, withBits); // 升序枚举 = 列序
    if (withCount > maxCols) {
        return ZK_ABI_ERR_ARGS; // 出参数组容量不足
    }
    if (colPtrs == nullptr && withCount != 0u) {
        return ZK_ABI_ERR_NULL;
    }

    // 单步扫描（archetype 向量序 → chunk 序，n = min(剩余, 容量)，与 EachBatches
    // 逐 chunk 一致）；游标推进语义在 ScanChunk 内：耗尽时 archIndex 指向下一个。
    uint32_t arch = cursor->archIndex;
    uint32_t chunkIdx = cursor->chunkIndex;
    uint32_t rowsLeft = cursor->rowsLeftArch;
    void* cols[ZK_ABI_MAX_COMPONENTS];
    uint32_t n = 0;
    if (!w->ScanChunk(with, maxCols, &arch, &chunkIdx, &rowsLeft, cols, &n)) {
        cursor->state = ZK_CURSOR_DONE;
        *outRowCount = cursor->rowsDelivered;
        *outChunkRowCount = 0u;
        return 0; // 迭代结束
    }

    for (uint32_t j = 0; j < withCount; ++j) {
        colPtrs[j] = cols[j];
    }
    cursor->archIndex = arch;
    cursor->chunkIndex = chunkIdx;
    cursor->rowsLeftArch = rowsLeft;
    cursor->rowsDelivered += n;
    *outRowCount = cursor->rowsDelivered; // 累计（含本批）
    *outChunkRowCount = n;                // 本 chunk 可读行数
    return 1;
}

// =============================================================================
// 观测
// =============================================================================
int ZK_WorldStats(ZkWorldHandle world, ZkWorldStatsAbi* outStats)
{
    if (outStats == nullptr) {
        return ZK_ABI_ERR_NULL;
    }
    ZkWorld* w = ResolveWorld(world);
    if (w == nullptr) {
        return ZK_ABI_ERR_HANDLE;
    }
    const ZkWorldStats s = w->Stats();
    outStats->entityCount = w->EntityCount();
    outStats->chunkCount = w->ChunkCount();
    outStats->migrations = s.migrations;
    outStats->rowsMoved = s.rowsMoved;
    outStats->bytesMoved = s.bytesMoved;
    return ZK_ABI_OK;
}

} // extern "C"
