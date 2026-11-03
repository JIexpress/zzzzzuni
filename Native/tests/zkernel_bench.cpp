// =============================================================================
// zkernel_bench.cpp —— 数据内核基准程序（TASK-B-R1-01 / TASK-B-R8-03 / B-R9-12）
//
// a) 组件列定位：ZkBitsBelow（硬件 POPCNT 低位置位计数）+ 偏移表取数，
//    RDTSC 采样取中位周期数（amortize RDTSC 对开销）
// b) 100 万实体 Spawn(3 组件, 28B/行) + 全量 Each 遍历 3 列求和，
//    报毫秒与 M 行/秒（3 次取中位）
// c) TASK-B-R8-03 架型迁移吞吐：1 万实体各 Add/Remove 3 次（预热 1 轮不计入——
//    目标 archetype 与 chunk 首次分配落在预热），报毫秒与 rowsMoved/s
// d) TASK-B-R8-03 Compact 回收：100 万实体 Destroy 一半后 Compact，
//    报毫秒与释放 chunk 数（Destroy 毫秒顺带报告，非验收指标）
// e) TASK-B-R9-12 EachParallel 扩展比：1M 实体 3 列读求和，threadCount=1/2/4/8/16
//    各 5 轮取中位，报毫秒、M 行/s 与对 1 线程的加速比
//
// 仅 Release 参与默认构建（CMake 排除其它配置），不注册 ctest：
// 基准数字进施工报告，不做 pass/fail 判定。
//
// TASK-B-R9-10：`--json` 在人类可读输出之后追加一行 `BENCH_JSON {...}`
// （单行 JSON，供 Tools/bench_regression.ps1 程式化采集；不带 --json 时输出零变化）。
// TASK-B-R9-12：EachParallel 数字仅入人类可读表与施工报告（不进 --json——
// 门禁基线为封闭指标集，扩展比强依赖机器拓扑，不适合做跨机回归门禁）。
// =============================================================================
#include "bench_json.h"
#include "zkernel/zkernel.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <intrin.h> // __cpuid / __rdtsc
#include <vector>

namespace {

struct Position { float x, y, z; };    // 组件 0：12B
struct Velocity { float dx, dy, dz; }; // 组件 1：12B
struct Tag { uint32_t v; };            // 组件 2：4B（合计 28B/行 → 容量 585）
struct Weight { float w; };            // 组件 3：4B（迁移用第 4 组件）

uint64_t g_sink = 0; // 防优化下沉，末尾打印

const char* CpuBrand()
{
    static char brand[49] = {};
    std::int32_t regs[4] = {};
    __cpuid(regs, 0x80000000);
    if (regs[0] >= static_cast<std::int32_t>(0x80000004)) {
        for (std::int32_t i = 0; i < 3; ++i) {
            __cpuid(regs, 0x80000002 + i);
            std::memcpy(brand + i * 16, regs, 16);
        }
        brand[48] = '\0';
    }
    return brand;
}

uint32_t MedianOf(std::vector<uint32_t>& v)
{
    std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(v.size() / 2), v.end());
    return v[v.size() / 2];
}

double MedianOf3(double a, double b, double c)
{
    return std::max(std::min(a, b), std::min(std::max(a, b), c));
}

/// a) 列定位基准（两项；位索引取 k&127 随迭代在寄存器内变化，防循环不变量外提）：
///    pure = ZkBitsBelow(POPCNT) 纯位定位（位索引在寄存器，无访存链）
///    full = ZkBitsBelow + 列偏移表取数（真实定位路径，含 L1 载入延迟链）
void BenchColumnLocate(double& pure, double& full)
{
    // 模拟典型 archetype 掩码：26 个组件位散布在 w0/w1
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
        // ---- 纯位定位：POPCNT 计数（位索引在寄存器，模拟 compId 已在寄存器）----
        uint64_t t0 = __rdtsc();
        for (uint32_t k = 0; k < kInner; ++k) {
            slotAcc += ZkBitsBelow(mask, k & 127u);
        }
        uint64_t t1 = __rdtsc();
        cyclesPure[s] = static_cast<uint32_t>((t1 - t0) / kInner);

        // ---- 完整定位路径：POPCNT + 偏移表取数（含 L1 载入延迟链）----
        t0 = __rdtsc();
        for (uint32_t k = 0; k < kInner; ++k) {
            acc += colOffset[ZkBitsBelow(mask, k & 127u)];
        }
        t1 = __rdtsc();
        cyclesFull[s] = static_cast<uint32_t>((t1 - t0) / kInner);
    }
    g_sink += acc + slotAcc;
    pure = static_cast<double>(MedianOf(cyclesPure));
    full = static_cast<double>(MedianOf(cyclesFull));
}

/// b) 百万实体 Spawn + 全量 Each
void BenchWorld(double& spawnMs, double& eachMs, double& checksum, double& expected)
{
    constexpr uint32_t kCount = 1000000;
    ZkWorld world;
    world.RegisterComponent(0, static_cast<uint32_t>(sizeof(Position)),
        static_cast<uint32_t>(alignof(Position)));
    world.RegisterComponent(1, static_cast<uint32_t>(sizeof(Velocity)),
        static_cast<uint32_t>(alignof(Velocity)));
    world.RegisterComponent(2, static_cast<uint32_t>(sizeof(Tag)),
        static_cast<uint32_t>(alignof(Tag)));
    Mask128 mask{};
    mask.Set(0);
    mask.Set(1);
    mask.Set(2);

    // ---- Spawn 100 万（不初始化数据，纯生成成本）----
    std::vector<ZkEntity> ents(kCount);
    const auto t0 = std::chrono::steady_clock::now();
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
    }
    const auto t1 = std::chrono::steady_clock::now();
    spawnMs = std::chrono::duration<double, std::milli>(t1 - t0).count();

    // ---- 初始化数据（Each 批量写，计时外 setup）----
    uint32_t cursor = 0;
    world.Each(mask, [&](void** comps, uint32_t rowCount) {
        float* pos = static_cast<float*>(comps[0]);
        float* vel = static_cast<float*>(comps[1]);
        uint32_t* tag = static_cast<uint32_t*>(comps[2]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            const float f = static_cast<float>(cursor + r);
            pos[3u * r] = f;
            vel[3u * r] = f;
            tag[r] = cursor + r;
        }
        cursor += rowCount;
    });

    // ---- 全量 Each：3 列读求和（3 次取中位）----
    double ms3[3];
    checksum = 0.0;
    for (int rep = 0; rep < 3; ++rep) {
        double sum = 0.0;
        const auto s0 = std::chrono::steady_clock::now();
        world.Each(mask, [&](void** comps, uint32_t rowCount) {
            const float* pos = static_cast<const float*>(comps[0]);
            const float* vel = static_cast<const float*>(comps[1]);
            const uint32_t* tag = static_cast<const uint32_t*>(comps[2]);
            for (uint32_t r = 0; r < rowCount; ++r) {
                sum += pos[3u * r] + vel[3u * r] + static_cast<double>(tag[r]);
            }
        });
        const auto s1 = std::chrono::steady_clock::now();
        ms3[rep] = std::chrono::duration<double, std::milli>(s1 - s0).count();
        checksum = sum;
    }
    eachMs = MedianOf3(ms3[0], ms3[1], ms3[2]);
    expected = 3.0 * 999999.0 * 1000000.0 / 2.0; // 3 列各 Σi
    g_sink += static_cast<uint64_t>(checksum);
}

/// c) 架型迁移吞吐（TASK-B-R8-03）：1 万实体各 Add/Remove 3 次。
///    预热 1 轮不计入计时（目标 archetype 与 chunk 首次分配落在预热，
///    计时轮为纯迁移路径：逐列 memcpy + swap-back 收行 + 索引更新）。
void BenchMigrate(double& addMs, double& removeMs, double& rowsPerSec, uint64_t& migrations)
{
    constexpr uint32_t kCount = 10000;
    ZkWorld world;
    world.RegisterComponent(0, static_cast<uint32_t>(sizeof(Position)),
        static_cast<uint32_t>(alignof(Position)));
    world.RegisterComponent(1, static_cast<uint32_t>(sizeof(Velocity)),
        static_cast<uint32_t>(alignof(Velocity)));
    world.RegisterComponent(2, static_cast<uint32_t>(sizeof(Tag)),
        static_cast<uint32_t>(alignof(Tag)));
    world.RegisterComponent(3, static_cast<uint32_t>(sizeof(Weight)),
        static_cast<uint32_t>(alignof(Weight)));
    Mask128 mask{};
    mask.Set(0);
    mask.Set(1);
    mask.Set(2);

    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
    }

    const Weight initW{1.0f};
    // 预热 1 轮（不计入计时）：{P,V,T}→{P,V,T,W} 目标链首次建齐
    for (uint32_t i = 0; i < kCount; ++i) {
        world.AddComponent(ents[i], 3, &initW);
    }
    for (uint32_t i = 0; i < kCount; ++i) {
        world.RemoveComponent(ents[i], 3);
    }
    const ZkWorldStats s0 = world.Stats();

    // 3 轮 Add/Remove（每轮 = 1 万 Add + 1 万 Remove，迁移回同一稳态）
    double addTotal = 0.0;
    double removeTotal = 0.0;
    for (int round = 0; round < 3; ++round) {
        const auto a0 = std::chrono::steady_clock::now();
        for (uint32_t i = 0; i < kCount; ++i) {
            world.AddComponent(ents[i], 3, &initW);
        }
        const auto a1 = std::chrono::steady_clock::now();
        for (uint32_t i = 0; i < kCount; ++i) {
            world.RemoveComponent(ents[i], 3);
        }
        const auto a2 = std::chrono::steady_clock::now();
        addTotal += std::chrono::duration<double, std::milli>(a1 - a0).count();
        removeTotal += std::chrono::duration<double, std::milli>(a2 - a1).count();
    }
    const ZkWorldStats s1 = world.Stats();

    migrations = s1.migrations - s0.migrations; // = 60000（3 轮 × 2 万）
    const uint64_t rows = s1.rowsMoved - s0.rowsMoved;
    addMs = addTotal / 3.0;
    removeMs = removeTotal / 3.0;
    rowsPerSec = static_cast<double>(rows) / ((addTotal + removeTotal) / 1000.0);
    g_sink += rows + migrations;
}

/// d) Compact 回收（TASK-B-R8-03）：100 万实体 Destroy 一半后 Compact。
///    Destroy 毫秒顺带报告（非验收指标）；Compact 毫秒 + 释放 chunk 数为交付数字。
void BenchCompact(double& destroyMs, double& compactMs, uint32_t& freed, uint32_t& chunksBefore)
{
    constexpr uint32_t kCount = 1000000;
    ZkWorld world;
    world.RegisterComponent(0, static_cast<uint32_t>(sizeof(Position)),
        static_cast<uint32_t>(alignof(Position)));
    world.RegisterComponent(1, static_cast<uint32_t>(sizeof(Velocity)),
        static_cast<uint32_t>(alignof(Velocity)));
    world.RegisterComponent(2, static_cast<uint32_t>(sizeof(Tag)),
        static_cast<uint32_t>(alignof(Tag)));
    Mask128 mask{};
    mask.Set(0);
    mask.Set(1);
    mask.Set(2);

    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
    }

    // Destroy 前 50 万（swap-back 高频跨 chunk，计时顺带报告）
    const auto d0 = std::chrono::steady_clock::now();
    for (uint32_t i = 0; i < kCount / 2; ++i) {
        world.Destroy(ents[i]);
    }
    const auto d1 = std::chrono::steady_clock::now();
    destroyMs = std::chrono::duration<double, std::milli>(d1 - d0).count();

    chunksBefore = world.ChunkCount();
    const auto c0 = std::chrono::steady_clock::now();
    freed = world.Compact();
    const auto c1 = std::chrono::steady_clock::now();
    compactMs = std::chrono::duration<double, std::milli>(c1 - c0).count();
    g_sink += freed;
}

/// e) EachParallel 扩展比（TASK-B-R9-12）：1M 实体 3 组件读求和，
///    threadCount = 1/2/4/8/16 各 5 轮取中位；报告毫秒、M 行/s 与对 1 线程的
///    加速比（28MB 工作集超 L3，预期内存带宽瓶颈——实际曲线如实呈现）。
///    聚合口径：chunk 内 double 本地累加（列值为精确整数 < 2^53，无舍入），
///    每 chunk 一次整数原子合并（~1710 次/轮，开销可忽略）。
void BenchEachParallel(double ms[5], double mrows[5], double speedup[5], double& checksum,
    double& expected)
{
    constexpr uint32_t kCount = 1000000;
    constexpr uint32_t kThreadCounts[5] = {1u, 2u, 4u, 8u, 16u};
    constexpr int kRounds = 5;

    ZkWorld world;
    world.RegisterComponent(0, static_cast<uint32_t>(sizeof(Position)),
        static_cast<uint32_t>(alignof(Position)));
    world.RegisterComponent(1, static_cast<uint32_t>(sizeof(Velocity)),
        static_cast<uint32_t>(alignof(Velocity)));
    world.RegisterComponent(2, static_cast<uint32_t>(sizeof(Tag)),
        static_cast<uint32_t>(alignof(Tag)));
    Mask128 mask{};
    mask.Set(0);
    mask.Set(1);
    mask.Set(2);

    std::vector<ZkEntity> ents(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ents[i] = world.Spawn(mask);
    }
    ents.clear();
    ents.shrink_to_fit();

    // 初始化数据（Each 批量写，计时外 setup）：pos=vel=i, tag=i
    uint32_t cursor = 0;
    world.Each(mask, [&](void** comps, uint32_t rowCount) {
        float* pos = static_cast<float*>(comps[0]);
        float* vel = static_cast<float*>(comps[1]);
        uint32_t* tag = static_cast<uint32_t*>(comps[2]);
        for (uint32_t r = 0; r < rowCount; ++r) {
            const float f = static_cast<float>(cursor + r);
            pos[3u * r] = f;
            vel[3u * r] = f;
            tag[r] = cursor + r;
        }
        cursor += rowCount;
    });

    expected = 3.0 * 999999.0 * 1000000.0 / 2.0; // 3 列各 Σi
    std::vector<uint32_t> roundUs(static_cast<size_t>(kRounds)); // μs 精度
    for (uint32_t tc = 0; tc < 5; ++tc) {
        for (int rep = 0; rep < kRounds; ++rep) {
            std::atomic<uint64_t> sum{0};
            const auto s0 = std::chrono::steady_clock::now();
            world.EachParallel(mask,
                [&sum](void** comps, uint32_t rowCount) {
                    const float* pos = static_cast<const float*>(comps[0]);
                    const float* vel = static_cast<const float*>(comps[1]);
                    const uint32_t* tag = static_cast<const uint32_t*>(comps[2]);
                    double local = 0.0;
                    for (uint32_t r = 0; r < rowCount; ++r) {
                        local += static_cast<double>(pos[3u * r])
                            + static_cast<double>(vel[3u * r]) + static_cast<double>(tag[r]);
                    }
                    sum.fetch_add(static_cast<uint64_t>(local), std::memory_order_relaxed);
                },
                kThreadCounts[tc]);
            const auto s1 = std::chrono::steady_clock::now();
            roundUs[static_cast<size_t>(rep)] = static_cast<uint32_t>(
                std::chrono::duration<double, std::micro>(s1 - s0).count());
            checksum = static_cast<double>(sum.load());
        }
        ms[tc] = static_cast<double>(MedianOf(roundUs)) / 1000.0;
        mrows[tc] = 1000.0 / ms[tc]; // kCount = 1M 行
        speedup[tc] = ms[0] / ms[tc];
    }
    g_sink += static_cast<uint64_t>(checksum);
}

} // namespace

int main(int argc, char** argv)
{
    std::printf("==== zkernel_bench (zkernel %s) ====\n", ZK_GetVersion());
    std::printf("CPU: %s\n\n", CpuBrand());

    double locatePure = 0.0, locateFull = 0.0;
    BenchColumnLocate(locatePure, locateFull);
    double spawnMs = 0.0, eachMs = 0.0, checksum = 0.0, expected = 0.0;
    BenchWorld(spawnMs, eachMs, checksum, expected);
    double addMs = 0.0, removeMs = 0.0, rowsPerSec = 0.0;
    uint64_t migrations = 0;
    BenchMigrate(addMs, removeMs, rowsPerSec, migrations);
    double destroyMs = 0.0, compactMs = 0.0;
    uint32_t freed = 0, chunksBefore = 0;
    BenchCompact(destroyMs, compactMs, freed, chunksBefore);
    double parMs[5] = {}, parMrows[5] = {}, parSpeedup[5] = {}, parChecksum = 0.0,
           parExpected = 0.0;
    BenchEachParallel(parMs, parMrows, parSpeedup, parChecksum, parExpected);

    std::printf("%-46s%s\n", "基准项", "结果");
    std::printf("%-46s%s\n", "----------------------------------------------",
        "------------------------");
    std::printf("[组件列定位] ZkBitsBelow(POPCNT) 纯位定位 中位 : %.2f cycles/op\n", locatePure);
    std::printf("[组件列定位] +偏移表取数(完整路径,L1链) 中位  : %.2f cycles/op\n", locateFull);
    std::printf("[Spawn      ] 1,000,000 实体 x 3 组件(28B/行) : %.1f ms (%.2f M 实体/s)\n",
        spawnMs, 1000.0 / spawnMs);
    std::printf("[Each       ] 全量遍历 3 列读求和(中位)      : %.1f ms (%.0f M 行/s)\n", eachMs,
        1000.0 / eachMs);
    std::printf("[校验       ] Each 求和 = %.0f (期望 %.0f)\n", checksum, expected);
    std::printf("[架构型迁移] 10,000 实体 Add(28B→32B/行) 每轮 : %.2f ms\n", addMs);
    std::printf("[架构型迁移] 10,000 实体 Remove(32B→28B) 每轮 : %.2f ms\n", removeMs);
    std::printf("[架构型迁移] 迁移吞吐 %llu 次迁移(6 万次计)   : %.2f M 行迁移/s\n",
        static_cast<unsigned long long>(migrations), rowsPerSec / 1e6);
    std::printf("[Compact    ] 100 万实体 Destroy 一半(顺带)    : %.1f ms\n", destroyMs);
    std::printf("[Compact    ] Destroy 一半后回收 %u chunk      : %.2f ms (%u -> %u)\n", freed,
        compactMs, chunksBefore, chunksBefore - freed);
    std::printf("[EachParallel] 1,000,000 实体 3 列读求和(5 轮中位):\n");
    const uint32_t parThreads[5] = {1u, 2u, 4u, 8u, 16u};
    for (uint32_t tc = 0; tc < 5; ++tc) {
        std::printf("[EachParallel] threadCount=%2u                   : %7.2f ms"
            " (%5.0f M 行/s, 加速 %.2fx)\n",
            parThreads[tc], parMs[tc], parMrows[tc], parSpeedup[tc]);
    }
    std::printf("[EachParallel 校验] 求和 = %.0f (期望 %.0f)%s\n", parChecksum, parExpected,
        (parChecksum == parExpected) ? "" : "  <- 校验不符!");
    std::printf("(sink=%llu)\n", static_cast<unsigned long long>(g_sink));

    // TASK-B-R9-10：--json 单行输出（与上方人类可读表同源的同一批数字）
    if (zbench::HasJsonFlag(argc, argv)) {
        zbench::JsonLineWriter w("zkernel_bench");
        w.Metric("spawn_ms", spawnMs);
        w.Metric("spawn_m_per_s", 1000.0 / spawnMs);
        w.Metric("each_ms", eachMs);
        w.Metric("each_m_rows_per_s", 1000.0 / eachMs);
        w.Metric("migrate_add_ms", addMs);
        w.Metric("migrate_remove_ms", removeMs);
        w.Metric("migrate_rows_per_sec", rowsPerSec);
        w.Metric("compact_destroy_ms", destroyMs);
        w.Metric("compact_ms", compactMs);
        w.MetricU64("compact_freed_chunks", freed);
        std::printf("%s\n", w.Finish().c_str());
    }
    return 0;
}
