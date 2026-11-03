// =============================================================================
// zfg_dxgi_proxy.cpp —— dxgi.dll 代理（TASK-B-R9-03 阶段 A 门禁）
//
// 为什么用代理而不是注入：Windows 对**静态导入**的 DLL 先搜可执行文件目录，
// 因此把自制 dxgi.dll 放进 Player 目录即可在 UnityPlayer.dll 的导入解析期接管，
// 不需要挂起进程 + CreateRemoteThread（B-R9-01 的做法），也没有杀软/时序风险。
//
// 三条纪律（缺一不可）：
//   1. **精确转发**：系统 dxgi.dll 的全部 20 个按名导出逐个转发（导出名 + 序号
//      与系统表一致，见 zfg_dxgi.def）。转发用 GetProcAddress 得到的真实地址，
//      参数按**声明的形参个数**原样透传——导出名 → 真实参数个数 → 转发覆盖三者的
//      单一事实来源是 zfgprobe/zfg_dxgi_exports.h（TASK-R10-03 A-1）。
//      历史缺陷：这里曾断言"20 个导出均 ≤ 4 个参数"并用 4 参通用型转发，
//      而 DXGID3D10CreateDevice 是 7 参 → 第 5–7 参取未初始化栈槽 → 野地址写。
//      现在未文档化的导出按 8 槽窗口声明并全量透传，**任何 ≤8 参的导出都不会被截断**。
//      **不用 .def 的 "模块.函数" 转发语法**：那会 LoadLibrary("dxgi.dll") 命中
//      我们自己（同名），必然自我递归。
//   2. **默认零干预**：ZFG_HOOK 未设（或 "0"）时只做转发——不装 hook、不写日志、
//      不改任何 vtable、不加载 zfg_hook.dll。行为与原版 dxgi 逐位等价。
//   3. **失败安全**：系统 dll 加载失败/单个导出解析失败/zfg_hook 加载失败，全部
//      降级为"返回失败码"或"只转发"，绝不 abort、不抛异常逃逸、不阻止宿主启动。
//
// 系统 dll 的加载路径用 GetSystemDirectoryW 拼（不硬编码盘符），并且**按全路径**
// LoadLibrary——同名不同路径可以共存（实测：本机返回两个不同 HMODULE，系统那份
// 的 CreateDXGIFactory2 非空、我们这份的私有导出不在系统表里）。
//
// 安装 hook 的时机：首次 CreateDXGIFactory* 调用内（此时已离开加载器锁，可以安全
// LoadLibrary）。Unity 的交换链创建必然在这之后，所以 hook 一定赶得上。
// =============================================================================
#include <windows.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>

#include "zfgprobe/zfg_dxgi_exports.h"
#include "zfgprobe/zfg_env.h"
#include "zfgprobe/zfg_fg_input.h"

namespace {

/// 8 槽通用调用型（Win64：前 4 参走 rcx/rdx/r8/r9，其余走栈槽）。
/// 用 8 槽调用一个 N(<8) 参的真实函数是安全的：调用方清栈，被调方只读自己声明的槽。
using GenericFn8 = uintptr_t(WINAPI*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t,
    uintptr_t, uintptr_t, uintptr_t);

// ---- 系统 dxgi.dll 的导出表（顺序 = zfg::kDxgiExports 的顺序）----------------
struct ExportSlot {
    const char* name;
    FARPROC real;
};

ExportSlot g_slots[zfg::kDxgiExportCount] = {};
bool g_slotsBuilt = false;

void BuildSlotTable()
{
    if (g_slotsBuilt) {
        return;
    }
    for (size_t i = 0u; i < zfg::kDxgiExportCount; ++i) {
        g_slots[i].name = zfg::kDxgiExports[i].name;
        g_slots[i].real = nullptr;
    }
    g_slotsBuilt = true;
}

constexpr size_t kSlotCount = zfg::kDxgiExportCount;

enum : LONG { kStateCold = 0, kStateBusy = 1, kStateReady = 2, kStateFailed = 3 };

LONG g_state = kStateCold;
SRWLOCK g_stateLock = SRWLOCK_INIT;
HMODULE g_real = nullptr;
HMODULE g_self = nullptr;
HMODULE g_hookModule = nullptr;

HANDLE g_log = INVALID_HANDLE_VALUE;
SRWLOCK g_logLock = SRWLOCK_INIT;
bool g_logTried = false;

/// 失败返回值：HRESULT(DXGI_ERROR_UNSUPPORTED)。转发不可用时用它——宿主拿到
/// 明确的失败码可以自行降级，比跳空指针安全。
constexpr uintptr_t kFailUnsupported = 0x887A0004ull;

void OpenLogIfNeeded()
{
    if (g_logTried) {
        return;
    }
    ::AcquireSRWLockExclusive(&g_logLock);
    if (!g_logTried) {
        g_logTried = true;
        // B-2（TASK-R10-05）：三态读取。历史缺陷：char mode[32] 不看返回值——值
        // ≥ 32 字符时缓冲无 NUL，strcmp 越界读。Truncated → 按"非 0 的未知值"
        // 处理（本模块的门禁语义：只有 "0" 关闭）。
        char mode[32] = {};
        const zfg::EnvRead modeRead = zfg::ReadEnvA("ZFG_HOOK", mode, sizeof(mode));
        const bool hookRequested
            = (modeRead == zfg::EnvRead::Set && std::strcmp(mode, "0") != 0)
            || (modeRead == zfg::EnvRead::Truncated);
        if (hookRequested) {
            // B-2：日志路径三态读取。Truncated（≥ MAX_PATH）→ CreateFileA(ANSI)
            // 无法可靠打开超长路径 → 明确拒绝、回落默认路径（证据不静默丢失）。
            char path[MAX_PATH] = {};
            const zfg::EnvRead pathRead = zfg::ReadEnvA("ZFG_HOOK_LOG", path, sizeof(path));
            if (pathRead == zfg::EnvRead::Truncated) {
                (void)std::snprintf(path, sizeof(path), "zfg_probe.log"); // 不可靠值 → 拒绝
            } else if (pathRead == zfg::EnvRead::Unset) {
                char tempDir[MAX_PATH] = {};
                if (::GetTempPathA(static_cast<DWORD>(sizeof(tempDir)), tempDir) != 0u) {
                    (void)std::snprintf(path, sizeof(path), "%szfg_probe.log", tempDir);
                } else {
                    (void)std::snprintf(path, sizeof(path), "zfg_probe.log");
                }
            }
            // FILE_APPEND_DATA：与 zfg_hook.dll 共用同一份日志时，两条独立的写
            // 句柄各自追加，NTFS 上小写入是原子的，不会互相覆盖。
            g_log = ::CreateFileA(path, FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL, nullptr);
        }
    }
    ::ReleaseSRWLockExclusive(&g_logLock);
}

void Log(const char* fmt, ...)
{
    OpenLogIfNeeded();
    if (g_log == INVALID_HANDLE_VALUE) {
        return;
    }
    char body[512];
    va_list args;
    va_start(args, fmt);
    const int written = std::vsnprintf(body, sizeof(body) - 2u, fmt, args);
    va_end(args);
    if (written < 0) {
        return;
    }
    size_t len = static_cast<size_t>(written);
    if (len > sizeof(body) - 2u) {
        len = sizeof(body) - 2u;
    }
    body[len] = '\n';
    body[len + 1] = '\0';
    ::AcquireSRWLockExclusive(&g_logLock);
    if (g_log != INVALID_HANDLE_VALUE) {
        DWORD wrote = 0;
        (void)::WriteFile(g_log, body, static_cast<DWORD>(len + 1u), &wrote, nullptr);
    }
    ::ReleaseSRWLockExclusive(&g_logLock);
}

/// 取模块所在目录（拼兄弟 DLL 用）
bool SelfDirectory(wchar_t* out, size_t capacity)
{
    wchar_t path[MAX_PATH] = {};
    if (::GetModuleFileNameW(g_self, path, MAX_PATH) == 0u) {
        return false;
    }
    size_t i = std::wcslen(path);
    while (i > 0u && path[i - 1u] != L'\\') {
        --i;
    }
    if (i == 0u || i + 1u > capacity) {
        return false;
    }
    for (size_t k = 0u; k < i; ++k) {
        out[k] = path[k];
    }
    out[i] = L'\0';
    return true;
}

bool LoadRealDxgi()
{
    BuildSlotTable();
    wchar_t sysDir[MAX_PATH] = {};
    const UINT sysLen = ::GetSystemDirectoryW(sysDir, MAX_PATH);
    if (sysLen == 0u || sysLen >= MAX_PATH) {
        Log("[dxgi] FAILED: GetSystemDirectoryW err=%lu", ::GetLastError());
        return false;
    }
    wchar_t full[MAX_PATH] = {};
    if (std::swprintf(full, MAX_PATH, L"%s\\dxgi.dll", sysDir) < 0) {
        return false;
    }
    // 全路径加载 → 与同名（本模块）并存，不会被"已加载同名模块"短路
    g_real = ::LoadLibraryW(full);
    if (g_real == nullptr) {
        Log("[dxgi] FAILED: LoadLibraryW(system dxgi) err=%lu", ::GetLastError());
        return false;
    }
    wchar_t resolved[MAX_PATH] = {};
    (void)::GetModuleFileNameW(g_real, resolved, MAX_PATH);
    size_t missing = 0u;
    for (ExportSlot& slot : g_slots) {
        slot.real = ::GetProcAddress(g_real, slot.name);
        if (slot.real == nullptr) {
            ++missing;
            Log("[dxgi] WARN: system dxgi lacks export '%s' (forward returns E_UNSUPPORTED)",
                slot.name);
        }
    }
    Log("[dxgi] proxy active: self=%p real=%p realPath=%ls missing=%zu/%zu", g_self, g_real,
        resolved, missing, kSlotCount);
    // 缺一两个导出不算致命（宿主可能不用），但要在日志里留证
    return missing < kSlotCount;
}

/// 首次 CreateDXGIFactory* 时加载 hook（ZFG_HOOK 未设 → 完全不碰）
void MaybeInstallHook()
{
    static LONG s_hookState = 0; // 0=cold 1=busy 2=done
    if (::InterlockedCompareExchange(&s_hookState, 1, 0) != 0) {
        return; // 另一个线程在做，或者已经做完
    }
    char mode[32] = {};
    // B-2（TASK-R10-05）：三态读取；Truncated → 按"非 0 的未知值"（继续装 hook，
    // hook 侧会把未知模式归为 LogOnly），与旧语义一致且不再 UB。
    const zfg::EnvRead modeRead = zfg::ReadEnvA("ZFG_HOOK", mode, sizeof(mode));
    if (modeRead == zfg::EnvRead::Unset
        || (modeRead == zfg::EnvRead::Set && std::strcmp(mode, "0") == 0)) {
        s_hookState = 2;
        return; // 门禁状态：完全不干预
    }
    wchar_t dir[MAX_PATH] = {};
    if (!SelfDirectory(dir, MAX_PATH)) {
        Log("[dxgi] hook: cannot resolve own directory; forwarding only");
        s_hookState = 2;
        return;
    }
    wchar_t dllPath[MAX_PATH] = {};
    if (std::swprintf(dllPath, MAX_PATH, L"%szfg_hook.dll", dir) < 0) {
        s_hookState = 2;
        return;
    }
    HMODULE hook = ::LoadLibraryW(dllPath);
    if (hook == nullptr) {
        Log("[dxgi] hook: LoadLibraryW(zfg_hook.dll) err=%lu -> forwarding only",
            ::GetLastError());
        s_hookState = 2;
        return;
    }
    using EnsureFn = int (*)();
    const auto ensure = reinterpret_cast<EnsureFn>(
        ::GetProcAddress(hook, "ZFG_ProbeEnsureInstalled"));
    if (ensure == nullptr) {
        Log("[dxgi] hook: zfg_hook.dll missing ZFG_ProbeEnsureInstalled -> forwarding only");
        s_hookState = 2;
        return;
    }
    const int rc = ensure();
    g_hookModule = hook;
    Log("[dxgi] hook: mode=%s installed=%d", mode, rc);
    s_hookState = 2;
}

bool EnsureReal()
{
    if (g_state == kStateReady) {
        MaybeInstallHook();
        return true;
    }
    if (g_state == kStateFailed) {
        return false;
    }
    bool ok = false;
    ::AcquireSRWLockExclusive(&g_stateLock);
    if (g_state == kStateReady) {
        ok = true;
    } else if (g_state == kStateCold) {
        g_state = kStateBusy;
        ok = LoadRealDxgi();
        g_state = ok ? kStateReady : kStateFailed;
    } else {
        ok = (g_state == kStateReady);
    }
    ::ReleaseSRWLockExclusive(&g_stateLock);
    if (ok) {
        MaybeInstallHook(); // 锁外：它内部会调 CreateDXGIFactory1，不能重入本锁
    }
    return ok;
}

/// 转发一个导出（未解析 → 返回 E_UNSUPPORTED）。
///
/// 8 槽全量透传（TASK-R10-03 A-1）：调用方把自己的 8 个形参原样交进来，这里按
/// 8 参函数型调用真实实现。**绝不再按"我以为只有 4 参"截断**——那会让真实实现
/// 从本函数栈帧里读未初始化值（R10-02 A-1 的野地址写）。
uintptr_t ForwardArgs(size_t index, const uintptr_t* args)
{
    if (args == nullptr || index >= kSlotCount) {
        return kFailUnsupported;
    }
    if (!EnsureReal()) {
        return kFailUnsupported;
    }
    const FARPROC target = g_slots[index].real;
    if (target == nullptr) {
        return kFailUnsupported;
    }
    return reinterpret_cast<GenericFn8>(target)(args[0], args[1], args[2], args[3], args[4],
        args[5], args[6], args[7]);
}

} // namespace

// =============================================================================
// 系统 dxgi.dll 的 20 个导出：逐个转发。
//
// 形参个数 = 转发覆盖（见 zfg_dxgi_exports.h 的 declaredArity）：
//   · 有文档/公开原型且 ≤4 参的按真实签名声明；
//   · 其余一律 8 槽窗口声明，未用到的槽位由调用方按 0 传。
// =============================================================================

extern "C" uintptr_t WINAPI ApplyCompatResolutionQuirking(uintptr_t a, uintptr_t b, uintptr_t c,
    uintptr_t d, uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxApplyCompatResolutionQuirking, args);
}

extern "C" uintptr_t WINAPI CompatString(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxCompatString, args);
}

extern "C" uintptr_t WINAPI CompatValue(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxCompatValue, args);
}

extern "C" HRESULT WINAPI CreateDXGIFactory(REFIID riid, void** factory)
{
    const uintptr_t args[zfg::kForwardSlots] = {reinterpret_cast<uintptr_t>(&riid),
        reinterpret_cast<uintptr_t>(factory), 0u, 0u, 0u, 0u, 0u, 0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxCreateDXGIFactory, args));
}

extern "C" HRESULT WINAPI CreateDXGIFactory1(REFIID riid, void** factory)
{
    const uintptr_t args[zfg::kForwardSlots] = {reinterpret_cast<uintptr_t>(&riid),
        reinterpret_cast<uintptr_t>(factory), 0u, 0u, 0u, 0u, 0u, 0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxCreateDXGIFactory1, args));
}

extern "C" HRESULT WINAPI CreateDXGIFactory2(UINT flags, REFIID riid, void** factory)
{
    const uintptr_t args[zfg::kForwardSlots] = {static_cast<uintptr_t>(flags),
        reinterpret_cast<uintptr_t>(&riid), reinterpret_cast<uintptr_t>(factory), 0u, 0u, 0u, 0u,
        0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxCreateDXGIFactory2, args));
}

// ---- R10-02 A-1 的三条截断点：现在 7/6/7 参全部透传 --------------------------
extern "C" uintptr_t WINAPI DXGID3D10CreateDevice(uintptr_t hModule, uintptr_t pFactory,
    uintptr_t pAdapter, uintptr_t flags, uintptr_t pExtensions, uintptr_t numExtensions,
    uintptr_t ppDevice, uintptr_t pad0)
{
    const uintptr_t args[zfg::kForwardSlots] = {hModule, pFactory, pAdapter, flags, pExtensions,
        numExtensions, ppDevice, pad0};
    return ForwardArgs(zfg::kIdxDXGID3D10CreateDevice, args);
}

extern "C" uintptr_t WINAPI DXGID3D10CreateLayeredDevice(uintptr_t hModule, uintptr_t pFactory,
    uintptr_t pAdapter, uintptr_t flags, uintptr_t pExtensions, uintptr_t numExtensions,
    uintptr_t pad0, uintptr_t pad1)
{
    const uintptr_t args[zfg::kForwardSlots] = {hModule, pFactory, pAdapter, flags, pExtensions,
        numExtensions, pad0, pad1};
    return ForwardArgs(zfg::kIdxDXGID3D10CreateLayeredDevice, args);
}

extern "C" uintptr_t WINAPI DXGID3D10GetLayeredDeviceSize(uintptr_t hModule, uintptr_t pFactory,
    uintptr_t pAdapter, uintptr_t flags, uintptr_t pExtensions, uintptr_t numExtensions,
    uintptr_t pLayeredDeviceSize, uintptr_t pad0)
{
    const uintptr_t args[zfg::kForwardSlots] = {hModule, pFactory, pAdapter, flags, pExtensions,
        numExtensions, pLayeredDeviceSize, pad0};
    return ForwardArgs(zfg::kIdxDXGID3D10GetLayeredDeviceSize, args);
}

extern "C" uintptr_t WINAPI DXGID3D10RegisterLayers(uintptr_t pLayers, uintptr_t numLayers,
    uintptr_t pad0, uintptr_t pad1, uintptr_t pad2, uintptr_t pad3, uintptr_t pad4, uintptr_t pad5)
{
    const uintptr_t args[zfg::kForwardSlots] = {pLayers, numLayers, pad0, pad1, pad2, pad3, pad4,
        pad5};
    return ForwardArgs(zfg::kIdxDXGID3D10RegisterLayers, args);
}

extern "C" HRESULT WINAPI DXGIDeclareAdapterRemovalSupport()
{
    const uintptr_t args[zfg::kForwardSlots] = {0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxDXGIDeclareAdapterRemovalSupport, args));
}

extern "C" HRESULT WINAPI DXGIDisableVBlankVirtualization()
{
    const uintptr_t args[zfg::kForwardSlots] = {0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxDXGIDisableVBlankVirtualization, args));
}

extern "C" void WINAPI DXGIDumpJournal(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    (void)ForwardArgs(zfg::kIdxDXGIDumpJournal, args);
}

extern "C" HRESULT WINAPI DXGIGetDebugInterface1(UINT flags, REFIID riid, void** debug)
{
    const uintptr_t args[zfg::kForwardSlots] = {static_cast<uintptr_t>(flags),
        reinterpret_cast<uintptr_t>(&riid), reinterpret_cast<uintptr_t>(debug), 0u, 0u, 0u, 0u,
        0u};
    return static_cast<HRESULT>(ForwardArgs(zfg::kIdxDXGIGetDebugInterface1, args));
}

extern "C" uintptr_t WINAPI DXGIReportAdapterConfiguration(uintptr_t a, uintptr_t b, uintptr_t c,
    uintptr_t d, uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxDXGIReportAdapterConfiguration, args);
}

extern "C" uintptr_t WINAPI PIXBeginCapture(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxPIXBeginCapture, args);
}

extern "C" uintptr_t WINAPI PIXEndCapture(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxPIXEndCapture, args);
}

extern "C" uintptr_t WINAPI PIXGetCaptureState(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d,
    uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxPIXGetCaptureState, args);
}

extern "C" uintptr_t WINAPI SetAppCompatStringPointer(uintptr_t a, uintptr_t b, uintptr_t c,
    uintptr_t d, uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxSetAppCompatStringPointer, args);
}

extern "C" uintptr_t WINAPI UpdateHMDEmulationStatus(uintptr_t a, uintptr_t b, uintptr_t c,
    uintptr_t d, uintptr_t e, uintptr_t f, uintptr_t g, uintptr_t h)
{
    const uintptr_t args[zfg::kForwardSlots] = {a, b, c, d, e, f, g, h};
    return ForwardArgs(zfg::kIdxUpdateHMDEmulationStatus, args);
}

// =============================================================================
// ZFG_*：只有本代理才有的辅助导出（托管 probe 用；ZFG_HOOK 未设时全部空转）
// =============================================================================

namespace {

using SubmitFn = int (*)(const ZfgFgFrameInputs*);
using StatsFn = int (*)(ZfgFgStats*);
using FlagFn = int (*)(int);
using VoidFn = int (*)();

void* HookProc(const char* name)
{
    if (g_hookModule == nullptr) {
        return nullptr;
    }
    return reinterpret_cast<void*>(::GetProcAddress(g_hookModule, name));
}

} // namespace

/// 托管侧每帧提交 depth/MV + 相机参数（返回 1 = 已受理）
extern "C" __declspec(dllexport) int ZFG_FgSubmitInputs(const ZfgFgFrameInputs* inputs)
{
    const auto fn = reinterpret_cast<SubmitFn>(HookProc("ZFG_FgSubmitInputs"));
    return (fn != nullptr) ? fn(inputs) : 0;
}

/// 托管侧读取统计（返回 1 = 有 hook 接管）
extern "C" __declspec(dllexport) int ZFG_FgGetStats(ZfgFgStats* stats)
{
    const auto fn = reinterpret_cast<StatsFn>(HookProc("ZFG_FgGetStats"));
    return (fn != nullptr) ? fn(stats) : 0;
}

/// 运行期开关插帧（0 = 只 wrap 不插帧/直通；1 = 插帧）；返回 1 = 已受理
extern "C" __declspec(dllexport) int ZFG_FgSetEnabled(int enabled)
{
    const auto fn = reinterpret_cast<FlagFn>(HookProc("ZFG_FgSetEnabled"));
    return (fn != nullptr) ? fn(enabled) : 0;
}

/// 呈现帧像素读回（真帧/插帧的统计打到 hook 日志里）
extern "C" __declspec(dllexport) int ZFG_FgAnalyzeCaptures(void)
{
    const auto fn = reinterpret_cast<VoidFn>(HookProc("ZFG_FgAnalyzeCaptures"));
    return (fn != nullptr) ? fn() : 0;
}

/// B-3（TASK-R10-05）：关停 FFX 上下文与代理交换链（幂等）。托管侧退出前调用
/// （未来 Application.quitting 接线点）；hook 缺席时返回 0。
extern "C" __declspec(dllexport) int ZFG_FgShutdown(void)
{
    const auto fn = reinterpret_cast<VoidFn>(HookProc("ZFG_FgShutdown"));
    return (fn != nullptr) ? fn() : 0;
}

/// 代理是否已接管（1 = 本模块正在为宿主提供 DXGI）
extern "C" __declspec(dllexport) int ZFG_ProxyReady(void)
{
    return (g_state == kStateReady) ? 1 : 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        (void)::DisableThreadLibraryCalls(instance);
        g_self = instance;
        // 不在 DllMain 里做任何加载/建对象：此处仍持有加载器锁（会与
        // LoadLibrary(system dxgi) 的加载器重入撞死锁）。全部推迟到首次导出调用。
    }
    return TRUE;
}
