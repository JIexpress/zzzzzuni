// =============================================================================
// zfg_probe_hook.h —— DXGI 交换链探测 hook（TASK-B-R9-01 阶段 0 spike）
//
// 目的：回答"Unity 2022.3 在 D3D12 下实际走哪个 DXGI 创建路径"。做法是只
// **记录不替换**——把 IDXGIFactory2 / IDXGISwapChain 的 vtable 槽位换成记录
// 函数，记录后原样转发给原始实现。返回给宿主的对象与未安装 hook 时完全同源。
//
// 安装方式：patch dxgi.dll 里工厂/交换链对象共享的静态 vtable（VirtualProtect
// 临时放开 .rdata 的写保护），因此不需要指令级 detour，也不依赖导入表。
// 安装时机只要早于第一次 CreateSwapChain* 调用即可（Unity 原生插件在引擎
// 初始化期载入，满足该条件）。
//
// 逃生门：环境变量 ZFG_PROBE=0（或不存在）→ 完全不安装，进程行为与原生一致。
// 日志路径：环境变量 ZFG_PROBE_LOG 指定；缺省 %TEMP%\zfg_probe.log。
// =============================================================================
#pragma once

#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <dxgi1_2.h>

namespace zfg {

/// 安装结果（证据行直出）
enum class ProbeInstall : int {
    Disabled = 0,  // ZFG_PROBE 关闭 / 未安装
    Installed = 1, // vtable 已替换，记录+转发生效
    Failed = 2,    // 拿不到工厂或 vtable（fail-safe：不装，宿主照常运行）
};

/// 按环境变量安装（ZFG_PROBE=1|wrap；ZFG_PROBE_LOG=<path>）。可重复调用、可多线程
/// 并发调用：内部是三态守卫（0=未装/1=安装中/2=已装），只有抢到安装权的调用方
/// patch，其余复用；已安装时直接返回 Installed。（TASK-B-R9-05 G1）
ProbeInstall InstallFromEnv();

/// 直接安装到指定日志文件（path 为空 → %TEMP%\zfg_probe.log）。自测入口用。
ProbeInstall Install(const char* logPath);

/// 地址是否落在本模块（zfg_probe.dll / zfg_hook.dll）内。
/// 安装时用它做自递归兜底：若某个槽位的"原始函数"已经在我们的模块里，说明那是
/// 我们自己（或同源的另一份副本）的 hook，再 patch 就会让 hook 调自己 → 拒绝并
/// 回滚。（TASK-B-R9-05 G2）
bool IsProbeCode(void* address);

/// 卸载（恢复原始 vtable 槽位；幂等）。仅供自测使用。
void Uninstall();

bool IsInstalled();

/// 当前日志文件路径（安装后有效；未安装返回空串）。
const char* LogPath();

/// 同模块内其它编译单元的日志出口（格式同 printf；未安装时静默）
void ProbeLog(const char* fmt, ...);

/// 交换链汇总（TASK-B-R9-04 阶段 1）：每条链的创建者调用栈/desc/独立呈现计数，
/// 以及 WrapOnFrame 的调用次数与跳过原因。未安装时静默。
void DumpSwapchainSummary(const char* reason);

/// ForHwnd 创建 hook（TASK-R10-05 B-7）：与匿名命名空间内的同族 hook 不同，本函数
/// 放在 zfg 外层命名空间——测试目标（test_zfg_fg_shutdown.cpp）直接驱动它验证
/// "空 outSwapChain → E_INVALIDARG 且不建代理链"。patch 安装仍取本函数地址。
/// 语义：outSwapChain == nullptr 时返回 E_INVALIDARG（不 wrap、不转发原生创建）；
/// 其余行为 = 先尝试 FFX 代理链（A-3 宿主 desc 透传），失败回退原生创建。
HRESULT STDMETHODCALLTYPE HookCreateSwapChainForHwnd(IDXGIFactory2* self, IUnknown* device,
    HWND hwnd, const DXGI_SWAP_CHAIN_DESC1* desc,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain);

} // namespace zfg
