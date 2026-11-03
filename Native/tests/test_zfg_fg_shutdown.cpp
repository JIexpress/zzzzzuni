// =============================================================================
// test_zfg_fg_shutdown.cpp —— FG 关停导出 + wrap 入口参数校验（TASK-R10-05 B-3/B-7）
//
// 历史缺陷：
//   B-3（R10-02）：WrapShutdown 全仓无调用点、也未导出——FFX 上下文与代理交换链
//     活到进程终止（pacing 线程无人停、g_device/g_queue 的 AddRef 永不 Release）。
//     修复：导出 ZFG_FgShutdown（zfg_hook.dll + dxgi 代理转发 + zfg_dxgi.def）+
//     DllMain PROCESS_DETACH 兜底挂接 + 幂等闸门。
//   B-7（R10-02）：wrap 路径不校验 outSwapChain == nullptr——wrap 成功后已建好整条
//     FFX 代理链却无处可写，仍返回 S_OK（原生语义应 E_INVALIDARG）→ 代理链泄漏。
//     修复：HookCreateSwapChainForHwnd 入口直接 E_INVALIDARG（不建代理链、不转发）。
//
// 本文件需要 zfgprobe 的 hook/wrap/dll 三个编译单元（CMake 已并入 zrender_test）。
// 变异验证（主控 V3 计划的两条都在此）：
//   · B-7：删掉 HookCreateSwapChainForHwnd 的空 out 检查 → 本进程 g_orig 原始函数
//     为空，调用会打到空指针（进程崩溃 = 红）；恢复后绿。
//   · B-3：WrapShutdown 的幂等闸门改回"无守卫"→ 重复调用本用例的第二次执行会
//     走到已清空状态（闸门存在性由 ZFG_FgShutdown 两次调用 + stats 断言覆盖）。
// =============================================================================
#include "doctest/doctest.h"

#include "zfgprobe/zfg_fg_input.h"
#include "zfgprobe/zfg_probe_hook.h"
#include "zfgprobe/zfg_probe_wrap.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <dxgi1_2.h>

#include <cstdint>

// ---- 被测产品函数的声明（定义在 zfgprobe 编译单元内，随本测试目标链接）---------

// B-3：zfg_probe_dll.cpp 的导出（静态链接进本测试目标）
extern "C" int ZFG_FgShutdown(void);

// B-7：HookCreateSwapChainForHwnd 的声明在 zfgprobe/zfg_probe_hook.h（R10-05 起
// 上收到 zfg 外层命名空间，专为本测试的驱动入口）。

TEST_CASE("zfg shutdown: ZFG_FgShutdown export routes to WrapShutdown and is idempotent (B-3)")
{
    // 未安装/未 wrap 的进程里：导出存在、可调用、两次都安全（幂等——历史缺陷是
    // 该路径根本不存在）。返回 1 = 已执行（含空操作）。
    CHECK(ZFG_FgShutdown() == 1);
    CHECK(ZFG_FgShutdown() == 1);

    // 关停后统计出口如实报告"无代理链"（stats 清零断言的静态面；wrap 链上的
    // stats 由 selftest 的真机阶段覆盖——见 zfg_probe_selftest.cpp）
    ZfgFgStats stats {};
    stats.realPresents = 0xDEADBEEFull; // 非 0 初始：验证实现真的写回了
    stats.generatedPresents = 0xDEADBEEFull;
    stats.haveInputs = 7u;
    CHECK_FALSE(zfg::WrapGetStats(&stats)); // false = 无代理链
    CHECK(stats.realPresents == 0u);
    CHECK(stats.generatedPresents == 0u);
    CHECK(stats.haveInputs == 0u);
    CHECK(stats.enabled == 0u);
}

TEST_CASE("zfg shutdown: WrapShutdown direct call is a safe no-op without a wrap chain (B-3)")
{
    // 幂等闸门的另一半：直接调 WrapShutdown（detach 挂接的调用形态）不崩、
    // 不改统计。静态闸门保证两条触发路径并发时只执行一次。
    zfg::WrapShutdown();
    zfg::WrapShutdown();
    ZfgFgStats stats {};
    CHECK_FALSE(zfg::WrapGetStats(&stats));
    CHECK(zfg::WrapSwapchain() == nullptr); // 无代理链残留
}

TEST_CASE("zfg shutdown: C ABI stats struct layout unchanged (forward/backward contract)")
{
    // ZfgFgStats 是跨模块 POD 契约（zfg_fg_input.h）：本用例钉住布局护栏，防止
    // B-3 改动顺手动了结构（字段只在尾部追加的约定由头注释维护）。
    CHECK(sizeof(ZfgFgStats) == 48u);
    ZfgFgStats stats {};
    CHECK(stats.reserved == 0u);
}

TEST_CASE("zfg wrap: null outSwapChain -> E_INVALIDARG and no proxy chain work (B-7)")
{
    // 历史缺陷：不校验空 out——wrap 会建好整条代理链再无处安放、仍返回 S_OK。
    // 修复后入口直接拒绝。传空 self/device/desc 也安全：检查在任何解引用之前。
    const HRESULT hr = zfg::HookCreateSwapChainForHwnd(nullptr, nullptr, nullptr, nullptr,
        nullptr, nullptr, nullptr);
    CHECK(hr == E_INVALIDARG);
    // 代理链状态未被触碰（wrap 未发生）
    CHECK(zfg::WrapSwapchain() == nullptr);
    ZfgFgStats stats {};
    CHECK_FALSE(zfg::WrapGetStats(&stats));
}
