// =============================================================================
// test_zkernel_migrate.cpp —— zkernel 架型迁移 + chunk 收缩单测（TASK-B-R8-03）
//
// 覆盖任务简报五类场景：
//   1. Add：旧组件数据保留 / 新组件零或 initData / mask 更新 / 查询立即命中
//   2. Remove：被删列数据丢弃（重加为零值而非旧值）/ 查询剔除 / 其余列保留
//   3. 跨 chunk 迁移（源/目标多 chunk）+ swap-back 后其他实体数据完整性
//   4. 连续 Add/Remove 1000 次同实体（archetype 撕裂防护）+ stats 计数正确
//   5. 大段落 Destroy 后 Compact 回收（chunk 数下降断言）+ 每次迭代仍正确
//   6. 代际/句柄在迁移后仍有效（同句柄读新组件；销毁后旧句柄仍被拒）
// 附加：失败路径世界状态不变 / 空掩码往返 / 批量接口（混合源 archetype）/
//       stats 精确会计（rowsMoved/bytesMoved 逐字节断言）
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <vector>

#include "zkernel/zkernel.h"

namespace {

struct Position { float x, y, z; };    // 组件 0：12B / align 4
struct Velocity { float dx, dy, dz; }; // 组件 1：12B / align 4
struct Tag { uint32_t v; };            // 组件 2：4B / align 4
struct Weight { float w; };            // 组件 3：4B / align 4
struct Big { uint8_t bytes[1024]; };   // 组件 4：1024B / align 1 → 单 chunk 容量 32

constexpr uint32_t kCompPosition = 0;
constexpr uint32_t kCompVelocity = 1;
constexpr uint32_t kCompTag = 2;
constexpr uint32_t kCompWeight = 3;
constexpr uint32_t kCompBig = 4;

Mask128 MakeMask(std::initializer_list<uint32_t> bits)
{
    Mask128 m{};
    for (uint32_t b : bits) {
        m.Set(b);
    }
    return m;
}

/// 从 Big 列第 r 行读出前 4 字节编码的 uint32（列 stride = sizeof(Big)，SoA）
uint32_t ReadBig(const void* bigCol, uint32_t r)
{
    uint32_t v = 0;
    std::memcpy(&v, static_cast<const uint8_t*>(bigCol) + static_cast<size_t>(r) * sizeof(Big),
        sizeof(v));
    return v;
}

} // namespace

// -----------------------------------------------------------------------------
// 场景 1：Add —— 数据保留 / 零或 initData / mask 更新 / 查询立即命中
// -----------------------------------------------------------------------------
TEST_CASE("Add：数据保留/零或 initData/mask 更新/查询立即命中")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    Mask128 mPos = MakeMask({kCompPosition});
    ZkEntity e = world.Spawn(mPos);
    REQUIRE(e != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{1.0f, 2.0f, 3.0f};

    // initData 路径：旧组件数据保留 + 新组件 = initData
    Tag initTag{77u};
    CHECK(world.AddComponent(e, kCompTag, &initTag));
    const Position* pos = static_cast<const Position*>(world.GetComponent(e, kCompPosition));
    REQUIRE(pos != nullptr);
    CHECK(pos->x == 1.0f);
    CHECK(pos->y == 2.0f);
    CHECK(pos->z == 3.0f);
    const Tag* tag = static_cast<const Tag*>(world.GetComponent(e, kCompTag));
    REQUIRE(tag != nullptr);
    CHECK(tag->v == 77u);
    CHECK(world.ArchetypeCount() == 2u); // mask 更新 → 新 archetype

    // 查询立即命中新 archetype（同帧可见）
    uint32_t rows = 0;
    double sumTag = 0.0;
    world.Each(MakeMask({kCompPosition, kCompTag}), [&](void** comps, uint32_t rowCount) {
        const Tag* t = static_cast<const Tag*>(comps[1]); // with 升序位序：pos(0)→comps[0]，tag(2)→comps[1]
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumTag += static_cast<double>(t[r].v);
        }
        rows += rowCount;
    });
    CHECK(rows == 1u);
    CHECK(sumTag == 77.0);

    // 零初始化路径：initData = nullptr → 新列全零
    ZkEntity e2 = world.Spawn(mPos);
    REQUIRE(e2 != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(e2, kCompPosition)) = Position{9.0f, 9.0f, 9.0f};
    CHECK(world.AddComponent(e2, kCompTag, nullptr));
    const Tag* tag2 = static_cast<const Tag*>(world.GetComponent(e2, kCompTag));
    REQUIRE(tag2 != nullptr);
    CHECK(tag2->v == 0u); // 零初始化而非未定义内存
    const Position* pos2 = static_cast<const Position*>(world.GetComponent(e2, kCompPosition));
    REQUIRE(pos2 != nullptr);
    CHECK(pos2->x == 9.0f);

    rows = 0;
    sumTag = 0.0;
    world.Each(MakeMask({kCompPosition, kCompTag}), [&](void** comps, uint32_t rowCount) {
        const Tag* t = static_cast<const Tag*>(comps[1]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumTag += static_cast<double>(t[r].v);
        }
        rows += rowCount;
    });
    CHECK(rows == 2u);
    CHECK(sumTag == 77.0); // 77 + 0
    CHECK(world.EntityCount() == 2u);
}

// -----------------------------------------------------------------------------
// 场景 2：Remove —— 数据丢弃 / 查询剔除 / 其余列保留
// -----------------------------------------------------------------------------
TEST_CASE("Remove：数据丢弃/查询剔除/其余列保留")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity e = world.Spawn(MakeMask({kCompPosition, kCompVelocity, kCompTag}));
    REQUIRE(e != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{1.0f, 2.0f, 3.0f};
    *static_cast<Velocity*>(world.GetComponent(e, kCompVelocity)) = Velocity{4.0f, 5.0f, 6.0f};
    *static_cast<Tag*>(world.GetComponent(e, kCompTag)) = Tag{1234u};

    CHECK(world.RemoveComponent(e, kCompTag));

    // 被删列访问拒绝；其余列数据保留
    CHECK(world.GetComponent(e, kCompTag) == nullptr);
    const Position* pos = static_cast<const Position*>(world.GetComponent(e, kCompPosition));
    const Velocity* vel = static_cast<const Velocity*>(world.GetComponent(e, kCompVelocity));
    REQUIRE(pos != nullptr);
    REQUIRE(vel != nullptr);
    CHECK(pos->x == 1.0f);
    CHECK(pos->z == 3.0f);
    CHECK(vel->dy == 5.0f);

    // 查询剔除：含 tag 的查询不再命中；不含 tag 的查询仍命中
    uint32_t rowsWithTag = 0;
    world.Each(MakeMask({kCompPosition, kCompVelocity, kCompTag}),
        [&](void** /*comps*/, uint32_t rowCount) { rowsWithTag += rowCount; });
    CHECK(rowsWithTag == 0u);
    uint32_t rowsNoTag = 0;
    world.Each(MakeMask({kCompPosition, kCompVelocity}),
        [&](void** /*comps*/, uint32_t rowCount) { rowsNoTag += rowCount; });
    CHECK(rowsNoTag == 1u);

    // 数据丢弃证明：重新 Add（nullptr）得到零值，而非被删前的 1234
    CHECK(world.AddComponent(e, kCompTag, nullptr));
    const Tag* tag = static_cast<const Tag*>(world.GetComponent(e, kCompTag));
    REQUIRE(tag != nullptr);
    CHECK(tag->v == 0u);
    CHECK(world.ArchetypeCount() == 2u); // {pos,vel,tag} 与 {pos,vel}（空后保留复用）
    CHECK(world.EntityCount() == 1u);
}

// -----------------------------------------------------------------------------
// 附加：失败路径 —— 返回 false 且世界状态不变
// -----------------------------------------------------------------------------
TEST_CASE("Add/Remove 失败路径：句柄/注册/掩码校验，状态不变")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity e = world.Spawn(MakeMask({kCompPosition}));
    REQUIRE(e != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{7.0f, 0.0f, 0.0f};

    // 未注册组件
    CHECK(!world.AddComponent(e, kCompVelocity, nullptr));
    CHECK(!world.RemoveComponent(e, kCompVelocity));
    CHECK(!world.RemoveComponent(e, kCompWeight));
    // 已含该组件
    Tag t{5u};
    CHECK(world.AddComponent(e, kCompTag, &t));
    CHECK(!world.AddComponent(e, kCompTag, &t));
    // 坏句柄：越界索引 / 代际失配
    ZkEntity bad{999u, 0u};
    CHECK(!world.AddComponent(bad, kCompTag, nullptr));
    CHECK(!world.RemoveComponent(bad, kCompTag));
    ZkEntity stale{e.index, e.generation + 1u};
    CHECK(!world.AddComponent(stale, kCompTag, nullptr));
    CHECK(!world.RemoveComponent(stale, kCompTag));
    CHECK(!world.Destroy(stale));

    // 状态不变：实体/archetype/统计不被失败调用污染
    CHECK(world.EntityCount() == 1u);
    CHECK(world.ArchetypeCount() == 2u);
    CHECK(world.ChunkCount() == 2u);
    CHECK(world.Stats().migrations == 1u);
    const Position* pos = static_cast<const Position*>(world.GetComponent(e, kCompPosition));
    REQUIRE(pos != nullptr);
    CHECK(pos->x == 7.0f);

    // 销毁后旧句柄：迁移 API 同样拒绝（代际失配优先于掩码校验）
    ZkEntity old = e;
    CHECK(world.Destroy(old));
    CHECK(!world.AddComponent(old, kCompTag, nullptr));
    CHECK(!world.RemoveComponent(old, kCompTag));
    CHECK(world.EntityCount() == 0u);
}

// -----------------------------------------------------------------------------
// 场景 3：跨 chunk 迁移 + swap-back 完整性 + Compact chunk 回收
// -----------------------------------------------------------------------------
TEST_CASE("跨 chunk 迁移：Big 容量 32 × 100 实体全量 Add + swap-back 完整性")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompBig, sizeof(Big), alignof(Big)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    // 源 archetype {big}：1024B/行 → 容量 32；100 实体跨 4 chunk
    Mask128 mBig = MakeMask({kCompBig});
    std::vector<ZkEntity> ents(100);
    for (uint32_t i = 0; i < 100; ++i) {
        ents[i] = world.Spawn(mBig);
        REQUIRE(ents[i] != ZK_INVALID_ENTITY);
        const uint32_t v = i + 1u;
        std::memcpy(static_cast<Big*>(world.GetComponent(ents[i], kCompBig))->bytes, &v, sizeof(v));
    }
    CHECK(world.ChunkCount() == 4u);

    // 预填目标 archetype {big,tag}（1028B/行 → 容量 32）：40 实体占 2 chunk，
    // 迫使后续迁移在目标链扩容（新 chunk 分配发生在迁移路径上）
    Mask128 mBigTag = MakeMask({kCompBig, kCompTag});
    std::vector<ZkEntity> pre(40);
    for (uint32_t i = 0; i < 40; ++i) {
        pre[i] = world.Spawn(mBigTag);
        REQUIRE(pre[i] != ZK_INVALID_ENTITY);
        const uint32_t v = 10000u + i;
        std::memcpy(static_cast<Big*>(world.GetComponent(pre[i], kCompBig))->bytes, &v, sizeof(v));
        *static_cast<Tag*>(world.GetComponent(pre[i], kCompTag)) = Tag{7u};
    }
    CHECK(world.ChunkCount() == 6u); // 4 + 2

    // 全量迁移 100 实体（源行号 0..99 逐个搬出，源 chunk 间高频 swap-back）
    Tag initTag{3u};
    for (uint32_t i = 0; i < 100; ++i) {
        CHECK(world.AddComponent(ents[i], kCompTag, &initTag));
    }
    CHECK(world.EntityCount() == 140u);
    CHECK(world.Stats().migrations == 100u);
    // 源 archetype 清空但 chunk 保留（Destroy/迁移不自动收缩）；目标 140 行 → 5 chunk
    CHECK(world.ChunkCount() == 9u); // 4（源，空）+ 5（目标）

    // 全量回读：迁移实体 big 保留 + tag = initData；预填实体不受迁移扰动
    uint32_t rows = 0;
    double sumBig = 0.0;
    double sumTag = 0.0;
    world.Each(mBigTag, [&](void** comps, uint32_t rowCount) {
        const Tag* tagCol = static_cast<const Tag*>(comps[0]); // 升序位序：tag(2) < big(4)
        const uint8_t* bigCol = static_cast<const uint8_t*>(comps[1]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumTag += static_cast<double>(tagCol[r].v);
            sumBig += static_cast<double>(ReadBig(bigCol, r));
        }
        rows += rowCount;
    });
    CHECK(rows == 140u);
    CHECK(sumBig == doctest::Approx(5050.0 + 400780.0)); // Σ(1..100) + Σ(10000..10039)
    CHECK(sumTag == doctest::Approx(580.0));             // 100×3 + 40×7

    // 句柄级 spot check（含被多次 swap-back 搬行的实体）
    uint32_t v0 = 0;
    std::memcpy(&v0, static_cast<Big*>(world.GetComponent(ents[0], kCompBig))->bytes, sizeof(v0));
    CHECK(v0 == 1u);
    uint32_t v99 = 0;
    std::memcpy(&v99, static_cast<Big*>(world.GetComponent(ents[99], kCompBig))->bytes, sizeof(v99));
    CHECK(v99 == 100u);
    const Tag* t50 = static_cast<const Tag*>(world.GetComponent(ents[50], kCompTag));
    REQUIRE(t50 != nullptr);
    CHECK(t50->v == 3u);

    // Compact：空源 archetype 4 chunk 全释放，目标链已最小（5）不动
    CHECK(world.Compact() == 4u);
    CHECK(world.ChunkCount() == 5u);
    CHECK(world.Stats().migrations == 100u); // Compact 不计迁移

    // 收缩后迭代仍正确（chunk 释放不破坏存活行）
    rows = 0;
    world.Each(mBigTag, [&](void** comps, uint32_t rowCount) {
        const uint8_t* bigCol = static_cast<const uint8_t*>(comps[1]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            CHECK(ReadBig(bigCol, r) >= 1u); // 存活行数据可读
        }
        rows += rowCount;
    });
    CHECK(rows == 140u);
}

// -----------------------------------------------------------------------------
// 场景 4：连续 Add/Remove 1000 次 —— archetype 撕裂防护 + stats 计数
// -----------------------------------------------------------------------------
TEST_CASE("连续 Add/Remove 1000 次：撕裂防护（archetype/chunk 不泄漏）+ stats")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity e = world.Spawn(MakeMask({kCompPosition}));
    REQUIRE(e != ZK_INVALID_ENTITY);

    constexpr uint32_t kRounds = 1000;
    for (uint32_t r = 0; r < kRounds; ++r) {
        *static_cast<Position*>(world.GetComponent(e, kCompPosition)) =
            Position{static_cast<float>(r), 0.0f, 0.0f};
        if (r % 2 == 0) {
            Tag t{r};
            CHECK(world.AddComponent(e, kCompTag, &t));
            const Tag* tag = static_cast<const Tag*>(world.GetComponent(e, kCompTag));
            REQUIRE(tag != nullptr);
            CHECK(tag->v == r);
        } else {
            CHECK(world.RemoveComponent(e, kCompTag));
            CHECK(world.GetComponent(e, kCompTag) == nullptr);
        }
        const Position* pos = static_cast<const Position*>(world.GetComponent(e, kCompPosition));
        REQUIRE(pos != nullptr);
        CHECK(pos->x == static_cast<float>(r)); // 保留列每次迁移后仍正确
    }

    // 撕裂防护：实体数恒 1、archetype 恒 2（{pos} 与 {pos,tag}）、chunk 恒 2 不泄漏
    CHECK(world.EntityCount() == 1u);
    CHECK(world.ArchetypeCount() == 2u);
    CHECK(world.ChunkCount() == 2u);
    CHECK(world.Stats().migrations == kRounds);
    CHECK(world.Stats().rowsMoved >= kRounds); // 每次迁移至少搬 1 行

    // 终态（第 999 轮 Remove 后）：{pos} 单实体数据完好
    const Position* pos = static_cast<const Position*>(world.GetComponent(e, kCompPosition));
    REQUIRE(pos != nullptr);
    CHECK(pos->x == 999.0f);
    CHECK(world.GetComponent(e, kCompTag) == nullptr);
}

// -----------------------------------------------------------------------------
// 场景 5：大段落 Destroy 后 Compact —— chunk 数下降 + 迭代正确 + 复用
// -----------------------------------------------------------------------------
TEST_CASE("Compact 收缩：大段 Destroy 后 chunk 数下降/迭代正确/空 archetype 全释放")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompBig, sizeof(Big), alignof(Big)));

    Mask128 mBig = MakeMask({kCompBig});
    std::vector<ZkEntity> ents(100);
    for (uint32_t i = 0; i < 100; ++i) {
        ents[i] = world.Spawn(mBig);
        REQUIRE(ents[i] != ZK_INVALID_ENTITY);
        const uint32_t v = i + 1u;
        std::memcpy(static_cast<Big*>(world.GetComponent(ents[i], kCompBig))->bytes, &v, sizeof(v));
    }
    CHECK(world.ChunkCount() == 4u);

    // 销毁前 90 个（swap-back 高频跨 chunk）→ 10 幸存；chunk 保留 4（不自动收缩，
    // 供复用防抖动——收缩统一走 Compact 入口）
    for (uint32_t i = 0; i < 90; ++i) {
        CHECK(world.Destroy(ents[i]));
    }
    CHECK(world.EntityCount() == 10u);
    CHECK(world.ChunkCount() == 4u);

    // Compact：need = ceil(10/32) = 1 → 释放 3 个尾部空 chunk
    CHECK(world.Compact() == 3u);
    CHECK(world.ChunkCount() == 1u);

    // 收缩后迭代仍正确：幸存值集合恰为 {91..100}
    // （swap-back 收行使行序逆置，与既有 datakernel 测试同策：排序后断言集合）
    std::vector<uint32_t> alive;
    world.Each(mBig, [&](void** comps, uint32_t rowCount) {
        const uint8_t* bigCol = static_cast<const uint8_t*>(comps[0]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            alive.push_back(ReadBig(bigCol, r));
        }
    });
    REQUIRE(alive.size() == 10u);
    std::sort(alive.begin(), alive.end());
    for (uint32_t k = 0; k < alive.size(); ++k) {
        CHECK(alive[k] == 91u + k);
    }
    // 幸存句柄 spot check
    uint32_t v95 = 0;
    std::memcpy(&v95, static_cast<Big*>(world.GetComponent(ents[94], kCompBig))->bytes, sizeof(v95));
    CHECK(v95 == 95u);

    // 无可释放时 Compact 返回 0（幂等）
    CHECK(world.Compact() == 0u);
    CHECK(world.ChunkCount() == 1u);

    // 复用：收缩后的世界里再 Spawn 正常
    ZkEntity n = world.Spawn(mBig);
    REQUIRE(n != ZK_INVALID_ENTITY);
    const uint32_t nv = 777u;
    std::memcpy(static_cast<Big*>(world.GetComponent(n, kCompBig))->bytes, &nv, sizeof(nv));
    CHECK(world.EntityCount() == 11u);
    CHECK(world.ChunkCount() == 1u); // 11 行 ≤ 容量 32，不扩链

    // 清空 → 空 archetype 的 Compact 全释放（chunk 数归零，archetype 本体保留）
    for (uint32_t i = 90; i < 100; ++i) {
        CHECK(world.Destroy(ents[i]));
    }
    CHECK(world.Destroy(n));
    CHECK(world.EntityCount() == 0u);
    CHECK(world.ChunkCount() == 1u);
    CHECK(world.Compact() == 1u);
    CHECK(world.ChunkCount() == 0u);
    CHECK(world.ArchetypeCount() == 1u); // archetype 注册保留复用

    // 全释放后再 Spawn：重新分配 chunk，功能完整
    ZkEntity m = world.Spawn(mBig);
    REQUIRE(m != ZK_INVALID_ENTITY);
    CHECK(world.ChunkCount() == 1u);
    const uint32_t mv = 888u;
    std::memcpy(static_cast<Big*>(world.GetComponent(m, kCompBig))->bytes, &mv, sizeof(mv));
    uint32_t back = 0;
    std::memcpy(&back, static_cast<Big*>(world.GetComponent(m, kCompBig))->bytes, sizeof(back));
    CHECK(back == 888u);
}

// -----------------------------------------------------------------------------
// 场景 6：代际/句柄迁移后仍有效
// -----------------------------------------------------------------------------
TEST_CASE("代际句柄：迁移后同句柄读新组件/销毁后仍被拒")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity e = world.Spawn(MakeMask({kCompPosition}));
    REQUIRE(e != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(e, kCompPosition)) = Position{5.0f, 0.0f, 0.0f};
    const ZkEntity h = e; // 迁移前取得的句柄

    // 连续两次 Add：同一句柄始终有效
    Tag t{7u};
    CHECK(world.AddComponent(h, kCompTag, &t));
    const Tag* tag = static_cast<const Tag*>(world.GetComponent(h, kCompTag));
    REQUIRE(tag != nullptr);
    CHECK(tag->v == 7u);
    Velocity v{1.0f, 2.0f, 3.0f};
    CHECK(world.AddComponent(h, kCompVelocity, &v));
    const Velocity* vel = static_cast<const Velocity*>(world.GetComponent(h, kCompVelocity));
    REQUIRE(vel != nullptr);
    CHECK(vel->dy == 2.0f);

    // Remove 后同句柄读剩余组件
    CHECK(world.RemoveComponent(h, kCompPosition));
    CHECK(world.GetComponent(h, kCompPosition) == nullptr);
    const Velocity* vel2 = static_cast<const Velocity*>(world.GetComponent(h, kCompVelocity));
    const Tag* tag2 = static_cast<const Tag*>(world.GetComponent(h, kCompTag));
    REQUIRE(vel2 != nullptr);
    REQUIRE(tag2 != nullptr);
    CHECK(vel2->dz == 3.0f);
    CHECK(tag2->v == 7u);

    // 迁移不改变代际：Destroy 后旧句柄（迁移前取得）照常失配
    CHECK(world.Destroy(h));
    CHECK(world.GetComponent(h, kCompVelocity) == nullptr);
    CHECK(!world.Destroy(h));
    ZkEntity n = world.Spawn(MakeMask({kCompVelocity}));
    CHECK(n.index == h.index);      // 槽位复用
    CHECK(n.generation == h.generation + 1u); // 代际只因销毁递增，迁移不递增
}

// -----------------------------------------------------------------------------
// 附加：空掩码往返（Spawn 空 → Add → Remove → 空）
// -----------------------------------------------------------------------------
TEST_CASE("空掩码往返：零列 archetype 迁移（chunkBytes=0 路径）")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity e = world.Spawn(Mask128{});
    REQUIRE(e != ZK_INVALID_ENTITY);
    CHECK(world.ArchetypeCount() == 1u);
    uint32_t rows = 0;
    world.Each(Mask128{}, [&](void** /*comps*/, uint32_t rowCount) { rows += rowCount; });
    CHECK(rows == 1u);

    // 空 → Add：无共享列可搬，仅写新列
    Tag t{9u};
    CHECK(world.AddComponent(e, kCompTag, &t));
    const Tag* tag = static_cast<const Tag*>(world.GetComponent(e, kCompTag));
    REQUIRE(tag != nullptr);
    CHECK(tag->v == 9u);
    // 空掩码查询命中所有 archetype（既有语义）：Add 后总数仍 1；
    // {tag} 查询命中证明实体已离开空掩码 archetype
    rows = 0;
    world.Each(Mask128{}, [&](void** /*comps*/, uint32_t rowCount) { rows += rowCount; });
    CHECK(rows == 1u);
    rows = 0;
    world.Each(MakeMask({kCompTag}), [&](void** /*comps*/, uint32_t rowCount) { rows += rowCount; });
    CHECK(rows == 1u);

    // {tag} → Remove：目标为空掩码 archetype（无列布局，chunkBytes=0）
    CHECK(world.RemoveComponent(e, kCompTag));
    CHECK(world.GetComponent(e, kCompTag) == nullptr);
    rows = 0;
    world.Each(Mask128{}, [&](void** /*comps*/, uint32_t rowCount) { rows += rowCount; });
    CHECK(rows == 1u);

    // 再 Add（nullptr 零初始化）往返仍正确
    CHECK(world.AddComponent(e, kCompTag, nullptr));
    tag = static_cast<const Tag*>(world.GetComponent(e, kCompTag));
    REQUIRE(tag != nullptr);
    CHECK(tag->v == 0u);
    CHECK(world.EntityCount() == 1u);
    CHECK(world.Stats().migrations == 3u);
}

// -----------------------------------------------------------------------------
// 附加：批量接口（混合源 archetype + 失效实体跳过）
// -----------------------------------------------------------------------------
TEST_CASE("AddComponentBatch：混合源 archetype/返回成功数/缓存正确性")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    // 三种源 archetype 交错排列（演练 1 项解析缓存的换档正确性）
    constexpr uint32_t kA = 50, kB = 30, kC = 20;
    std::vector<ZkEntity> a(kA), b(kB), c(kC);
    for (uint32_t i = 0; i < kA; ++i) {
        a[i] = world.Spawn(MakeMask({kCompPosition}));
        *static_cast<Position*>(world.GetComponent(a[i], kCompPosition)) =
            Position{static_cast<float>(i), 0.0f, 0.0f};
    }
    for (uint32_t i = 0; i < kB; ++i) {
        b[i] = world.Spawn(MakeMask({kCompVelocity}));
        *static_cast<Velocity*>(world.GetComponent(b[i], kCompVelocity)) =
            Velocity{static_cast<float>(i), 0.0f, 0.0f};
    }
    for (uint32_t i = 0; i < kC; ++i) {
        c[i] = world.Spawn(MakeMask({kCompPosition, kCompVelocity}));
        *static_cast<Position*>(world.GetComponent(c[i], kCompPosition)) =
            Position{static_cast<float>(i), 0.0f, 0.0f};
        *static_cast<Velocity*>(world.GetComponent(c[i], kCompVelocity)) =
            Velocity{static_cast<float>(i), 0.0f, 0.0f};
    }
    std::vector<ZkEntity> batch;
    batch.reserve(kA + kB + kC);
    for (uint32_t i = 0; i < kA || i < kB || i < kC; ++i) {
        if (i < kA) {
            batch.push_back(a[i]);
        }
        if (i < kB) {
            batch.push_back(b[i]);
        }
        if (i < kC) {
            batch.push_back(c[i]);
        }
    }

    Tag init{5u};
    CHECK(world.AddComponentBatch(batch.data(), static_cast<uint32_t>(batch.size()), kCompTag,
        &init) == kA + kB + kC);
    CHECK(world.Stats().migrations == kA + kB + kC);

    // 全量回读：tag = initData；原组件保留（{pos,tag} 升序位序：pos(0)→comps[0]）
    uint32_t rows = 0;
    world.Each(MakeMask({kCompPosition, kCompTag}), [&](void** comps, uint32_t rowCount) {
        const Tag* tags = static_cast<const Tag*>(comps[1]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            CHECK(tags[r].v == 5u);
        }
        rows += rowCount;
    });
    CHECK(rows == kA + kC); // {pos,tag} 命中 A 与 C
    CHECK(world.GetComponent(b[0], kCompVelocity) != nullptr);
    CHECK(world.GetComponent(b[0], kCompTag) != nullptr);
    CHECK(world.GetComponent(a[0], kCompPosition) != nullptr);
    const Position* pa = static_cast<const Position*>(world.GetComponent(a[7], kCompPosition));
    REQUIRE(pa != nullptr);
    CHECK(pa->x == 7.0f);

    // 失效实体（已销毁）混入批次：跳过不中断，返回成功数
    std::vector<ZkEntity> extra(10);
    for (uint32_t i = 0; i < 10; ++i) {
        extra[i] = world.Spawn(MakeMask({kCompPosition}));
        REQUIRE(extra[i] != ZK_INVALID_ENTITY);
    }
    CHECK(world.Destroy(extra[2]));
    CHECK(world.Destroy(extra[5]));
    CHECK(world.AddComponentBatch(extra.data(), 10u, kCompTag, &init) == 8u);
    CHECK(world.Stats().migrations == kA + kB + kC + 8u);
    // 空批次 / null 列表
    CHECK(world.AddComponentBatch(nullptr, 0u, kCompTag, &init) == 0u);
    CHECK(world.AddComponentBatch(extra.data(), 0u, kCompTag, &init) == 0u);
}

// -----------------------------------------------------------------------------
// 附加：stats 精确会计（rowsMoved/bytesMoved 逐字节断言；Destroy 不计入）
// -----------------------------------------------------------------------------
TEST_CASE("ZkWorldStats 精确会计：迁移行 + swap-back 收行逐字节断言")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    ZkEntity a = world.Spawn(MakeMask({kCompPosition}));
    ZkEntity b = world.Spawn(MakeMask({kCompPosition}));
    REQUIRE(a != ZK_INVALID_ENTITY);
    REQUIRE(b != ZK_INVALID_ENTITY);
    *static_cast<Position*>(world.GetComponent(a, kCompPosition)) = Position{1.0f, 0.0f, 0.0f};
    *static_cast<Position*>(world.GetComponent(b, kCompPosition)) = Position{2.0f, 0.0f, 0.0f};

    const ZkWorldStats s0 = world.Stats();
    CHECK(s0.migrations == 0u);
    CHECK(s0.rowsMoved == 0u);
    CHECK(s0.bytesMoved == 0u);

    // Add(a, tag)：a 在行 0、尾行 1（b）→ swap-back 发生。
    // 搬移字节 = 共享列 pos 12 + 新列 tag 4 + swap-back b 行 12 = 28；搬行 = 2。
    Tag t{1u};
    CHECK(world.AddComponent(a, kCompTag, &t));
    const ZkWorldStats s1 = world.Stats();
    CHECK(s1.migrations == 1u);
    CHECK(s1.rowsMoved == 2u);
    CHECK(s1.bytesMoved == 12u + 4u + 12u);

    // Remove(a, tag)：a 独占 {pos,tag}（行 0 = 尾行）→ 无 swap-back。
    // 搬移字节 = 共享列 pos 12；搬行 = 1。目标 {pos} 追加行 1。
    CHECK(world.RemoveComponent(a, kCompTag));
    const ZkWorldStats s2 = world.Stats();
    CHECK(s2.migrations == 2u);
    CHECK(s2.rowsMoved == 3u);
    CHECK(s2.bytesMoved == 28u + 12u);

    // swap-back 后 b 数据完好（b 在 Add 时被搬入源行 0）
    const Position* pb = static_cast<const Position*>(world.GetComponent(b, kCompPosition));
    REQUIRE(pb != nullptr);
    CHECK(pb->x == 2.0f);
    const Position* pa = static_cast<const Position*>(world.GetComponent(a, kCompPosition));
    REQUIRE(pa != nullptr);
    CHECK(pa->x == 1.0f);

    // Destroy 不计入迁移统计
    CHECK(world.Destroy(b));
    const ZkWorldStats s3 = world.Stats();
    CHECK(s3.migrations == 2u);
    CHECK(s3.rowsMoved == 3u);
    CHECK(s3.bytesMoved == 40u);
}
