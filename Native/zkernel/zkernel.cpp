// zkernel.cpp —— 数据内核实现（TASK-B-R1-01 / TASK-B-R8-03 / TASK-B-R9-12）
//
// 存储：archetype = 掩码相等的实体集合（线性查找，数量小）；每 archetype 持
// SoA chunk 向量。chunk 容量 = max(32, 16384 / bytesPerRow)（16KB 预算）；
// 行在 chunk 间紧凑排布：行 r 位于第 r/capacity 个 chunk 的第 r%capacity 行。
// 组件列槽位序 = ZkBitsBelow(mask, bit)（硬件 POPCNT 低位置位计数）。
// 实体：槽位数组（generation/archetype/row）+ 空闲链；销毁 swap-back 尾行入洞。
// 迭代路径（EachBatches / GetComponent）无 new/malloc/realloc。
//
// TASK-B-R8-03 架型迁移 + chunk 收缩：
// - 迁移（MigrateComponent）：共享列逐列直接 memcpy（SoA 行内跨列不连续 → 列寻址
//   拷贝，无需暂存缓冲，搬行路径零堆分配）；分配点仅为新 archetype 创建与目标链
//   扩容 AddChunk（与 Spawn 同策略，非迭代路径）。
// - 收行（RemoveRowSwapBack）：Destroy 与迁移共用的 swap-back 单点实现——尾行各列
//   搬入洞行 + 行->实体 / 实体->行号映射修正 + count--，保证"行紧凑 + 索引一致"
//   不变量只有一处维护。
// - 收缩（Compact）：行紧凑下空闲行恒为链尾后缀，最小 chunk 数 = ceil(count/容量)；
//   Destroy 不自动收缩（chunk 保留复用防抖动），显式 Compact 释放尾部空 chunk。
// - 迭代安全：EachBatches 期间布局变更（Spawn/Destroy/Add/Remove/Compact）在
//   debug 构建断言拦截、所有构建运行时直接拒绝（返回失败，世界状态不变）。
//
// TASK-B-R9-12 并行 Each（EachParallel）：
// - 只并行"读回调"：chunk 列表快照（调用方约定快照期世界冻结）→ std::atomic
//   计数器把 chunk 逐个分配给工作线程（fetch_add 抢占，无锁、负载自动均衡）→
//   各线程独立遍历（chunk 内连续 SoA，跨线程零共享写）。
// - 线程选型：一次性 spawn/join（非常驻池）——chunk 粒度任务量（数千行级）
//   远大于 spawn 成本（~20μs），池化只在"高频小查询"场景才有收益；简单可靠
//   优先，调用线程自身也参与执行（threadCount 个并发度）。threadCount==1 退化
//   为调用线程内联（零 spawn 开销）。
// - iterating 计数原子化（选型：共享 atomic 而非 per-thread 本地+聚合）——
//   并行防护要求"任意线程"在他人迭代期间改世界都被拒（per-thread 计数看不到
//   其他线程的迭代态）；布局变更 API 为冷路径，seq_cst 开销可忽略。
// - Each（单线程版）零改动：同一 EachBatches 路径原样保留，计数器换 atomic
//   后单线程语义不变。
#include "zkernel/zkernel.h"

#include <atomic>
#include <cassert>
#include <cstring>
#include <malloc.h> // _aligned_malloc / _aligned_free
#include <new>      // std::bad_alloc
#include <thread>
#include <vector>

namespace {

constexpr uint32_t ZK_MAX_COMPONENTS = 128;
constexpr uint32_t ZK_CHUNK_BUDGET_BYTES = 16384; // 16KB chunk 预算
constexpr uint32_t ZK_MIN_CHUNK_ROWS = 32;        // chunk 最小行数
constexpr uint32_t ZK_INVALID_SLOT = 0xFFFFFFFFu;
constexpr uint32_t ZK_MAX_PARALLEL_THREADS = 64;  // EachParallel 并发度上限（防病态 spawn）

} // namespace

extern "C" {

const char* ZK_GetVersion(void)
{
    return ZR_VERSION_STRING; // 由 CMake 注入，单一事实来源
}

} // extern "C"

// =============================================================================
// ZkWorld::Impl —— 内部存储（struct：成员默认 public，供 ZkWorld 成员函数访问）
// =============================================================================
struct ZkWorld::Impl {
    struct Chunk {
        void* data; // 对齐分配的列存储（列按槽位序布局）
    };

    struct Archetype {
        Mask128 mask{};
        uint64_t chunkBytes = 0;                       // 单 chunk 列存储总字节数
        uint32_t capacity = 0;                         // 单 chunk 行数
        uint32_t count = 0;                            // 已用行数（0..chunks*capacity）
        uint32_t columnCount = 0;                      // 列数 = mask.PopCount()
        uint32_t maxAlign = 1;                         // 列存储分配对齐值
        uint64_t columnOffset[ZK_MAX_COMPONENTS] = {}; // 槽位 k -> 列起始字节偏移
        uint32_t columnSize[ZK_MAX_COMPONENTS] = {};   // 槽位 k -> 组件字节数
        std::vector<Chunk> chunks;                     // chunk 数组（追加式，摊还增长）
        std::vector<uint32_t> rowEntity;               // 行号 -> 实体槽位（几何增长）

        uint32_t ChunkIndexOf(uint32_t row) const { return row / capacity; }
        uint32_t RowInChunk(uint32_t row) const { return row % capacity; }
    };

    std::vector<Archetype> archetypes;
    std::vector<uint32_t> freeSlots;  // 空闲实体槽位链（栈式复用）
    std::vector<uint32_t> generation; // 实体代际
    std::vector<int32_t> archOf;      // 实体 -> archetype 下标（-1 = 空闲）
    std::vector<uint32_t> rowOf;      // 实体 -> archetype 内行号
    uint32_t alive = 0;
    // Each 遍历深度（回调内禁布局变更的防护计数）。TASK-B-R9-12 原子化：并行
    // 回调在 worker 线程上执行，布局变更 API 必须能在"任意线程"读到非零迭代态
    // （per-thread 本地计数无法覆盖跨线程防护）；仅迭代入口/出口写入（冷路径），
    // 默认 seq_cst 开销可忽略。
    std::atomic<uint32_t> iterating{0};
    ZkWorldStats stats{}; // 迁移成本统计（TASK-B-R8-03）
    // 布局版本号（TASK-B-R9-17）：Spawn/Destroy/Add/Remove/Compact 每成功变更 +1。
    // C ABI 游标（zkernel_abi.h）在 Begin 时快照本值，Next 时比对——跨调用迭代
    // 期间无法用 iterating 计数锁住布局（游标在调用方手里），改用版本号作废：
    // 布局动过 = 游标看到的行映射已失效 = 拒绝继续推进。
    uint64_t layoutVersion = 0;

    uint32_t compSize[ZK_MAX_COMPONENTS] = {};
    uint32_t compAlign[ZK_MAX_COMPONENTS] = {};
    bool compRegistered[ZK_MAX_COMPONENTS] = {};

    ~Impl()
    {
        for (Archetype& a : archetypes) {
            for (Chunk& c : a.chunks) {
                _aligned_free(c.data);
            }
        }
    }

    /// swap-back 收行（TASK-B-R8-03 抽出，Destroy / 迁移共用）：
    /// 把 archetype a 的 row 行移除——尾行各列字节搬入洞行（行与尾行可能分属不同
    /// chunk）+ 行->实体 / 实体->行号映射修正 + count--。不触代际 / 存活计数 /
    /// 实体槽位（调用方负责）。row 已是尾行时无搬移，返回 0；否则返回搬移字节数。
    uint64_t RemoveRowSwapBack(Archetype& a, uint32_t row)
    {
        const uint32_t last = a.count - 1;
        uint64_t bytes = 0;
        if (row != last) {
            char* dstBase = static_cast<char*>(a.chunks[a.ChunkIndexOf(row)].data);
            char* srcBase = static_cast<char*>(a.chunks[a.ChunkIndexOf(last)].data);
            const uint64_t dstR = a.RowInChunk(row);
            const uint64_t srcR = a.RowInChunk(last);
            for (uint32_t k = 0; k < a.columnCount; ++k) {
                const uint64_t off = a.columnOffset[k];
                const uint64_t size = a.columnSize[k];
                std::memcpy(dstBase + off + dstR * size, srcBase + off + srcR * size,
                    static_cast<size_t>(size));
                bytes += size;
            }
            const uint32_t movedSlot = a.rowEntity[last];
            a.rowEntity[row] = movedSlot;
            rowOf[movedSlot] = row;
        }
        a.count -= 1;
        return bytes;
    }

    /// archetype 掩码查找（线性扫描；archetype 数量小，后续任务可换哈希）
    int32_t FindArchetype(const Mask128& mask) const
    {
        for (size_t i = 0; i < archetypes.size(); ++i) {
            if (archetypes[i].mask == mask) {
                return static_cast<int32_t>(i);
            }
        }
        return -1;
    }

    /// 新建 archetype：按 16KB 预算定容、按（对齐后）列偏移布局；返回下标
    int32_t CreateArchetype(const Mask128& mask)
    {
        Archetype a;
        a.mask = mask;

        uint32_t bits[ZK_MAX_COMPONENTS];
        const uint32_t bitCount = ZkListBits(mask, bits); // TZCNT 升序枚举
        a.columnCount = bitCount;

        uint64_t bytesPerRow = 0;
        for (uint32_t k = 0; k < bitCount; ++k) {
            bytesPerRow += compSize[bits[k]];
            if (compAlign[bits[k]] > a.maxAlign) {
                a.maxAlign = compAlign[bits[k]];
            }
        }

        // 容量 = max(1, 16384 / bytesPerRow)，最小 32（行超预算时 chunk 超 16KB）
        uint32_t cap = ZK_MIN_CHUNK_ROWS;
        if (bytesPerRow > 0) {
            const uint32_t byBudget = static_cast<uint32_t>(ZK_CHUNK_BUDGET_BYTES / bytesPerRow);
            if (byBudget > cap) {
                cap = byBudget;
            }
        }
        a.capacity = cap;

        // 列偏移布局：槽位序 = 掩码升序位序；列起始对齐到组件对齐值
        uint64_t offset = 0;
        for (uint32_t k = 0; k < bitCount; ++k) {
            const uint32_t size = compSize[bits[k]];
            const uint64_t align = compAlign[bits[k]];
            offset = (offset + align - 1) & ~(align - 1); // 对齐列起始
            a.columnOffset[k] = offset;
            a.columnSize[k] = size;
            offset += static_cast<uint64_t>(size) * cap;
        }
        a.chunkBytes = (bitCount == 0) ? 0 : offset;

        archetypes.push_back(a); // 可能 realloc，仅发生在新 archetype（非迭代路径）
        return static_cast<int32_t>(archetypes.size()) - 1;
    }

    /// 追加 chunk（容量耗尽时，Spawn 路径）；rowEntity 几何增长摊还拷贝
    void AddChunk(Archetype& a)
    {
        const size_t needed = (a.chunks.size() + 1) * a.capacity;
        if (a.rowEntity.capacity() < needed) {
            a.rowEntity.reserve(needed * 2);
        }
        const size_t bytes = (a.chunkBytes != 0) ? static_cast<size_t>(a.chunkBytes) : 1u;
        void* data = _aligned_malloc(bytes, a.maxAlign);
        if (data == nullptr) {
            throw std::bad_alloc();
        }
        a.chunks.push_back(Chunk{data});
        a.rowEntity.resize(needed, ZK_INVALID_SLOT);
    }
};

// =============================================================================
// ZkWorld —— 公共 API
// =============================================================================
ZkWorld::ZkWorld() : m_impl(new Impl) {}

ZkWorld::~ZkWorld() { delete m_impl; }

bool ZkWorld::RegisterComponent(uint32_t compId, uint32_t sizeBytes, uint32_t alignBytes)
{
    Impl& w = *m_impl;
    if (compId >= ZK_MAX_COMPONENTS) {
        return false;
    }
    if (sizeBytes == 0) {
        return false;
    }
    if (alignBytes == 0 || (alignBytes & (alignBytes - 1)) != 0) {
        return false; // 必须为 2 的幂
    }
    if (w.compRegistered[compId]) {
        return false; // 重复注册
    }
    w.compRegistered[compId] = true;
    w.compSize[compId] = sizeBytes;
    w.compAlign[compId] = alignBytes;
    return true;
}

ZkEntity ZkWorld::Spawn(Mask128 mask)
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 Spawn（行映射会失效）");
        return ZK_INVALID_ENTITY; // 迭代期间布局冻结（TASK-B-R8-03 加固）
    }
    if ((mask.w[2] | mask.w[3]) != 0ull) {
        return ZK_INVALID_ENTITY; // 组件 ID 只允许 0..127（低 2 字）
    }
    uint32_t bits[ZK_MAX_COMPONENTS];
    const uint32_t bitCount = ZkListBits(mask, bits); // TZCNT 枚举
    for (uint32_t k = 0; k < bitCount; ++k) {
        if (!w.compRegistered[bits[k]]) {
            return ZK_INVALID_ENTITY; // 掩码含未注册组件
        }
    }

    int32_t archIdx = w.FindArchetype(mask);
    if (archIdx < 0) {
        archIdx = w.CreateArchetype(mask);
    }
    Impl::Archetype& a = w.archetypes[static_cast<size_t>(archIdx)];

    if (a.chunks.size() * a.capacity == a.count) {
        w.AddChunk(a); // 容量耗尽才分配（仅 Spawn 路径，迭代路径不触达）
    }
    const uint32_t row = a.count;
    a.count += 1;

    // 取实体槽位（空闲链优先，栈式复用）
    uint32_t slot;
    if (!w.freeSlots.empty()) {
        slot = w.freeSlots.back();
        w.freeSlots.pop_back();
    } else {
        slot = static_cast<uint32_t>(w.generation.size());
        w.generation.push_back(0);
        w.archOf.push_back(-1);
        w.rowOf.push_back(0);
    }
    w.archOf[slot] = archIdx;
    w.rowOf[slot] = row;
    a.rowEntity[row] = slot;
    w.alive += 1;
    w.layoutVersion += 1; // TASK-B-R9-17：布局变更（ABI 游标失效判定）
    return ZkEntity{slot, w.generation[slot]};
}

bool ZkWorld::Destroy(ZkEntity e)
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 Destroy（行映射会失效）");
        return false; // 迭代期间布局冻结（TASK-B-R8-03 加固）
    }
    if (e.index >= w.generation.size()) {
        return false;
    }
    const uint32_t idx = e.index;
    if (w.generation[idx] != e.generation) {
        return false; // 旧代际句柄拒绝
    }
    const int32_t archIdx = w.archOf[idx];
    if (archIdx < 0) {
        return false; // 已销毁
    }

    Impl::Archetype& a = w.archetypes[static_cast<size_t>(archIdx)];
    w.RemoveRowSwapBack(a, w.rowOf[idx]); // swap-back 收行（非迁移路径，不计 stats）
    w.archOf[idx] = -1;
    w.generation[idx] += 1; // 旧句柄自此失配
    w.freeSlots.push_back(idx);
    w.alive -= 1;
    w.layoutVersion += 1; // TASK-B-R9-17：布局变更（ABI 游标失效判定）
    return true;
}

// =============================================================================
// 架型迁移（TASK-B-R8-03）：AddComponent / RemoveComponent / 批量 / 迁移核心
// =============================================================================
bool ZkWorld::MigrateComponent(
    uint32_t slot, uint32_t compId, bool add, const void* initData, int32_t* memoSrc,
    int32_t* memoDst)
{
    Impl& w = *m_impl;
    const int32_t srcArchIdx = w.archOf[slot]; // 前置校验由调用方完成（>= 0）
    Mask128 dstMask = w.archetypes[static_cast<size_t>(srcArchIdx)].mask;
    if (add) {
        dstMask.Set(compId);
    } else {
        dstMask.Unset(compId);
    }

    // 目标 archetype 解析（批量路径带 1 项缓存：同源实体免重复线性扫描；
    // archetype 下标终生稳定，缓存不会失效）
    int32_t dstArchIdx;
    if (memoSrc != nullptr && *memoSrc == srcArchIdx) {
        dstArchIdx = *memoDst;
    } else {
        dstArchIdx = w.FindArchetype(dstMask);
        if (dstArchIdx < 0) {
            dstArchIdx = w.CreateArchetype(dstMask); // 分配点①：新 archetype（每掩码一次）
        }
        if (memoSrc != nullptr) {
            *memoSrc = srcArchIdx;
            *memoDst = dstArchIdx;
        }
    }

    // 引用绑定必须放在 CreateArchetype（archetypes 向量可能重分配）之后
    Impl::Archetype& src = w.archetypes[static_cast<size_t>(srcArchIdx)];
    Impl::Archetype& dst = w.archetypes[static_cast<size_t>(dstArchIdx)];

    // 目标行追加（容量耗尽 → AddChunk：分配点②，与 Spawn 同策略）
    if (dst.chunks.size() * dst.capacity == dst.count) {
        w.AddChunk(dst);
    }
    const uint32_t dstRow = dst.count;
    dst.count += 1;

    // 搬行：共享列逐列 memcpy（SoA 行内跨列不连续 → 直接列寻址拷贝，无暂存缓冲）
    const uint32_t srcRow = w.rowOf[slot];
    char* srcBase = static_cast<char*>(src.chunks[src.ChunkIndexOf(srcRow)].data);
    char* dstBase = static_cast<char*>(dst.chunks[dst.ChunkIndexOf(dstRow)].data);
    const uint64_t srcR = src.RowInChunk(srcRow);
    const uint64_t dstR = dst.RowInChunk(dstRow);
    uint32_t bits[ZK_MAX_COMPONENTS];
    const uint32_t sharedCount = ZkListBits(src.mask, bits); // TZCNT 升序枚举
    uint64_t bytes = 0;
    for (uint32_t k = 0; k < sharedCount; ++k) {
        if (!add && bits[k] == compId) {
            continue; // Remove：被删列数据丢弃，不迁移（否则越出目标列布局写错位）
        }
        const uint32_t srcSlot = k; // 升序枚举保证第 k 个置位位下方恰有 k 个置位
        const uint32_t dstSlot = ZkBitsBelow(dst.mask, bits[k]);
        const uint64_t size = src.columnSize[srcSlot];
        std::memcpy(dstBase + dst.columnOffset[dstSlot] + dstR * size,
            srcBase + src.columnOffset[srcSlot] + srcR * size, static_cast<size_t>(size));
        bytes += size;
    }
    // 新列（仅 Add 迁移）：initData 或零初始化
    if (add) {
        const uint32_t newSlot = ZkBitsBelow(dst.mask, compId);
        const uint64_t size = dst.columnSize[newSlot];
        void* cell = dstBase + dst.columnOffset[newSlot] + dstR * size;
        if (initData != nullptr) {
            std::memcpy(cell, initData, static_cast<size_t>(size));
        } else {
            std::memset(cell, 0, static_cast<size_t>(size));
        }
        bytes += size;
    }
    dst.rowEntity[dstRow] = slot;

    // 源 archetype swap-back 收行（含行->实体 / 实体->行号映射修正）
    const uint64_t swapBytes = w.RemoveRowSwapBack(src, srcRow);

    // 实体索引更新（代际不变 → 迁移后原句柄仍有效）
    w.archOf[slot] = dstArchIdx;
    w.rowOf[slot] = dstRow;

    // 迁移成本统计
    w.stats.migrations += 1;
    w.stats.rowsMoved += 1u + (swapBytes != 0ull ? 1u : 0u);
    w.stats.bytesMoved += bytes + swapBytes;
    w.layoutVersion += 1; // TASK-B-R9-17：布局变更（Add/Remove/批量共用迁移核心）
    return true;
}

bool ZkWorld::AddComponent(ZkEntity e, uint32_t compId, const void* initData)
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 AddComponent（行映射会失效）");
        return false; // 迭代期间布局冻结
    }
    if (e.index >= w.generation.size() || w.generation[e.index] != e.generation) {
        return false; // 句柄失效 / 旧代际
    }
    if (w.archOf[e.index] < 0) {
        return false; // 已销毁
    }
    if (compId >= ZK_MAX_COMPONENTS || !w.compRegistered[compId]) {
        return false; // 组件未注册
    }
    if (w.archetypes[static_cast<size_t>(w.archOf[e.index])].mask.Has(compId)) {
        return false; // 已含该组件（显式拒绝，不做过载幂等语义）
    }
    return MigrateComponent(e.index, compId, true, initData, nullptr, nullptr);
}

bool ZkWorld::RemoveComponent(ZkEntity e, uint32_t compId)
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 RemoveComponent（行映射会失效）");
        return false; // 迭代期间布局冻结
    }
    if (e.index >= w.generation.size() || w.generation[e.index] != e.generation) {
        return false; // 句柄失效 / 旧代际
    }
    if (w.archOf[e.index] < 0) {
        return false; // 已销毁
    }
    if (compId >= ZK_MAX_COMPONENTS || !w.compRegistered[compId]) {
        return false; // 组件未注册
    }
    if (!w.archetypes[static_cast<size_t>(w.archOf[e.index])].mask.Has(compId)) {
        return false; // 掩码不含该组件
    }
    return MigrateComponent(e.index, compId, false, nullptr, nullptr, nullptr);
}

uint32_t ZkWorld::AddComponentBatch(const ZkEntity* entities, uint32_t count, uint32_t compId,
    const void* initData)
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 AddComponentBatch（行映射会失效）");
        return 0; // 迭代期间布局冻结
    }
    if (entities == nullptr || count == 0) {
        return 0;
    }
    if (compId >= ZK_MAX_COMPONENTS || !w.compRegistered[compId]) {
        return 0; // compId 全批只校验一次
    }
    uint32_t migrated = 0;
    int32_t memoSrc = -1; // 1 项 archetype 解析缓存（混合源批次逐实体换档）
    int32_t memoDst = -1;
    for (uint32_t i = 0; i < count; ++i) {
        const ZkEntity e = entities[i];
        if (e.index >= w.generation.size() || w.generation[e.index] != e.generation) {
            continue; // 失效句柄跳过，不中断批次
        }
        if (w.archOf[e.index] < 0
            || w.archetypes[static_cast<size_t>(w.archOf[e.index])].mask.Has(compId)) {
            continue; // 已销毁 / 已含该组件
        }
        if (MigrateComponent(e.index, compId, true, initData, &memoSrc, &memoDst)) {
            migrated += 1;
        }
    }
    return migrated;
}

void* ZkWorld::GetComponent(ZkEntity e, uint32_t compId)
{
    Impl& w = *m_impl;
    if (e.index >= w.generation.size()) {
        return nullptr;
    }
    const uint32_t idx = e.index;
    if (w.generation[idx] != e.generation) {
        return nullptr; // 代际失配（已销毁的旧句柄）
    }
    const int32_t archIdx = w.archOf[idx];
    if (archIdx < 0) {
        return nullptr;
    }
    if (compId >= ZK_MAX_COMPONENTS || !w.compRegistered[compId]) {
        return nullptr;
    }

    const Impl::Archetype& a = w.archetypes[static_cast<size_t>(archIdx)];
    if (!a.mask.Has(compId)) {
        return nullptr; // 该实体掩码不含此组件
    }

    const uint32_t row = w.rowOf[idx];
    const uint32_t slot = ZkBitsBelow(a.mask, compId); // POPCNT 硬件定位
    char* base = static_cast<char*>(a.chunks[a.ChunkIndexOf(row)].data);
    const uint64_t off
        = a.columnOffset[slot] + static_cast<uint64_t>(a.RowInChunk(row)) * a.columnSize[slot];
    return base + off;
}

void ZkWorld::EachBatches(const Mask128& with, ZkBatchFn fn, void* user)
{
    Impl& w = *m_impl;
    // 迭代深度计数（RAII：回调抛出也不泄漏）：布局变更 API 据此拒绝/断言。
    // TASK-B-R9-12 起为 atomic（并行路径 worker 线程也要读到迭代态）；单线程
    // 语义不变。
    struct IterScope {
        std::atomic<uint32_t>& counter;
        explicit IterScope(std::atomic<uint32_t>& c) : counter(c) { counter.fetch_add(1u); }
        ~IterScope() { counter.fetch_sub(1u); }
        IterScope(const IterScope&) = delete;
        IterScope& operator=(const IterScope&) = delete;
    } scope(w.iterating);

    uint32_t withBits[ZK_MAX_COMPONENTS];
    const uint32_t withCount = ZkListBits(with, withBits); // TZCNT 升序枚举

    void* compPtrs[ZK_MAX_COMPONENTS]; // 栈上批量指针数组：迭代零堆分配
    for (Impl::Archetype& a : w.archetypes) {
        if (!a.mask.Contains(with)) {
            continue; // AND 包含过滤：(mask & with) == with
        }
        uint32_t rowsLeft = a.count;
        for (size_t ci = 0; ci < a.chunks.size() && rowsLeft > 0; ++ci) {
            const uint32_t n = (rowsLeft < a.capacity) ? rowsLeft : a.capacity;
            char* base = static_cast<char*>(a.chunks[ci].data);
            for (uint32_t j = 0; j < withCount; ++j) {
                const uint32_t slot = ZkBitsBelow(a.mask, withBits[j]); // POPCNT 定位
                compPtrs[j] = base + a.columnOffset[slot];
            }
            fn(user, compPtrs, n);
            rowsLeft -= n;
        }
    }
}

// =============================================================================
// 并行 Each（TASK-B-R9-12）：快照 + 原子 chunk 分配器 + 一次性 spawn/join
// =============================================================================
void ZkWorld::EachParallelBatches(
    const Mask128& with, ZkBatchFn fn, void* user, uint32_t threadCount)
{
    Impl& w = *m_impl;
    // 迭代防护同 EachBatches：整个并行执行期间调用线程持有深度计数，任意线程
    // （worker 回调内 / 并发的外部调用）触发布局变更 API 都会被拒绝/断言。
    struct IterScope {
        std::atomic<uint32_t>& counter;
        explicit IterScope(std::atomic<uint32_t>& c) : counter(c) { counter.fetch_add(1u); }
        ~IterScope() { counter.fetch_sub(1u); }
        IterScope(const IterScope&) = delete;
        IterScope& operator=(const IterScope&) = delete;
    } scope(w.iterating);

    // ---- 快照阶段（调用方约定：此刻起至返回，没有任何线程修改本世界）--------
    // 快照路径允许一次性堆分配（迭代热路径仍零分配——Each 单线程版不变）。
    uint32_t withBits[ZK_MAX_COMPONENTS];
    const uint32_t withCount = ZkListBits(with, withBits);

    // 每个 chunk 一个快照项：列偏移按 archetype 解析一次（worker 零 POPCNT 计算）。
    // 先完整建好 offsets 向量再建 chunks（chunks 存的指针指向 offsets，两段式
    // 避免 offsets 重分配悬挂）。
    struct SnapChunk {
        void* data;              // chunk 列存储基址
        const uint64_t* offsets; // with 各列在 chunk 内的字节偏移（withCount 项）
        uint32_t rows;           // 本 chunk 行数（尾 chunk = 余数）
    };
    std::vector<uint64_t> offsets; // 匹配 archetype 依次拼接的列偏移池
    {
        size_t totalOffsets = 0;
        for (const Impl::Archetype& a : w.archetypes) {
            if (a.mask.Contains(with)) {
                totalOffsets += withCount;
            }
        }
        offsets.reserve(totalOffsets);
        for (const Impl::Archetype& a : w.archetypes) {
            if (!a.mask.Contains(with)) {
                continue;
            }
            for (uint32_t j = 0; j < withCount; ++j) {
                const uint32_t slot = ZkBitsBelow(a.mask, withBits[j]);
                offsets.push_back(a.columnOffset[slot]);
            }
        }
    }
    std::vector<SnapChunk> chunks;
    {
        size_t offsetCursor = 0;
        for (const Impl::Archetype& a : w.archetypes) {
            if (!a.mask.Contains(with)) {
                continue;
            }
            const uint64_t* archOffsets
                = offsets.empty() ? nullptr : offsets.data() + offsetCursor;
            offsetCursor += withCount;
            uint32_t rowsLeft = a.count;
            for (size_t ci = 0; ci < a.chunks.size() && rowsLeft > 0; ++ci) {
                const uint32_t n = (rowsLeft < a.capacity) ? rowsLeft : a.capacity;
                chunks.push_back(SnapChunk{a.chunks[ci].data, archOffsets, n});
                rowsLeft -= n;
            }
        }
    }
    if (chunks.empty()) {
        return; // 空分配：无 chunk 可派发，不 spawn（负载均衡用例覆盖）
    }

    // ---- chunk 分配器 + 工作函数 ------------------------------------------
    std::atomic<uint32_t> nextChunk{0};
    const uint32_t totalChunks = static_cast<uint32_t>(chunks.size());

    // 线程本地栈上指针数组；fetch_add 抢占下一个 chunk（relaxed 足够：chunk 索引
    // 互斥发放，快照数据在线程创建时已发布，回调自有状态的同步由调用方负责）。
    auto runChunks = [&]() {
        void* comps[ZK_MAX_COMPONENTS];
        for (;;) {
            const uint32_t ci = nextChunk.fetch_add(1u, std::memory_order_relaxed);
            if (ci >= totalChunks) {
                break;
            }
            const SnapChunk& c = chunks[ci];
            for (uint32_t j = 0; j < withCount; ++j) {
                comps[j] = static_cast<char*>(c.data) + c.offsets[j];
            }
            fn(user, comps, c.rows);
        }
    };

    // ---- 线程数裁决 --------------------------------------------------------
    uint32_t workers = threadCount;
    if (workers == 0u) {
        const unsigned hw = std::thread::hardware_concurrency();
        workers = (hw == 0u) ? 1u : static_cast<uint32_t>(hw);
    }
    if (workers > ZK_MAX_PARALLEL_THREADS) {
        workers = ZK_MAX_PARALLEL_THREADS;
    }
    if (workers <= 1u) {
        runChunks(); // 单线程退化：调用线程内联执行，零 spawn 开销
        return;
    }

    // ---- 一次性 spawn/join（选型说明见文件头注释）---------------------------
    // 调用线程自身也参与执行（workers 个并发度 = spawn 的 workers-1 个 + 本线程）。
    std::vector<std::thread> pool;
    pool.reserve(workers - 1u);
    for (uint32_t t = 0; t < workers - 1u; ++t) {
        pool.emplace_back(runChunks);
    }
    // RAII join：主线程回调抛出异常时也必须先 join（thread 析构时 joinable =
    // std::terminate）；worker 内抛出仍为 terminate（异常不跨线程，文档约定）。
    struct JoinAll {
        std::vector<std::thread>* pool;
        ~JoinAll()
        {
            for (std::thread& t : *pool) {
                if (t.joinable()) {
                    t.join();
                }
            }
        }
        JoinAll(const JoinAll&) = delete;
        JoinAll& operator=(const JoinAll&) = delete;
    } joiner{&pool};
    runChunks();
}

uint32_t ZkWorld::ArchetypeCount() const
{
    return static_cast<uint32_t>(m_impl->archetypes.size());
}

uint32_t ZkWorld::EntityCount() const { return m_impl->alive; }

// =============================================================================
// chunk 收缩与观测（TASK-B-R8-03）
// =============================================================================
uint32_t ZkWorld::Compact()
{
    Impl& w = *m_impl;
    if (w.iterating != 0u) {
        assert(false && "ZkWorld: Each 回调内禁止 Compact（正在遍历的 chunk 会被释放）");
        return 0; // 迭代期间布局冻结
    }
    uint32_t freed = 0;
    for (Impl::Archetype& a : w.archetypes) {
        // 链最小 chunk 数 = ceil(count / capacity)（行紧凑不变量的下界；
        // 空 archetype 为 0 → 全部释放）。 Destroy 不自动收缩，回收统一在此。
        const uint32_t need = (a.capacity != 0u) ? (a.count + a.capacity - 1u) / a.capacity : 0u;
        while (a.chunks.size() > need) {
            _aligned_free(a.chunks.back().data);
            a.chunks.pop_back();
            freed += 1;
        }
        // 行->实体映射同步收缩（只缩 size 不缩 capacity：后续 Spawn 复用免重分配）
        a.rowEntity.resize(static_cast<size_t>(a.chunks.size()) * a.capacity);
    }
    if (freed != 0u) {
        w.layoutVersion += 1; // TASK-B-R9-17：chunk 存储被释放（ABI 游标失效判定）
    }
    return freed;
}

uint32_t ZkWorld::ChunkCount() const
{
    uint32_t total = 0;
    for (const Impl::Archetype& a : m_impl->archetypes) {
        total += static_cast<uint32_t>(a.chunks.size());
    }
    return total;
}

ZkWorldStats ZkWorld::Stats() const { return m_impl->stats; }

// =============================================================================
// ABI 支撑查询（TASK-B-R9-17）：只读校验助手 + 布局版本号
// =============================================================================
bool ZkWorld::IsAlive(ZkEntity e) const
{
    const Impl& w = *m_impl;
    if (e.index >= w.generation.size()) {
        return false;
    }
    return w.generation[e.index] == e.generation && w.archOf[e.index] >= 0;
}

bool ZkWorld::IsComponentRegistered(uint32_t compId) const
{
    if (compId >= ZK_MAX_COMPONENTS) {
        return false;
    }
    return m_impl->compRegistered[compId];
}

bool ZkWorld::HasComponent(ZkEntity e, uint32_t compId) const
{
    const Impl& w = *m_impl;
    if (!IsAlive(e) || compId >= ZK_MAX_COMPONENTS || !w.compRegistered[compId]) {
        return false;
    }
    return w.archetypes[static_cast<size_t>(w.archOf[e.index])].mask.Has(compId);
}

uint32_t ZkWorld::ComponentSize(uint32_t compId) const
{
    if (compId >= ZK_MAX_COMPONENTS || !m_impl->compRegistered[compId]) {
        return 0;
    }
    return m_impl->compSize[compId];
}

uint64_t ZkWorld::LayoutVersion() const { return m_impl->layoutVersion; }

bool ZkWorld::ScanChunk(const Mask128& with, uint32_t maxCols, uint32_t* ioArch, uint32_t* ioChunk,
    uint32_t* ioRowsLeft, void** outCols, uint32_t* outRows) const
{
    const Impl& w = *m_impl;
    uint32_t withBits[ZK_MAX_COMPONENTS];
    const uint32_t withCount = ZkListBits(with, withBits); // 升序枚举 = 列序
    if (withCount > maxCols) {
        return false; // 出参容量不足（ABI 层已先行校验，此处防御兜底）
    }

    // ---- 定位（archetype, chunk）------------------------------------------
    uint32_t arch = *ioArch;
    uint32_t rowsLeft = *ioRowsLeft;
    uint32_t chunkIdx = *ioChunk;
    if (rowsLeft == 0u) {
        const uint32_t archCount = static_cast<uint32_t>(w.archetypes.size());
        bool found = false;
        while (arch < archCount) {
            const Impl::Archetype& a = w.archetypes[arch];
            if (a.count != 0u && a.mask.Contains(with)) { // 空 archetype 跳过
                rowsLeft = a.count;
                chunkIdx = 0;
                found = true;
                break;
            }
            ++arch;
        }
        if (!found) {
            return false; // 穷尽
        }
    }

    // ---- 交付本 chunk（与 EachBatches 相同的 n = min(剩余, 容量) 语义）------
    const Impl::Archetype& a = w.archetypes[arch];
    const uint32_t n = (rowsLeft < a.capacity) ? rowsLeft : a.capacity;
    char* base = static_cast<char*>(a.chunks[chunkIdx].data); // data 本体非 const（只读使用）
    for (uint32_t j = 0; j < withCount; ++j) {
        const uint32_t slot = ZkBitsBelow(a.mask, withBits[j]); // POPCNT 定位
        outCols[j] = base + a.columnOffset[slot];
    }
    *outRows = n;

    // ---- 推进游标位置（耗尽时 ioArch 指向下一个待扫描 archetype）-----------
    rowsLeft -= n;
    chunkIdx += 1;
    if (rowsLeft == 0u) {
        *ioArch = arch + 1u;
        *ioChunk = 0u;
        *ioRowsLeft = 0u;
    } else {
        *ioArch = arch;
        *ioChunk = chunkIdx;
        *ioRowsLeft = rowsLeft;
    }
    return true;
}
