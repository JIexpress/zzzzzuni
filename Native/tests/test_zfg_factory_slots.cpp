// =============================================================================
// test_zfg_factory_slots.cpp —— DXGI vtable 槽位守卫（TASK-R10-03 A-2）
//
// 为什么需要这条用例：R10-02 A-2 的缺陷是"槽号常量写错、装错槽还静默成功"
// （Composition 曾写 17，实为 GetSharedResourceAdapterLuid；真正槽位 24）。
// 运行期只能证明"槽里是系统 dxgi 的函数"，证不了"这个槽就是这个方法"——
// 语义只能靠 SDK 头。
//
// 做法：本 TU 用 CINTERFACE 模式把 SDK 头里的 C 风格结构体
// IDXGIFactory2Vtbl / IDXGISwapChain1Vtbl 打开（C++ 模式下接口是虚函数声明、
// 没有 Vtbl 结构体），对每个成员取 offsetof/8 得到**权威槽号**，再与产品代码
// 共用的 zfgprobe/zfg_factory_slots.h 逐槽比对：
//   - 常量改回 17 → 用例红
//   - SDK 布局变化（成员增删/重排）→ 用例红
//   - 表里新增槽位但具名常量没同步 → 用例红
// 纯编译期/查表，不需要 GPU、不需要创建任何 COM 对象。
// =============================================================================
#include "doctest/doctest.h"

#include <cstddef>
#include <cstring>

// CINTERFACE：让 dxgi1_2.h 暴露 C 风格 Vtbl 结构体（本 TU 局部宏，不外泄）。
// CINTERFACE 必须在**任何** COM 头（含 windows.h 拉进来的 unknwn.h）之前定义。
#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#  define NOMINMAX
#endif
#define CINTERFACE
#include <windows.h>
#include <dxgi1_2.h>

#include "zfgprobe/zfg_factory_slots.h"

namespace {

/// SDK 头给出的权威槽号（= 结构体内偏移 / 指针宽度）
template <typename Vtbl>
size_t SdkSlot(size_t memberOffset)
{
    return memberOffset / sizeof(void*);
}

#define ZFG_SDK_SLOT(vtblType, member) SdkSlot<vtblType>(offsetof(vtblType, member))

/// 表项 → SDK 权威槽号；返回 SIZE_MAX 表示"表里有、SDK 头里查不到"（红）
size_t SdkSlotOf(const zfg::VtblSlotSpec& spec)
{
    const char* m = spec.method;
    if (spec.family == zfg::VtblFamily::Factory2) {
        if (std::strcmp(m, "CreateSwapChain") == 0) {
            return ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChain);
        }
        if (std::strcmp(m, "CreateSwapChainForHwnd") == 0) {
            return ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForHwnd);
        }
        if (std::strcmp(m, "CreateSwapChainForCoreWindow") == 0) {
            return ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForCoreWindow);
        }
        if (std::strcmp(m, "CreateSwapChainForComposition") == 0) {
            return ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForComposition);
        }
    } else {
        if (std::strcmp(m, "Present") == 0) {
            return ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present);
        }
        if (std::strcmp(m, "ResizeBuffers") == 0) {
            return ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, ResizeBuffers);
        }
        if (std::strcmp(m, "Present1") == 0) {
            return ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present1);
        }
    }
    return static_cast<size_t>(-1);
}

} // namespace

TEST_CASE("zfg slot table: 每个被 patch 的槽与 SDK 头布局逐槽一致")
{
    size_t checked = 0u;
    for (size_t i = 0u; i < zfg::kVtblSlotCount; ++i) {
        const zfg::VtblSlotSpec& spec = zfg::kVtblSlots[i];
        CAPTURE(spec.method);
        CAPTURE(spec.slot);
        const size_t sdkSlot = SdkSlotOf(spec);
        CHECK(sdkSlot != static_cast<size_t>(-1)); // 表里有、SDK 头里必须有
        CHECK(spec.slot == sdkSlot);
        ++checked;
    }
    CHECK(checked == zfg::kVtblSlotCount);
}

TEST_CASE("zfg slot table: IDXGIFactory2Vtbl 共 25 项且 Composition 是最后一项")
{
    // 结构体总长 / 指针宽度 = 成员数（Vtbl 里全是等宽函数指针）
    CHECK(ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForComposition) + 1u
        == sizeof(IDXGIFactory2Vtbl) / sizeof(void*));
    CHECK(zfg::kIdxgiFactory2VtblCount == sizeof(IDXGIFactory2Vtbl) / sizeof(void*));
    CHECK(zfg::kSlotCreateSwapChainForComposition + 1u == zfg::kIdxgiFactory2VtblCount);
    // 表里标记 lastOfInterface 的项必须真的是最后一项
    for (size_t i = 0u; i < zfg::kVtblSlotCount; ++i) {
        const zfg::VtblSlotSpec& spec = zfg::kVtblSlots[i];
        if (!spec.lastOfInterface) {
            continue;
        }
        CAPTURE(spec.method);
        CHECK(spec.family == zfg::VtblFamily::Factory2);
        CHECK(spec.slot + 1u == zfg::kIdxgiFactory2VtblCount);
    }
}

TEST_CASE("zfg slot table: 回归钉 —— 槽 17 是 GetSharedResourceAdapterLuid，不是 Composition")
{
    // R10-02 A-2 的原缺陷：kSlotCreateSwapChainForComposition 曾等于 17。
    const size_t luid = ZFG_SDK_SLOT(IDXGIFactory2Vtbl, GetSharedResourceAdapterLuid);
    const size_t composition = ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForComposition);
    CHECK(luid == 17u);
    CHECK(composition == 24u);
    CHECK(zfg::kSlotCreateSwapChainForComposition != luid);
    CHECK(zfg::kSlotCreateSwapChainForComposition == composition);
    // 相邻关系也钉住：15/16 是 Hwnd/CoreWindow（这两个原本就是对的）
    CHECK(ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForHwnd) == 15u);
    CHECK(ZFG_SDK_SLOT(IDXGIFactory2Vtbl, CreateSwapChainForCoreWindow) == 16u);
}

TEST_CASE("zfg slot table: 具名常量与槽位表同源（改一处不改另一处即红）")
{
    const zfg::VtblSlotSpec* sc = nullptr;
    const zfg::VtblSlotSpec* hwnd = nullptr;
    const zfg::VtblSlotSpec* core = nullptr;
    const zfg::VtblSlotSpec* comp = nullptr;
    const zfg::VtblSlotSpec* present = nullptr;
    const zfg::VtblSlotSpec* resize = nullptr;
    const zfg::VtblSlotSpec* present1 = nullptr;
    for (size_t i = 0u; i < zfg::kVtblSlotCount; ++i) {
        const zfg::VtblSlotSpec& spec = zfg::kVtblSlots[i];
        const char* m = spec.method;
        if (spec.family == zfg::VtblFamily::Factory2) {
            if (std::strcmp(m, "CreateSwapChain") == 0) {
                sc = &spec;
            } else if (std::strcmp(m, "CreateSwapChainForHwnd") == 0) {
                hwnd = &spec;
            } else if (std::strcmp(m, "CreateSwapChainForCoreWindow") == 0) {
                core = &spec;
            } else if (std::strcmp(m, "CreateSwapChainForComposition") == 0) {
                comp = &spec;
            }
        } else {
            if (std::strcmp(m, "Present") == 0) {
                present = &spec;
            } else if (std::strcmp(m, "ResizeBuffers") == 0) {
                resize = &spec;
            } else if (std::strcmp(m, "Present1") == 0) {
                present1 = &spec;
            }
        }
    }
    REQUIRE(sc != nullptr);
    REQUIRE(hwnd != nullptr);
    REQUIRE(core != nullptr);
    REQUIRE(comp != nullptr);
    REQUIRE(present != nullptr);
    REQUIRE(resize != nullptr);
    REQUIRE(present1 != nullptr);

    CHECK(sc->slot == zfg::kSlotCreateSwapChain);
    CHECK(hwnd->slot == zfg::kSlotCreateSwapChainForHwnd);
    CHECK(core->slot == zfg::kSlotCreateSwapChainForCoreWindow);
    CHECK(comp->slot == zfg::kSlotCreateSwapChainForComposition);
    CHECK(present->slot == zfg::kSlotPresent);
    CHECK(resize->slot == zfg::kSlotResizeBuffers);
    CHECK(present1->slot == zfg::kSlotPresent1);
}

TEST_CASE("zfg slot table: 被 patch 的槽互不相同且在各自接口范围内")
{
    const size_t factoryCount = sizeof(IDXGIFactory2Vtbl) / sizeof(void*);
    const size_t swapchainCount = sizeof(IDXGISwapChain1Vtbl) / sizeof(void*);
    size_t factorySlots = 0u;
    size_t swapchainSlots = 0u;
    for (size_t i = 0u; i < zfg::kVtblSlotCount; ++i) {
        const zfg::VtblSlotSpec& a = zfg::kVtblSlots[i];
        CAPTURE(a.method);
        CHECK(a.patched);
        if (a.family == zfg::VtblFamily::Factory2) {
            CHECK(a.slot < factoryCount);
            ++factorySlots;
        } else {
            CHECK(a.slot < swapchainCount);
            ++swapchainSlots;
        }
        for (size_t k = i + 1u; k < zfg::kVtblSlotCount; ++k) {
            const zfg::VtblSlotSpec& b = zfg::kVtblSlots[k];
            if (a.family != b.family) {
                continue;
            }
            CAPTURE(b.method);
            CHECK(a.slot != b.slot);
        }
    }
    CHECK(factorySlots == 4u);   // CreateSwapChain / ForHwnd / ForCoreWindow / ForComposition
    CHECK(swapchainSlots == 3u); // Present / ResizeBuffers / Present1
}

TEST_CASE("zfg slot table: IDXGISwapChain1 的 Present/ResizeBuffers/Present1 槽号")
{
    CHECK(ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present) == 8u);
    CHECK(ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, ResizeBuffers) == 13u);
    CHECK(ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present1) == 22u);
    // 派生接口的 vtable 是"基类成员在前"的线性布局：Present1 必须在 Present 之后
    CHECK(ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present) < ZFG_SDK_SLOT(IDXGISwapChain1Vtbl, Present1));
}
