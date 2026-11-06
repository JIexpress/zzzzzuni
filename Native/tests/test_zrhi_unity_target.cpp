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
// 真渲染（建设备 → 共享纹理 → 回读中心像素）需要真实 GPU，属验证程序
// zrhi_unity_bridge / Unity Player 的职责，不进 doctest。
// =============================================================================
#include "doctest/doctest.h"

#include "zplugin/zplugin.h"
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <cstddef>
#include <fstream>
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
