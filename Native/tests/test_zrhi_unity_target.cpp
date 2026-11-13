// =============================================================================
// test_zrhi_unity_target.cpp —— Unity 共享渲染目标单测（TASK-B-UNITY-02）
//
// 覆盖（全部无需 GPU：本机/CI 都能跑）：
//   1. 参数校验：GetStats / GetSharedTargetHandle 的空指针 → ZR_UNITY_ERR_NULL
//   2. 未接入设备时的明确错误：Create/Render → ZR_UNITY_ERR_NO_DEVICE，
//      取句柄 → ZR_UNITY_ERR_STATE 且写 0（而不是返回野句柄）
//   3. 尺寸校验：0 维 / 超上限 → ZR_UNITY_ERR_ARGS
//   4. 接入入口的空指针守卫
//   5. **嵌入式 first_draw.hlsl 与 Native/shaders/first_draw.hlsl 逐字节一致**
//      —— DLL 在 Player 里没有仓库路径，只能内嵌副本；这条用例守住两份不漂移
//
// TASK-B-PLANET-05 追加：行星相机原点 C ABI（zplugin.h 行星声明块组 1，存取 +
// 读回；RenderFrame 消费属 B-PLANET-04，不在本组）：
//   6. GetCameraOrigin 初始零 + 空指针拒绝（设计稿 §3.3/§11-05）
//   7. Set/Get round-trip 逐位（含 6.4e6m 量级 / 负零 / DBL_MAX——只拒 NaN/Inf）
//   8. NaN / Inf 任一分量拒绝（ZR_UNITY_ERR_ARGS，状态不变）
//   9. 跨帧持久：Begin/EndScene ×2 与 Begin/EndFrame ×2 均不清
//
// TASK-B-PLANET-06 追加：行星上下文 + 高度采样 C ABI（zplugin.h 行星声明块组 2/组 4，
// 设计稿 §6.1/§11-06；SetWorld 快照 + SampleHeight 纯函数同步采样，消费 zplanet
// 生产头 PlanetTerrainHeightM——与 chunk 顶点生成同一函数，§6.3「同一函数同一值」）：
//   10. SetWorld 前置态 ERR_STATE + 校验序全表（NULL→ABI→NONFINITE；拒绝不动快照；
//       七码组适用面核对——半径不入参/seed 全域合法，RANGE/HANDLE/FULL 不适用）
//   11. **SampleHeight(C+dir·R) 与 chunk 顶点 heightM 逐位一致**（§10 补充判据，
//       本任务最重要的牙）：生产头 ChunkVertexIndex/PlanetChunkVertexAtGlobal
//       规范路由生成顶点，逐顶点 memcmp 比对（近档整 chunk 4096 点）
//   12. 逐位一致扩展：共享边邻居侧全局索引 + 第二面近档 + 远档 chunk +
//       非零行星中心（float 输出粒度吸收 normalize 的 1-2 ULP double 域方向漂移）
//   13. 同种子确定性逐位 + 快照幂等替换 + 拒绝不清快照 + 种子可配（过半不同）+
//       中心生效（C=(0,-R,0) 时 pos=(0,0,0) 方向=(0,1,0)，与生产头直接求值一致）
//   14. 方向语义径向不变（幂 2 缩放全链路指数平移 → 逐位必等；§6.3 输出语义）+
//       退化方向拒绝（pos ≡ 中心零向量 → ERR_RANGE；错误路径不写 out）
//   15. 任意线程并发冒烟（§8/D5——4 读线程共享锁快照 + 1 同值 SetWorld 写者，
//       读结果与单线程基准逐位一致；撕裂读会被 memcmp 捕获）
//
// 真渲染（建设备 → 共享纹理 → 回读中心像素）需要真实 GPU，属验证程序
// zrhi_unity_bridge / Unity Player 的职责，不进 doctest。
// =============================================================================
#include "doctest/doctest.h"

#include "zplanet/planet_math.h" // TASK-B-PLANET-06：chunk 顶点规范路由 + PlanetTerrainHeightM（生产头）
#include "zplugin/zplugin.h"
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <atomic>
#include <cstddef>
#include <cstring>
#include <fstream>
#include <limits>
#include <string>
#include <thread>
#include <vector>

namespace {

/// 读整个文件（二进制）；失败返回 false
bool ReadWholeFile(const char* path, std::string& out)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        return false;
    }
    const std::streampos length = file.tellg();
    if (length <= 0) {
        return false;
    }
    file.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(length));
    return static_cast<bool>(file.read(&out[0], length));
}

/// CRLF → LF（仓库规范是 LF，但 git autocrlf 可能让检出件带 CR）
std::string NormalizeLineEndings(const std::string& text)
{
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0u; i < text.size(); ++i) {
        if (text[i] == '\r') {
            continue;
        }
        out.push_back(text[i]);
    }
    return out;
}

} // namespace

TEST_CASE("unity_target: 空指针参数一律明确拒绝")
{
    CHECK(ZR_Unity_GetTargetStats(nullptr) == ZR_UNITY_ERR_NULL);
    CHECK(ZR_Unity_GetSharedTargetHandle(nullptr) == ZR_UNITY_ERR_NULL);
    CHECK(ZR_Unity_AttachDeviceFromTexture(nullptr) == ZR_UNITY_ERR_NULL);
}

TEST_CASE("unity_target: 未接入宿主设备时的错误码")
{
    // 进程内单例：本用例集合在不接入设备的进程里跑，状态即"未接入"
    CHECK(zr::unity_target::HasTarget() == false);

    CHECK(ZR_Unity_CreateSharedTarget(640u, 480u) == ZR_UNITY_ERR_NO_DEVICE);
    CHECK(ZR_Unity_RenderFrame() == ZR_UNITY_ERR_NO_DEVICE);

    uint64_t handle = 0xDEADBEEFu;
    CHECK(ZR_Unity_GetSharedTargetHandle(&handle) == ZR_UNITY_ERR_STATE);
    CHECK(handle == 0u); // 失败必须归零，绝不能把哨兵值当句柄带出去
}

TEST_CASE("unity_target: 尺寸校验在碰设备之前就拦住")
{
    // 0 维与超上限都在参数层拒绝（不能依赖后端的行为去兜）
    CHECK(ZR_Unity_CreateSharedTarget(0u, 480u) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_CreateSharedTarget(640u, 0u) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_CreateSharedTarget(0u, 0u) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_CreateSharedTarget(16385u, 480u) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_CreateSharedTarget(640u, 16385u) == ZR_UNITY_ERR_ARGS);
}

TEST_CASE("unity_target: 释放是幂等的（未创建时调用不炸）")
{
    ZR_Unity_ReleaseSharedTarget();
    ZR_Unity_ReleaseSharedTarget();
    CHECK(zr::unity_target::HasTarget() == false);
}

TEST_CASE("unity_target: 统计结构体布局是 POD 定宽约定")
{
    // structSize 由实现填；托管侧按同一布局封送（见 ZrNative.cs 的 ZrUnityTargetStatsNative）
    CHECK(sizeof(ZrUnityTargetStats) == 48u);
    CHECK(offsetof(ZrUnityTargetStats, centerRGBA) == 24u);
    CHECK(offsetof(ZrUnityTargetStats, sharedHandle) == 40u);

    ZrUnityTargetStats stats {};
    CHECK(ZR_Unity_GetTargetStats(&stats) == ZR_UNITY_OK);
    CHECK(stats.structSize == sizeof(ZrUnityTargetStats));
}

TEST_CASE("unity_target: 内嵌 first_draw.hlsl 与仓库 shader 逐字节一致")
{
    uint32_t embeddedSize = 0u;
    const char* embedded = zr::unity_target::EmbeddedFirstDrawHlsl(&embeddedSize);
    REQUIRE(embedded != nullptr);
    REQUIRE(embeddedSize > 0u);

    // 用例工作目录 = 仓库根（CMake add_test 的 WORKING_DIRECTORY），与
    // test_zmesh_gallery / test_zrhi_vulkan_spv 同一约定；另备仓库根相对候选
    const char* candidates[] = {"Native/shaders/first_draw.hlsl"};
    std::string fileText;
    for (const char* candidate : candidates) {
        if (ReadWholeFile(candidate, fileText)) {
            break;
        }
    }
    INFO("shader path tried: Native/shaders/first_draw.hlsl (cwd = repo root expected)");
    REQUIRE(!fileText.empty());

    const std::string normalized = NormalizeLineEndings(fileText);
    const std::string embeddedText(embedded, embeddedSize);
    CHECK(normalized.size() == embeddedText.size());
    CHECK(normalized == embeddedText);
}

// ---- TASK-B-PLANET-05：行星相机原点 C ABI（设计稿 §3.3/§11-05 指定 doctest）----
//
// 协议要点（设计稿 §3.3，权威文本 §14 组 1）：NaN/Inf 任一分量 → ZR_UNITY_ERR_ARGS；
// Get 空指针 → ZR_UNITY_ERR_NULL；初始 (0,0,0)；跨帧持久（BeginScene 不清，对照
// zplugin.h「清空绘制表；相机与网格句柄表保留」）；同帧多次调用最后一个生效。
//
// 用例顺序说明：doctest 缺省 order-by=file（文件内按行号序），初始零只在本文件
// 首个行星用例验一次（全仓只有本组用例写原点状态），其余用例一律自 Set 起步，
// 不依赖跨用例执行序。

TEST_CASE("planet abi: GetCameraOrigin 初始零与空指针拒绝")
{
    // 进程内原点状态的首读：非零哨兵，实现必须逐元素覆写为 (0,0,0)
    double origin[3] = {1.0, 2.0, 3.0};
    REQUIRE(ZR_Unity_GetCameraOrigin(origin) == ZR_UNITY_OK);
    CHECK(origin[0] == 0.0);
    CHECK(origin[1] == 0.0);
    CHECK(origin[2] == 0.0);

    CHECK(ZR_Unity_GetCameraOrigin(nullptr) == ZR_UNITY_ERR_NULL);
}

TEST_CASE("planet abi: Set/Get round-trip 逐位（6.4e6m 量级 + 负零 + 无幅值钳制）")
{
    struct OriginCase {
        const char* label;
        double x, y, z;
    };
    const OriginCase cases[] = {
        {"零向量（合法初始态同值）", 0.0, 0.0, 0.0},
        {"常规小值", 1.0, -2.5, 0.125},
        {"行星半径量级 6.4e6m（kPlanetRadiusM = 6.371e6，§1.1 口径）",
            -6.4e6, 6.4e6, 6.371e6},
        {"B-TECH-04 maxOriginOffset 9.01e6m（§3.2 口径）", 9.01e6, -9.01e6, 4.0e6 + 0.5},
        {"负零（符号位逐位保持）", -0.0, 0.0, -0.0},
        {"DBL_MAX（协议只拒 NaN/Inf，不做幅值钳制）",
            std::numeric_limits<double>::max(), -std::numeric_limits<double>::max(), 0.0},
    };
    for (const OriginCase& c : cases) {
        INFO(c.label);
        REQUIRE(ZR_Unity_SetCameraOrigin(c.x, c.y, c.z) == ZR_UNITY_OK);
        double out[3] = {0.0, 0.0, 0.0};
        REQUIRE(ZR_Unity_GetCameraOrigin(out) == ZR_UNITY_OK);
        // 逐位比较（验收口径就是位形一致：尾数位 / 符号位全保留，不用 == 表达）
        CHECK(std::memcmp(&out[0], &c.x, sizeof(double)) == 0);
        CHECK(std::memcmp(&out[1], &c.y, sizeof(double)) == 0);
        CHECK(std::memcmp(&out[2], &c.z, sizeof(double)) == 0);
    }

    // 同帧多次调用：最后一个生效（§3.3）
    CHECK(ZR_Unity_SetCameraOrigin(1.0, 2.0, 3.0) == ZR_UNITY_OK);
    CHECK(ZR_Unity_SetCameraOrigin(4.5, -6.25, 7.0) == ZR_UNITY_OK);
    double last[3] = {0.0, 0.0, 0.0};
    REQUIRE(ZR_Unity_GetCameraOrigin(last) == ZR_UNITY_OK);
    CHECK(last[0] == 4.5);
    CHECK(last[1] == -6.25);
    CHECK(last[2] == 7.0);
}

TEST_CASE("planet abi: NaN 任一分量拒绝（ERR_ARGS，状态保持上次成功值）")
{
    const double qnan = std::numeric_limits<double>::quiet_NaN();
    const double snan = std::numeric_limits<double>::signaling_NaN();
    REQUIRE(ZR_Unity_SetCameraOrigin(10.0, 20.0, 30.0) == ZR_UNITY_OK);

    CHECK(ZR_Unity_SetCameraOrigin(qnan, 0.0, 0.0) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_SetCameraOrigin(0.0, qnan, 0.0) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_SetCameraOrigin(0.0, 0.0, qnan) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_SetCameraOrigin(snan, 0.0, 0.0) == ZR_UNITY_ERR_ARGS);

    double out[3] = {0.0, 0.0, 0.0};
    REQUIRE(ZR_Unity_GetCameraOrigin(out) == ZR_UNITY_OK);
    CHECK(out[0] == 10.0);
    CHECK(out[1] == 20.0);
    CHECK(out[2] == 30.0);
}

TEST_CASE("planet abi: Inf 任一分量拒绝（ERR_ARGS，状态保持上次成功值）")
{
    const double inf = std::numeric_limits<double>::infinity();
    REQUIRE(ZR_Unity_SetCameraOrigin(-11.5, 22.25, 33.0) == ZR_UNITY_OK);

    CHECK(ZR_Unity_SetCameraOrigin(inf, 0.0, 0.0) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_SetCameraOrigin(0.0, -inf, 0.0) == ZR_UNITY_ERR_ARGS);
    CHECK(ZR_Unity_SetCameraOrigin(0.0, 0.0, inf) == ZR_UNITY_ERR_ARGS);

    double out[3] = {0.0, 0.0, 0.0};
    REQUIRE(ZR_Unity_GetCameraOrigin(out) == ZR_UNITY_OK);
    CHECK(out[0] == -11.5);
    CHECK(out[1] == 22.25);
    CHECK(out[2] == 33.0);
}

TEST_CASE("planet abi: 跨帧持久（Begin/EndScene 与 Begin/EndFrame 均不清）")
{
    REQUIRE(ZR_Unity_SetCameraOrigin(1234.5, -6789.25, 6.4e6) == ZR_UNITY_OK);

    // 场景帧边界 ×2（无设备时返回 NO_DEVICE——不论返回值，都不得触碰原点；
    // 原点不属于 BeginScene 的清空范围，与「相机与网格句柄表保留」同侧）
    ZR_Unity_BeginScene();
    ZR_Unity_EndScene();
    ZR_Unity_BeginScene();
    ZR_Unity_EndScene();

    // 宿主帧边界 ×2（B-UNITY-01 桥的 BeginFrame/EndFrame；同样不得清）
    ZR_Unity_BeginFrame(nullptr, 0u, 0u);
    ZR_Unity_EndFrame();
    ZR_Unity_BeginFrame(nullptr, 0u, 0u);
    ZR_Unity_EndFrame();

    const double expected[3] = {1234.5, -6789.25, 6.4e6};
    double out[3] = {0.0, 0.0, 0.0};
    REQUIRE(ZR_Unity_GetCameraOrigin(out) == ZR_UNITY_OK);
    CHECK(std::memcmp(out, expected, sizeof(expected)) == 0);
}

// ---- TASK-B-PLANET-06：行星上下文 + 高度采样 C ABI（设计稿 §6.1/§10/§11-06）----
//
// 用例顺序说明（与上方 B-PLANET-05 组同口径）：doctest 缺省按文件内行序执行；
// 全仓只有本组用例调用 ZR_Planet_SetWorld / ZR_Planet_SampleHeight，故本组行号
// 最靠前的用例看到进程级快照的「未设态」——ERR_STATE 只在该用例验一次，其余
// 用例一律自 SetWorld 起步，不依赖跨用例执行序。

namespace {

/// 六面 (u,v) 网格 → 海平面采样点 C + dir·R（确定性/种子可配/并发冒烟共用）。
/// pointsPerAxis=8 时 u,v = -1 + 2k/7（含 ±1 端点，跨面棱位形在内）。
std::vector<zplanet::Vec3d> MakeSeaLevelPoints(const zplanet::Vec3d& center, int pointsPerAxis)
{
    std::vector<zplanet::Vec3d> out;
    out.reserve(static_cast<size_t>(6 * pointsPerAxis * pointsPerAxis));
    for (int face = 0; face < 6; ++face) {
        for (int j = 0; j < pointsPerAxis; ++j) {
            for (int i = 0; i < pointsPerAxis; ++i) {
                const double u = -1.0
                    + 2.0 * static_cast<double>(i) / static_cast<double>(pointsPerAxis - 1);
                const double v = -1.0
                    + 2.0 * static_cast<double>(j) / static_cast<double>(pointsPerAxis - 1);
                const zplanet::Vec3d dir = zplanet::CubeSphereDir(face, u, v);
                out.push_back(
                    zplanet::VecAdd(center, zplanet::VecScale(dir, zplanet::kPlanetRadiusM)));
            }
        }
    }
    return out;
}

/// SetWorld 便捷封装（成功路径；structSize 协议的负例在专项用例）。
void SetPlanetWorld(uint32_t seed, double cx, double cy, double cz)
{
    ZrPlanetWorld world {};
    world.structSize = sizeof(ZrPlanetWorld);
    world.seed = seed;
    world.centerAbs[0] = cx;
    world.centerAbs[1] = cy;
    world.centerAbs[2] = cz;
    REQUIRE(ZR_Planet_SetWorld(&world) == ZR_PLANET_OK);
}

/// 一个 chunk 整网格顶点的逐点比对（§10 补充判据的执行体）：
/// 顶点由生产头规范路由 PlanetChunkVertexAtGlobal(ChunkVertexIndex(...)) 生成
/// （与渲染网格构建同一函数，§6.3「同一函数同一值」），采样点 = C + normal·R
///（海平面），返回 SampleHeight 输出与 (float)heightM 逐位不一致数（全绿为 0）。
/// 比较口径 = ABI 输出类型 float 的位形 memcmp——normalize(d·R) 的 1-2 ULP
/// double 域方向漂移对高度的影响实测 ≤3.5e-10 m，被输出 float 粒度
///（ULP(2.4km) = 0.244mm）完全吸收（探针 40896 样本零失配，见任务日志）。
long long CheckChunkVertices(const zplanet::Vec3d& center, uint32_t seed, int faceId,
    int32_t cx, int32_t cy, int32_t stride, int32_t cellsPerEdge)
{
    const int32_t total = cellsPerEdge * stride;
    long long mismatch = 0;
    for (int32_t j = 0; j <= stride; ++j) {
        for (int32_t i = 0; i <= stride; ++i) {
            const int32_t gU = zplanet::ChunkVertexIndex(cx, i, stride);
            const int32_t gV = zplanet::ChunkVertexIndex(cy, j, stride);
            const zplanet::PlanetChunkVertex vert = zplanet::PlanetChunkVertexAtGlobal(
                center, faceId, gU, gV, total, seed);
            const zplanet::Vec3d pos = zplanet::VecAdd(center,
                zplanet::VecScale(vert.normal, zplanet::kPlanetRadiusM));
            float sampled = 0.0f;
            REQUIRE(ZR_Planet_SampleHeight(pos.x, pos.y, pos.z, &sampled) == ZR_PLANET_OK);
            const float expected = static_cast<float>(vert.heightM);
            if (std::memcmp(&sampled, &expected, sizeof(float)) != 0) {
                ++mismatch;
            }
            CHECK(sampled >= -zplanet::kTerrainMaxAmpM);
            CHECK(sampled <= zplanet::kTerrainMaxAmpM);
        }
    }
    return mismatch;
}

} // namespace

TEST_CASE("planet abi: SetWorld 前置态 ERR_STATE + 校验序全表（NULL→ABI→NONFINITE）")
{
    // 进程级快照未设态的首读（全仓仅本组用例触碰行星上下文，本用例行号在本组
    // 最靠前）：SampleHeight 未 SetWorld 必须 ERR_STATE，且不写 out。
    float out = 123.0f;
    CHECK(ZR_Planet_SampleHeight(1.0, 2.0, 3.0, &out) == ZR_PLANET_ERR_STATE);
    CHECK(out == 123.0f); // 错误路径不写 out
    // STATE 先于 RANGE：退化判据（pos ≡ 中心）依赖中心 C，上下文未设时无从成立
    //（zplugin.h 组 4 注释按码罗列非优先级序——优先级序见 zplugin.cpp 实现注）。
    CHECK(ZR_Planet_SampleHeight(0.0, 0.0, 0.0, &out) == ZR_PLANET_ERR_STATE);

    // ---- SetWorld/SampleHeight 空指针（§6.1：NULL 最先）----
    CHECK(ZR_Planet_SetWorld(nullptr) == ZR_PLANET_ERR_NULL);
    CHECK(ZR_Planet_SampleHeight(1.0, 2.0, 3.0, nullptr) == ZR_PLANET_ERR_NULL);
    CHECK(ZR_Planet_SampleHeight(
        std::numeric_limits<double>::quiet_NaN(), 2.0, 3.0, nullptr)
        == ZR_PLANET_ERR_NULL); // NULL 先于 NONFINITE

    // ---- SetWorld 校验序：ABI（structSize）先于 NONFINITE（布局未知不得读 centerAbs
    // ——centerAbs 预埋 NaN：若实现先读中心，这里会误报 NONFINITE 而非 ABI）----
    ZrPlanetWorld world {};
    world.seed = 7u;
    world.centerAbs[0] = std::numeric_limits<double>::quiet_NaN();
    world.structSize = 0u;
    CHECK(ZR_Planet_SetWorld(&world) == ZR_PLANET_ERR_ABI);
    world.structSize = sizeof(ZrPlanetWorld) - 1u;
    CHECK(ZR_Planet_SetWorld(&world) == ZR_PLANET_ERR_ABI);
    world.structSize = sizeof(ZrPlanetWorld) + 1u;
    CHECK(ZR_Planet_SetWorld(&world) == ZR_PLANET_ERR_ABI);
    world.structSize = 56u; // ZrPlanetChunkDesc 宽度——相邻 ABI 尺寸混淆防呆
    CHECK(ZR_Planet_SetWorld(&world) == ZR_PLANET_ERR_ABI);

    // structSize 正确 + centerAbs NaN/Inf 逐分量 → NONFINITE
    world.structSize = sizeof(ZrPlanetWorld);
    const double qnan = std::numeric_limits<double>::quiet_NaN();
    const double inf = std::numeric_limits<double>::infinity();
    const double badCenters[6][3] = {
        {qnan, 0.0, 0.0}, {0.0, qnan, 0.0}, {0.0, 0.0, qnan},
        {inf, 0.0, 0.0}, {0.0, -inf, 0.0}, {0.0, 0.0, inf},
    };
    for (const auto& badCenter : badCenters) {
        world.centerAbs[0] = badCenter[0];
        world.centerAbs[1] = badCenter[1];
        world.centerAbs[2] = badCenter[2];
        CHECK(ZR_Planet_SetWorld(&world) == ZR_PLANET_ERR_NONFINITE);
    }
    // 全部拒绝后上下文仍未设（拒绝路径不动快照）
    CHECK(ZR_Planet_SampleHeight(1.0, 2.0, 3.0, &out) == ZR_PLANET_ERR_STATE);
    // 七码组适用面核对（§4.5）：SetWorld 只适用 NULL/ABI/NONFINITE——半径不入参
    //（R = kPlanetRadiusM 锁定于 planet_math.h）、seed 为 uint32 全域合法，
    // RANGE/HANDLE/FULL/STATE 对本函数无从构造。
}

TEST_CASE("planet abi: SampleHeight 与 chunk 顶点 heightM 逐位一致（近档整 chunk·§10 判据）")
{
    // 本任务最重要的牙（设计稿 §10 补充判据）：玩法采样通道与地形渲染网格同源。
    // C = (0,0,0)（§3.3「行星中心=世界原点」合法初始态场景）：C + dir·R − C 的
    // 加/减无损，隔离 ·R 与 normalize 的纯舍入路径；非零中心见后续用例。
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    const uint32_t seed = 1337u; // kP2Seed 口径（B-TECH-04）
    SetPlanetWorld(seed, center.x, center.y, center.z);
    CHECK(sizeof(ZrPlanetWorld) == 32u); // 托管封送契约（zplugin.h static_assert 对位）

    // 近档 64×64 整 chunk（face 0，面内 chunk (1,1)）：顶点全走生产头规范路由
    // ChunkVertexIndex → ChunkFaceCoord → CubeSphereDir → PlanetTerrainHeightM。
    CHECK(CheckChunkVertices(center, seed, 0, 1, 1, 63, 4) == 0);
}

TEST_CASE("planet abi: 逐位一致扩展（共享边邻居侧 + 第二面近档 + 远档 chunk）")
{
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    const uint32_t seed = 1337u;
    SetPlanetWorld(seed, center.x, center.y, center.z);

    // 第二面近档整 chunk（face 3 = −X，chunk (0,0)）：不同面基，覆盖 u/v 全负象限
    CHECK(CheckChunkVertices(center, seed, 3, 0, 0, 63, 4) == 0);
    // 远档 16×16 整 chunk（face 4 = +Y，chunk (2,2)，stride 15）
    CHECK(CheckChunkVertices(center, seed, 4, 2, 2, 15, 4) == 0);

    // 共享边邻居侧表达：chunk (2,1) 左缘 i=0 的全局索引 gU = 2·63 与 chunk (1,1)
    // 右缘 i=63 恒等（整数证据——test_planet.cpp 共享边用例同款构造）；采样通道
    // 对「邻居侧表达的同一条共享边」同样逐位一致。
    const int32_t stride = 63;
    const int32_t total = 4 * stride;
    for (int32_t j = 0; j <= stride; ++j) {
        const int32_t gNeighbor = zplanet::ChunkVertexIndex(2, 0, stride);
        const int32_t gOwner = zplanet::ChunkVertexIndex(1, stride, stride);
        REQUIRE(gNeighbor == gOwner);
        const int32_t gV = zplanet::ChunkVertexIndex(1, j, stride);
        const zplanet::PlanetChunkVertex vert = zplanet::PlanetChunkVertexAtGlobal(
            center, 0, gNeighbor, gV, total, seed);
        const zplanet::Vec3d pos = zplanet::VecAdd(center,
            zplanet::VecScale(vert.normal, zplanet::kPlanetRadiusM));
        float sampled = 0.0f;
        REQUIRE(ZR_Planet_SampleHeight(pos.x, pos.y, pos.z, &sampled) == ZR_PLANET_OK);
        const float expected = static_cast<float>(vert.heightM);
        CHECK(std::memcmp(&sampled, &expected, sizeof(float)) == 0);
    }
}

TEST_CASE("planet abi: 非零行星中心逐位一致（float 粒度吸收 double 域方向漂移）")
{
    // C = (0,−R,0)（test_planet.cpp 切平面口径同款中心）：pos−C 的加/减在
    // ULP(R) = 2^-30 量级重整，方向产生 ≤3.3e-16 的 double 域漂移（加减往返 +
    // normalize(d·R) 的 1-2 ULP，构造性不可避免）——高度漂移实测 ≤3.5e-10 m，
    // 被输出 float 粒度（ULP(2.4km) = 0.244mm）完全吸收：探针 40896 样本零失配。
    const zplanet::Vec3d center = {0.0, -zplanet::kPlanetRadiusM, 0.0};
    const uint32_t seed = 1337u;
    SetPlanetWorld(seed, center.x, center.y, center.z);
    CHECK(CheckChunkVertices(center, seed, 1, 0, 0, 15, 4) == 0); // face 1 远档整 chunk
    CHECK(CheckChunkVertices(center, seed, 0, 1, 1, 63, 4) == 0); // face 0 近档整 chunk
}

TEST_CASE("planet abi: 同种子确定性逐位 + 快照幂等替换 + 拒绝不清快照")
{
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    const uint32_t seed = 1337u;
    SetPlanetWorld(seed, center.x, center.y, center.z);
    const std::vector<zplanet::Vec3d> points = MakeSeaLevelPoints(center, 8); // 384 点
    REQUIRE(points.size() == 6u * 8u * 8u);

    auto SampleAll = [&points](std::vector<float>& out) {
        out.resize(points.size());
        for (size_t k = 0; k < points.size(); ++k) {
            REQUIRE(ZR_Planet_SampleHeight(
                points[k].x, points[k].y, points[k].z, &out[k]) == ZR_PLANET_OK);
        }
    };

    std::vector<float> passA;
    SampleAll(passA);
    // 值域有界（§6.3：|h| ≤ kTerrainMaxAmpM）
    for (float height : passA) {
        CHECK(height >= -zplanet::kTerrainMaxAmpM);
        CHECK(height <= zplanet::kTerrainMaxAmpM);
    }
    // 同 world 参数 + 同坐标多次采样：逐位一致（确定性契约，§11-06 第一条）
    std::vector<float> passB;
    SampleAll(passB);
    REQUIRE(passB.size() == passA.size());
    CHECK(std::memcmp(passA.data(), passB.data(), passA.size() * sizeof(float)) == 0);
    // 快照幂等替换：同参数再 SetWorld 后仍逐位一致
    SetPlanetWorld(seed, center.x, center.y, center.z);
    std::vector<float> passC;
    SampleAll(passC);
    CHECK(std::memcmp(passA.data(), passC.data(), passA.size() * sizeof(float)) == 0);
    // 拒绝不清快照：NONFINITE 拒绝后快照仍为 seed=1337（与首拍逐位一致）
    ZrPlanetWorld bad {};
    bad.structSize = sizeof(ZrPlanetWorld);
    bad.seed = 42u;
    bad.centerAbs[0] = std::numeric_limits<double>::infinity();
    CHECK(ZR_Planet_SetWorld(&bad) == ZR_PLANET_ERR_NONFINITE);
    std::vector<float> passD;
    SampleAll(passD);
    CHECK(std::memcmp(passA.data(), passD.data(), passA.size() * sizeof(float)) == 0);
    // 状态已设后：pos ≡ 中心 → ERR_RANGE（零向量方向退化，§6.1），不写 out
    float out = -1.0f;
    CHECK(ZR_Planet_SampleHeight(0.0, 0.0, 0.0, &out) == ZR_PLANET_ERR_RANGE);
    CHECK(out == -1.0f);
}

TEST_CASE("planet abi: 种子可配（换种子过半位形不同）+ 中心生效")
{
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    const std::vector<zplanet::Vec3d> points = MakeSeaLevelPoints(center, 8);
    SetPlanetWorld(1337u, center.x, center.y, center.z);
    std::vector<float> seedA(points.size());
    for (size_t k = 0; k < points.size(); ++k) {
        REQUIRE(ZR_Planet_SampleHeight(
            points[k].x, points[k].y, points[k].z, &seedA[k]) == ZR_PLANET_OK);
    }
    SetPlanetWorld(1339u, center.x, center.y, center.z);
    std::vector<float> seedB(points.size());
    for (size_t k = 0; k < points.size(); ++k) {
        REQUIRE(ZR_Planet_SampleHeight(
            points[k].x, points[k].y, points[k].z, &seedB[k]) == ZR_PLANET_OK);
    }
    // 换种子：过半采样位形不同（全同 = 种子未生效；test_planet.cpp 种子用例同口径）
    size_t differ = 0;
    for (size_t k = 0; k < points.size(); ++k) {
        if (std::memcmp(&seedA[k], &seedB[k], sizeof(float)) != 0) {
            ++differ;
        }
    }
    CHECK(differ > points.size() / 2);
    // 回到原种子：与首拍逐位一致（快照替换是种子的唯一生效入口）
    SetPlanetWorld(1337u, center.x, center.y, center.z);
    std::vector<float> seedC(points.size());
    for (size_t k = 0; k < points.size(); ++k) {
        REQUIRE(ZR_Planet_SampleHeight(
            points[k].x, points[k].y, points[k].z, &seedC[k]) == ZR_PLANET_OK);
    }
    CHECK(std::memcmp(seedA.data(), seedC.data(), seedA.size() * sizeof(float)) == 0);
    // 中心生效：C=(0,−R,0) 时 pos=(0,0,0) 的方向 = (0,1,0)——dy = 0−(−R) = R 精确、
    // len = sqrt(R²) = R 精确（R² 可精确表示），dir 逐位 (0,1,0)；与生产头直接
    // 求值 PlanetTerrainHeightM(seed, 0,1,0) 的 float 位形一致。
    SetPlanetWorld(1337u, 0.0, -zplanet::kPlanetRadiusM, 0.0);
    float atOrigin = 0.0f;
    REQUIRE(ZR_Planet_SampleHeight(0.0, 0.0, 0.0, &atOrigin) == ZR_PLANET_OK);
    const float direct
        = static_cast<float>(zplanet::PlanetTerrainHeightM(1337u, 0.0, 1.0, 0.0));
    CHECK(std::memcmp(&atOrigin, &direct, sizeof(float)) == 0);
}

TEST_CASE("planet abi: 方向语义径向不变 + 退化方向拒绝（零向量）")
{
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    SetPlanetWorld(1337u, center.x, center.y, center.z);
    // §6.3 输出语义：高度只依赖方向（dir = normalize(pos − C)），与径向距离无关。
    const zplanet::Vec3d dir = zplanet::CubeSphereDir(0, 0.375, -0.625);
    float hR = 0.0f;
    float hQuarter = 0.0f;
    float hAbove = 0.0f;
    const zplanet::Vec3d pR = zplanet::VecScale(dir, zplanet::kPlanetRadiusM);
    const zplanet::Vec3d pQuarter = zplanet::VecScale(dir, zplanet::kPlanetRadiusM * 0.25);
    const zplanet::Vec3d pAbove = zplanet::VecScale(dir, zplanet::kPlanetRadiusM + 500.0);
    REQUIRE(ZR_Planet_SampleHeight(pR.x, pR.y, pR.z, &hR) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_SampleHeight(pQuarter.x, pQuarter.y, pQuarter.z, &hQuarter) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_SampleHeight(pAbove.x, pAbove.y, pAbove.z, &hAbove) == ZR_PLANET_OK);
    // r = 0.25·R：幂 2 缩放使全链路（乘/平方/和/sqrt/除）只做指数平移、尾数不变
    // → 方向逐位必等 → 高度逐位必等（数学保证，非概率）。
    CHECK(std::memcmp(&hQuarter, &hR, sizeof(float)) == 0);
    // r = R + 500：非幂 2 幅值 → 方向 1-2 ULP double 域漂移 → 高度 ~1e-10 m 漂移，
    // 被输出 float 粒度吸收（本文件非零中心用例同款机制）。
    CHECK(std::memcmp(&hAbove, &hR, sizeof(float)) == 0);
    // 六面 18 方向的幂 2 径向不变（0.125·R vs R）
    for (int face = 0; face < 6; ++face) {
        for (int k = 1; k < 4; ++k) {
            const double t = -1.0 + 0.5 * static_cast<double>(k);
            const zplanet::Vec3d d = zplanet::CubeSphereDir(face, t, t * 0.5);
            const zplanet::Vec3d pa = zplanet::VecScale(d, zplanet::kPlanetRadiusM);
            const zplanet::Vec3d pb = zplanet::VecScale(d, zplanet::kPlanetRadiusM * 0.125);
            float ha = 0.0f;
            float hb = 0.0f;
            REQUIRE(ZR_Planet_SampleHeight(pa.x, pa.y, pa.z, &ha) == ZR_PLANET_OK);
            REQUIRE(ZR_Planet_SampleHeight(pb.x, pb.y, pb.z, &hb) == ZR_PLANET_OK);
            CHECK(std::memcmp(&ha, &hb, sizeof(float)) == 0);
        }
    }
    // 简报口径核对（§6.1 权威）：SampleHeight 收位置不收方向——「非单位向量」幅值
    // 属合法输入（内部归一化，即上方径向不变断言），无错误码可拒；零向量
    //（pos ≡ 中心，|pos−C| = 0 方向退化）→ ERR_RANGE，不写 out。
    float out = -7.0f;
    CHECK(ZR_Planet_SampleHeight(0.0, 0.0, 0.0, &out) == ZR_PLANET_ERR_RANGE);
    CHECK(out == -7.0f);
}

TEST_CASE("planet abi: 任意线程并发冒烟（共享锁快照 + 同值写者不撕裂）")
{
    // §8/D5：SampleHeight 任意线程可调（共享锁拷贝快照后无锁计算）。冒烟口径：
    // 4 读线程 × 25 拍 × 384 点与单线程基准逐位比对；并发的同值 SetWorld 写者
    //（独占锁替换相同快照）若造成撕裂读，读侧 memcmp 失配即被抓。
    const zplanet::Vec3d center = {0.0, 0.0, 0.0};
    const uint32_t seed = 1337u;
    SetPlanetWorld(seed, center.x, center.y, center.z);
    const std::vector<zplanet::Vec3d> points = MakeSeaLevelPoints(center, 8);
    std::vector<float> reference(points.size());
    for (size_t k = 0; k < points.size(); ++k) {
        REQUIRE(ZR_Planet_SampleHeight(
            points[k].x, points[k].y, points[k].z, &reference[k]) == ZR_PLANET_OK);
    }

    ZrPlanetWorld world {};
    world.structSize = sizeof(ZrPlanetWorld);
    world.seed = seed;
    world.centerAbs[0] = center.x;
    world.centerAbs[1] = center.y;
    world.centerAbs[2] = center.z;

    std::atomic<long long> mismatch {0};
    constexpr int kReaderCount = 4;
    constexpr int kPasses = 25;
    std::vector<std::thread> threads;
    threads.reserve(static_cast<size_t>(kReaderCount) + 1u);
    for (int r = 0; r < kReaderCount; ++r) {
        threads.emplace_back([&points, &reference, &world, &mismatch, kPasses]() {
            long long local = 0;
            float sampled = 0.0f;
            for (int pass = 0; pass < kPasses; ++pass) {
                for (size_t k = 0; k < points.size(); ++k) {
                    if (ZR_Planet_SampleHeight(points[k].x, points[k].y, points[k].z, &sampled)
                            != ZR_PLANET_OK
                        || std::memcmp(&sampled, &reference[k], sizeof(float)) != 0) {
                        ++local;
                    }
                }
            }
            mismatch.fetch_add(local, std::memory_order_relaxed);
        });
    }
    // 同值写者：SetWorld 独占锁替换相同快照——读侧结果必须不受扰动
    threads.emplace_back([&world]() {
        for (int w = 0; w < 2000; ++w) {
            (void)ZR_Planet_SetWorld(&world);
        }
    });
    for (std::thread& thread : threads) {
        thread.join();
    }
    CHECK(mismatch.load() == 0);
    // 并发结束后状态完好：串行复拍仍与基准逐位一致
    std::vector<float> after(points.size());
    for (size_t k = 0; k < points.size(); ++k) {
        REQUIRE(ZR_Planet_SampleHeight(
            points[k].x, points[k].y, points[k].z, &after[k]) == ZR_PLANET_OK);
    }
    CHECK(std::memcmp(reference.data(), after.data(), reference.size() * sizeof(float)) == 0);
}
