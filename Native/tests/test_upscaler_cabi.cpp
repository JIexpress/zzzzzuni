// =============================================================================
// test_upscaler_cabi.cpp —— 超分设置 C ABI 参数校验单测（TASK-B-R6-02）
//
// 覆盖：
//   1. ZR_Upscaler_SetPreferred：越界拒绝（负值/超上界）+ 合法值全接受
//   2. ZR_Upscaler_QueryCaps：空指针 / structSize 不符 → 明确错误码
//   3. ZR_Upscaler_QueryCaps 成功路径：字段填满、preferred 回读一致、
//      active 落在合法后端区间（1..5）
//   4. ZR_Upscaler_GetActive：Auto 模式下按能力选优，恒在 1..5
//
// 注意：C ABI 内部状态是进程级单例，本文件所有用例结束都把偏好复位到 AUTO，
// 避免影响其他测试文件（doctest 同进程串行执行）。
// =============================================================================
#include "doctest/doctest.h"

#include "zplugin/zplugin.h"

#include <cstring>

namespace {

/// 复位到 Auto（每个用例前后各调一次，保证独立性）
void ResetPreferred()
{
    REQUIRE(ZR_Upscaler_SetPreferred(ZR_UPSCALER_PREF_AUTO) == ZR_UPSCALER_OK);
}

ZrUpscalerCaps MakeCaps()
{
    ZrUpscalerCaps caps {};
    caps.structSize = static_cast<uint32_t>(sizeof(ZrUpscalerCaps));
    return caps;
}

} // namespace

TEST_CASE("ZR_Upscaler_SetPreferred: range validation")
{
    ResetPreferred();
    // 越界：负值与 >= COUNT 都必须被拒绝，且返回明确错误码
    CHECK(ZR_Upscaler_SetPreferred(-1) == ZR_UPSCALER_ERR_RANGE);
    CHECK(ZR_Upscaler_SetPreferred(-100) == ZR_UPSCALER_ERR_RANGE);
    CHECK(ZR_Upscaler_SetPreferred(ZR_UPSCALER_PREF_COUNT) == ZR_UPSCALER_ERR_RANGE);
    CHECK(ZR_Upscaler_SetPreferred(ZR_UPSCALER_PREF_COUNT + 42) == ZR_UPSCALER_ERR_RANGE);

    // 合法值全部接受（Auto + 五个后端）
    for (int pref = ZR_UPSCALER_PREF_AUTO; pref < ZR_UPSCALER_PREF_COUNT; ++pref) {
        CAPTURE(pref);
        CHECK(ZR_Upscaler_SetPreferred(pref) == ZR_UPSCALER_OK);
        const int active = ZR_Upscaler_GetActive();
        if (pref == ZR_UPSCALER_PREF_AUTO) {
            // Auto：active 是解析结果（1..5），不是 0
            CHECK(active >= ZR_UPSCALER_PREF_BILINEAR);
            CHECK(active < ZR_UPSCALER_PREF_COUNT);
        } else {
            CHECK(active == pref); // 显式偏好原样回读
        }
    }
    ResetPreferred();
}

TEST_CASE("ZR_Upscaler_QueryCaps: null pointer and struct size validation")
{
    ResetPreferred();
    CHECK(ZR_Upscaler_QueryCaps(nullptr) == ZR_UPSCALER_ERR_NULL);

    ZrUpscalerCaps wrong = MakeCaps();
    wrong.structSize = static_cast<uint32_t>(sizeof(ZrUpscalerCaps) - 4u);
    CHECK(ZR_Upscaler_QueryCaps(&wrong) == ZR_UPSCALER_ERR_SIZE);

    ZrUpscalerCaps zero {};
    CHECK(ZR_Upscaler_QueryCaps(&zero) == ZR_UPSCALER_ERR_SIZE); // structSize=0 拒绝

    ZrUpscalerCaps good = MakeCaps();
    CHECK(ZR_Upscaler_QueryCaps(&good) == ZR_UPSCALER_OK);
}

TEST_CASE("ZR_Upscaler_QueryCaps: successful fill matches preferred/active")
{
    ResetPreferred();
    ZrUpscalerCaps caps = MakeCaps();
    REQUIRE(ZR_Upscaler_QueryCaps(&caps) == ZR_UPSCALER_OK);

    CHECK(caps.preferredBackend == static_cast<uint32_t>(ZR_UPSCALER_PREF_AUTO));
    CHECK(caps.activeBackend >= ZR_UPSCALER_PREF_BILINEAR);
    CHECK(caps.activeBackend < ZR_UPSCALER_PREF_COUNT);
    // 布尔位只允许 0/1
    CHECK(caps.fsrRuntimePresent <= 1u);
    CHECK(caps.fsr4Available <= 1u);
    CHECK(caps.dlssAvailable <= 1u);
    // 非 N 卡机器上 DLSS 必为不可用（门控含 nvidiaHardware）
    if (caps.vendorId != 0x10DEu) {
        CHECK(caps.dlssAvailable == 0u);
    }

    // 显式偏好 FSR3 → active 回读一致
    REQUIRE(ZR_Upscaler_SetPreferred(ZR_UPSCALER_PREF_FSR3) == ZR_UPSCALER_OK);
    ZrUpscalerCaps caps2 = MakeCaps();
    REQUIRE(ZR_Upscaler_QueryCaps(&caps2) == ZR_UPSCALER_OK);
    CHECK(caps2.preferredBackend == static_cast<uint32_t>(ZR_UPSCALER_PREF_FSR3));
    CHECK(caps2.activeBackend == static_cast<uint32_t>(ZR_UPSCALER_PREF_FSR3));

    // 显式偏好 DLSS（本机不可用）仍原样记录——ABI 不偷偷改写用户设置；
    // 渲染侧由 Chain 决定实际回退（日志为权威）
    REQUIRE(ZR_Upscaler_SetPreferred(ZR_UPSCALER_PREF_DLSS) == ZR_UPSCALER_OK);
    ZrUpscalerCaps caps3 = MakeCaps();
    REQUIRE(ZR_Upscaler_QueryCaps(&caps3) == ZR_UPSCALER_OK);
    CHECK(caps3.preferredBackend == static_cast<uint32_t>(ZR_UPSCALER_PREF_DLSS));

    ResetPreferred();
}

TEST_CASE("ZR_Upscaler_GetActive: Auto resolves to a supported backend")
{
    ResetPreferred();
    const int active = ZR_Upscaler_GetActive();
    CHECK(active >= ZR_UPSCALER_PREF_BILINEAR);
    CHECK(active < ZR_UPSCALER_PREF_COUNT);

    // Auto 解析规则（与 UpscalerDefaultPriority 一致）：Dlss → Fsr4 → Fsr3 →
    // Bilinear，取首个能力满足者。本机 780M 期望落到 FSR3（Dlss 无 N 卡、
    // Fsr4 provider 未枚举）。
    ZrUpscalerCaps caps = MakeCaps();
    REQUIRE(ZR_Upscaler_QueryCaps(&caps) == ZR_UPSCALER_OK);
    int expected = ZR_UPSCALER_PREF_BILINEAR;
    if (caps.dlssAvailable != 0u) {
        expected = ZR_UPSCALER_PREF_DLSS;
    } else if (caps.fsr4Available != 0u) {
        expected = ZR_UPSCALER_PREF_FSR4;
    } else if (caps.fsrRuntimePresent != 0u) {
        expected = ZR_UPSCALER_PREF_FSR3;
    }
    CHECK(active == expected);
    CHECK(caps.activeBackend == static_cast<uint32_t>(active));

    // 本机（AMD 780M）的具体结论作为证据记录：无 FSR4 provider
    if (caps.vendorId == 0x1002u && caps.deviceId == 0x15BFu) {
        CHECK(caps.fsr4Available == 0u);
        CHECK(active == ZR_UPSCALER_PREF_FSR3);
    }
}
