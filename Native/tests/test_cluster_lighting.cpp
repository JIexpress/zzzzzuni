// =============================================================================
// test_cluster_lighting.cpp —— CPU 簇光源分配单测（TASK-B-R5-01）
//
// 覆盖（简报设计要点 1 的单测清单）：
//   - LightData → PackedLight 打包：32B 布局 static_assert + kind 编码往返
//   - 单光源命中簇集合精确断言（构造已知几何：光源置于簇中心，半径小 → 恰 1 簇）
//   - 两光源 start/count 不重叠分配（平坦表段逐段核对 + 内容精确断言）
//   - 球-AABB 相交边界用例（面相切/角相切/内部/外侧）
//   - 簇覆盖屏幕完整（每像素×视深采样都有合法归属簇）
//   - 方向光写全簇 / 光源越界（相机后/远平面外）零命中
//   - PackStartCount 打包往返
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

#include "zrender/cluster_lighting.h"

namespace {

// 测试网格：默认 16×9×16 / near 0.3 / far 60，视场 60°、宽高比 4:3（与 --lights 一致）
ClusterGrid MakeTestGrid()
{
    ClusterGrid grid;
    grid.tanHalfFovY = 0.57735026f; // tan(30°)
    grid.aspect = 4.0f / 3.0f;
    return grid;
}

/// 簇 (cx,cy,cz) 子视锥体段的几何中心点（必然落在该簇体内）：
/// 取切片深度中点 + NDC 瓦片中点反投影
void ClusterCenter(const ClusterGrid& grid, uint32_t cx, uint32_t cy, uint32_t cz, float out[3])
{
    const float zSpan = grid.farZ - grid.nearZ;
    const float zm = grid.nearZ
        + zSpan * (static_cast<float>(cz) + 0.5f) / static_cast<float>(grid.dimZ);
    const float ndcX = -1.0f + 2.0f * (static_cast<float>(cx) + 0.5f) / static_cast<float>(grid.dimX);
    const float v = (static_cast<float>(cy) + 0.5f) / static_cast<float>(grid.dimY);
    const float ndcY = 1.0f - 2.0f * v;
    out[0] = ndcX * zm * grid.aspect * grid.tanHalfFovY;
    out[1] = ndcY * zm * grid.tanHalfFovY;
    out[2] = zm;
}

LightData MakePointLight(float x, float y, float z, float radius, float intensity)
{
    LightData l {};
    l.pos[0] = x;
    l.pos[1] = y;
    l.pos[2] = z;
    l.radius = radius;
    l.color[0] = 1.0f;
    l.color[1] = 1.0f;
    l.color[2] = 1.0f;
    l.intensity = intensity;
    l.kind = kZrLightPoint;
    return l;
}

/// 簇索引布局（与 ClusterIndexFromUv / pbr_forward.hlsl 一致）
uint32_t ClusterIndexOf(const ClusterGrid& grid, uint32_t cx, uint32_t cy, uint32_t cz)
{
    return (cz * grid.dimY + cy) * grid.dimX + cx;
}

/// 期望命中簇集合——测试内独立实现（切片/反投影公式与球-AABB 最近点判定全部
/// 重写，不调用 ClusterGrid::ClusterAabb/SphereAabbIntersect，双重实现互证）。
/// 返回升序簇索引；含保守 AABB 近似（与被测实现同语义：球对簇"包围盒"相交）。
std::vector<uint32_t> ExpectedHitClusters(
    const ClusterGrid& grid, const float pos[3], float radius)
{
    std::vector<uint32_t> hits;
    const float scaleX = grid.aspect * grid.tanHalfFovY;
    const float scaleY = grid.tanHalfFovY;
    const float zSpan = grid.farZ - grid.nearZ;
    for (uint32_t cz = 0; cz < grid.dimZ; ++cz) {
        const float z0 = grid.nearZ + zSpan * static_cast<float>(cz) / static_cast<float>(grid.dimZ);
        const float z1
            = grid.nearZ + zSpan * static_cast<float>(cz + 1u) / static_cast<float>(grid.dimZ);
        for (uint32_t cy = 0; cy < grid.dimY; ++cy) {
            const float ndcY0 = 1.0f - 2.0f * static_cast<float>(cy + 1u) / static_cast<float>(grid.dimY);
            const float ndcY1 = 1.0f - 2.0f * static_cast<float>(cy) / static_cast<float>(grid.dimY);
            for (uint32_t cx = 0; cx < grid.dimX; ++cx) {
                const float ndcX0 = -1.0f + 2.0f * static_cast<float>(cx) / static_cast<float>(grid.dimX);
                const float ndcX1
                    = -1.0f + 2.0f * static_cast<float>(cx + 1u) / static_cast<float>(grid.dimX);
                // 8 顶点全枚举取包围盒（x = ndcX*z*scaleX, y = ndcY*z*scaleY）
                float bmin[3] = {1e30f, 1e30f, z0};
                float bmax[3] = {-1e30f, -1e30f, z1};
                const float ndcXs[2] = {ndcX0, ndcX1};
                const float ndcYs[2] = {ndcY0, ndcY1};
                for (const float ndcX : ndcXs) {
                    for (const float z : {z0, z1}) {
                        const float x = ndcX * z * scaleX;
                        bmin[0] = (x < bmin[0]) ? x : bmin[0];
                        bmax[0] = (x > bmax[0]) ? x : bmax[0];
                    }
                }
                for (const float ndcY : ndcYs) {
                    for (const float z : {z0, z1}) {
                        const float y = ndcY * z * scaleY;
                        bmin[1] = (y < bmin[1]) ? y : bmin[1];
                        bmax[1] = (y > bmax[1]) ? y : bmax[1];
                    }
                }
                // 最近点距离平方（重写）
                float d2 = 0.0f;
                for (int axis = 0; axis < 3; ++axis) {
                    float d = pos[axis];
                    if (d < bmin[axis]) {
                        d = bmin[axis] - d;
                    } else if (d > bmax[axis]) {
                        d = d - bmax[axis];
                    } else {
                        d = 0.0f;
                    }
                    d2 += d * d;
                }
                if (d2 <= radius * radius) {
                    hits.push_back(ClusterIndexOf(grid, cx, cy, cz));
                }
            }
        }
    }
    return hits;
}

/// 跑一次分配并返回 {ranges, flat, flatCount}
struct AssignResult {
    std::vector<uint32_t> ranges;
    std::vector<uint32_t> flat;
    int32_t flatCount;
};

AssignResult RunAssign(const ClusterGrid& grid, const std::vector<LightData>& lights)
{
    AssignResult r;
    r.ranges.assign(grid.ClusterCount(), 0u);
    r.flat.assign(ZR_CLUSTER_FLAT_MAX, 0xFFFFFFFFu);
    r.flatCount = AssignLightsToClusters(grid, lights.data(),
        static_cast<uint32_t>(lights.size()), r.ranges.data(), r.flat.data(),
        static_cast<uint32_t>(r.flat.size()));
    return r;
}

} // namespace

// =============================================================================
// 打包与常量布局
// =============================================================================

TEST_CASE("lightdata: PackedLight 32B 布局 + kind 编码往返（点光/方向光）")
{
    static_assert(sizeof(PackedLight) == 32, "GPU 布局必须 32B（float4×2）");
    static_assert(ZR_MAX_LIGHTS * sizeof(PackedLight) == 1024, "CB1 光源数组 1024B");
    static_assert(ZR_CLUSTER_COUNT * 4 == 9216, "CB2 范围数组 9216B（简报 2304×4B）");

    // 点光：posRadius=(pos, radius)、colorIntensity=(color, intensity)
    LightData point = MakePointLight(1.0f, -2.0f, 3.5f, 4.5f, 2.0f);
    point.color[1] = 0.5f;
    PackedLight packed {};
    PackLights(&point, 1u, &packed);
    CHECK(packed.posRadius[0] == 1.0f);
    CHECK(packed.posRadius[1] == -2.0f);
    CHECK(packed.posRadius[2] == 3.5f);
    CHECK(packed.posRadius[3] == 4.5f); // 点光 w=radius（≥0）
    CHECK(packed.colorIntensity[0] == 1.0f);
    CHECK(packed.colorIntensity[1] == 0.5f);
    CHECK(packed.colorIntensity[2] == 1.0f);
    CHECK(packed.colorIntensity[3] == 2.0f);
    CHECK_FALSE(ZrIsDirectionalLight(packed));

    // 方向光：pos=方向直传，w=-1 哨兵（kind 编码进符号）
    LightData dir {};
    dir.pos[0] = 0.4f;
    dir.pos[1] = 0.8f;
    dir.pos[2] = 0.35f;
    dir.color[0] = 0.9f;
    dir.intensity = 1.25f;
    dir.kind = kZrLightDirectional;
    PackLights(&dir, 1u, &packed);
    CHECK(packed.posRadius[3] == -1.0f);
    CHECK(ZrIsDirectionalLight(packed));
    CHECK(packed.colorIntensity[3] == 1.25f);
}

TEST_CASE("lightdata: PackLights 超上限截断与空指针防御")
{
    PackedLight out[ZR_MAX_LIGHTS] = {};
    std::vector<LightData> many(ZR_MAX_LIGHTS + 4u, MakePointLight(0, 0, 1, 1, 1));
    PackLights(many.data(), static_cast<uint32_t>(many.size()), out); // 截断到 32，不越界写
    CHECK(out[ZR_MAX_LIGHTS - 1u].posRadius[2] == 1.0f);
    PackLights(nullptr, 1u, out); // 空指针：无副作用
    PackLights(many.data(), 1u, nullptr);
}

// =============================================================================
// 簇索引与屏幕覆盖
// =============================================================================

TEST_CASE("cluster: 像素→簇索引——边界像素归属 + 与 UV 入口一致")
{
    const ClusterGrid grid = MakeTestGrid();
    constexpr uint32_t w = 640u;
    constexpr uint32_t h = 480u;

    // 角像素：左上 (0,0) → (0,0,·)；右下 (639,479) → (15,8,·)
    CHECK((grid.ClusterIndexFromPixel(0u, 0u, 1.0f, w, h) % (ZR_CLUSTER_DIM_X * ZR_CLUSTER_DIM_Y))
        == 0u); // cx=0, cy=0
    const uint32_t br = grid.ClusterIndexFromPixel(639u, 479u, 1.0f, w, h);
    CHECK(br % ZR_CLUSTER_DIM_X == 15u);                     // cx=15
    CHECK((br / ZR_CLUSTER_DIM_X) % ZR_CLUSTER_DIM_Y == 8u); // cy=8

    // 像素入口 = UV 入口的中心采样（+0.5），两侧一致
    for (uint32_t py = 0; py < h; py += 97u) {
        for (uint32_t px = 0; px < w; px += 101u) {
            const float u = (static_cast<float>(px) + 0.5f) / static_cast<float>(w);
            const float v = (static_cast<float>(py) + 0.5f) / static_cast<float>(h);
            CHECK(grid.ClusterIndexFromPixel(px, py, 7.0f, w, h)
                == grid.ClusterIndexFromUv(u, v, 7.0f));
        }
    }
}

TEST_CASE("cluster: 簇覆盖屏幕完整——每像素×视深采样都有合法归属簇（含 clamp）")
{
    const ClusterGrid grid = MakeTestGrid();
    constexpr uint32_t w = 640u;
    constexpr uint32_t h = 480u;
    const float depths[6] = { 0.3f, 0.31f, 1.0f, 7.0f, 59.99f, 60.0f };
    for (uint32_t py = 0; py < h; ++py) {
        for (uint32_t px = 0; px < w; px += 5u) { // 步进 5 覆盖每行全部瓦片
            for (const float depth : depths) {
                const uint32_t idx = grid.ClusterIndexFromPixel(px, py, depth, w, h);
                CHECK(idx < grid.ClusterCount());
            }
        }
    }
    // 视深越界（比近平面更近/比远平面更远）也被 clamp 进合法簇
    CHECK(grid.ClusterIndexFromPixel(320u, 240u, 0.01f, w, h) < grid.ClusterCount());
    CHECK(grid.ClusterIndexFromPixel(320u, 240u, 100.0f, w, h) < grid.ClusterCount());
    // 近/远平面两端分别落到第 0 与最后一个切片
    CHECK(grid.ClusterIndexFromPixel(320u, 240u, 0.3f, w, h) / ZR_CLUSTER_DIM_X / ZR_CLUSTER_DIM_Y
        == 0u);
    CHECK(grid.ClusterIndexFromPixel(320u, 240u, 60.0f, w, h) / ZR_CLUSTER_DIM_X / ZR_CLUSTER_DIM_Y
        == ZR_CLUSTER_DIM_Z - 1u);
}

TEST_CASE("cluster: PackStartCount 打包往返（含边界值）")
{
    CHECK(ClusterGrid::PackStartCount(0u, 0u) == 0u);
    CHECK(ClusterGrid::UnpackStart(ClusterGrid::PackStartCount(14000u, 32u)) == 14000u);
    CHECK(ClusterGrid::UnpackCount(ClusterGrid::PackStartCount(14000u, 32u)) == 32u);
    CHECK(ClusterGrid::UnpackStart(ClusterGrid::PackStartCount(65535u, 65535u)) == 65535u);
    CHECK(ClusterGrid::UnpackCount(ClusterGrid::PackStartCount(65535u, 65535u)) == 65535u);
}

// =============================================================================
// 球-AABB 相交（边界用例）
// =============================================================================

TEST_CASE("cluster: 球-AABB 相交——面相切/角相切/内部/外侧精确判定")
{
    const float bmin[3] = { 0.0f, 0.0f, 0.0f };
    const float bmax[3] = { 1.0f, 1.0f, 1.0f };
    const float inside[3] = { 0.5f, 0.5f, 0.5f };
    CHECK(ClusterGrid::SphereAabbIntersect(inside, 0.1f, bmin, bmax)); // 内部

    const float belowFace[3] = { 0.5f, -1.0f, 0.5f };
    CHECK(ClusterGrid::SphereAabbIntersect(belowFace, 1.0f, bmin, bmax)); // 面相切（d²==r²）
    CHECK_FALSE(ClusterGrid::SphereAabbIntersect(belowFace, 0.999f, bmin, bmax)); // 差一点

    const float corner[3] = { 2.0f, 2.0f, 2.0f };
    const float cornerDist = std::sqrt(3.0f);
    CHECK(ClusterGrid::SphereAabbIntersect(corner, cornerDist, bmin, bmax)); // 角相切
    CHECK_FALSE(ClusterGrid::SphereAabbIntersect(corner, cornerDist - 0.001f, bmin, bmax));

    const float farAway[3] = { 5.0f, 0.5f, 0.5f };
    CHECK_FALSE(ClusterGrid::SphereAabbIntersect(farAway, 1.0f, bmin, bmax)); // 外侧
    CHECK(ClusterGrid::SphereAabbIntersect(farAway, 4.001f, bmin, bmax)); // 面外相切略过
}

// =============================================================================
// 簇分配（单光源精确断言 / 两光源不重叠 / 方向光全簇 / 越界零命中）
// =============================================================================

TEST_CASE("cluster: 单点光置于簇中心——命中簇集合与独立实现精确一致")
{
    const ClusterGrid grid = MakeTestGrid();
    const uint32_t target = ClusterIndexOf(grid, 8u, 4u, 2u); // (cx,cy,cz)=(8,4,2)
    float center[3];
    ClusterCenter(grid, 8u, 4u, 2u, center);
    // 半径取簇体半尺寸的 1/10（远小于到瓦片边界的距离）
    std::vector<LightData> lights { MakePointLight(center[0], center[1], center[2], 0.05f, 1.0f) };
    const AssignResult r = RunAssign(grid, lights);
    REQUIRE(r.flatCount >= 1);

    // 期望集合：测试内独立实现的几何判定（已知几何精确断言）
    const std::vector<uint32_t> expected
        = ExpectedHitClusters(grid, lights[0].pos, lights[0].radius);
    CHECK(expected.size() == 1u); // 簇中心+小半径：恰命中 1 簇（即目标簇）
    CHECK(expected[0] == target);
    CHECK(r.flatCount == static_cast<int32_t>(expected.size()));

    // 全网格逐簇与期望集合精确一致（count 与内容）
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        const uint32_t start = ClusterGrid::UnpackStart(r.ranges[c]);
        const uint32_t count = ClusterGrid::UnpackCount(r.ranges[c]);
        const uint32_t expectCount
            = (std::find(expected.begin(), expected.end(), c) != expected.end()) ? 1u : 0u;
        CHECK(count == expectCount);
        if (count == 1u) {
            CHECK(r.flat[start] == 0u); // 光源 0
        }
    }
}

TEST_CASE("cluster: 两点光不重叠分配——平坦段逐段核对 + 内容与独立实现精确一致")
{
    const ClusterGrid grid = MakeTestGrid();
    float a[3];
    float b[3];
    ClusterCenter(grid, 8u, 4u, 2u, a); // 簇 A
    ClusterCenter(grid, 3u, 2u, 1u, b); // 簇 B（与 A 不同）
    const uint32_t idxA = ClusterIndexOf(grid, 8u, 4u, 2u);
    const uint32_t idxB = ClusterIndexOf(grid, 3u, 2u, 1u);
    REQUIRE(idxA != idxB);

    std::vector<LightData> lights {
        MakePointLight(a[0], a[1], a[2], 0.05f, 1.0f), // 光源 0 → 簇 A
        MakePointLight(b[0], b[1], b[2], 0.05f, 1.0f), // 光源 1 → 簇 B
    };
    const AssignResult r = RunAssign(grid, lights);
    REQUIRE(r.flatCount >= 2);

    // 期望集合（独立实现；含保守 AABB 近似可能带来的相邻簇）
    const std::vector<uint32_t> expected0 = ExpectedHitClusters(grid, lights[0].pos, lights[0].radius);
    const std::vector<uint32_t> expected1 = ExpectedHitClusters(grid, lights[1].pos, lights[1].radius);
    CHECK(std::find(expected0.begin(), expected0.end(), idxA) != expected0.end());
    CHECK(std::find(expected1.begin(), expected1.end(), idxB) != expected1.end());
    CHECK(r.flatCount == static_cast<int32_t>(expected0.size() + expected1.size()));

    // 每簇段 [start, start+count) 互不重叠（平坦分配不重叠）且恰好铺满 [0, flatCount)
    std::vector<uint8_t> used(static_cast<size_t>(r.flatCount), 0u);
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        const uint32_t start = ClusterGrid::UnpackStart(r.ranges[c]);
        const uint32_t count = ClusterGrid::UnpackCount(r.ranges[c]);
        CHECK(start + count <= static_cast<uint32_t>(r.flatCount));
        // 内容与期望集合精确一致（段内按光源序升序：先 0 后 1）
        const bool expect0 = std::find(expected0.begin(), expected0.end(), c) != expected0.end();
        const bool expect1 = std::find(expected1.begin(), expected1.end(), c) != expected1.end();
        CHECK(count == (expect0 ? 1u : 0u) + (expect1 ? 1u : 0u));
        if (expect0) {
            CHECK(r.flat[start] == 0u);
        }
        if (expect1) {
            CHECK(r.flat[start + (expect0 ? 1u : 0u)] == 1u);
        }
        for (uint32_t j = 0; j < count; ++j) {
            CHECK(used[start + j] == 0u); // 无重叠
            used[start + j] = 1u;
        }
    }
    uint32_t usedTotal = 0u;
    for (const uint8_t u : used) {
        usedTotal += u;
    }
    CHECK(usedTotal == static_cast<uint32_t>(r.flatCount));
}

TEST_CASE("cluster: 两点光大半径交叠——公共簇 count=2 且两段仍不重叠")
{
    const ClusterGrid grid = MakeTestGrid();
    std::vector<LightData> lights {
        MakePointLight(0.0f, 0.0f, 3.0f, 6.0f, 1.0f),
        MakePointLight(0.5f, 0.5f, 5.0f, 6.0f, 1.0f),
    };
    const AssignResult r = RunAssign(grid, lights);
    REQUIRE(r.flatCount >= 2);

    // 不重叠核对
    std::vector<uint8_t> used(static_cast<size_t>(r.flatCount), 0u);
    uint32_t overlap2Count = 0u;
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        const uint32_t start = ClusterGrid::UnpackStart(r.ranges[c]);
        const uint32_t count = ClusterGrid::UnpackCount(r.ranges[c]);
        CHECK(start + count <= static_cast<uint32_t>(r.flatCount));
        for (uint32_t j = 0; j < count; ++j) {
            CHECK(used[start + j] == 0u);
            used[start + j] = 1u;
            CHECK(r.flat[start + j] < lights.size()); // 索引合法
        }
        if (count == 2u) {
            ++overlap2Count;
        }
    }
    // 两球交叠区非空 → 必有 count=2 的簇
    CHECK(overlap2Count > 0u);
}

TEST_CASE("cluster: 方向光写全簇——2304 簇各 1 项且平坦表确定")
{
    const ClusterGrid grid = MakeTestGrid();
    std::vector<LightData> lights;
    LightData dir {};
    dir.pos[0] = 0.3f;
    dir.pos[1] = 0.9f;
    dir.pos[2] = 0.2f;
    dir.intensity = 1.0f;
    dir.kind = kZrLightDirectional;
    lights.push_back(dir);
    const AssignResult r = RunAssign(grid, lights);
    REQUIRE(r.flatCount == static_cast<int32_t>(grid.ClusterCount()));
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        CHECK(ClusterGrid::UnpackCount(r.ranges[c]) == 1u);
        CHECK(ClusterGrid::UnpackStart(r.ranges[c]) == c); // 前缀连续
        CHECK(r.flat[c] == 0u);                            // 方向光索引
    }
}

TEST_CASE("cluster: 方向光+点光混合——方向光在前、点光次序确定")
{
    const ClusterGrid grid = MakeTestGrid();
    std::vector<LightData> lights;
    LightData dir {};
    dir.pos[0] = 0.0f;
    dir.pos[1] = 1.0f;
    dir.pos[2] = 0.0f;
    dir.kind = kZrLightDirectional;
    lights.push_back(dir);
    lights.push_back(MakePointLight(0.0f, 0.0f, 3.0f, 6.0f, 1.0f)); // 光源 1
    const AssignResult r = RunAssign(grid, lights);
    REQUIRE(r.flatCount > 0);
    // 每簇段首 = 方向光（0），其后（若有）为点光 1；段仍不重叠
    std::vector<uint8_t> used(static_cast<size_t>(r.flatCount), 0u);
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        const uint32_t start = ClusterGrid::UnpackStart(r.ranges[c]);
        const uint32_t count = ClusterGrid::UnpackCount(r.ranges[c]);
        REQUIRE(count >= 1u); // 方向光写全簇
        CHECK(r.flat[start] == 0u);
        for (uint32_t j = 0; j < count; ++j) {
            CHECK(used[start + j] == 0u);
            used[start + j] = 1u;
        }
    }
}

TEST_CASE("cluster: 光源越界零命中——相机后方 / 远平面外 / 空光源数组")
{
    const ClusterGrid grid = MakeTestGrid();
    // 相机后方（z<0）
    AssignResult r = RunAssign(grid, { MakePointLight(0.0f, 0.0f, -5.0f, 2.0f, 1.0f) });
    CHECK(r.flatCount == 0);
    // 远平面外（z=70 > farZ=60，球面也够不着 z=60 切片）
    r = RunAssign(grid, { MakePointLight(0.0f, 0.0f, 70.0f, 2.0f, 1.0f) });
    CHECK(r.flatCount == 0);
    // 零命中时全簇 count=0
    for (uint32_t c = 0; c < grid.ClusterCount(); ++c) {
        CHECK(ClusterGrid::UnpackCount(r.ranges[c]) == 0u);
    }
}

TEST_CASE("cluster: Assign 参数与预算报错（超 32 光源/超平坦容量/空指针）")
{
    const ClusterGrid grid = MakeTestGrid();
    std::vector<uint32_t> ranges(grid.ClusterCount(), 0u);
    std::vector<uint32_t> flat(ZR_CLUSTER_FLAT_MAX, 0u);
    std::vector<LightData> tooMany(ZR_MAX_LIGHTS + 1u, MakePointLight(0, 0, 3, 1, 1));
    CHECK(AssignLightsToClusters(grid, tooMany.data(), static_cast<uint32_t>(tooMany.size()),
              ranges.data(), flat.data(), static_cast<uint32_t>(flat.size()))
        < 0); // 超 32 光源报错
    std::vector<LightData> one { MakePointLight(0.0f, 0.0f, 3.0f, 1.0f, 1.0f) };
    CHECK(AssignLightsToClusters(grid, one.data(), 1u, nullptr, flat.data(),
              static_cast<uint32_t>(flat.size()))
        < 0); // 空指针
    CHECK(AssignLightsToClusters(grid, one.data(), 1u, ranges.data(), flat.data(), 0u)
        < 0); // 容量 0（1 项也放不下）
    CHECK(AssignLightsToClusters(grid, nullptr, 1u, ranges.data(), flat.data(),
              static_cast<uint32_t>(flat.size()))
        < 0);
    // 正常调用返回平坦总数
    CHECK(AssignLightsToClusters(grid, one.data(), 1u, ranges.data(), flat.data(),
              static_cast<uint32_t>(flat.size()))
        >= 1);
}

// =============================================================================
// 9. 簇网格容量上限（TASK-H-02 D1）
// =============================================================================
// AssignLightsToClusters 的工作缓冲是 ZR_CLUSTER_COUNT 项 static 数组，
// ClusterGrid::dimX/dimY/dimZ 为 public 可写成员——超容量的网格必须在写数组之前
// 被拒（返回码语义，Release 下不可依赖 assert）。
TEST_CASE("cluster: 簇网格超容量拒绝且不写输出数组（TASK-H-02 D1）")
{
    // 默认维度行为不变（回归护栏置于超限用例之前：超限用例之后不再依赖工作缓冲）
    {
        const ClusterGrid grid = MakeTestGrid();
        REQUIRE(grid.ClusterCount() == ZR_CLUSTER_COUNT);
        const std::vector<LightData> lights { MakePointLight(0.0f, 0.0f, 3.0f, 1.0f, 1.0f) };
        const AssignResult r = RunAssign(grid, lights);
        CHECK(r.flatCount >= 1);
    }

    const uint32_t kSentinel = 0xC0FFEEu;
    const std::vector<LightData> lights { MakePointLight(1000.0f, 1000.0f, 1000.0f, 0.0f, 1.0f) };
    std::vector<uint32_t> flat(ZR_CLUSTER_FLAT_MAX, kSentinel);

    // dimX=32 → 32×9×16 = 4608 > 2304：必须失败返回，且输出数组一项都不写
    {
        ClusterGrid tooBig = MakeTestGrid();
        tooBig.dimX = 32u;
        REQUIRE(tooBig.ClusterCount() == 4608u);
        std::vector<uint32_t> ranges(tooBig.ClusterCount(), kSentinel);
        const int32_t rc = AssignLightsToClusters(tooBig, lights.data(), 1u, ranges.data(),
            flat.data(), static_cast<uint32_t>(flat.size()));
        CHECK(rc == -3); // 契约返回码（容量不足），不是笼统的"<0"
        CHECK(ranges[0] == kSentinel);
        CHECK(ranges[ZR_CLUSTER_COUNT - 1u] == kSentinel);
        CHECK(ranges[ranges.size() - 1u] == kSentinel);
        CHECK(flat[0] == kSentinel);
    }

    // dimY 单轴放大同样拒绝（45×9×16 = 6480）
    {
        ClusterGrid tall = MakeTestGrid();
        tall.dimY = 45u;
        std::vector<uint32_t> ranges(tall.ClusterCount(), kSentinel);
        CHECK(AssignLightsToClusters(tall, lights.data(), 1u, ranges.data(), flat.data(),
                  static_cast<uint32_t>(flat.size()))
            == -3);
        CHECK(ranges[0] == kSentinel);
    }

    // 32 位乘积极大（65536×65536×65536 会回绕为 0）：64 位判据下同样拒绝
    {
        ClusterGrid wrap = MakeTestGrid();
        wrap.dimX = 65536u;
        wrap.dimY = 65536u;
        wrap.dimZ = 65536u;
        std::vector<uint32_t> ranges(16u, kSentinel);
        CHECK(AssignLightsToClusters(wrap, lights.data(), 1u, ranges.data(), flat.data(),
                  static_cast<uint32_t>(flat.size()))
            == -3);
        CHECK(ranges[0] == kSentinel);
    }

    // 维度为 0（除零/空簇表）同样拒绝
    {
        ClusterGrid zeroDim = MakeTestGrid();
        zeroDim.dimZ = 0u;
        std::vector<uint32_t> ranges(8u, kSentinel);
        CHECK(AssignLightsToClusters(zeroDim, lights.data(), 1u, ranges.data(), flat.data(),
                  static_cast<uint32_t>(flat.size()))
            == -3);
        CHECK(ranges[0] == kSentinel);
    }
}
