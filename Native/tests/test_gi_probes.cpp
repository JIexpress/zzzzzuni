// =============================================================================
// test_gi_probes.cpp —— 动态 GI 探针辐射缓存 CPU 侧单测（TASK-B-R5-03）
//
// 覆盖（简报设计要点 5 的单测清单）：
//   - 探针索引/三线性权重：已知位置断言 8 邻域与权重和 = 1（权重和恒 1——
//     边缘 clamp 后同索引重复出现，权重不重归一）
//   - 恰在探针中心 → 该探针权重 1（恒等采样）
//   - 网格外位置 → 边缘 clamp（权重和仍 1）
//   - CPU 辐照度累加：方向光已知几何（常数贡献）/点光已知几何（与衰减式
//     手算值断言）
//   - 轮转切片：4 帧并集覆盖全部探针、每探针每周期恰一次
//   - CB 布局 static_assert（与 pbr_r5.hlsl CBufGi 逐字节契约）
// =============================================================================
#include "doctest/doctest.h"

#include <cmath>
#include <cstddef>
#include <cstdint>

#include "zrender/cluster_lighting.h"
#include "zrender/gi_probes.h"

namespace {

/// 由探针索引计算探针世界位置（与窗口程序 --r5 累加路径同式）
void ProbeWorldPos(const GiProbeGrid& grid, uint32_t index, float out[3])
{
    const uint32_t px = index % ZR_GI_PROBE_DIM_X;
    const uint32_t py = (index / ZR_GI_PROBE_DIM_X) % ZR_GI_PROBE_DIM_Y;
    const uint32_t pz = index / (ZR_GI_PROBE_DIM_X * ZR_GI_PROBE_DIM_Y);
    out[0] = grid.origin[0] + grid.spacing[0] * (static_cast<float>(px) + 0.5f);
    out[1] = grid.origin[1] + grid.spacing[1] * (static_cast<float>(py) + 0.5f);
    out[2] = grid.origin[2] + grid.spacing[2] * (static_cast<float>(pz) + 0.5f);
}

} // namespace

// =============================================================================
// 三线性权重
// =============================================================================
TEST_CASE("GiProbeGrid: trilinear weights sum to 1 with 8 in-range indices")
{
    GiProbeGrid grid;
    // 网格内部/边界附近/角落多个已知位置（含简报指定断言：权重和 = 1）
    const float positions[][3] = {
        {0.0f, -1.0f, 6.5f},   // 网格 y 下边界（clamp 到层 0）
        {-1.2f, 0.4f, 2.2f},   // 内部
        {9.9f, 2.9f, 19.9f},   // 右上后角内侧
        {-10.0f, -1.0f, 0.0f}, // 原点（边界）
        {0.0f, 0.0f, 0.0f},    // 场景中心
    };
    for (const float* pos : positions) {
        uint32_t indices[8];
        float weights[8];
        grid.TrilinearWeights(pos, indices, weights);
        float sum = 0.0f;
        for (uint32_t c = 0; c < 8u; ++c) {
            CHECK(indices[c] < ZR_GI_PROBE_COUNT);
            CHECK(weights[c] >= 0.0f);
            CHECK(weights[c] <= 1.0f);
            sum += weights[c];
        }
        CHECK(sum == doctest::Approx(1.0f).epsilon(1e-5));
    }
}

TEST_CASE("GiProbeGrid: exact probe center samples that probe with weight 1")
{
    GiProbeGrid grid;
    const uint32_t target = (3u * ZR_GI_PROBE_DIM_Y + 1u) * ZR_GI_PROBE_DIM_X + 5u; // (5,1,3)
    float pos[3];
    ProbeWorldPos(grid, target, pos);

    uint32_t indices[8];
    float weights[8];
    grid.TrilinearWeights(pos, indices, weights);
    float sum = 0.0f;
    float targetWeight = 0.0f;
    for (uint32_t c = 0; c < 8u; ++c) {
        sum += weights[c];
        if (indices[c] == target) {
            targetWeight += weights[c]; // frac=0：偏移角权重为 0（索引可为邻居）
        }
    }
    CHECK(sum == doctest::Approx(1.0f).epsilon(1e-5));
    CHECK(targetWeight == doctest::Approx(1.0f).epsilon(1e-5));

    // 恒等采样：目标探针值原样返回
    float probes[ZR_GI_PROBE_COUNT][3] = {};
    probes[target][0] = 1.0f;
    probes[target][1] = 2.0f;
    probes[target][2] = 3.0f;
    float out[3];
    grid.SampleIrradiance(probes, pos, out);
    CHECK(out[0] == doctest::Approx(1.0f).epsilon(1e-5));
    CHECK(out[1] == doctest::Approx(2.0f).epsilon(1e-5));
    CHECK(out[2] == doctest::Approx(3.0f).epsilon(1e-5));
}

TEST_CASE("GiProbeGrid: known interior position gives 8 distinct neighbors with hand-computed weights")
{
    GiProbeGrid grid;
    // 网格 uv=(0.5, 0.5, 0.5)：世界 (0, 1, 10)——cell c = uv*dims-0.5 = (3.5, 0.5, 3.5)
    // → floor=(3,0,3)，frac=0.5 → 8 邻域权重全 0.125（无 clamp——内部位置）
    const float pos[3] = {0.0f, 1.0f, 10.0f};
    uint32_t indices[8];
    float weights[8];
    grid.TrilinearWeights(pos, indices, weights);
    // 8 邻域互异（内部：无 clamp 重复）
    for (uint32_t a = 0; a < 8u; ++a) {
        for (uint32_t b = a + 1u; b < 8u; ++b) {
            CHECK(indices[a] != indices[b]);
        }
        CHECK(weights[a] == doctest::Approx(0.125).epsilon(1e-6));
    }
    // 索引序契约：(cz*dimY+cy)*dimX+cx；cell (3..4, 0..1, 3..4)
    // 最小角 (3,0,3) → (3*2+0)*8+3 = 51；最大角 (4,1,4) → (4*2+1)*8+4 = 76
    bool hasMin = false;
    bool hasMax = false;
    for (uint32_t c = 0; c < 8u; ++c) {
        hasMin = hasMin || (indices[c] == 51u);
        hasMax = hasMax || (indices[c] == 76u);
    }
    CHECK(hasMin);
    CHECK(hasMax);
}

TEST_CASE("GiProbeGrid: outside-grid positions clamp to edge (weights still sum to 1)")
{
    GiProbeGrid grid;
    const float outside[3] = {-50.0f, -10.0f, -50.0f}; // 远外
    uint32_t indices[8];
    float weights[8];
    grid.TrilinearWeights(outside, indices, weights);
    float sum = 0.0f;
    for (uint32_t c = 0; c < 8u; ++c) {
        CHECK(indices[c] < ZR_GI_PROBE_COUNT);
        sum += weights[c];
    }
    CHECK(sum == doctest::Approx(1.0f).epsilon(1e-5));
    // 全部 clamp 到探针 0（各轴 0）
    for (uint32_t c = 0; c < 8u; ++c) {
        CHECK(indices[c] == 0u);
    }
}

// =============================================================================
// CPU 辐照度累加（已知几何）
// =============================================================================
TEST_CASE("AccumulateProbeIrradiance: directional light is position-independent (sphere-averaged N.L = 1)")
{
    LightData dir {};
    dir.pos[0] = 0.4f;
    dir.pos[1] = 0.85f;
    dir.pos[2] = 0.35f;
    dir.color[0] = 1.0f;
    dir.color[1] = 0.95f;
    dir.color[2] = 0.88f;
    dir.intensity = 1.0f;
    dir.kind = kZrLightDirectional;

    const float posA[3] = {-10.0f, -1.0f, 0.0f};
    const float posB[3] = {10.0f, 3.0f, 20.0f};
    float eA[3] = {0.0f, 0.0f, 0.0f};
    float eB[3] = {0.0f, 0.0f, 0.0f};
    AccumulateProbeIrradiance(posA, &dir, 1u, eA);
    AccumulateProbeIrradiance(posB, &dir, 1u, eB);
    // 方向光球面平均贡献 = color * intensity，与探针位置无关（简报指定）
    for (int ch = 0; ch < 3; ++ch) {
        CHECK(eA[ch] == doctest::Approx(dir.color[ch] * dir.intensity).epsilon(1e-6));
        CHECK(eB[ch] == doctest::Approx(dir.color[ch] * dir.intensity).epsilon(1e-6));
    }
}

TEST_CASE("AccumulateProbeIrradiance: point light matches hand-computed window^2/(d^2+1)")
{
    // 点光 (0, 0.9, 6.5)，radius 3.5，intensity 6——与窗口程序 --r5 动画光源同参
    LightData point {};
    point.pos[0] = 0.0f;
    point.pos[1] = 0.9f;
    point.pos[2] = 6.5f;
    point.radius = 3.5f;
    point.color[0] = 1.0f;
    point.color[1] = 0.85f;
    point.color[2] = 0.6f;
    point.intensity = 6.0f;
    point.kind = kZrLightPoint;

    // 探针 (1.25, 0, 6.25)：d² = 1.25² + 0.9² + 0.25² = 2.435
    const float probe[3] = {1.25f, 0.0f, 6.25f};
    const float dx = 0.0f - probe[0];
    const float dy = 0.9f - probe[1];
    const float dz = 6.5f - probe[2];
    const float dist2 = dx * dx + dy * dy + dz * dz;
    const float window = 1.0f - dist2 / (3.5f * 3.5f);
    const float expected[3] = {point.color[0] * point.intensity * window * window / (dist2 + 1.0f),
        point.color[1] * point.intensity * window * window / (dist2 + 1.0f),
        point.color[2] * point.intensity * window * window / (dist2 + 1.0f)};

    float e[3] = {0.0f, 0.0f, 0.0f};
    AccumulateProbeIrradiance(probe, &point, 1u, e);
    for (int ch = 0; ch < 3; ++ch) {
        CHECK(e[ch] == doctest::Approx(expected[ch]).epsilon(1e-5));
    }
    CHECK(dist2 == doctest::Approx(2.435f).epsilon(1e-4)); // 几何前提自证
}

TEST_CASE("AccumulateProbeIrradiance: beyond-radius probe receives zero")
{
    LightData point {};
    point.pos[0] = 0.0f;
    point.pos[1] = 0.9f;
    point.pos[2] = 6.5f;
    point.radius = 3.5f;
    point.color[0] = 1.0f;
    point.color[1] = 1.0f;
    point.color[2] = 1.0f;
    point.intensity = 6.0f;
    point.kind = kZrLightPoint;

    const float farProbe[3] = {0.0f, 0.0f, 18.0f}; // 距离 ~11.5 > 半径
    float e[3] = {0.0f, 0.0f, 0.0f}; // 累加语义：从 0 起
    AccumulateProbeIrradiance(farProbe, &point, 1u, e);
    for (int ch = 0; ch < 3; ++ch) {
        CHECK(e[ch] == doctest::Approx(0.0f).epsilon(1e-7));
    }
}

// =============================================================================
// 轮转切片（每 4 帧轮转）
// =============================================================================
TEST_CASE("UpdateProbeSlice: four slices cover all probes exactly once per cycle")
{
    uint32_t slice[ZR_GI_PROBE_COUNT] = {};
    uint32_t seen[ZR_GI_PROBE_COUNT] = {};
    for (uint32_t frame = 0u; frame < 4u; ++frame) {
        const uint32_t count = UpdateProbeSlice(frame, 4u, slice, ZR_GI_PROBE_COUNT);
        CHECK(count == ZR_GI_PROBE_COUNT / 4u);
        for (uint32_t i = 0; i < count; ++i) {
            CHECK(slice[i] < ZR_GI_PROBE_COUNT);
            CHECK(seen[slice[i]] == 0u); // 周期内不重复
            seen[slice[i]] = 1u;
        }
    }
    for (uint32_t probe = 0; probe < ZR_GI_PROBE_COUNT; ++probe) {
        CHECK(seen[probe] == 1u); // 4 帧并集全覆盖
    }
}

TEST_CASE("UpdateProbeSlice: same slice repeats every 4 frames and rejects invalid input")
{
    uint32_t a[ZR_GI_PROBE_COUNT] = {};
    uint32_t b[ZR_GI_PROBE_COUNT] = {};
    const uint32_t countA = UpdateProbeSlice(7u, 4u, a, ZR_GI_PROBE_COUNT);
    const uint32_t countB = UpdateProbeSlice(11u, 4u, b, ZR_GI_PROBE_COUNT);
    REQUIRE(countA == countB);
    for (uint32_t i = 0; i < countA; ++i) {
        CHECK(a[i] == b[i]); // frame % 4 相同 → 同切片
    }
    CHECK(UpdateProbeSlice(0u, 0u, a, ZR_GI_PROBE_COUNT) == 0u);
    CHECK(UpdateProbeSlice(0u, 4u, nullptr, ZR_GI_PROBE_COUNT) == 0u);
}

// =============================================================================
// CB 布局契约
// =============================================================================
TEST_CASE("GiCbufferLayout matches GPU contract (2080 bytes, 128 probes)")
{
    CHECK(ZR_GI_PROBE_COUNT == 128u);
    CHECK(sizeof(GiCbufferLayout) == 2080u);
    CHECK(offsetof(GiCbufferLayout, probes) == 32u);
}
