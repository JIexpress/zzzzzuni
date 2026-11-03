// =============================================================================
// zrender_bench.cpp —— DrawList 10 万项 Add+Sort+Batch 毫秒基准（TASK-B-R4-02）
//
// 数字进施工报告，不做 pass/fail 判定 → 不注册 ctest；仅 Release 默认构建
// （与 zkernel_bench 同级同策）。QPC 计时，预热 2 轮 + 计时 10 轮取平均。
//
// TASK-B-R9-10：`--json` 在人类可读输出之后追加一行 `BENCH_JSON {...}`
// （单行 JSON，供 Tools/bench_regression.ps1 程式化采集；不带 --json 时输出零变化）。
//
// 两个场景：
//   A. 状态全域随机（shader/material/mesh 32bit 全域）——排序压力上限，
//      预期合批率≈0（相邻同状态概率趋零）
//   B. 真实状态分布（8 shader x 32 材质 x 16 网格）——状态聚簇合批的真实收益
// =============================================================================
#include "bench_json.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <random>
#include <vector>

#include <windows.h>

#include "zrender/draw_list.h"

namespace {

double QpcMs()
{
    static LARGE_INTEGER freq = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return f;
    }();
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return static_cast<double>(now.QuadPart) * 1000.0 / static_cast<double>(freq.QuadPart);
}

struct ScenarioResult {
    double addMs;
    double sortMs;
    double batchMs;
    uint32_t batches;
};

/// 生成项集：mesh/shader/material 取值域由调用方给（0 表示 32bit 全域）；
/// 深度范围/网格决定"每深度桶项数"——深度是 key 首位字段，状态聚簇只在
/// 同量化深度桶内发生，桶内项数必须显著大于状态域才有可观合批率。
std::vector<RenderItem> MakeItems(uint32_t n, uint32_t shaderDomain, uint32_t materialDomain,
    uint32_t meshDomain, float depthMax, float depthGrid)
{
    std::vector<RenderItem> items(n);
    std::mt19937 rng(0xD5A0C0DEu);
    std::uniform_real_distribution<float> depthDist(0.0f, depthMax);
    std::uniform_int_distribution<uint32_t> u32(0u, 0xFFFFFFFFu);
    for (uint32_t i = 0; i < n; ++i) {
        RenderItem it;
        it.meshHandle = meshDomain == 0u ? u32(rng) : (u32(rng) % meshDomain);
        it.materialId = materialDomain == 0u ? u32(rng) : (u32(rng) % materialDomain);
        it.shaderId = shaderDomain == 0u ? u32(rng) : (u32(rng) % shaderDomain);
        it.lod = u32(rng) & 0x3u;
        it.depth = static_cast<float>(static_cast<int>(depthDist(rng) / depthGrid)) * depthGrid;
        it.translucent = (u32(rng) & 1u) != 0u;
        items[i] = it;
    }
    return items;
}

ScenarioResult RunScenario(const std::vector<RenderItem>& items, int warmup, int timed)
{
    const uint32_t n = static_cast<uint32_t>(items.size());
    DrawList dl(n);
    ScenarioResult r { 0.0, 0.0, 0.0, 0 };

    for (int iter = 0; iter < warmup + timed; ++iter) {
        const bool isTimed = iter >= warmup;

        double t0 = QpcMs();
        dl.Reset();
        for (uint32_t i = 0; i < n; ++i) {
            if (!dl.Add(items[i], 0xFFu)) {
                std::printf("FATAL: Add failed at %u\n", i);
                std::exit(1);
            }
        }
        double t1 = QpcMs();
        dl.Sort();
        double t2 = QpcMs();
        r.batches = dl.BuildBatches();
        double t3 = QpcMs();

        if (isTimed) {
            r.addMs += t1 - t0;
            r.sortMs += t2 - t1;
            r.batchMs += t3 - t2;
        }
    }
    r.addMs /= timed;
    r.sortMs /= timed;
    r.batchMs /= timed;
    return r;
}

void Report(const char* name, uint32_t n, const ScenarioResult& r)
{
    const uint32_t merged = n - r.batches; // 合并省掉的绘制调用数
    std::printf("%s: N=%u  add=%.3f ms  sort=%.3f ms  batch=%.3f ms  total=%.3f ms  "
                "batches=%u  drawCallsSaved=%u (%.1f%%)\n",
        name, n, r.addMs, r.sortMs, r.batchMs, r.addMs + r.sortMs + r.batchMs,
        r.batches, merged, 100.0 * static_cast<double>(merged) / static_cast<double>(n));
}

} // namespace

int main(int argc, char** argv)
{
    constexpr uint32_t kN = 100000u;
    constexpr int kWarmup = 2;
    constexpr int kTimed = 10;

    const ScenarioResult a = RunScenario(MakeItems(kN, 0u, 0u, 0u, 1000.0f, 0.5f), kWarmup, kTimed);
    Report("A 全域随机(深度[0,1000])", kN, a);
    const ScenarioResult b = RunScenario(MakeItems(kN, 4u, 8u, 4u, 10.0f, 0.5f), kWarmup, kTimed);
    Report("B 小状态域(4s/8m/4mesh, 深度[0,10])", kN, b);

    // TASK-B-R9-10：--json 单行输出（与上方人类可读行同源的同一批数字）
    if (zbench::HasJsonFlag(argc, argv)) {
        zbench::JsonLineWriter w("zrender_bench");
        w.Metric("add_ms_a", a.addMs);
        w.Metric("sort_ms_a", a.sortMs);
        w.Metric("batch_ms_a", a.batchMs);
        w.Metric("saved_pct_a", 100.0 * static_cast<double>(kN - a.batches)
                / static_cast<double>(kN));
        w.MetricU64("batches_a", a.batches);
        w.Metric("add_ms_b", b.addMs);
        w.Metric("sort_ms_b", b.sortMs);
        w.Metric("batch_ms_b", b.batchMs);
        w.Metric("saved_pct_b", 100.0 * static_cast<double>(kN - b.batches)
                / static_cast<double>(kN));
        w.MetricU64("batches_b", b.batches);
        std::printf("%s\n", w.Finish().c_str());
    }
    return 0;
}
