// =============================================================================
// test_smoke.cpp —— doctest 冒烟测试（TASK-B-R0-01）
//
// 覆盖：zplugin C ABI 三函数（版本/字符串/构建信息）+ 五模块版本函数 + 基础断言。
// =============================================================================
#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include "doctest/doctest.h"

#include <cstdint>
#include <cstring>

#include "zkernel/zkernel.h"
#include "zplugin/zplugin.h"
#include "zrender/zrender.h"
#include "zrhi/zrhi.h"
#include "zupscaler/zupscaler.h"

TEST_CASE("C ABI: ZR_ApiVersion 返回 3")
{
    // v3（TASK-B-PLANET-02）：追加行星通道 ABI（zplugin.h 末尾声明块，接口冻结）。
    CHECK(ZR_ApiVersion() == 3u);
}

TEST_CASE("行星通道 ABI 常量与设计稿 §14 一致（TASK-B-PLANET-02）")
{
    // 结构尺寸 32/56/80B 由 zplugin.h 的 static_assert 编译期锁定（本文件已包含
    // zplugin.h）；本用例锁错误码七值与常量/掩码（接口冻结自检的运行时部分）。
    CHECK(ZR_PLANET_OK == 0);
    CHECK(ZR_PLANET_ERR_NULL == -1);
    CHECK(ZR_PLANET_ERR_ABI == -2);
    CHECK(ZR_PLANET_ERR_RANGE == -3);
    CHECK(ZR_PLANET_ERR_HANDLE == -4);
    CHECK(ZR_PLANET_ERR_STATE == -5);
    CHECK(ZR_PLANET_ERR_FULL == -6);
    CHECK(ZR_PLANET_ERR_NONFINITE == -7);
    CHECK(ZR_PLANET_MAX_CHUNKS == 8192u);
    CHECK(ZR_PLANET_VERTEX_STRIDE_FLOATS == 8u);
    CHECK(ZR_PLANET_EDGE_NEG_U == (1u << 0));
    CHECK(ZR_PLANET_EDGE_POS_U == (1u << 1));
    CHECK(ZR_PLANET_EDGE_NEG_V == (1u << 2));
    CHECK(ZR_PLANET_EDGE_POS_V == (1u << 3));
    CHECK(sizeof(ZrPlanetWorld) == 32);
    CHECK(sizeof(ZrPlanetChunkDesc) == 56);
    CHECK(sizeof(ZrPlanetStats) == 80);
}

TEST_CASE("C ABI: ZR_GetVersion 返回 \"0.1.0\"")
{
    const char* version = ZR_GetVersion();
    REQUIRE(version != nullptr);
    CHECK(std::strcmp(version, "0.1.0") == 0);
}

TEST_CASE("C ABI: ZR_GetBuildInfo 非空且包含诊断要素")
{
    const char* info = ZR_GetBuildInfo();
    REQUIRE(info != nullptr);
    CHECK(info[0] != '\0');
    CHECK(std::strstr(info, "0.1.0") != nullptr); // 整体版本
#if defined(_MSC_VER)
    CHECK(std::strstr(info, "MSVC") != nullptr); // 编译器
#endif
    CHECK(std::strstr(info, "kernel=") != nullptr); // 模块聚合段
}

TEST_CASE("五模块版本函数与整体版本对齐")
{
    CHECK(std::strcmp(ZK_GetVersion(), "0.1.0") == 0);
    CHECK(std::strcmp(ZRHI_GetVersion(), "0.1.0") == 0);
    CHECK(std::strcmp(ZREN_GetVersion(), "0.1.0") == 0);
    CHECK(std::strcmp(ZUP_GetVersion(), "0.1.0") == 0);
}

TEST_CASE("基础断言冒烟")
{
    CHECK(1 + 1 == 2);
    CHECK(sizeof(std::uint32_t) == 4);
    CHECK(sizeof(void*) == 8); // 目标平台 x64
    const char* version = ZR_GetVersion();
    CHECK(std::strlen(version) > 0);
}
