// =============================================================================
// test_zkernel_parallel.cpp —— zkernel 并行 Each（EachParallel）单测（TASK-B-R9-12）
//
// 覆盖任务简报四类场景：
//   1. 结果等价：并行求和 == 单线程求和（多 archetype / 跨 chunk / 每 row 恰一次）
//   2. 负载均衡：chunk 数 < 线程数（16 线程 3 chunk）不崩；空世界 / 掩码无命中
//      （空分配）正确；threadCount=0 硬件并发 / 超大值裁剪到 64
//   3. 无数据竞争：TSAN 在 MSVC 工具链不可用 → 以"百万行重复并行求和 + 精确
//      校验"压测替代（手段与局限见施工报告），另含双线程并发 EachParallel 读路径
//   4. 回调内禁改世界（Each 约定在并行路径的延伸）：Spawn/Destroy/Add/Remove/
//      Batch/Compact 在并行回调内运行时拒绝、世界状态不变——debug 断言与运行时
//      拒绝同一判定点，断言仅在 debug 构建生效，本用例（会真实触发拒绝路径）按
//      测试套件 Release/NDEBUG 惯例仅在 NDEBUG 构建执行（debug 构建下断言会
//      abort，无法在进程内断言）
//
// 附带：嵌套 Each（迭代深度计数原子化后单线程语义回归）。
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <thread>
#include <vector>

#include "zkernel/zkernel.h"

namespace {

struct Position { float x, y, z; };  // 组件 0：12B / align 4
struct Velocity { float dx, dy, dz; }; // 组件 1：12B / align 4
struct Tag { uint32_t v; };          // 组件 2：4B / align 4
struct Big { uint8_t bytes[1024]; }; // 组件 3：1024B / align 1 → 单 chunk 容量 32

constexpr uint32_t kCompPosition = 0;
constexpr uint32_t kCompVelocity = 1;
constexpr uint32_t kCompTag = 2;
constexpr uint32_t kCompBig = 3;

constexpr uint32_t kArchACount = 300; // {pos,vel,big}      1048B/行 → 容量 32 → 10 chunk
constexpr uint32_t kArchBCount = 100; // {pos,vel,tag,big}  1052B/行 → 容量 32 → 4 chunk

/// Big 列前 4 字节编码的 uint32（列 stride = sizeof(Big)，SoA）
void WriteBigId(void* bigCol, uint32_t r, uint32_t id)
{
    std::memcpy(static_cast<uint8_t*>(bigCol) + static_cast<size_t>(r) * sizeof(Big), &id,
        sizeof(id));
}
uint32_t ReadBigId(const void* bigCol, uint32_t r)
{
    uint32_t v = 0;
    std::memcpy(&v, static_cast<const uint8_t*>(bigCol) + static_cast<size_t>(r) * sizeof(Big),
        sizeof(v));
    return v;
}

/// 构造等价性用例世界：A（300 实体）+ B（100 实体），每个实体的 Big 前 4 字节
/// 与 Tag.v 编码唯一 id（0..399），pos.x = float(id)（浮点求和对照用）。
/// chunk 总数 = 14（容量 32，尾 chunk 余数），查询 {pos,vel,big} 命中全部 400 行。
void MakeEquivalenceWorld(ZkWorld& world, Mask128& with)
{
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    REQUIRE(world.RegisterComponent(kCompBig, sizeof(Big), alignof(Big)));

    Mask128 mA{};
    mA.Set(kCompPosition);
    mA.Set(kCompVelocity);
    mA.Set(kCompBig);
    Mask128 mB = mA;
    mB.Set(kCompTag);
    with = mA;

    uint32_t id = 0;
    for (uint32_t i = 0; i < kArchACount; ++i) {
        ZkEntity e = world.Spawn(mA);
        REQUIRE(e != ZK_INVALID_ENTITY);
        *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{
            static_cast<float>(id), 0.0f, 0.0f};
        WriteBigId(world.GetComponent(e, kCompBig), 0, id);
        ++id;
    }
    for (uint32_t i = 0; i < kArchBCount; ++i) {
        ZkEntity e = world.Spawn(mB);
        REQUIRE(e != ZK_INVALID_ENTITY);
        *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{
            static_cast<float>(id), 0.0f, 0.0f};
        *static_cast<Tag*>(world.GetComponent(e, kCompTag)) = Tag{id};
        WriteBigId(world.GetComponent(e, kCompBig), 0, id);
        ++id;
    }
    CHECK(world.ArchetypeCount() == 2u);
    CHECK(world.EntityCount() == kArchACount + kArchBCount);
    CHECK(world.ChunkCount() == (kArchACount + 31u) / 32u + (kArchBCount + 31u) / 32u);
}

} // namespace

// -----------------------------------------------------------------------------
// 场景 1：结果等价（并行 == 单线程；多 archetype / 跨 chunk / 每 row 恰一次）
// -----------------------------------------------------------------------------
TEST_CASE("EachParallel 等价性：并行求和==单线程求和（多 archetype 跨 chunk）")
{
    ZkWorld world;
    Mask128 with{};
    MakeEquivalenceWorld(world, with);
    constexpr uint32_t kTotal = kArchACount + kArchBCount;
    constexpr uint64_t kExpectedSum = static_cast<uint64_t>(kTotal) * (kTotal - 1) / 2;

    // ---- 单线程参照（Each）：整数和（顺序无关精确）+ 逐 row 访问计数 ----------
    std::vector<std::atomic<uint32_t>> visitsRef(kTotal);
    uint64_t sumRef = 0;
    double floatSumRef = 0.0;
    world.Each(with, [&](void** comps, uint32_t rowCount) {
        // 列指针约定：comps[j] 为"列起始"（SoA）——pos 列行步长 = sizeof(Position)，
        // 行 r 的 x = static_cast<const float*>(comps[0])[3u * r]（与 bench 同口径；
        // 若按 Position* 以 [3r] 索引会 3 倍跨行读到未初始化列——本用例初版踩过）
        const float* posX = static_cast<const float*>(comps[0]);    // with 升序：pos(0)→slot0
        const uint8_t* big = static_cast<const uint8_t*>(comps[2]); // big(3)→slot2
        for (uint32_t r = 0; r < rowCount; ++r) {
            const uint32_t id = ReadBigId(big, r);
            sumRef += id;
            floatSumRef += posX[3u * r];
            visitsRef[id].fetch_add(1u, std::memory_order_relaxed);
        }
    });
    CHECK(sumRef == kExpectedSum);
    for (uint32_t id = 0; id < kTotal; ++id) {
        CHECK(visitsRef[id].load() == 1u); // 单线程每 row 恰一次
    }

    // ---- 并行：多组线程数下整数和逐位相等 + 每 row 恰一次 ----------------------
    for (uint32_t threads : {1u, 2u, 4u, 8u, 16u}) {
        CAPTURE(threads);
        std::vector<std::atomic<uint32_t>> visits(kTotal); // vector 值初始化 → 全零
        std::atomic<uint64_t> sum{0};
        world.EachParallel(with,
            [&](void** comps, uint32_t rowCount) {
                const uint8_t* big = static_cast<const uint8_t*>(comps[2]);
                uint64_t local = 0; // chunk 内本地累加，跨线程零共享写
                for (uint32_t r = 0; r < rowCount; ++r) {
                    const uint32_t id = ReadBigId(big, r);
                    local += id;
                    visits[id].fetch_add(1u, std::memory_order_relaxed);
                }
                sum.fetch_add(local, std::memory_order_relaxed);
            },
            threads);

        CHECK(sum.load() == kExpectedSum); // 整数和与单线程逐位相等（顺序无关）
        CHECK(sum.load() == sumRef);
        for (uint32_t id = 0; id < kTotal; ++id) {
            CHECK(visits[id].load() == 1u); // 并行下每 row 恰一次（不重不漏）
        }
    }

    // ---- 浮点列（comps[0] pos）并行读路径：与单线程参照相等 ---------------------
    // 所有列值为非负整数 (< 2^24) 且各 chunk 部分和 < 2^53：double 累加任意次序
    // 均精确 → chunk 和可经整数原子合并（C++17 无 atomic<double> fetch_add）
    std::atomic<uint64_t> floatSumInt{0};
    world.EachParallel(with,
        [&](void** comps, uint32_t rowCount) {
            const float* posX = static_cast<const float*>(comps[0]); // 列起始，行步长 12B
            double local = 0.0;
            for (uint32_t r = 0; r < rowCount; ++r) {
                local += posX[3u * r];
            }
            floatSumInt.fetch_add(static_cast<uint64_t>(local), std::memory_order_relaxed);
        },
        8);
    CHECK(static_cast<double>(floatSumInt.load()) == floatSumRef);

    // ---- 嵌套 Each（迭代深度计数原子化后单线程语义回归）-----------------------
    uint32_t nestedRows = 0;
    world.Each(with, [&](void** /*comps*/, uint32_t rowCount) {
        uint32_t inner = 0;
        world.Each(with, [&](void** /*comps*/, uint32_t innerRows) { inner += innerRows; });
        CHECK(inner == kTotal); // 内层看见全部行（外层迭代中布局只读冻结仍成立）
        nestedRows += rowCount;
    });
    CHECK(nestedRows == kTotal);
}

// -----------------------------------------------------------------------------
// 场景 2：负载均衡边界（chunk 数 < 线程数 / 空分配 / 线程数裁决）
// -----------------------------------------------------------------------------
TEST_CASE("EachParallel 负载均衡：16 线程小 chunk 数不崩、空分配正确、线程数裁决")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompBig, sizeof(Big), alignof(Big)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    Mask128 mBig{};
    mBig.Set(kCompBig);

    // ---- 空世界：无 chunk 可派发，不 spawn，回调零调用 -------------------------
    {
        std::atomic<uint32_t> calls{0};
        std::atomic<uint32_t> rows{0};
        world.EachParallel(mBig,
            [&](void** /*comps*/, uint32_t rowCount) {
                calls.fetch_add(1u, std::memory_order_relaxed);
                rows.fetch_add(rowCount, std::memory_order_relaxed);
            },
            16);
        CHECK(calls.load() == 0u);
        CHECK(rows.load() == 0u);
    }

    // ---- 掩码无命中（实体存在但 archetype 不含查询组件）：同样空分配 -----------
    Mask128 mTag{};
    mTag.Set(kCompTag);
    for (uint32_t i = 0; i < 8u; ++i) {
        CHECK(world.Spawn(mTag) != ZK_INVALID_ENTITY);
    }
    {
        std::atomic<uint32_t> calls{0};
        world.EachParallel(mBig, [&](void** /*comps*/, uint32_t /*rowCount*/) {
                calls.fetch_add(1u, std::memory_order_relaxed);
            },
            8);
        CHECK(calls.load() == 0u);
    }

    // ---- 96 实体 = 3 chunk < 16 线程：不崩，每 row 恰一次，求和精确 -----------
    constexpr uint32_t kCount = 96; // 容量 32 → 3 chunk
    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mBig);
        REQUIRE(ents[i] != ZK_INVALID_ENTITY);
        WriteBigId(world.GetComponent(ents[i], kCompBig), 0, i);
    }
    CHECK(world.ChunkCount() == 4u); // {tag} 8 实体 1 chunk（容量 4096）+ {big} 96 实体 3 chunk
    for (uint32_t threads : {2u, 16u, 64u, 100000u}) { // 100000 → 裁剪到 64（ZK_MAX_PARALLEL_THREADS）
        CAPTURE(threads);
        std::vector<std::atomic<uint32_t>> visits(kCount);
        for (auto& v : visits) {
            v.store(0u, std::memory_order_relaxed);
        }
        std::atomic<uint64_t> sum{0};
        world.EachParallel(mBig,
            [&](void** comps, uint32_t rowCount) {
                const uint8_t* big = static_cast<const uint8_t*>(comps[0]);
                uint64_t local = 0;
                for (uint32_t r = 0; r < rowCount; ++r) {
                    const uint32_t id = ReadBigId(big, r);
                    local += id;
                    visits[id].fetch_add(1u, std::memory_order_relaxed);
                }
                sum.fetch_add(local, std::memory_order_relaxed);
            },
            threads);
        CHECK(sum.load() == static_cast<uint64_t>(kCount) * (kCount - 1) / 2);
        for (uint32_t id = 0; id < kCount; ++id) {
            CHECK(visits[id].load() == 1u);
        }
    }

    // ---- threadCount=0：硬件并发（不可得则 1），语义与显式线程数一致 -----------
    {
        std::atomic<uint64_t> sum{0};
        world.EachParallel(mBig,
            [&](void** comps, uint32_t rowCount) {
                const uint8_t* big = static_cast<const uint8_t*>(comps[0]);
                uint64_t local = 0;
                for (uint32_t r = 0; r < rowCount; ++r) {
                    local += ReadBigId(big, r);
                }
                sum.fetch_add(local, std::memory_order_relaxed);
            },
            0);
        CHECK(sum.load() == static_cast<uint64_t>(kCount) * (kCount - 1) / 2);
    }
}

// -----------------------------------------------------------------------------
// 场景 3：无数据竞争压测（TSAN 替代手段）——百万行重复 + 精确求和校验
// + 双线程并发 EachParallel 读路径
// -----------------------------------------------------------------------------
TEST_CASE("EachParallel 压测：百万行重复并行求和校验 + 并发只读查询")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    Mask128 mTag{};
    mTag.Set(kCompTag);

    constexpr uint32_t kCount = 50000; // 4B/行 → 容量 4096 → 13 chunk
    for (uint32_t i = 0; i < kCount; ++i) {
        ZkEntity e = world.Spawn(mTag);
        REQUIRE(e != ZK_INVALID_ENTITY);
        *static_cast<Tag*>(world.GetComponent(e, kCompTag)) = Tag{i};
    }
    constexpr uint64_t kExpectedSum = static_cast<uint64_t>(kCount) * (kCount - 1) / 2;
    constexpr uint32_t kRounds = 20; // 50000 行 × 20 轮 = 1,000,000 row 访问

    auto round = [&world, &mTag]() {
        std::atomic<uint64_t> sum{0};
        world.EachParallel(mTag,
            [&sum](void** comps, uint32_t rowCount) {
                const Tag* tag = static_cast<const Tag*>(comps[0]);
                uint64_t local = 0;
                for (uint32_t r = 0; r < rowCount; ++r) {
                    local += tag[r].v; // 逐行读全列（带宽型负载）
                }
                sum.fetch_add(local, std::memory_order_relaxed); // 每 chunk 一次原子合并
            },
            8);
        return sum.load();
    };

    // ---- 百万行重复：每轮求和精确等于期望（丢行/重行/撕裂读立即显形）----------
    for (uint32_t round_i = 0; round_i < kRounds; ++round_i) {
        CAPTURE(round_i);
        CHECK(round() == kExpectedSum);
    }

    // ---- 并发只读：两个线程同时各自 EachParallel（快照互相只读，安全）---------
    // iterating 计数原子化后并发查询不再有计数器数据竞争
    std::atomic<uint64_t> okA{0};
    std::atomic<uint64_t> okB{0};
    std::thread ta([&]() {
        for (uint32_t i = 0; i < 10; ++i) {
            if (round() == kExpectedSum) {
                okA.fetch_add(1u, std::memory_order_relaxed);
            }
        }
    });
    std::thread tb([&]() {
        for (uint32_t i = 0; i < 10; ++i) {
            if (round() == kExpectedSum) {
                okB.fetch_add(1u, std::memory_order_relaxed);
            }
        }
    });
    ta.join();
    tb.join();
    CHECK(okA.load() == 10u);
    CHECK(okB.load() == 10u);
}

// -----------------------------------------------------------------------------
// 场景 4：回调内禁改世界（并行路径防护——debug 断言与运行时拒绝同一判定点）
// 仅 NDEBUG 构建（Release）执行：debug 构建下断言 abort，无法进程内断言；
// 测试套件按 build_native.ps1 惯例以 Release 运行。
// -----------------------------------------------------------------------------
#ifdef NDEBUG
TEST_CASE("EachParallel 回调内禁改世界：并行路径运行时拒绝（断言同点）")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    Mask128 mPos{};
    mPos.Set(kCompPosition);

    // 预置实体与合法句柄（保证拒绝来自迭代防护而非句柄失效）
    std::vector<ZkEntity> ents(4);
    for (uint32_t i = 0; i < 4u; ++i) {
        ents[i] = world.Spawn(mPos);
        REQUIRE(ents[i] != ZK_INVALID_ENTITY);
    }
    Mask128 mTag{};
    mTag.Set(kCompTag);
    const uint32_t aliveBefore = world.EntityCount();
    const ZkEntity h = ents[0]; // 合法句柄

    std::atomic<uint32_t> rows{0};
    world.EachParallel(mPos,
        [&](void** /*comps*/, uint32_t rowCount) {
            rows.fetch_add(rowCount, std::memory_order_relaxed);
            // 全部布局变更 API：并行回调内必须被拒（与 Each 单线程同一条约定）
            CHECK(world.Spawn(mPos) == ZK_INVALID_ENTITY);           // Spawn
            CHECK(!world.Destroy(h));                                // Destroy
            const Tag init{1u};
            CHECK(!world.AddComponent(h, kCompTag, &init));          // AddComponent
            CHECK(!world.RemoveComponent(h, kCompPosition));         // RemoveComponent
            CHECK(world.AddComponentBatch(&h, 1u, kCompTag, &init) == 0u); // 批量
            CHECK(world.Compact() == 0u);                            // Compact
            // 只读访问在回调内合法
            CHECK(world.GetComponent(h, kCompPosition) != nullptr);
            CHECK(world.EntityCount() == aliveBefore);
        },
        4);
    CHECK(rows.load() == 4u); // 4 行全部被回调（防护没有吞掉迭代本身）
    // 并行返回后世界状态不变：实体数/查询行数/句柄数据全部原样
    CHECK(world.EntityCount() == aliveBefore);
    uint32_t rowsAfter = 0;
    world.Each(mPos, [&](void** /*comps*/, uint32_t rowCount) { rowsAfter += rowCount; });
    CHECK(rowsAfter == 4u);
    CHECK(world.GetComponent(h, kCompPosition) != nullptr);
    CHECK(world.ArchetypeCount() == 1u); // Spawn/AddComponent 均未产生新 archetype
}
#endif // NDEBUG
