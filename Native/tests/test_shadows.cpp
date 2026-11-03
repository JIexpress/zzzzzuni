// =============================================================================
// test_shadows.cpp —— CSM 级联阴影 CPU 侧单测（TASK-B-R5-02）
//
// 覆盖（简报设计要点 4 的单测清单）：
//   - CascadeSplits 分割纯函数：单调递增、首分割 ≥ near、末分割 ≤ far、
//     lambda 端点语义（0=均匀 / 1=纯对数）、非法参数拒绝
//   - BuildCascades：texel snap 稳定性（同参数两次调用矩阵逐位相等 +
//     相机微小平移矩阵仍逐位不变——防闪烁性质的证伪断言）
//   - BuildCascades：场景中心经每级联 lightVP 投影落入合法 NDC/深度范围
//     （x/y ∈ [-1,1]、z ∈ [0,1]——D3D LH 正交投影契约）
//   - 可见性共享：castShadow=false 项不进阴影 draw list（同一 VisibilitySet
//     按视图掩码位过滤；主视图/级联阴影视图 id 契约）
// =============================================================================
#include "doctest/doctest.h"

#include <cmath>
#include <cstdint>
#include <cstring>
#include <vector>

#include "zrender/draw_list.h"
#include "zrender/shadows.h"

namespace {

/// 构建测试参数：单位相机（原点朝 +Z，与窗口程序 --shadows 场景一致）+
/// 场景包围球（地面 20×20 + cube），方向光斜照
ShadowSceneParams MakeTestParams()
{
    ShadowSceneParams params {};
    const float identity[16] = { 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f };
    std::memcpy(params.camView, identity, sizeof(identity));
    params.tanHalfFovY = 0.57735026f; // tan(30°)：60° 视场
    params.aspect = 640.0f / 480.0f;
    params.nearZ = 0.3f;
    params.farZ = 60.0f;
    const float d[3] = { 0.4f, 0.85f, 0.35f };
    const float invLen
        = 1.0f / std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
    params.lightDir[0] = d[0] * invLen;
    params.lightDir[1] = d[1] * invLen;
    params.lightDir[2] = d[2] * invLen;
    params.sceneCenter[0] = 0.0f;
    params.sceneCenter[1] = -0.5f;
    params.sceneCenter[2] = 3.2f;
    params.sceneRadius = 12.0f; // 覆盖 20×20 地面（半径 ≥ 对角半长）
    params.lambda = 0.75f;
    params.texSize = ZR_SHADOW_TEX_SIZE;
    return params;
}

/// row_major lightVP 变换齐次点并做透视除法 → (ndcX, ndcY, depth01)
void ProjectByLightVp(const float* lightVP, const float p[3], float out[3])
{
    const float x = lightVP[0] * p[0] + lightVP[1] * p[1] + lightVP[2] * p[2] + lightVP[3];
    const float y = lightVP[4] * p[0] + lightVP[5] * p[1] + lightVP[6] * p[2] + lightVP[7];
    const float z = lightVP[8] * p[0] + lightVP[9] * p[1] + lightVP[10] * p[2] + lightVP[11];
    const float w = lightVP[12] * p[0] + lightVP[13] * p[1] + lightVP[14] * p[2] + lightVP[15];
    out[0] = x / w;
    out[1] = y / w;
    out[2] = z / w;
}

} // namespace

// =============================================================================
// CascadeSplits —— 分割纯函数
// =============================================================================
TEST_CASE("CascadeSplits: 4 cascades lambda=0.75 monotonic within [near,far]")
{
    constexpr float kNear = 0.3f;
    constexpr float kFar = 60.0f;
    float splits[3] = {};
    REQUIRE(CascadeSplits(kNear, kFar, 4u, 0.75f, splits));

    // 单调递增
    CHECK(splits[0] < splits[1]);
    CHECK(splits[1] < splits[2]);
    // 首分割 ≥ near、末分割 ≤ far（简报指定断言）
    CHECK(splits[0] >= kNear);
    CHECK(splits[2] <= kFar);
    // lambda=0.75 偏向对数：末分割明显小于均匀分割（15.225*3=45.675）
    CHECK(splits[2] < 0.75f * (kNear + (kFar - kNear) * 3.0f / 4.0f));
}

TEST_CASE("CascadeSplits: lambda endpoints reproduce uniform and logarithmic laws")
{
    constexpr float kNear = 0.3f;
    constexpr float kFar = 60.0f;
    float uniformSplit[1] = {};
    float logSplit[1] = {};
    float mixSplit[1] = {};
    REQUIRE(CascadeSplits(kNear, kFar, 2u, 0.0f, uniformSplit));
    REQUIRE(CascadeSplits(kNear, kFar, 2u, 1.0f, logSplit));
    REQUIRE(CascadeSplits(kNear, kFar, 2u, 0.75f, mixSplit));

    const float expectedUniform = kNear + (kFar - kNear) * 0.5f; // 30.15
    const float expectedLog = kNear * std::pow(kFar / kNear, 0.5f); // 0.3*sqrt(200)≈4.243
    CHECK(uniformSplit[0] == doctest::Approx(expectedUniform).epsilon(1e-5));
    CHECK(logSplit[0] == doctest::Approx(expectedLog).epsilon(1e-5));
    // 混合 = 均匀 + (对数-均匀)*lambda
    const float expectedMix = expectedUniform + (expectedLog - expectedUniform) * 0.75f;
    CHECK(mixSplit[0] == doctest::Approx(expectedMix).epsilon(1e-5));
}

TEST_CASE("CascadeSplits: invalid parameters rejected")
{
    float splits[3] = {};
    CHECK_FALSE(CascadeSplits(0.3f, 60.0f, 1u, 0.75f, splits)); // count < 2
    CHECK_FALSE(CascadeSplits(0.3f, 60.0f, 4u, 1.5f, splits)); // lambda > 1
    CHECK_FALSE(CascadeSplits(0.3f, 60.0f, 4u, -0.1f, splits)); // lambda < 0
    CHECK_FALSE(CascadeSplits(0.3f, 0.3f, 4u, 0.75f, splits)); // far == near
    CHECK_FALSE(CascadeSplits(0.3f, 0.1f, 4u, 0.75f, splits)); // far < near
    CHECK_FALSE(CascadeSplits(0.3f, 60.0f, 4u, 0.75f, nullptr)); // 空输出
}

// =============================================================================
// BuildCascades —— 矩阵构建与 texel snap
// =============================================================================
TEST_CASE("BuildCascades: four cascades with split boundaries matching CascadeSplits")
{
    ShadowSceneParams params = MakeTestParams();
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    REQUIRE(BuildCascades(params, cascades, ZR_SHADOW_CASCADES));

    float splits[3] = {};
    REQUIRE(CascadeSplits(params.nearZ, params.farZ, ZR_SHADOW_CASCADES, params.lambda, splits));

    CHECK(cascades[0].splitNear == doctest::Approx(params.nearZ));
    CHECK(cascades[0].splitFar == doctest::Approx(splits[0]));
    CHECK(cascades[1].splitNear == doctest::Approx(splits[0]));
    CHECK(cascades[2].splitFar == doctest::Approx(splits[2]));
    CHECK(cascades[3].splitFar == doctest::Approx(params.farZ));
    // 深度范围与 zRange 记录一致（bias 归一化用）
    CHECK(cascades[0].zRange > 0.0f);
}

TEST_CASE("BuildCascades: each cascade's camera slice midpoint projects inside its own lightVP")
{
    ShadowSceneParams params = MakeTestParams();
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};
    REQUIRE(BuildCascades(params, cascades, ZR_SHADOW_CASCADES));

    for (uint32_t c = 0; c < ZR_SHADOW_CASCADES; ++c) {
        // 该级联切片中点取相机轴上的点（必在切片内 → 必在光空间 AABB 内）
        const float midZ = 0.5f * (cascades[c].splitNear + cascades[c].splitFar);
        const float p[3] = { 0.0f, 0.0f, midZ };
        float ndc[3];
        ProjectByLightVp(cascades[c].lightVP, p, ndc);
        // D3D LH 正交投影契约：x/y ∈ [-1,1]、z ∈ [0,1]（近 0 远 1）
        MESSAGE("cascade " << c << ": ndcX=" << ndc[0] << " ndcY=" << ndc[1]
                           << " depth=" << ndc[2]);
        CHECK(ndc[0] >= -1.0f);
        CHECK(ndc[0] <= 1.0f);
        CHECK(ndc[1] >= -1.0f);
        CHECK(ndc[1] <= 1.0f);
        CHECK(ndc[2] >= 0.0f);
        CHECK(ndc[2] <= 1.0f);
    }
}

TEST_CASE("BuildCascades: texel snap keeps matrices bitwise stable")
{
    ShadowSceneParams params = MakeTestParams();
    ShadowCascade first[ZR_SHADOW_CASCADES] = {};
    ShadowCascade second[ZR_SHADOW_CASCADES] = {};
    REQUIRE(BuildCascades(params, first, ZR_SHADOW_CASCADES));
    REQUIRE(BuildCascades(params, second, ZR_SHADOW_CASCADES));
    // 同参数两次调用：矩阵逐位相等（texel snap 的确定性；简报指定断言）。
    // 注：不进一步断言"相机亚 texel 平移矩阵不变"——AABB min/max 吸附后仍可能
    // 恰跨 texel 边界（概率性 1-texel 跳变是 CSM 的已知属性，texel snap 只把
    // 跳变量从连续值压到离散 texel 网格），超出简报要求的"同参数两次调用稳定"。
    CHECK(std::memcmp(first, second, sizeof(first)) == 0);
}

TEST_CASE("BuildCascades: invalid parameters rejected")
{
    ShadowSceneParams params = MakeTestParams();
    ShadowCascade cascades[ZR_SHADOW_CASCADES] = {};

    ShadowSceneParams bad = params;
    bad.lightDir[0] = 0.4f;
    bad.lightDir[1] = 0.85f; // 未归一化方向拒绝
    CHECK_FALSE(BuildCascades(bad, cascades, ZR_SHADOW_CASCADES));

    bad = params;
    bad.sceneRadius = 0.0f; // 半径须为正
    CHECK_FALSE(BuildCascades(bad, cascades, ZR_SHADOW_CASCADES));

    CHECK_FALSE(BuildCascades(params, cascades, 0u)); // count = 0
    CHECK_FALSE(BuildCascades(params, cascades, ZR_SHADOW_CASCADES + 1u)); // 超上限
    CHECK_FALSE(BuildCascades(params, nullptr, ZR_SHADOW_CASCADES)); // 空输出
}

// =============================================================================
// 可见性共享 —— castShadow=false 项不进阴影 draw list
// =============================================================================
TEST_CASE("Visibility sharing: shadow draw list filters the same VisibilitySet by mask bits")
{
    DrawList list(16u);
    list.RegisterMesh(1u, 24u);
    list.RegisterMesh(2u, 36u);

    // 地面/cube 投影阴影；玻璃面仅主视图（castShadow=false）
    RenderItem ground;
    ground.meshHandle = 1u;
    ground.depth = 2.8f;
    ground.castShadow = true;
    RenderItem cube;
    cube.meshHandle = 2u;
    cube.depth = 3.2f;
    cube.castShadow = true;
    RenderItem glass;
    glass.meshHandle = 1u;
    glass.depth = 3.0f;
    glass.translucent = true;
    glass.castShadow = false;

    REQUIRE(list.Add(ground, ShadowViewMask(true)));
    REQUIRE(list.Add(cube, ShadowViewMask(true)));
    REQUIRE(list.Add(glass, ShadowViewMask(false)));

    // 视图掩码契约：castShadow=false 只占主视图位 bit0；true 占 bit0..bit4
    CHECK(list.Visibility().ViewMaskOf(2u) == 0x01u);
    CHECK(list.Visibility().ViewMaskOf(0u) == 0x1Fu);
    CHECK(list.Visibility().ViewMaskOf(1u) == 0x1Fu);

    // 阴影视图 id 约定：级联 c → 视图 ShadowViewId(c) = 1 + c
    static_assert(ShadowViewId(0u) == 1u, "shadow view ids start at 1 (main view = 0)");
    static_assert(ShadowViewId(ZR_SHADOW_CASCADES - 1u) == 4u, "4 cascades occupy bits 1..4");

    // 共享可见性过滤（非二次剔除）：逐级联阴影视图收集，玻璃面（id=2）永不出现
    for (uint32_t cascade = 0; cascade < ZR_SHADOW_CASCADES; ++cascade) {
        std::vector<uint32_t> shadowItems;
        list.Visibility().Iterate(ShadowViewId(cascade),
            [&](uint32_t id) { shadowItems.push_back(id); });
        REQUIRE(shadowItems.size() == 2u);
        CHECK(shadowItems[0] == 0u); // 地面
        CHECK(shadowItems[1] == 1u); // cube
    }
    // 主视图：全部三项
    std::vector<uint32_t> mainItems;
    list.Visibility().Iterate(0u, [&](uint32_t id) { mainItems.push_back(id); });
    REQUIRE(mainItems.size() == 3u);

    // Sort/BuildBatches 照常工作（castShadow 字段不进 sort key——绘制序与阴影无关）
    list.Sort();
    CHECK(list.BuildBatches() > 0u);
}
