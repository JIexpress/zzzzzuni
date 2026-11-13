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
// 真渲染（建设备 → 共享纹理 → 回读中心像素）需要真实 GPU，属验证程序
// zrhi_unity_bridge / Unity Player 的职责，不进 doctest。
// =============================================================================
#include "doctest/doctest.h"

#include "zplugin/zplugin.h"
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <cstddef>
#include <cstring>
#include <fstream>
#include <limits>
#include <string>

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
