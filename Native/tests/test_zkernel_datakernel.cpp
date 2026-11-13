// =============================================================================
// test_zkernel_datakernel.cpp —— zkernel 数据内核单测（TASK-B-R1-01）
//
// 覆盖验收清单：
//   1. 掩码位运算与 AND 查询过滤
//   2. Spawn/Destroy 代际复用（销毁后旧句柄访问被拒）
//   3. 跨 chunk 边界（实体数超过单 chunk 容量×2）
//   4. 多组件列读写回正确性
//   5. 组件列槽位序 = 掩码低位计数（直接构造已知掩码断言）
//   6. ZkBitsBelow 热路径周期实测断言（TASK-B-KERNEL-01：cycles/op < 40，
//      无 POPCNT/TZCNT 机器 SKIP + MISSING 说明，不许静默通过）
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <intrin.h> // __cpuid / __cpuidex（TASK-B-KERNEL-01 指令集能力检测）
#include <vector>

#include "zkernel/zkernel.h"

namespace {

struct Position { float x, y, z; };    // 组件 0：12B / align 4
struct Velocity { float dx, dy, dz; }; // 组件 1：12B / align 4
struct Tag { uint32_t v; };            // 组件 2：4B / align 4
struct Big { uint8_t bytes[1024]; };   // 组件 3：1024B / align 1 → 单列容量 32

constexpr uint32_t kCompPosition = 0;
constexpr uint32_t kCompVelocity = 1;
constexpr uint32_t kCompTag = 2;
constexpr uint32_t kCompBig = 3;

} // namespace

TEST_CASE("Mask128 位运算基础")
{
    Mask128 m{};
    CHECK(m.IsZero());
    CHECK(m.PopCount() == 0u);

    m.Set(0);
    m.Set(63);
    m.Set(64);
    m.Set(127);
    CHECK(m.Has(0));
    CHECK(m.Has(63));
    CHECK(m.Has(64));
    CHECK(m.Has(127));
    CHECK(!m.Has(1));
    CHECK(!m.Has(126));
    CHECK(m.PopCount() == 4u);

    m.Unset(63);
    CHECK(!m.Has(63));
    CHECK(m.PopCount() == 3u);
    m.Unset(63); // 幂等
    CHECK(m.PopCount() == 3u);

    Mask128 n{};
    n.Set(0);
    n.Set(64);
    n.Set(127);
    CHECK(m == n);
    n.Set(1);
    CHECK(m != n);
}

TEST_CASE("Mask128 AND 包含过滤（查询语义）")
{
    Mask128 posVel{};
    posVel.Set(kCompPosition);
    posVel.Set(kCompVelocity);
    Mask128 posVelTag = posVel;
    posVelTag.Set(kCompTag);
    Mask128 posOnly{};
    posOnly.Set(kCompPosition);
    Mask128 none{};

    CHECK(posVelTag.Contains(posVel));   // 超集包含
    CHECK(posVelTag.Contains(posVelTag)); // 自身
    CHECK(!posVel.Contains(posVelTag));  // 子集不含超集
    CHECK(posVel.Contains(posOnly));
    CHECK(posVel.Contains(none));        // 任意掩码包含空查询
}

TEST_CASE("ZkBitsBelow 槽位序 = 掩码低位置位计数")
{
    Mask128 m{};
    m.Set(3);
    m.Set(7);
    m.Set(100);
    CHECK(ZkBitsBelow(m, 3) == 0u);
    CHECK(ZkBitsBelow(m, 7) == 1u);
    CHECK(ZkBitsBelow(m, 100) == 2u);

    m.Set(63); // 在 7 与 100 之间插入低位，100 的槽位后移
    CHECK(ZkBitsBelow(m, 3) == 0u);
    CHECK(ZkBitsBelow(m, 7) == 1u);
    CHECK(ZkBitsBelow(m, 63) == 2u);
    CHECK(ZkBitsBelow(m, 100) == 3u);

    uint32_t bits[128];
    const uint32_t n = ZkListBits(m, bits); // TZCNT 升序枚举
    CHECK(n == 4u);
    CHECK(bits[0] == 3u);
    CHECK(bits[1] == 7u);
    CHECK(bits[2] == 63u);
    CHECK(bits[3] == 100u);
}

TEST_CASE("RegisterComponent 参数校验")
{
    ZkWorld world;
    CHECK(world.RegisterComponent(kCompPosition, 12u, 4u));
    CHECK(!world.RegisterComponent(kCompPosition, 12u, 4u));  // 重复注册
    CHECK(!world.RegisterComponent(128u, 12u, 4u));           // id 越界（0..127）
    CHECK(!world.RegisterComponent(kCompVelocity, 0u, 4u));   // 尺寸必须 > 0
    CHECK(!world.RegisterComponent(kCompVelocity, 12u, 0u));  // 对齐非 2 的幂
    CHECK(!world.RegisterComponent(kCompVelocity, 12u, 3u));
}

TEST_CASE("Spawn/GetComponent 多组件读写回与列布局")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    Mask128 mask{};
    mask.Set(kCompPosition);
    mask.Set(kCompVelocity);
    mask.Set(kCompTag);
    ZkEntity e = world.Spawn(mask);
    CHECK(e != ZK_INVALID_ENTITY);
    CHECK(world.EntityCount() == 1u);
    CHECK(world.ArchetypeCount() == 1u);

    void* pPos = world.GetComponent(e, kCompPosition);
    void* pVel = world.GetComponent(e, kCompVelocity);
    void* pTag = world.GetComponent(e, kCompTag);
    REQUIRE(pPos != nullptr);
    REQUIRE(pVel != nullptr);
    REQUIRE(pTag != nullptr);

    // 列起始按槽位序（掩码升序位序）单调递增；且满足组件对齐
    CHECK(reinterpret_cast<uintptr_t>(pPos) < reinterpret_cast<uintptr_t>(pVel));
    CHECK(reinterpret_cast<uintptr_t>(pVel) < reinterpret_cast<uintptr_t>(pTag));
    CHECK(reinterpret_cast<uintptr_t>(pPos) % alignof(Position) == 0u);
    CHECK(reinterpret_cast<uintptr_t>(pVel) % alignof(Velocity) == 0u);
    CHECK(reinterpret_cast<uintptr_t>(pTag) % alignof(Tag) == 0u);

    // 读写回
    Position pos{1.0f, 2.0f, 3.0f};
    Velocity vel{0.5f, -1.5f, 2.5f};
    Tag tag{42u};
    std::memcpy(pPos, &pos, sizeof(pos));
    std::memcpy(pVel, &vel, sizeof(vel));
    std::memcpy(pTag, &tag, sizeof(tag));

    Position posBack{};
    Velocity velBack{};
    Tag tagBack{};
    std::memcpy(&posBack, world.GetComponent(e, kCompPosition), sizeof(posBack));
    std::memcpy(&velBack, world.GetComponent(e, kCompVelocity), sizeof(velBack));
    std::memcpy(&tagBack, world.GetComponent(e, kCompTag), sizeof(tagBack));
    CHECK(posBack.x == 1.0f);
    CHECK(posBack.y == 2.0f);
    CHECK(posBack.z == 3.0f);
    CHECK(velBack.dx == 0.5f);
    CHECK(velBack.dy == -1.5f);
    CHECK(velBack.dz == 2.5f);
    CHECK(tagBack.v == 42u);

    // 掩码不含的组件返回 nullptr
    CHECK(world.GetComponent(e, kCompBig) == nullptr);
}

TEST_CASE("SoA 行步长 = 组件大小（同 chunk 内行连续）")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag))); // 4B → 容量 4096
    Mask128 mask{};
    mask.Set(kCompTag);
    ZkEntity e0 = world.Spawn(mask);
    ZkEntity e1 = world.Spawn(mask);
    uint8_t* p0 = static_cast<uint8_t*>(world.GetComponent(e0, kCompTag));
    uint8_t* p1 = static_cast<uint8_t*>(world.GetComponent(e1, kCompTag));
    REQUIRE(p0 != nullptr);
    REQUIRE(p1 != nullptr);
    CHECK(p1 - p0 == static_cast<std::ptrdiff_t>(sizeof(Tag)));
}

TEST_CASE("Destroy 代际复用：旧句柄被拒、swap-back 数据跟随")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    Mask128 mask{};
    mask.Set(kCompTag);

    ZkEntity a = world.Spawn(mask);
    ZkEntity b = world.Spawn(mask);
    ZkEntity c = world.Spawn(mask);
    *static_cast<Tag*>(world.GetComponent(a, kCompTag)) = Tag{10u};
    *static_cast<Tag*>(world.GetComponent(b, kCompTag)) = Tag{20u};
    *static_cast<Tag*>(world.GetComponent(c, kCompTag)) = Tag{30u};
    CHECK(world.EntityCount() == 3u);

    // 销毁中间实体 b：尾行（c）swap-back 搬入洞行
    ZkEntity bOld = b;
    CHECK(world.Destroy(bOld));
    CHECK(world.EntityCount() == 2u);

    // 旧句柄访问被拒
    CHECK(world.GetComponent(bOld, kCompTag) == nullptr);
    CHECK(!world.Destroy(bOld)); // 重复销毁失败

    // 幸存实体数据完好（c 经 swap-back 搬行，句柄仍有效）
    CHECK(c != ZK_INVALID_ENTITY);
    const Tag* tc = static_cast<const Tag*>(world.GetComponent(c, kCompTag));
    REQUIRE(tc != nullptr);
    CHECK(tc->v == 30u);
    const Tag* ta = static_cast<const Tag*>(world.GetComponent(a, kCompTag));
    REQUIRE(ta != nullptr);
    CHECK(ta->v == 10u);

    // 复用槽位：新实体 index 复用、代际 +1
    ZkEntity d = world.Spawn(mask);
    CHECK(d.index == bOld.index);
    CHECK(d.generation == bOld.generation + 1u);
    CHECK(world.EntityCount() == 3u);
    *static_cast<Tag*>(world.GetComponent(d, kCompTag)) = Tag{40u};
    CHECK(static_cast<const Tag*>(world.GetComponent(d, kCompTag))->v == 40u);
}

TEST_CASE("跨 chunk 边界：容量 32 × 2 以上实体读写与 swap-back 一致性")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompBig, sizeof(Big), alignof(Big))); // 1024B → 容量 32
    Mask128 mask{};
    mask.Set(kCompBig);

    constexpr uint32_t kCount = 100; // > 容量(32) × 2 → 跨 4 个 chunk
    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
        REQUIRE(ents[i] != ZK_INVALID_ENTITY);
        uint32_t v = i + 1u;
        std::memcpy(static_cast<Big*>(world.GetComponent(ents[i], kCompBig))->bytes, &v, sizeof(v));
    }
    CHECK(world.EntityCount() == kCount);
    CHECK(world.ArchetypeCount() == 1u);

    // 全量回读（覆盖跨 chunk 的 GetComponent 定位）
    for (uint32_t i = 0; i < kCount; ++i) {
        uint32_t v = 0;
        std::memcpy(&v, static_cast<Big*>(world.GetComponent(ents[i], kCompBig))->bytes, sizeof(v));
        CHECK(v == i + 1u);
    }

    // 销毁 40 个中间实体（横跨 chunk0/chunk1），触发多次跨 chunk swap-back
    for (uint32_t i = 10; i < 50; ++i) {
        CHECK(world.Destroy(ents[i]));
    }
    CHECK(world.EntityCount() == kCount - 40u);

    // Each 收集幸存值集合
    std::vector<uint32_t> alive;
    world.Each(mask, [&](void** comps, uint32_t rowCount) {
        uint8_t* col = static_cast<uint8_t*>(comps[0]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            uint32_t v = 0;
            std::memcpy(&v, col + static_cast<size_t>(r) * sizeof(Big), sizeof(v));
            alive.push_back(v);
        }
    });
    REQUIRE(alive.size() == kCount - 40u);
    std::sort(alive.begin(), alive.end());
    for (uint32_t k = 0; k < alive.size(); ++k) {
        const uint32_t expect = (k < 10u) ? k + 1u : k + 41u; // {1..10} ∪ {51..100}
        CHECK(alive[k] == expect);
    }

    // 幸存句柄 spot check（含被多次 swap-back 搬行的实体）
    uint32_t v50 = 0;
    std::memcpy(&v50, static_cast<Big*>(world.GetComponent(ents[50], kCompBig))->bytes, sizeof(v50));
    CHECK(v50 == 51u);
    uint32_t v99 = 0;
    std::memcpy(&v99, static_cast<Big*>(world.GetComponent(ents[99], kCompBig))->bytes, sizeof(v99));
    CHECK(v99 == 100u);
}

TEST_CASE("Each 掩码过滤与多 archetype 批量遍历")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    Mask128 mPosVel{};
    mPosVel.Set(kCompPosition);
    mPosVel.Set(kCompVelocity);
    Mask128 mPosVelTag = mPosVel;
    mPosVelTag.Set(kCompTag);
    Mask128 mPos{};
    mPos.Set(kCompPosition);

    constexpr uint32_t kA = 10, kB = 7, kC = 5; // A={pos,vel} B={pos,vel,tag} C={pos}
    for (uint32_t i = 0; i < kA; ++i) {
        ZkEntity e = world.Spawn(mPosVel);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        std::memcpy(world.GetComponent(e, kCompPosition), &p, sizeof(p));
    }
    for (uint32_t i = 0; i < kB; ++i) {
        ZkEntity e = world.Spawn(mPosVelTag);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        Velocity v{1.0f, 0.0f, 0.0f};
        Tag t{i};
        std::memcpy(world.GetComponent(e, kCompPosition), &p, sizeof(p));
        std::memcpy(world.GetComponent(e, kCompVelocity), &v, sizeof(v));
        std::memcpy(world.GetComponent(e, kCompTag), &t, sizeof(t));
    }
    for (uint32_t i = 0; i < kC; ++i) {
        ZkEntity e = world.Spawn(mPos);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        std::memcpy(world.GetComponent(e, kCompPosition), &p, sizeof(p));
    }
    CHECK(world.ArchetypeCount() == 3u);
    CHECK(world.EntityCount() == kA + kB + kC);

    // {pos,vel}：命中 A 与 B（17 行）；pos 列升序为 comps[0]
    uint32_t rows = 0;
    double sumX = 0.0;
    world.Each(mPosVel, [&](void** comps, uint32_t rowCount) {
        const float* pos = static_cast<const float*>(comps[0]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumX += pos[3u * r]; // 仅断言写过的 pos 列（A 的 vel 列未初始化，不作断言）
        }
        rows += rowCount;
    });
    CHECK(rows == kA + kB);
    CHECK(sumX == static_cast<double>((kA - 1) * kA / 2) + static_cast<double>((kB - 1) * kB / 2));

    // {pos,vel,tag}：仅命中 B（7 行）
    uint32_t rowsB = 0;
    double sumTag = 0.0;
    world.Each(mPosVelTag, [&](void** comps, uint32_t rowCount) {
        const Tag* tag = static_cast<const Tag*>(comps[2]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumTag += tag[r].v;
        }
        rowsB += rowCount;
    });
    CHECK(rowsB == kB);
    CHECK(sumTag == static_cast<double>((kB - 1) * kB / 2));

    // {pos}：命中全部三个 archetype（22 行）
    uint32_t rowsAll = 0;
    world.Each(mPos, [&](void** /*comps*/, uint32_t rowCount) { rowsAll += rowCount; });
    CHECK(rowsAll == kA + kB + kC);

    // 空查询掩码命中所有 archetype（含可能存在的空 archetype）
    uint32_t rowsNone = 0;
    world.Each(Mask128{}, [&](void** /*comps*/, uint32_t rowCount) { rowsNone += rowCount; });
    CHECK(rowsNone == kA + kB + kC);
}

TEST_CASE("空掩码实体与 Spawn 失败路径")
{
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));

    // 空掩码合法：无组件列，纯实体计数
    for (uint32_t i = 0; i < 5u; ++i) {
        CHECK(world.Spawn(Mask128{}) != ZK_INVALID_ENTITY);
    }
    CHECK(world.EntityCount() == 5u);
    CHECK(world.ArchetypeCount() == 1u);

    // 空查询遍历空掩码 archetype
    uint32_t rows = 0;
    world.Each(Mask128{}, [&](void** /*comps*/, uint32_t rowCount) { rows += rowCount; });
    CHECK(rows == 5u);

    // 未注册组件 → 无效句柄
    Mask128 bad{};
    bad.Set(50u);
    CHECK(world.Spawn(bad) == ZK_INVALID_ENTITY);

    // w[2]/w[3] 非零（组件位越界）→ 无效句柄
    Mask128 wide{};
    wide.w[2] = 1ull;
    CHECK(world.Spawn(wide) == ZK_INVALID_ENTITY);
}

TEST_CASE("多组件跨 chunk（容量 455 × 2 以上）读写回")
{
    // 3 组件共 28B/行 → 容量 = 16384/28 = 455；1000 实体跨 3 chunk
    ZkWorld world;
    REQUIRE(world.RegisterComponent(kCompPosition, sizeof(Position), alignof(Position)));
    REQUIRE(world.RegisterComponent(kCompVelocity, sizeof(Velocity), alignof(Velocity)));
    REQUIRE(world.RegisterComponent(kCompTag, sizeof(Tag), alignof(Tag)));
    Mask128 mask{};
    mask.Set(kCompPosition);
    mask.Set(kCompVelocity);
    mask.Set(kCompTag);

    constexpr uint32_t kCount = 1000;
    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
        Position p{static_cast<float>(i), 0.0f, 0.0f};
        Velocity v{static_cast<float>(i), 1.0f, 0.0f};
        Tag t{i};
        std::memcpy(world.GetComponent(ents[i], kCompPosition), &p, sizeof(p));
        std::memcpy(world.GetComponent(ents[i], kCompVelocity), &v, sizeof(v));
        std::memcpy(world.GetComponent(ents[i], kCompTag), &t, sizeof(t));
    }
    CHECK(world.EntityCount() == kCount);

    // Each 全量求和（三列，跨 chunk 批）
    uint32_t rows = 0;
    double sumPos = 0.0, sumVel = 0.0;
    uint64_t sumTag = 0;
    world.Each(mask, [&](void** comps, uint32_t rowCount) {
        const float* pos = static_cast<const float*>(comps[0]);
        const Velocity* vel = static_cast<const Velocity*>(comps[1]);
        const Tag* tag = static_cast<const Tag*>(comps[2]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            sumPos += pos[3u * r];
            sumVel += vel[r].dx;
            sumTag += tag[r].v;
        }
        rows += rowCount;
    });
    CHECK(rows == kCount);
    const double expected = static_cast<double>(kCount) * (kCount - 1) / 2.0;
    CHECK(sumPos == expected);
    CHECK(sumVel == expected);
    CHECK(sumTag == static_cast<uint64_t>(kCount) * (kCount - 1) / 2);
}

// =============================================================================
// TASK-B-KERNEL-01 —— 组件定位周期实测断言
//
// 需求「约 3 个 CPU 周期定位组件偏移」的实测闭环：
//   1. cpuid 检测 POPCNT（leaf1 ECX bit23）+ TZCNT（leaf7 EBX bit3 = BMI1）：
//      缺失时周期断言用例经 doctest::skip 装饰器整用例 SKIP，并在
//      「CPU 指令集能力自报」用例输出 MISSING 说明（WARN 级默认输出可见、
//      不致失败）——不许静默通过。
//   2. 有硬件支持时断言热路径 cycles/op < 40（宽松硬件上限：硬件指令直查
//      实测 ~1-6 cycles，逐位循环/软件查表实现实测远超 40——变异验证见
//      Docs/Logs/TASK-B-KERNEL-01.md 施工报告）。
// 口径与 zkernel_bench.cpp BenchColumnLocate 一致（kInner/kSamples/掩码分布
// 相同）：断言覆盖 a1 纯位定位与 a2 端到端（定位+chunk 列偏移表取数）；
// a3 随机取址含运行时除法，只测不断言（见 bench 人类可读表）。
// =============================================================================
namespace {

/// 本机是否具备 ZkBitsBelow 硬件直查前提（POPCNT + TZCNT/BMI1）。
bool CpuHasPopcntTzcnt()
{
    std::int32_t regs[4] = {};
    __cpuid(regs, 0);
    if (regs[0] < 1) {
        return false;
    }
    __cpuid(regs, 1);
    if ((regs[2] & (1 << 23)) == 0) { // ECX bit 23 = POPCNT
        return false;
    }
    __cpuidex(regs, 7, 0);
    return (regs[1] & (1 << 3)) != 0; // EBX bit 3 = TZCNT（BMI1）
}

/// 中位样本（与 zkernel_bench.cpp MedianOf 同法）
uint32_t MedianOf(std::vector<uint32_t>& v)
{
    std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(v.size() / 2), v.end());
    return v[v.size() / 2];
}

uint64_t g_cyclesSink = 0; // 防优化下沉（与 bench g_sink 同策）

} // namespace

TEST_CASE("CPU 指令集能力自报（cpuid）——无 POPCNT/TZCNT 时周期实测断言用例 SKIP")
{
    const bool has = CpuHasPopcntTzcnt();
    if (has) {
        MESSAGE("CPU 支持 POPCNT/TZCNT（BMI1）：ZkBitsBelow 周期实测断言用例启用");
        CHECK(has);
    } else {
        // WARN 级：默认输出可见（MISSING 说明），不致用例失败
        WARN_MESSAGE(false,
            "MISSING: 本机 CPU 无 POPCNT/TZCNT（BMI1）支持——"
            "「ZkBitsBelow 周期实测断言」用例已 SKIP；本机不构成『硬件指令直查』证据"
            "（zkernel 功能不受影响，周期口径在本机未验证）");
        CHECK(!has); // 缺支持路径也留显式断言记录
    }
}

TEST_CASE(
    "ZkBitsBelow 周期实测断言（热路径 <40 cycles/op）——无 POPCNT/TZCNT 时本用例 SKIP，MISSING 见『CPU 指令集能力自报』"
    * doctest::skip(!CpuHasPopcntTzcnt()))
{
    // —— 与 zkernel_bench.cpp BenchColumnLocate 同口径 ——
    // 模拟典型 archetype 掩码：26 个组件位散布在 w0/w1（稠疏混合）
    Mask128 mask{};
    for (uint32_t b = 5; b < 128; b += 5) {
        mask.Set(b);
    }
    uint64_t colOffset[128]; // 伪列偏移表（模拟 Archetype::columnOffset）
    for (uint32_t k = 0; k < 128; ++k) {
        colOffset[k] = 0x40ull * k;
    }

    constexpr uint32_t kInner = 96;    // 每 RDTSC 区间重复定位次数（摊还读对开销）
    constexpr uint32_t kSamples = 8192;
    std::vector<uint32_t> cyclesPure(kSamples);
    std::vector<uint32_t> cyclesFull(kSamples);
    uint32_t slotAcc = 0;
    uint64_t acc = 0;

    for (uint32_t s = 0; s < kSamples; ++s) {
        // a1 纯位定位：POPCNT 计数（位索引在寄存器，模拟 compId 已在寄存器）
        uint64_t t0 = __rdtsc();
        for (uint32_t k = 0; k < kInner; ++k) {
            slotAcc += ZkBitsBelow(mask, k & 127u);
        }
        uint64_t t1 = __rdtsc();
        cyclesPure[s] = static_cast<uint32_t>((t1 - t0) / kInner);

        // a2 端到端查询定位：POPCNT + chunk 列偏移表取数（含 L1 载入延迟链）
        t0 = __rdtsc();
        for (uint32_t k = 0; k < kInner; ++k) {
            acc += colOffset[ZkBitsBelow(mask, k & 127u)];
        }
        t1 = __rdtsc();
        cyclesFull[s] = static_cast<uint32_t>((t1 - t0) / kInner);
    }
    g_cyclesSink += acc + slotAcc;

    const double pure = static_cast<double>(MedianOf(cyclesPure));
    const double full = static_cast<double>(MedianOf(cyclesFull));
    MESSAGE("实测（中位, kInner=" << kInner << ", kSamples=" << kSamples
                                 << "）: a1 纯位定位 = " << pure << " cycles/op, "
                                 << "a2 定位+chunk列偏移表取数 = " << full << " cycles/op");

    // 宽松硬件上限 40 cycles/op：证明"硬件指令直查"而非"逐位循环"。
    // 变异验证（TASK-B-KERNEL-01 施工报告）：ZkBitsBelow 改逐位循环实现后
    // 本断言实测 >> 40 必红，还原后绿。
    CHECK_MESSAGE(pure < 40.0,
        "ZkBitsBelow 纯位定位实测 " << pure
            << " cycles/op ≥ 40——热路径疑似退化为逐位循环/软件查表，未实现硬件指令直查（变异防线）");
    CHECK_MESSAGE(full < 40.0,
        "定位+chunk 列偏移表取数实测 " << full
            << " cycles/op ≥ 40——端到端查询定位路径超宽松硬件上限（变异防线）");
}
