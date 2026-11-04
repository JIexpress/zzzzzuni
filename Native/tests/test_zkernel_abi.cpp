// =============================================================================
// test_zkernel_abi.cpp —— DataKernel C ABI 单测（TASK-B-R9-17）
//
// 覆盖简报验收点：
//   1. ABI 全往返：Create → Register → Spawn → Set → 游标读回 → Add → Get →
//      Destroy → 句柄失效（World 级与实体级各一）
//   2. 游标跨 chunk 推进正确：已知容量（1024B 组件 → 32 行/chunk）逐 chunk
//      断言行数与累计；三列 1000 实体（28B/行 → 容量 455）全量求和
//   3. 坏句柄 / 坏 size 拒绝（错误码逐一断言）
//   4. 布局变更作废游标 / 耗尽幂等 / 游标损坏拒绝
//
// 句柄表是进程级静态（8 槽）：每个用例经 WorldGuard RAII 释放，防止跨用例
// 耗尽句柄表。doctest main 由 test_smoke.cpp 提供。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <cstring>
#include <vector>

#include "zkernel/zkernel.h"
#include "zkernel/zkernel_abi.h"

namespace {

struct Position {
    float x, y, z;
}; // 组件 0：12B / align 4
struct Velocity {
    float dx, dy, dz;
}; // 组件 1：12B / align 4
struct Tag {
    uint32_t v;
}; // 组件 2：4B / align 4
struct Big {
    uint8_t bytes[1024];
}; // 组件 3：1024B / align 1 → 单 chunk 容量 32

constexpr uint32_t kCompPosition = 0;
constexpr uint32_t kCompVelocity = 1;
constexpr uint32_t kCompTag = 2;
constexpr uint32_t kCompBig = 3;

/// 句柄表是静态的（8 槽，进程级）：用例内 RAII 释放，跨用例不泄漏。
struct WorldGuard {
    ZkWorldHandle handle;
    explicit WorldGuard(ZkWorldHandle h) : handle(h) { }
    ~WorldGuard() { ZK_WorldDestroy(handle); }
    WorldGuard(const WorldGuard&) = delete;
    WorldGuard& operator=(const WorldGuard&) = delete;
};

/// 组两个组件位的掩码（lo = bit<64，hi = bit>=64）。
void MaskSet(uint64_t& lo, uint64_t& hi, uint32_t bit)
{
    if (bit < 64u) {
        lo |= 1ull << bit;
    } else {
        hi |= 1ull << (bit - 64u);
    }
}

/// 用例常用前缀：注册 pos/vel/tag/big 四组件。
void RegisterAll(ZkWorldHandle h)
{
    CHECK(ZK_RegisterComponent(h, kCompPosition, sizeof(Position), alignof(Position))
        == ZK_ABI_OK);
    CHECK(ZK_RegisterComponent(h, kCompVelocity, sizeof(Velocity), alignof(Velocity))
        == ZK_ABI_OK);
    CHECK(ZK_RegisterComponent(h, kCompTag, sizeof(Tag), alignof(Tag)) == ZK_ABI_OK);
    CHECK(ZK_RegisterComponent(h, kCompBig, sizeof(Big), alignof(Big)) == ZK_ABI_OK);
}

} // namespace

// =============================================================================
// 1. 全往返
// =============================================================================
TEST_CASE("ABI 全往返：Create→Register→Spawn→Set→游标读回→Add→Get→Destroy→句柄失效")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    REQUIRE(h != 0); // 0 恒无效
    WorldGuard guard(h);

    RegisterAll(h);

    // Spawn（掩码 = pos|tag）+ Set
    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompPosition);
    MaskSet(lo, hi, kCompTag);
    ZkEntityAbi e{};
    REQUIRE(ZK_Spawn(h, lo, hi, &e) == ZK_ABI_OK);
    CHECK(e.index != 0xFFFFFFFFu);

    const Position pos{1.5f, 2.5f, 3.5f};
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, sizeof(pos)) == ZK_ABI_OK);
    const Tag tag{77u};
    CHECK(ZK_SetComponent(h, e, kCompTag, &tag, sizeof(tag)) == ZK_ABI_OK);

    // 游标读回（掩码 = pos|tag，1 实体 → 1 chunk 1 行）
    ZkEachCursor cur;
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur) == ZK_ABI_OK);
    void* cols[4] = {};
    uint32_t cumulative = 0, chunkRows = 0;
    CHECK(ZK_EachBatchNext(h, &cur, cols, 4, &cumulative, &chunkRows) == 1);
    REQUIRE(chunkRows == 1u);
    REQUIRE(cumulative == 1u);
    REQUIRE(cols[0] != nullptr); // pos 列（with 升序位序：pos=bit0 → 第 0 列）
    REQUIRE(cols[1] != nullptr); // tag 列（bit2 → 第 1 列）
    const Position* posCol = static_cast<const Position*>(cols[0]);
    CHECK(posCol[0].x == 1.5f);
    CHECK(posCol[0].y == 2.5f);
    CHECK(posCol[0].z == 3.5f);
    const Tag* tagCol = static_cast<const Tag*>(cols[1]);
    CHECK(tagCol[0].v == 77u);
    CHECK(ZK_EachBatchNext(h, &cur, cols, 4, &cumulative, &chunkRows) == 0); // 耗尽
    CHECK(cumulative == 1u);

    // Add（vel，零初始化）→ Get 读回 ok 语义 → Set 覆写
    const Position before{9.0f, 9.0f, 9.0f};
    CHECK(ZK_AddComponent(h, e, kCompVelocity, nullptr) == ZK_ABI_OK); // initData 空 = 零初始化
    Velocity vel{};
    uint32_t ok = 0;
    CHECK(ZK_GetComponent(h, e, kCompVelocity, &vel, sizeof(vel), &ok) == ZK_ABI_OK);
    CHECK(ok == 1u);
    CHECK(vel.dx == 0.0f);
    CHECK(vel.dy == 0.0f);
    CHECK(vel.dz == 0.0f);
    CHECK(ZK_SetComponent(h, e, kCompVelocity, &before, sizeof(before)) == ZK_ABI_OK);
    CHECK(ZK_GetComponent(h, e, kCompVelocity, &vel, sizeof(vel), &ok) == ZK_ABI_OK);
    CHECK(ok == 1u);
    CHECK(vel.dx == 9.0f);

    // 缺组件读 → ok = 0（不是错误）
    Big big{};
    ok = 99u;
    CHECK(ZK_GetComponent(h, e, kCompBig, &big, sizeof(big), &ok) == ZK_ABI_OK);
    CHECK(ok == 0u);

    // Destroy 实体 → 实体句柄失效（Set/Get/重复 Destroy 拒绝）
    CHECK(ZK_Destroy(h, e) == ZK_ABI_OK);
    Position posSink{};
    CHECK(ZK_GetComponent(h, e, kCompPosition, &posSink, sizeof(posSink), &ok) == ZK_ABI_OK);
    CHECK(ok == 0u); // 失效实体：ok = 0 分流（不是错误）
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, sizeof(pos)) == ZK_ABI_ERR_STATE);
    CHECK(ZK_Destroy(h, e) == ZK_ABI_ERR_STATE); // 重复销毁
    CHECK(ZK_RemoveComponent(h, e, kCompPosition) == ZK_ABI_ERR_STATE);
    CHECK(ZK_AddComponent(h, e, kCompPosition, nullptr) == ZK_ABI_ERR_STATE);

    // World 句柄销毁后失效
    CHECK(ZK_WorldDestroy(h) == ZK_ABI_OK);
    CHECK(ZK_Spawn(h, lo, hi, &e) == ZK_ABI_ERR_HANDLE);
    ZkWorldStatsAbi stDead{};
    CHECK(ZK_WorldStats(h, &stDead) == ZK_ABI_ERR_HANDLE); // 出参有效才轮到句柄校验
    guard.handle = 0; // 已销毁，RAII 不再二次销毁（重复销毁本就 ERR_HANDLE，仍防呆）
}

// =============================================================================
// 2. 游标跨 chunk
// =============================================================================
TEST_CASE("游标跨 chunk 推进：容量 32（Big 1024B/行）× 100 实体逐 chunk 断言")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    REQUIRE(ZK_RegisterComponent(h, kCompBig, sizeof(Big), alignof(Big)) == ZK_ABI_OK);

    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompBig);
    constexpr uint32_t kCount = 100; // 容量 32 → chunks = 32/32/32/4
    for (uint32_t i = 0; i < kCount; ++i) {
        ZkEntityAbi e{};
        REQUIRE(ZK_Spawn(h, lo, hi, &e) == ZK_ABI_OK);
        Big v;
        v.bytes[0] = static_cast<uint8_t>(i & 0xFF);
        v.bytes[1] = static_cast<uint8_t>((i >> 8) & 0xFF);
        CHECK(ZK_SetComponent(h, e, kCompBig, &v, sizeof(v)) == ZK_ABI_OK);
    }

    ZkEachCursor cur;
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur) == ZK_ABI_OK);
    void* col = nullptr;
    void** colPtrs = &col;
    uint32_t cumulative = 0, chunkRows = 0;

    // chunk 0：32 行，累计 32（行内步长 = 1024B 的数据定位由下方全量回读覆盖；
    // 跨 chunk 指针距离不是契约——chunk 是独立堆分配，只保证行数推进）
    REQUIRE(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 1);
    CHECK(chunkRows == 32u);
    CHECK(cumulative == 32u);
    // chunk 1：32 行，累计 64
    REQUIRE(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 1);
    CHECK(chunkRows == 32u);
    CHECK(cumulative == 64u);
    // chunk 2：32 行，累计 96
    REQUIRE(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 1);
    CHECK(chunkRows == 32u);
    CHECK(cumulative == 96u);
    // chunk 3（尾批）：4 行，累计 100
    REQUIRE(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 1);
    CHECK(chunkRows == 4u);
    CHECK(cumulative == 100u);
    // 耗尽 + 幂等
    CHECK(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 0);
    CHECK(chunkRows == 0u);
    CHECK(cumulative == 100u);
    CHECK(ZK_EachBatchNext(h, &cur, colPtrs, 1, &cumulative, &chunkRows) == 0);
    CHECK(cumulative == 100u);

    // 全量回读数据完整性（跨 4 chunk 的 SoA 定位）
    ZkEachCursor cur2;
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur2) == ZK_ABI_OK);
    uint32_t rows = 0;
    while (ZK_EachBatchNext(h, &cur2, colPtrs, 1, &cumulative, &chunkRows) == 1) {
        const uint8_t* p = static_cast<const uint8_t*>(col);
        for (uint32_t r = 0; r < chunkRows; ++r) {
            const uint32_t i = rows + r;
            CHECK(p[r * sizeof(Big)] == (i & 0xFF));
            CHECK(p[r * sizeof(Big) + 1] == ((i >> 8) & 0xFF));
        }
        rows += chunkRows;
    }
    CHECK(rows == kCount);
    CHECK(cumulative == kCount);
}

TEST_CASE("游标三列 1000 实体全量求和（28B/行 → 容量 585 → chunks 585/415）与双 archetype 过滤")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    RegisterAll(h);

    uint64_t posVel = 0, hiZero = 0;
    MaskSet(posVel, hiZero, kCompPosition);
    MaskSet(posVel, hiZero, kCompVelocity);
    uint64_t posVelTag = posVel;
    MaskSet(posVelTag, hiZero, kCompTag);

    constexpr uint32_t kCount = 1000; // 28B/行 → 容量 = 16384/28 = 585 → chunks 585/415
    for (uint32_t i = 0; i < kCount; ++i) {
        ZkEntityAbi e{};
        REQUIRE(ZK_Spawn(h, posVelTag, hiZero, &e) == ZK_ABI_OK);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        Velocity v{static_cast<float>(i), 1.0f, 0.0f};
        Tag t{i};
        CHECK(ZK_SetComponent(h, e, kCompPosition, &p, sizeof(p)) == ZK_ABI_OK);
        CHECK(ZK_SetComponent(h, e, kCompVelocity, &v, sizeof(v)) == ZK_ABI_OK);
        CHECK(ZK_SetComponent(h, e, kCompTag, &t, sizeof(t)) == ZK_ABI_OK);
    }
    // 第二 archetype（pos|vel，10 实体）验证 {pos,vel} 查询的跨 archetype 过滤
    for (uint32_t i = 0; i < 10; ++i) {
        ZkEntityAbi e{};
        REQUIRE(ZK_Spawn(h, posVel, hiZero, &e) == ZK_ABI_OK);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        Velocity v{static_cast<float>(i), 2.0f, 0.0f};
        CHECK(ZK_SetComponent(h, e, kCompPosition, &p, sizeof(p)) == ZK_ABI_OK);
        CHECK(ZK_SetComponent(h, e, kCompVelocity, &v, sizeof(v)) == ZK_ABI_OK);
    }

    // {pos|vel|tag}：命中 1000 实体（3 chunk），三列求和全对
    ZkEachCursor cur;
    REQUIRE(ZK_EachBatchBegin(h, posVelTag, hiZero, &cur) == ZK_ABI_OK);
    void* cols[4] = {};
    uint32_t cumulative = 0, chunkRows = 0;
    uint32_t calls = 0;
    uint32_t rows = 0;
    double sumPos = 0.0, sumVel = 0.0;
    uint64_t sumTag = 0;
    while (ZK_EachBatchNext(h, &cur, cols, 4, &cumulative, &chunkRows) == 1) {
        const float* pos = static_cast<const float*>(cols[0]);
        const float* vel = static_cast<const float*>(cols[1]);
        const Tag* tag = static_cast<const Tag*>(cols[2]);
        for (uint32_t r = 0; r < chunkRows; ++r) {
            sumPos += pos[3u * r];
            sumVel += vel[3u * r];
            sumTag += tag[r].v;
        }
        rows += chunkRows;
        ++calls;
    }
    CHECK(calls == 2u); // 容量 585：1000 = 585 + 415
    CHECK(rows == kCount);
    CHECK(cumulative == kCount);
    const double expected = static_cast<double>(kCount) * (kCount - 1) / 2.0;
    CHECK(sumPos == expected);
    CHECK(sumVel == expected);
    CHECK(sumTag == static_cast<uint64_t>(kCount) * (kCount - 1) / 2);

    // {pos|vel}：命中 1000 + 10 = 1010 行（跨两个 archetype）
    ZkEachCursor cur2;
    REQUIRE(ZK_EachBatchBegin(h, posVel, hiZero, &cur2) == ZK_ABI_OK);
    rows = 0;
    double sumVel2 = 0.0;
    while (ZK_EachBatchNext(h, &cur2, cols, 4, &cumulative, &chunkRows) == 1) {
        const float* vel = static_cast<const float*>(cols[1]);
        for (uint32_t r = 0; r < chunkRows; ++r) {
            sumVel2 += vel[3u * r];
        }
        rows += chunkRows;
    }
    CHECK(rows == kCount + 10u);
    CHECK(sumVel2 == expected + 45.0); // 第二 archetype dx = 0..9
    CHECK(cumulative == kCount + 10u);
}

// =============================================================================
// 3. 游标防御
// =============================================================================
TEST_CASE("游标防御：maxCols 不足 / 游标损坏 / 空出参 / 空掩码遍历 / 布局变更作废")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    RegisterAll(h);

    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompPosition);
    MaskSet(lo, hi, kCompTag);
    for (uint32_t i = 0; i < 5; ++i) {
        ZkEntityAbi e{};
        REQUIRE(ZK_Spawn(h, lo, hi, &e) == ZK_ABI_OK);
    }

    ZkEachCursor cur;
    void* cols[4] = {};
    uint32_t cumulative = 0, chunkRows = 0;

    // maxCols（1）< 查询位列数（2）→ 拒绝
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur) == ZK_ABI_OK);
    CHECK(ZK_EachBatchNext(h, &cur, cols, 1, &cumulative, &chunkRows) == ZK_ABI_ERR_ARGS);

    // 游标 state 损坏 → 拒绝
    cur.state = 42u;
    CHECK(ZK_EachBatchNext(h, &cur, cols, 4, &cumulative, &chunkRows) == ZK_ABI_ERR_ARGS);
    // 游标 archIndex 损坏 → 拒绝
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur) == ZK_ABI_OK);
    cur.archIndex = 0xFFFFFFu;
    CHECK(ZK_EachBatchNext(h, &cur, cols, 4, &cumulative, &chunkRows) == ZK_ABI_ERR_ARGS);

    // colPtrs 空 + 查询有列 → 拒绝
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &cur) == ZK_ABI_OK);
    CHECK(ZK_EachBatchNext(h, &cur, nullptr, 4, &cumulative, &chunkRows) == ZK_ABI_ERR_NULL);

    // 空掩码：遍历全部 archetype（列数 0，允许 colPtrs 空）
    ZkEachCursor curEmpty;
    REQUIRE(ZK_EachBatchBegin(h, 0, 0, &curEmpty) == ZK_ABI_OK);
    CHECK(ZK_EachBatchNext(h, &curEmpty, nullptr, 0, &cumulative, &chunkRows) == 1);
    CHECK(chunkRows == 5u);
    CHECK(cumulative == 5u);
    CHECK(ZK_EachBatchNext(h, &curEmpty, nullptr, 0, &cumulative, &chunkRows) == 0);
    CHECK(cumulative == 5u);

    // Begin 后布局变更 → Next 作废（ERR_STATE），且幂等不再交付
    ZkEachCursor curMut;
    REQUIRE(ZK_EachBatchBegin(h, lo, hi, &curMut) == ZK_ABI_OK);
    ZkEntityAbi victim{};
    REQUIRE(ZK_Spawn(h, lo, hi, &victim) == ZK_ABI_OK);
    CHECK(ZK_Destroy(h, victim) == ZK_ABI_OK); // 布局变更（代际 +1、swap-back）
    CHECK(ZK_EachBatchNext(h, &curMut, cols, 4, &cumulative, &chunkRows) == ZK_ABI_ERR_STATE);
    CHECK(ZK_EachBatchNext(h, &curMut, cols, 4, &cumulative, &chunkRows) == 0); // 幂等耗尽
}

// =============================================================================
// 4. 坏句柄 / 坏 size
// =============================================================================
TEST_CASE("坏 World 句柄族：0 / 越界槽位 / 空代际 / 代际失配 / 销毁后")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);

    ZkEntityAbi e{};
    const ZkWorldHandle dead = h;
    REQUIRE(ZK_WorldDestroy(h) == ZK_ABI_OK);
    guard.handle = 0;

    // 全函数对坏句柄一致拒绝（0 / 越界 / 代际 0 / 失配）
    CHECK(ZK_WorldDestroy(0) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_WorldDestroy(0xFFFFFFFFFFFFFFFFull) == ZK_ABI_ERR_HANDLE); // 槽位越界
    CHECK(ZK_RegisterComponent(0, 0, 4, 4) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_RegisterComponent(dead, 0, 4, 4) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_Spawn(0, 1, 0, &e) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_Spawn(dead, 1, 0, &e) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_Destroy(0, e) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_SetComponent(0, e, 0, &e, 4) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_SetComponent(dead, e, 0, &e, 4) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_GetComponent(0, e, 0, &e, 4, &e.index) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_AddComponent(0, e, 0, nullptr) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_RemoveComponent(0, e, 0) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_EachBatchBegin(0, 1, 0, nullptr) == ZK_ABI_ERR_NULL); // outCursor 先判空
    ZkEachCursor cur;
    CHECK(ZK_EachBatchBegin(0, 1, 0, &cur) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_EachBatchBegin(dead, 1, 0, &cur) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_EachBatchNext(0, &cur, nullptr, 0, &e.index, &e.generation) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_EachBatchNext(dead, &cur, nullptr, 0, &e.index, &e.generation) == ZK_ABI_ERR_HANDLE);
    ZkWorldStatsAbi stats;
    CHECK(ZK_WorldStats(0, &stats) == ZK_ABI_ERR_HANDLE);
    CHECK(ZK_WorldStats(dead, &stats) == ZK_ABI_ERR_HANDLE);

    // 代际失配：同槽位重建 World 后旧句柄失效
    ZkWorldHandle h2 = 0;
    REQUIRE(ZK_WorldCreate(&h2) == ZK_ABI_OK);
    WorldGuard guard2(h2);
    if ((h2 >> 32) == (dead >> 32)) {
        // 复用了同一槽位（实现不保证，但发生时校验代际防线）
        CHECK(h2 != dead);
        CHECK(ZK_RegisterComponent(h2, 0, 4, 4) == ZK_ABI_OK);
        CHECK(ZK_Spawn(dead, 1, 0, &e) == ZK_ABI_ERR_HANDLE); // 旧代际句柄
    } else {
        CHECK(ZK_Spawn(dead, 1, 0, &e) == ZK_ABI_ERR_HANDLE);
    }
}

TEST_CASE("坏 size 拒绝：Set/Get 与注册字节数不一致（含坏实体句柄分流）")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    RegisterAll(h);

    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompPosition);
    ZkEntityAbi e{};
    REQUIRE(ZK_Spawn(h, lo, hi, &e) == ZK_ABI_OK);

    Position pos{1.0f, 2.0f, 3.0f};
    // size 偏差（多 4 / 少 4 / 0）
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, sizeof(pos) + 4) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, sizeof(pos) - 4) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, 0) == ZK_ABI_ERR_ARGS);
    uint32_t ok = 0;
    CHECK(ZK_GetComponent(h, e, kCompPosition, &pos, sizeof(pos) + 4, &ok) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_GetComponent(h, e, kCompPosition, &pos, 0, &ok) == ZK_ABI_ERR_ARGS);
    // size 错误优先于实体状态错误（校验顺序：NULL→HANDLE→RANGE→ARGS→STATE）
    ZkEntityAbi ghost{0xdeadbeefu, 0u};
    CHECK(ZK_SetComponent(h, ghost, kCompPosition, &pos, 3) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_SetComponent(h, ghost, kCompPosition, &pos, sizeof(pos)) == ZK_ABI_ERR_STATE);

    // 正确 size 回到 OK
    CHECK(ZK_SetComponent(h, e, kCompPosition, &pos, sizeof(pos)) == ZK_ABI_OK);
    Position back{};
    CHECK(ZK_GetComponent(h, e, kCompPosition, &back, sizeof(back), &ok) == ZK_ABI_OK);
    CHECK(ok == 1u);
    CHECK(back.x == 1.0f);
    CHECK(back.y == 2.0f);
    CHECK(back.z == 3.0f);
}

TEST_CASE("坏实体句柄族：未生成 index / 代际失配 / 已销毁 / 缺组件分流")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    RegisterAll(h);

    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompPosition);
    MaskSet(lo, hi, kCompTag);
    ZkEntityAbi a{};
    REQUIRE(ZK_Spawn(h, lo, hi, &a) == ZK_ABI_OK);
    ZkEntityAbi b{};
    REQUIRE(ZK_Spawn(h, lo, hi, &b) == ZK_ABI_OK);
    REQUIRE(ZK_Destroy(h, b) == ZK_ABI_OK);

    Tag tag{1u};
    // 未生成 index（越出槽位表）
    ZkEntityAbi far_{}; far_.index = 0x7FFFFFFFu; far_.generation = 0u;
    CHECK(ZK_SetComponent(h, far_, kCompTag, &tag, sizeof(tag)) == ZK_ABI_ERR_STATE);
    CHECK(ZK_Destroy(h, far_) == ZK_ABI_ERR_STATE);
    CHECK(ZK_AddComponent(h, far_, kCompTag, nullptr) == ZK_ABI_ERR_STATE);
    CHECK(ZK_RemoveComponent(h, far_, kCompTag) == ZK_ABI_ERR_STATE);
    // 代际失配（b 销毁后同槽位重建才成立——直接用旧代际句柄）
    CHECK(ZK_SetComponent(h, b, kCompTag, &tag, sizeof(tag)) == ZK_ABI_ERR_STATE);
    CHECK(ZK_Destroy(h, b) == ZK_ABI_ERR_STATE);
    // 实体不含该组件（a 无 vel）
    Velocity vel{1.0f, 2.0f, 3.0f};
    CHECK(ZK_SetComponent(h, a, kCompVelocity, &vel, sizeof(vel)) == ZK_ABI_ERR_STATE);
    CHECK(ZK_RemoveComponent(h, a, kCompVelocity) == ZK_ABI_ERR_STATE);
    // 组件未注册 → RANGE（优先于实体状态）
    CHECK(ZK_SetComponent(h, a, 50u, &tag, 4) == ZK_ABI_ERR_RANGE);
    CHECK(ZK_GetComponent(h, a, 50u, &tag, 4, &tag.v) == ZK_ABI_ERR_RANGE);
    CHECK(ZK_AddComponent(h, a, 50u, nullptr) == ZK_ABI_ERR_RANGE);
    CHECK(ZK_RemoveComponent(h, a, 50u) == ZK_ABI_ERR_RANGE);
    // compId 越界（128）
    CHECK(ZK_SetComponent(h, a, 128u, &tag, 4) == ZK_ABI_ERR_RANGE);
    // Add 幂等拒绝：已持有 tag
    CHECK(ZK_AddComponent(h, a, kCompTag, nullptr) == ZK_ABI_ERR_STATE);
    // Add 成功路径 + initData 非空
    CHECK(ZK_AddComponent(h, a, kCompVelocity, &vel) == ZK_ABI_OK);
    Velocity back{};
    uint32_t ok = 0;
    CHECK(ZK_GetComponent(h, a, kCompVelocity, &back, sizeof(back), &ok) == ZK_ABI_OK);
    CHECK(ok == 1u);
    CHECK(back.dx == 1.0f);
    CHECK(back.dy == 2.0f);
    CHECK(back.dz == 3.0f);
}

TEST_CASE("注册参数拒绝：id 越界 / size 0 / 对齐非 2 幂 / 重复注册 / 空出参")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);

    CHECK(ZK_RegisterComponent(h, 128u, 4, 4) == ZK_ABI_ERR_RANGE);
    CHECK(ZK_RegisterComponent(h, 0u, 0, 4) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_RegisterComponent(h, 0u, 4, 0) == ZK_ABI_ERR_ARGS);
    CHECK(ZK_RegisterComponent(h, 0u, 4, 3) == ZK_ABI_ERR_ARGS); // 非 2 的幂
    CHECK(ZK_RegisterComponent(h, 0u, 4, 4) == ZK_ABI_OK);
    CHECK(ZK_RegisterComponent(h, 0u, 8, 8) == ZK_ABI_ERR_STATE); // 重复注册

    ZkWorldHandle out = 0;
    CHECK(ZK_WorldCreate(nullptr) == ZK_ABI_ERR_NULL);
    (void)out;
    ZkEntityAbi e{};
    CHECK(ZK_Spawn(h, 1, 0, nullptr) == ZK_ABI_ERR_NULL);
    CHECK(ZK_SetComponent(h, e, 0u, nullptr, 4) == ZK_ABI_ERR_NULL);
    CHECK(ZK_GetComponent(h, e, 0u, nullptr, 4, &e.index) == ZK_ABI_ERR_NULL);
    CHECK(ZK_GetComponent(h, e, 0u, &e.index, 4, nullptr) == ZK_ABI_ERR_NULL);
    CHECK(ZK_WorldStats(h, nullptr) == ZK_ABI_ERR_NULL);
}

TEST_CASE("World 表满（8）与销毁释放槽位；代际随销毁单调")
{
    ZkWorldHandle hs[8] = {};
    for (uint32_t i = 0; i < 8; ++i) {
        REQUIRE(ZK_WorldCreate(&hs[i]) == ZK_ABI_OK);
        CHECK(hs[i] != 0);
    }
    ZkWorldHandle extra = 0;
    CHECK(ZK_WorldCreate(&extra) == ZK_ABI_ERR_FULL);

    // 全部 8 句柄互不相同
    for (uint32_t i = 0; i < 8; ++i) {
        for (uint32_t j = i + 1; j < 8; ++j) {
            CHECK(hs[i] != hs[j]);
        }
    }
    // 销毁一个 → 空位可再创建；新句柄 != 旧句柄（代际 +1）
    const ZkWorldHandle first = hs[3];
    CHECK(ZK_WorldDestroy(first) == ZK_ABI_OK);
    REQUIRE(ZK_WorldCreate(&extra) == ZK_ABI_OK);
    CHECK(extra != first);
    CHECK(ZK_WorldDestroy(extra) == ZK_ABI_OK);

    // 清场（WorldGuard 语义的平铺版）
    for (uint32_t i = 0; i < 8; ++i) {
        if (i != 3) {
            CHECK(ZK_WorldDestroy(hs[i]) == ZK_ABI_OK);
        }
    }
}

TEST_CASE("ZK_WorldStats：实体数 / chunk 数 / 迁移计数")
{
    ZkWorldHandle h = 0;
    REQUIRE(ZK_WorldCreate(&h) == ZK_ABI_OK);
    WorldGuard guard(h);
    RegisterAll(h);

    uint64_t lo = 0, hi = 0;
    MaskSet(lo, hi, kCompPosition);
    MaskSet(lo, hi, kCompTag);
    std::vector<ZkEntityAbi> ents(300);
    for (uint32_t i = 0; i < 300; ++i) {
        REQUIRE(ZK_Spawn(h, lo, hi, &ents[i]) == ZK_ABI_OK);
    }
    ZkWorldStatsAbi st{};
    REQUIRE(ZK_WorldStats(h, &st) == ZK_ABI_OK);
    CHECK(st.entityCount == 300u);
    CHECK(st.chunkCount >= 1u); // 16B/行 → 容量 1024 → 1 chunk
    CHECK(st.migrations == 0ull);

    // Add 迁移 300 次（pos|tag → pos|tag|vel）
    for (uint32_t i = 0; i < 300; ++i) {
        CHECK(ZK_AddComponent(h, ents[i], kCompVelocity, nullptr) == ZK_ABI_OK);
    }
    REQUIRE(ZK_WorldStats(h, &st) == ZK_ABI_OK);
    CHECK(st.entityCount == 300u);
    CHECK(st.migrations == 300ull);
    CHECK(st.rowsMoved >= 300ull); // 迁移实体行 + swap-back 收行
    CHECK(st.bytesMoved >= 300ull * (12u + 4u)); // 共享列 ≥ 一列拷贝

    // Destroy 后实体数下降；Remove 迁移计数继续累加
    CHECK(ZK_Destroy(h, ents[0]) == ZK_ABI_OK);
    CHECK(ZK_RemoveComponent(h, ents[1], kCompVelocity) == ZK_ABI_OK);
    REQUIRE(ZK_WorldStats(h, &st) == ZK_ABI_OK);
    CHECK(st.entityCount == 299u); // 300 - 1 销毁（Remove 不改实体数）
    CHECK(st.migrations == 301ull);
}
