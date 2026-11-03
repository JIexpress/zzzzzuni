// =============================================================================
// zfg_probe_inject.cpp —— 早期注入启动器（TASK-B-R9-01 阶段 0 spike）
//
// 为什么需要它：Unity 的原生插件是**惰性加载**的（实测 Player 里 zfg_probe.dll
// 直到首次 P/Invoke 才进进程），而交换链在引擎初始化期就已创建。要回答"Unity
// 走哪条 DXGI 创建路径"，hook 必须比引擎更早。
//
// 做法（不改 Unity 安装目录、不改 player 目录布局）：
//   1. CreateProcess(CREATE_SUSPENDED) —— 主线程冻结；
//   2. 远程线程 LoadLibraryW(zfg_probe.dll)；
//   3. 远程线程调 ZFG_ProbeEnsureInstalled()（同步安装 hook 并写日志）；
//   4. ResumeThread —— 引擎从现在才开始跑，hook 已就位。
//
// 用法：zfg_probe_inject.exe <目标 exe> [目标参数...]
//   环境变量 ZFG_PROBE / ZFG_PROBE_LOG 被子进程继承（与原行为一致）。
// =============================================================================
#include <windows.h>
#include <tlhelp32.h>

#include <cstdio>
#include <cstring>
#include <cwchar>

namespace {

/// 目标进程里某模块的基址（注入后用于算导出偏移）
HMODULE RemoteModuleBase(DWORD pid, const wchar_t* moduleName)
{
    const HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return nullptr;
    }
    MODULEENTRY32W entry {};
    entry.dwSize = sizeof(entry);
    HMODULE base = nullptr;
    if (::Module32FirstW(snapshot, &entry) == TRUE) {
        do {
            if (_wcsicmp(entry.szModule, moduleName) == 0) {
                base = entry.hModule;
                break;
            }
        } while (::Module32NextW(snapshot, &entry) == TRUE);
    }
    ::CloseHandle(snapshot);
    return base;
}

/// 在目标进程里跑一个单参数远程线程（LoadLibrary / 导出函数），返回退出码
bool RunRemoteThread(HANDLE process, void* startRoutine, void* argument, DWORD* outCode)
{
    const HANDLE thread = ::CreateRemoteThread(process, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(startRoutine), argument, 0, nullptr);
    if (thread == nullptr) {
        return false;
    }
    (void)::WaitForSingleObject(thread, 30000);
    DWORD code = 0;
    (void)::GetExitCodeThread(thread, &code);
    ::CloseHandle(thread);
    if (outCode != nullptr) {
        *outCode = code;
    }
    return true;
}

} // namespace

int main(int argc, char** argv)
{
    if (argc < 2) {
        std::printf("usage: zfg_probe_inject.exe <target.exe> [args...]\n");
        return 2;
    }

    // ---- 宿主机上的探测 DLL 路径（与本 exe 同目录）-----------------------------
    wchar_t dllPath[MAX_PATH] = {};
    {
        wchar_t selfPath[MAX_PATH] = {};
        const DWORD len = ::GetModuleFileNameW(nullptr, selfPath,
            static_cast<DWORD>(sizeof(selfPath) / sizeof(selfPath[0])));
        if (len == 0u) {
            std::printf("inject: GetModuleFileNameW failed\n");
            return 1;
        }
        wchar_t* slash = wcsrchr(selfPath, L'\\');
        if (slash == nullptr) {
            std::printf("inject: cannot derive directory\n");
            return 1;
        }
        *(slash + 1) = L'\0';
        (void)::swprintf_s(dllPath, MAX_PATH, L"%szfg_probe.dll", selfPath);
    }
    if (::GetFileAttributesW(dllPath) == INVALID_FILE_ATTRIBUTES) {
        std::printf("inject: probe dll not found: %ls\n", dllPath);
        return 1;
    }

    // ---- 拼目标命令行 ---------------------------------------------------------
    char commandLine[4096] = {};
    size_t used = 0u;
    used += static_cast<size_t>(std::snprintf(commandLine + used, sizeof(commandLine) - used,
        "\"%s\"", argv[1]));
    for (int i = 2; i < argc && used < sizeof(commandLine) - 2u; ++i) {
        used += static_cast<size_t>(std::snprintf(commandLine + used, sizeof(commandLine) - used,
            " %s", argv[i]));
    }

    STARTUPINFOA startup {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION info {};
    if (::CreateProcessA(argv[1], commandLine, nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
            nullptr, &startup, &info)
        == FALSE) {
        std::printf("inject: CreateProcess failed (err=%lu)\n", ::GetLastError());
        return 1;
    }
    std::printf("inject: pid=%lu suspended\n", info.dwProcessId);

    // ---- 1) 远程 LoadLibrary ------------------------------------------------
    const SIZE_T pathBytes = (wcslen(dllPath) + 1u) * sizeof(wchar_t);
    void* remotePath = ::VirtualAllocEx(info.hProcess, nullptr, pathBytes,
        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    bool ok = remotePath != nullptr
        && ::WriteProcessMemory(info.hProcess, remotePath, dllPath, pathBytes, nullptr) != FALSE;
    const auto loadLibraryAddr = reinterpret_cast<void*>(::GetProcAddress(
        ::GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    DWORD code = 0;
    ok = ok && RunRemoteThread(info.hProcess, loadLibraryAddr, remotePath, &code);
    std::printf("inject: LoadLibrary remote thread -> %lu\n", code);
    if (!ok || code == 0u) {
        std::printf("inject: LoadLibrary failed; resuming anyway (hook will be absent)\n");
        (void)::ResumeThread(info.hThread);
        return 1;
    }

    // ---- 2) 远程调用导出函数（同步安装 hook）--------------------------------
    const HMODULE localDll = ::LoadLibraryW(dllPath);
    if (localDll == nullptr) {
        std::printf("inject: local LoadLibrary failed\n");
        (void)::ResumeThread(info.hThread);
        return 1;
    }
    const auto localExport = reinterpret_cast<void*>(::GetProcAddress(localDll,
        "ZFG_ProbeEnsureInstalled"));
    const HMODULE remoteBase = RemoteModuleBase(info.dwProcessId, L"zfg_probe.dll");
    if (localExport != nullptr && remoteBase != nullptr) {
        const auto offset = reinterpret_cast<unsigned long long>(localExport)
            - reinterpret_cast<unsigned long long>(localDll);
        const auto remoteExport = reinterpret_cast<void*>(
            reinterpret_cast<unsigned long long>(remoteBase) + offset);
        DWORD installCode = 0;
        (void)RunRemoteThread(info.hProcess, remoteExport, nullptr, &installCode);
        std::printf("inject: EnsureInstalled -> %lu (1=installed)\n", installCode);
    } else {
        std::printf("inject: cannot resolve export (hook install skipped)\n");
    }

    // ---- 3) 冻结解除 --------------------------------------------------------
    (void)::ResumeThread(info.hThread);
    std::printf("inject: resumed\n");
    ::CloseHandle(info.hThread);
    ::CloseHandle(info.hProcess);
    return 0;
}
