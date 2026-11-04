// =============================================================================
// zkernel.h —— ZRender 数据内核公共头（128 位掩码 archetype + SoA chunk + 查询）
//
// TASK-B-R1-01 实装：
//   - Mask128  ：128 位组件掩码（组件 ID 0..127），archetype 身份 = 掩码相等
//   - ZkEntity ：index + generation 代际句柄，销毁 swap-back、generation 防 dangling
//   - ZkWorld  ：组件注册 / Spawn / Destroy / GetComponent / Each 批量查询
//
// TASK-B-R8-03 实装：
//   - AddComponent / RemoveComponent：运行时跨 archetype 迁移（共享列 memcpy 保留 /
//     新列 initData 或零初始化 / Remove 数据丢弃），swap-back 收行 + 索引一致更新
//   - AddComponentBatch：批量迁移（compId 单次校验 + archetype 解析 1 项缓存）
//   - Compact：显式 chunk 收缩（释放尾部空 chunk）；ZkWorldStats 迁移成本统计
//
// TASK-B-R9-12 实装：
//   - EachParallel：并行批量查询——chunk 快照 + 原子计数器把 chunk 分配给工作线程
//     （一次性 spawn/join，无内部锁）；只并行"读回调"，写回调/迁移仍单线程约定
//     （回调内布局变更在并行路径同样被 debug 断言 + 运行时拒绝拦截）
//
// 核心定位机制：组件列在 chunk 内的槽位序 = 掩码低位置位计数
//   slot = POPCount(mask & ((1 << bit) - 1))
// 由硬件 POPCNT/TZCNT 指令（__popcnt64/_tzcnt_u64，x64）完成，
// 无软件查表回退（要求 CPU 支持 POPCNT/BMI1，2013 年后的 x64 均具备）。
//
// 存储模型：每 archetype 持 SoA chunk 链（16KB 预算定容），行在 chunk 间
// 紧凑排布；查询按（archetype, chunk）批量回调，迭代路径零堆分配。
//
// 线程模型：布局变更（Spawn/Destroy/Add/Remove/Compact）单线程独占（内部数据
// 结构无锁）；EachParallel 并行只读查询——调用方约定：查询调用期间没有任何线程
// 修改世界（快照期冻结），zkernel 不引入内部锁（TASK-B-R9-12）。
// 本头为 C++ 接口（类 + 模板），按任务简报约定不跨 DLL ABI；
// 唯一 C ABI 导出仍为 ZK_GetVersion。
// =============================================================================
#pragma once

#include <cstdint>
#include <immintrin.h> // __popcnt64 / _tzcnt_u64（x64 POPCNT/BMI 硬件指令）
#include <type_traits>

extern "C" {

/// 数据内核模块版本（与 ZRender 整体版本同步，形如 "0.1.0"）。
const char* ZK_GetVersion(void);

} // extern "C"

// =============================================================================
// Mask128 —— 128 位组件掩码（组件 ID 0..127 占低 2 字；高 2 字保留）
// =============================================================================
struct Mask128 {
    uint64_t w[4]; // bit i 即 w[i >> 6] 的第 (i & 63) 位；w[2]/w[3] 恒为 0（保留）

    /// bit（0..127）是否置位
    bool Has(uint32_t bit) const { return ((w[bit >> 6] >> (bit & 63)) & 1ull) != 0ull; }

    /// 置位 bit
    void Set(uint32_t bit) { w[bit >> 6] |= 1ull << (bit & 63); }

    /// 清除 bit
    void Unset(uint32_t bit) { w[bit >> 6] &= ~(1ull << (bit & 63)); }

    /// 全零
    bool IsZero() const { return (w[0] | w[1] | w[2] | w[3]) == 0ull; }

    /// 四字相等（archetype 身份判定）
    bool operator==(const Mask128& o) const
    {
        return w[0] == o.w[0] && w[1] == o.w[1] && w[2] == o.w[2] && w[3] == o.w[3];
    }
    bool operator!=(const Mask128& o) const { return !(*this == o); }

    /// 包含测试：(self & with) == with —— 查询过滤核心
    bool Contains(const Mask128& with) const
    {
        return ((w[0] & with.w[0]) == with.w[0]) && ((w[1] & with.w[1]) == with.w[1])
            && ((w[2] & with.w[2]) == with.w[2]) && ((w[3] & with.w[3]) == with.w[3]);
    }

    /// 置位个数（硬件 POPCNT）
    uint32_t PopCount() const
    {
        return static_cast<uint32_t>(__popcnt64(w[0]) + __popcnt64(w[1]) + __popcnt64(w[2])
            + __popcnt64(w[3]));
    }
};

/// 硬件 POPCNT 位定位：mask 中低于 bit 的置位个数 = 该组件列的槽位序。
/// 例：mask={3,7,100} 时 ZkBitsBelow(mask,100)==2。全内联（1~2 条 popcnt）。
inline uint32_t ZkBitsBelow(const Mask128& m, uint32_t bit)
{
    const uint32_t word = bit >> 6;
    const uint64_t low = m.w[word] & ((1ull << (bit & 63)) - 1ull);
    uint32_t count = static_cast<uint32_t>(__popcnt64(low));
    for (uint32_t i = 0; i < word; ++i) {
        count += static_cast<uint32_t>(__popcnt64(m.w[i]));
    }
    return count;
}

/// 硬件 TZCNT 枚举：升序列出 mask 的全部置位位，返回个数。out 容量必须 >= 128。
inline uint32_t ZkListBits(const Mask128& m, uint32_t* out)
{
    uint32_t n = 0;
    for (uint32_t wi = 0; wi < 4; ++wi) {
        uint64_t bits = m.w[wi];
        while (bits != 0ull) {
            const uint32_t tz = static_cast<uint32_t>(_tzcnt_u64(bits));
            out[n++] = (wi << 6) + tz;
            bits &= bits - 1ull; // 消去最低置位
        }
    }
    return n;
}

// =============================================================================
// ZkEntity —— 代际句柄
// =============================================================================
struct ZkEntity {
    uint32_t index;      // 实体槽位索引（销毁后复用）
    uint32_t generation; // 代际：销毁 +1，旧句柄因代际不匹配被拒

    bool operator==(const ZkEntity& o) const
    {
        return index == o.index && generation == o.generation;
    }
    bool operator!=(const ZkEntity& o) const { return !(*this == o); }
};

/// 无效句柄（Spawn 失败：掩码含未注册组件 / 越界字非零）
constexpr ZkEntity ZK_INVALID_ENTITY{0xFFFFFFFFu, 0xFFFFFFFFu};

// =============================================================================
// ZkWorldStats —— 迁移成本统计快照（TASK-B-R8-03，基准与观测用）
// =============================================================================
struct ZkWorldStats {
    uint64_t migrations; // 成功迁移次数（Add/RemoveComponent 与批量；不含 Spawn/Destroy）
    uint64_t rowsMoved;  // 迁移路径物理搬行次数 = 迁移实体行 + 迁移 swap-back 收行
    uint64_t bytesMoved; // 迁移路径 memcpy/memset 字节总数（共享列拷贝 + 新列写入 + swap-back）
};

// =============================================================================
// ZkWorld —— 数据内核世界（查询可并行（EachParallel）；布局变更单线程；
//             类接口，不跨 DLL ABI）
// =============================================================================
class ZkWorld {
public:
    ZkWorld();
    ~ZkWorld();
    ZkWorld(const ZkWorld&) = delete;
    ZkWorld& operator=(const ZkWorld&) = delete;

    /// 注册组件：compId 显式分配（0..127），sizeBytes > 0，alignBytes 为 2 的幂。
    /// 重复注册 / 参数非法返回 false。
    bool RegisterComponent(uint32_t compId, uint32_t sizeBytes, uint32_t alignBytes);

    /// 按掩码生成实体（组件内存未初始化，调用方负责写入）。
    /// 掩码含未注册组件或 w[2]/w[3] 非零时返回 ZK_INVALID_ENTITY。
    ZkEntity Spawn(Mask128 mask);

    /// 销毁实体：swap-back 尾行搬洞 + 代际 +1。句柄失效（代际不匹配/已销毁）返回 false。
    bool Destroy(ZkEntity e);

    /// 运行时增组件（TASK-B-R8-03）：目标 archetype = 当前掩码 | bit（无则建）。
    /// 共享列逐列 memcpy 保留数据（SoA 行内跨列不连续 → 直接列寻址拷贝，迁移路径
    /// 无暂存缓冲零分配；分配点仅为新 archetype 创建 / 目标链扩容，与 Spawn 同策略）；
    /// 新列写 initData（nullptr 则零初始化）；源 archetype swap-back 收行并同步
    /// 实体行号 / chunk 索引映射。句柄与代际不变（迁移后原句柄仍有效）。
    /// 失败（句柄失效 / 组件未注册 / 已含该组件）返回 false，世界状态不变。
    bool AddComponent(ZkEntity e, uint32_t compId, const void* initData);

    /// 运行时删组件（TASK-B-R8-03）：目标 archetype = 当前掩码 & ~bit（无则建），
    /// 被删列数据丢弃（不迁移）；其余共享列 memcpy 保留；收行与索引更新同上。
    /// 失败（句柄失效 / 组件未注册 / 掩码不含该组件）返回 false，世界状态不变。
    bool RemoveComponent(ZkEntity e, uint32_t compId);

    /// 批量增组件（TASK-B-R8-03，选做交付）：对 entities[0..count) 逐一执行
    /// AddComponent 同一 compId（initData 为同一缓冲，逐实体拷贝进各自新列）。
    /// compId 只校验一次；目标 archetype 解析带 1 项缓存（连续同源实体免重复
    /// 线性查找）。单个实体失败不中断批次。返回成功迁移数。
    uint32_t AddComponentBatch(const ZkEntity* entities, uint32_t count, uint32_t compId,
        const void* initData);

    /// 取组件列内数据指针（该实体所在行）。实体失效 / 掩码不含该组件返回 nullptr。
    void* GetComponent(ZkEntity e, uint32_t compId);

    /// 批量查询：遍历满足 (archetype.mask & with) == with 的全部 archetype 行，
    /// 按（chunk 批）回调 fn(comps, rowCount)：
    ///   comps[j] = with 升序位序第 j 个组件的列起始指针（SoA，行 0..rowCount-1 连续）
    ///   rowCount = 本批行数（满批 = chunk 容量，尾批为余数）
    /// 迭代路径零堆分配；禁止在回调内 Spawn/Destroy/AddComponent/RemoveComponent/
    /// Compact（会使行映射失效）：debug 构建断言拦截，所有构建运行时直接拒绝
    /// （返回失败，世界状态不变）——与 B-R1-01"回调内禁 Spawn/Destroy"同策并加固。
    template <class F>
    void Each(Mask128 with, F&& fn)
    {
        using Fn = std::remove_reference_t<F>;
        auto thunk = [](void* user, void** comps, uint32_t rowCount) {
            (*static_cast<Fn*>(user))(comps, rowCount);
        };
        EachBatches(with, thunk, static_cast<void*>(&fn));
    }

    /// 并行批量查询（TASK-B-R9-12）：与 Each 相同的批回调约定，但 chunk 由原子
    /// 计数器动态分配给多个工作线程并发遍历（chunk 内行序保持，行间连续 SoA）。
    ///
    /// 回调约定（与 Each 相同签名 fn(comps, rowCount)）：
    ///   - comps[j] = with 升序位序第 j 个组件的列起始指针（本 chunk 内行 0..rowCount-1 连续）
    ///   - 只支持读回调：回调内禁止 Spawn/Destroy/AddComponent/RemoveComponent/
    ///     AddComponentBatch/Compact（与 Each 同一条约定在并行路径的延伸）——
    ///     debug 构建断言拦截，所有构建运行时直接拒绝（返回失败，世界状态不变）
    ///   - 回调会被多线程同时执行：累加器等回调自有状态由调用方保证并发安全
    ///     （建议 atomic 或 per-chunk 归并）；回调内抛出异常 = std::terminate
    ///     （异常不跨线程传播，与 std::thread 语义一致）
    ///
    /// 顺序性：跨 chunk 无顺序保证（chunk 分配次序取决于线程竞争，随运行变化；
    ///   chunk 内行序保持）。要求确定顺序的聚合请自行按实体数据归并。
    ///
    /// 线程前置（调用方约定）：本调用期间没有任何线程修改本世界——快照与并行
    ///   遍历期间布局冻结；zkernel 内部不引入锁。只读查询（含多个 EachParallel /
    ///   Each 并发调用）互相安全。
    ///
    /// threadCount：并发度（含调用线程自身；额外 spawn threadCount-1 个工作线程）。
    ///   0 = 硬件并发（std::thread::hardware_concurrency，不可得则 1）；
    ///   1 = 调用线程内联执行（无 spawn 开销，行为等价 Each 的分块遍历）；
    ///   超过 ZK_MAX_PARALLEL_THREADS(64) 裁剪到 64。
    template <class F>
    void EachParallel(Mask128 with, F&& fn, uint32_t threadCount = 0)
    {
        using Fn = std::remove_reference_t<F>;
        auto thunk = [](void* user, void** comps, uint32_t rowCount) {
            (*static_cast<Fn*>(user))(comps, rowCount);
        };
        EachParallelBatches(with, thunk, static_cast<void*>(&fn), threadCount);
    }

    /// 显式收缩（TASK-B-R8-03）：对全部 archetype 释放尾部空 chunk（_aligned_free）。
    /// 行紧凑不变量下空闲行恒为 chunk 链尾后缀，链最小 chunk 数 = ceil(count/容量)
    /// （空 archetype 为 0，全部释放；archetype 本体与掩码注册保留复用）。
    /// Destroy 不自动收缩（chunk 保留供复用，避免高频增删抖动）——回收统一走本入口。
    /// 返回释放的 chunk 数。
    uint32_t Compact();

    /// archetype 数（含空 archetype；chunk 与 archetype 空后保留复用）
    uint32_t ArchetypeCount() const;

    /// 存活实体数
    uint32_t EntityCount() const;

    /// chunk 总数（全部 archetype 的 chunk 计数和；收缩 / 观测用，O(archetype 数)）
    uint32_t ChunkCount() const;

    /// 迁移成本统计快照（migrations/rowsMoved/bytesMoved，语义见 ZkWorldStats）
    ZkWorldStats Stats() const;

    // ---- TASK-B-R9-17：ABI 支撑查询（只读；C ABI 层的校验/游标失效判定用）----
    /// 实体是否存活（槽位存在、代际匹配、未销毁）。
    bool IsAlive(ZkEntity e) const;
    /// 组件是否已注册。
    bool IsComponentRegistered(uint32_t compId) const;
    /// 实体当前掩码是否包含该组件（句柄无效 / 组件未注册返回 false）。
    bool HasComponent(ZkEntity e, uint32_t compId) const;
    /// 组件注册字节数（未注册返回 0）。
    uint32_t ComponentSize(uint32_t compId) const;
    /// 布局版本号：Spawn/Destroy/Add/Remove（含批量）/Compact 每成功变更一次
    /// +1（只读查询不变更）。跨调用游标据此判定"Begin 后布局是否被动过"。
    uint64_t LayoutVersion() const;

    /// 游标式批量迭代单步（TASK-B-R9-17：EachBatches 的跨调用形态，供 C ABI
    /// 层驱动；扫描序与 EachBatches 逐 chunk 一致：archetype 向量序 → chunk 序）。
    /// 输入 *ioArch/*ioChunk/*ioRowsLeft 为游标位置（*ioRowsLeft == 0 表示当前
    /// archetype 已耗尽，从 *ioArch 起扫描下一个匹配且非空的 archetype）；命中时
    /// 填 outCols（with 升序位序各列在本 chunk 的起始指针，容量 maxCols >= 位列
    /// 数）与 *outRows（本 chunk 行数），并推进游标位置（当前 archetype 耗尽时
    /// *ioArch 指向下一个待扫描 archetype）；无可交付行返回 false（出参内容
    /// *io* 保持不变，调用方按耗尽处理）。
    /// 只读操作：不触发布局、不进 iterating 计数；调用方约定扫描期间无布局变更
    /// （C ABI 层以布局版本号快照防护）。
    bool ScanChunk(const Mask128& with, uint32_t maxCols, uint32_t* ioArch, uint32_t* ioChunk,
        uint32_t* ioRowsLeft, void** outCols, uint32_t* outRows) const;

private:
    /// 批回调类型（C 函数指针 + 用户上下文；无捕获 lambda 转换零分配）
    using ZkBatchFn = void (*)(void* user, void** comps, uint32_t rowCount);

    void EachBatches(const Mask128& with, ZkBatchFn fn, void* user);

    /// 并行批执行（TASK-B-R9-12）：快照 chunk 列表 → 原子计数器分配 → 工作线程
    /// 并发回调。实现细节（快照结构/线程 spawn 选型）见 zkernel.cpp。
    void EachParallelBatches(const Mask128& with, ZkBatchFn fn, void* user, uint32_t threadCount);

    /// 迁移核心（TASK-B-R8-03）：把实体槽位 slot 迁往（当前掩码 ± compId）的
    /// archetype（无则建），共享列 memcpy 保留；add = true 时新列写 initData/零，
    /// false 时该列数据丢弃。调用方负责句柄 / 注册 / 掩码前置校验。
    /// memoSrc/memoDst：批量路径的 1 项 archetype 解析缓存（nullptr = 单次调用）。
    bool MigrateComponent(
        uint32_t slot, uint32_t compId, bool add, const void* initData, int32_t* memoSrc,
        int32_t* memoDst);

    struct Impl;
    Impl* m_impl;
};
