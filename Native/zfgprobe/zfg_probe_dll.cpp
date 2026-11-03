// =============================================================================
// zfg_probe_dll.cpp —— hook 模块的 DLL 入口（TASK-B-R9-01 阶段 0 spike；
// TASK-B-R9-03 起同时作为 dxgi 代理惰性加载的 zfg_hook.dll）
//
// 不实现任何 Unity 图形接口，只做"进程内 vtable hook + FFX 代理交换链"。
//
// 安装不在 DllMain 里做：DllMain 持有加载器锁，此时创建 DXGI 工厂会再次进入
// 加载器（dxgi.dll / d3d12.dll），有死锁风险。改由代理（dxgi.dll）在首次
// CreateDXGIFactory* 时显式调用 ZFG_ProbeEnsureInstalled（此时加载器锁已释放）；
// 迟到的兜底线程只在没有代理时才有意义（注入式 spike 的旧路径）。
// =============================================================================
#include "zfgprobe/zfg_fg_input.h"
#include "zfgprobe/zfg_probe_hook.h"
#include "zfgprobe/zfg_probe_wrap.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace {

DWORD WINAPI InstallThreadProc(LPVOID)
{
    // 兜底：没有代理接管时，插件被 Unity 按需加载（晚于交换链创建）也不会崩，
    // 只是 hook 赶不上第一次创建（B-R9-01 已实证的时序事实）。
    ::Sleep(50);
    (void)::zfg::InstallFromEnv();
    return 0;
}

} // namespace

/// 显式安装入口：dxgi 代理在首次 CreateDXGIFactory* 内调用。返回 1=已安装。
extern "C" __declspec(dllexport) int ZFG_ProbeEnsureInstalled(void)
{
    return (::zfg::InstallFromEnv() == ::zfg::ProbeInstall::Installed) ? 1 : 0;
}

/// 托管侧每帧提交 depth/MV + 相机参数（dxgi 代理转发）。返回 1 = 已受理。
extern "C" __declspec(dllexport) int ZFG_FgSubmitInputs(const ZfgFgFrameInputs* inputs)
{
    return ::zfg::WrapSubmitInputs(inputs) ? 1 : 0;
}

/// 托管侧读取插帧统计（dxgi 代理转发）。返回 1 = 代理链已建立。
extern "C" __declspec(dllexport) int ZFG_FgGetStats(ZfgFgStats* out)
{
    return ::zfg::WrapGetStats(out) ? 1 : 0;
}

/// 托管侧运行期开关（dxgi 代理转发）。返回 1 = 已受理。
extern "C" __declspec(dllexport) int ZFG_FgSetEnabled(int enabled)
{
    return ::zfg::WrapSetFrameGenEnabled(enabled);
}

/// 呈现帧像素读回（dxgi 代理转发）。返回 1 = 已执行。
extern "C" __declspec(dllexport) int ZFG_FgAnalyzeCaptures(void)
{
    return ::zfg::WrapAnalyzeCaptures();
}

/// B-3（TASK-R10-05）：关停 FFX 上下文与代理交换链。历史缺陷：WrapShutdown 全仓
/// 无调用点也未导出——FFX 上下文与代理交换链活到进程终止（pacing 线程无人停、
/// g_device/g_queue 的 AddRef 永不 Release）。幂等：重复调用为空操作。
/// 返回 1 = 已执行（无 wrap 时也是安全的空操作）。
extern "C" __declspec(dllexport) int ZFG_FgShutdown(void)
{
    ::zfg::WrapShutdown();
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        (void)::DisableThreadLibraryCalls(instance);
        const HANDLE thread = ::CreateThread(nullptr, 0, &InstallThreadProc, nullptr, 0, nullptr);
        if (thread != nullptr) {
            (void)::CloseHandle(thread);
        }
    } else if (reason == DLL_PROCESS_DETACH) {
        // B-3：进程退出路径挂接（历史缺陷：全仓无人关停）。幂等闸门保证与托管侧
        // 显式 ZFG_FgShutdown 并发时只执行一次。
        // 边界（诚实记录）：detach 持加载器锁——FFX/d3d12 均已加载（无 LoadLibrary），
        // GPU 追平带 2s 超时、全部失败路径吞掉（退出期宁可不取证也不阻塞/崩溃）；
        // FFX DestroyContext 内部若等待自身线程，理论上仍有 loader-lock 类死锁面
        // （审计 B-3 自己也写"先确认退出是否偶发崩溃"）。**推荐路径仍是托管侧在
        // Application.quitting 显式调 ZFG_FgShutdown**（锁外、GPU 存活时）；本挂接
        // 是"没人调"时的兜底，且只在 wrap 生效过（g_fg != nullptr）时才有实际动作。
        ::zfg::WrapShutdown();
    }
    return TRUE;
}
