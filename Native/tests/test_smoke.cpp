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

TEST_CASE("C ABI: ZR_ApiVersion 返回 2")
{
    // v2（TASK-B-FSR-02）：ZrUpscalerCaps 末尾追加 fsr4ProbeMode。
    CHECK(ZR_ApiVersion() == 2u);
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
