// =============================================================================
// zfg_probe_hook.cpp —— DXGI vtable 探测 hook 实现（TASK-B-R9-01 阶段 0 spike）
//
// 机制：DXGI 的工厂对象与交换链对象各自共享一份静态 vtable（在 dxgi.dll 的
// .rdata 内）。先自己创建一个工厂拿到该 vtable，再对目标槽位做 VirtualProtect
// + 覆盖为记录函数；记录函数完成"记参数 → 原样转发原始实现"，不改变任何返回值。
// 交换链的 Present/ResizeBuffers 在第一次拿到实例时按类 patch（同类共享 vtable）。
//
// 为什么不用指令级 detour：.rdata 的 vtable 覆盖不需要反汇编/重定位，写坏也只
// 影响我们自己进程，且卸载时能精确恢复。风险面最小。
//
// 三种模式（ZFG_HOOK；未设/0 = 完全不安装。B-R9-01 的 ZFG_PROBE 仍兼容）：
//   log  = 只记录，返回 Unity 自己的交换链
//   wrap = 换成 FFX 代理交换链但不插帧（frameGenerationEnabled=false）
//   fg   = 代理交换链 + 每帧 Configure/PrepareV2（TASK-B-R9-03 阶段 C）
//
// 失败安全：任何一步失败（拿不到工厂 / VirtualProtect 失败 / 槽位为空 / FFX
// 拒绝）都不替换，宿主继续走原生路径。
// =============================================================================
#include "zfgprobe/zfg_probe_hook.h"

#include "zfgprobe/zfg_factory_slots.h"
#include "zfgprobe/zfg_env.h"
#include "zfgprobe/zfg_present_reentry.h"
#include "zfgprobe/zfg_probe_wrap.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <dxgi1_6.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>

namespace zfg {

/// 前向声明（TASK-B-R9-05 G2）：定义见本文件末尾。匿名命名空间里的安装/打补丁
/// 代码用它做"拿到的原始函数是否落在本模块内 → 拒绝 patch"的兜底判定。
bool IsProbeCode(void* address);

namespace {

// ---- vtable 槽位 ---------------------------------------------------------------
// 槽号常量与槽位表已上收到 zfgprobe/zfg_factory_slots.h（TASK-R10-03 A-2）：
// 产品代码与单测共用同一份表，单测再按 SDK 头（CINTERFACE 下的
// IDXGIFactory2Vtbl / IDXGISwapChain1Vtbl）反算偏移 —— 常量改错即红。
// 历史缺陷：Composition 曾写 17（实为 GetSharedResourceAdapterLuid），真正槽位 24。
using zfg::kSlotCreateSwapChain;
using zfg::kSlotCreateSwapChainForComposition;
using zfg::kSlotCreateSwapChainForCoreWindow;
using zfg::kSlotCreateSwapChainForHwnd;
using zfg::kSlotPresent;
using zfg::kSlotPresent1;
using zfg::kSlotResizeBuffers;
using zfg::kIdxgiFactory2VtblCount;
using zfg::kVtblSlotCount;
using zfg::kVtblSlots;
using zfg::VtblFamily;
using zfg::VtblSlotSpec;

enum class Mode : int { LogOnly = 0, Wrap = 1, Fg = 2 };

const char* ModeName(Mode mode)
{
    switch (mode) {
    case Mode::Wrap:
        return "wrap";
    case Mode::Fg:
        return "fg";
    default:
        return "log";
    }
}

// ---- 原始实现（记录后转发）---------------------------------------------------
using FnCreateSwapChain = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory* self, IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** outSwapChain);
using FnCreateSwapChainForHwnd = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2* self,
    IUnknown* device, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1* desc,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain);
using FnCreateSwapChainForCoreWindow = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2* self,
    IUnknown* device, IUnknown* coreWindow, const DXGI_SWAP_CHAIN_DESC1* desc,
    IDXGIOutput* restrictToOutput, IDXGISwapChain1** outSwapChain);
using FnCreateSwapChainForComposition = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2* self,
    IUnknown* device, const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain);
using FnPresent = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain* self, UINT syncInterval, UINT flags);
using FnPresent1 = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1* self, UINT syncInterval, UINT flags,
    const DXGI_PRESENT_PARAMETERS* params);
using FnResizeBuffers = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain* self, UINT bufferCount,
    UINT width, UINT height, DXGI_FORMAT newFormat, UINT swapChainFlags);

FnCreateSwapChain g_origCreateSwapChain = nullptr;
FnCreateSwapChainForHwnd g_origCreateSwapChainForHwnd = nullptr;
FnCreateSwapChainForCoreWindow g_origCreateSwapChainForCoreWindow = nullptr;
FnCreateSwapChainForComposition g_origCreateSwapChainForComposition = nullptr;

/// 线程内重入标记：FFX 的 ForHwnd 路径会用同一个 IDXGIFactory 再建一条**内部**
/// 交换链，会再次进到本 hook。若不加这道闸，wrap 会自我递归（实测：日志里只剩
/// 入口行、Unity 反复重试）。重入时一律走原生创建。
thread_local bool t_inWrap = false;

/// 线程内重入标记（TASK-B-R9-04）：FFX 的 Present1 实现内部会再调一次**同一个
/// 代理对象**的 Present（日志里 #1 Present1 → #2 Present 同指针）。那是 FFX 的
/// 内部转发，不是应用帧；若也在那里跑 Configure/Prepare，frameID 会被二次推进。
thread_local bool t_inWrappedPresent = false;

/// 每个交换链类一份原始指针（原生类与 FFX 包装类是两个 vtable，不能混用）
struct SwapchainHooks {
    void** vtbl;
    FnPresent present;
    FnPresent1 present1;
    FnResizeBuffers resizeBuffers;
};
constexpr size_t kMaxSwapchainClasses = 4u;
SwapchainHooks g_scHooks[kMaxSwapchainClasses] = {};
size_t g_scHookCount = 0u;

// ---- 日志 -------------------------------------------------------------------
HANDLE g_logFile = INVALID_HANDLE_VALUE;
SRWLOCK g_logLock = SRWLOCK_INIT;
SRWLOCK g_logOpenLock = SRWLOCK_INIT;   // 只保护"首次打开日志文件"这一段
char g_logPath[MAX_PATH] = {};
Mode g_mode = Mode::LogOnly;
volatile LONG64 g_presentCount = 0;
volatile LONG64 g_createCalls = 0;

// =============================================================================
// 安装守卫（TASK-B-R9-05 G1）
//
// B-R9-04 验收的 R1：`g_installed` 曾是普通 bool，"检查 → patch"非原子。进程里
// 有 2–4 个安装入口（dxgi 代理的 ZFG_ProbeEnsureInstalled + DllMain 的 50ms 兜底
// 线程），两个线程同时判定"没装过"就都会 patch；第二个 PatchSlot 读到的"原始
// 函数"是**第一个线程刚写进去的自家 hook** → HookCreateSwapChainForHwnd 自我
// 递归 → 栈溢出（exit 139，证据 TempLogs/r9_04/audit_wrap.log）。
//
// 现在换成三态机，只有 CAS 的赢家能改 vtable：
//   0 = 未装 / 1 = 安装中 / 2 = 已装
// 状态 1 的竞争者自旋等待（带超时）；超时 → 放弃并保持原生（绝不二次 patch）。
// 安装中途失败 → 回滚已写槽位 → 状态退回 0 并置 g_installAbandoned，本进程不再重试。
// =============================================================================
constexpr LONG kInstallIdle = 0;
constexpr LONG kInstallBusy = 1;
constexpr LONG kInstallDone = 2;

volatile LONG g_installState = kInstallIdle;
/// 安装失败并已回滚后的终态标记：后续入口直接保持原生，不再反复尝试/刷日志
volatile LONG g_installAbandoned = 0;
/// 竞争等待超时（毫秒）。正常安装是"建一个工厂 + 4 次 VirtualProtect"，亚毫秒级，
/// 3s 已远超任何合理耗时；等不到就说明对端卡死 → 宁可原生也不二次 patch。
constexpr unsigned long long kInstallWaitTimeoutMs = 3000ull;

/// ZFG_PROBE_INSTALL_STAGGER_MS 的解析缓存（-1 = 尚未读取）
volatile LONG g_staggerMsCache = -1;
/// 设备门控（G3）的一次性日志标记（两类各一次，避免刷屏）
volatile LONG g_gateLoggedD3D12 = 0;
volatile LONG g_gateLoggedOther = 0;

// =============================================================================
// 交换链注册表（TASK-B-R9-04 阶段 1 取证）
//
// B-R9-03 只知道"同 hwnd 上出现两条链"，不知道第二条是谁建的。这里给每条链
// 建档：创建序号、hwnd、desc、是否 wrap、**创建调用栈的模块归属**（可证伪：
// 栈里出现哪个 dll 就是谁调的），以及**每条链独立**的 Present/Present1 计数
// （按 ALLOW_TEARING、线程、成败分桶）。汇总表在 ZFG_FgAnalyzeCaptures 时 dump，
// 并附 DXGI 自己的 GetLastPresentCount —— 那个数字才是"DXGI 真的呈现了几次"。
// =============================================================================
constexpr size_t kMaxChains = 8u;

struct ChainRecord {
    void* sc = nullptr;
    int index = 0;              // 1-based 创建序号
    bool wrapped = false;       // true = 返回给宿主的是 FFX 代理链
    HWND hwnd = nullptr;
    unsigned width = 0;
    unsigned height = 0;
    unsigned buffers = 0;
    unsigned usage = 0;
    unsigned flags = 0;
    int format = 0;
    int effect = 0;
    DWORD createTid = 0;
    unsigned long long createMs = 0;
    char creator[192] = {};     // 创建调用栈（模块名+偏移，跳过本模块帧）
    // ---- 呈现计数（跨线程，全部原子）----
    volatile LONG64 nPresent = 0;    // IDXGISwapChain::Present 次数
    volatile LONG64 nPresent1 = 0;   // IDXGISwapChain1::Present1 次数
    volatile LONG64 nOk = 0;         // 返回 S_OK（DXGI 真的呈现了）
    volatile LONG64 nNotOk = 0;      // 非 S_OK（被遮挡/被跳过的呈现）
    volatile LONG64 nTearing = 0;    // flags & DXGI_PRESENT_ALLOW_TEARING（FFX 底层链）
    volatile LONG64 nApp = 0;        // 非 ALLOW_TEARING 且非嵌套 → 应用级呈现
    DWORD tidApp = 0;                // 首个应用级呈现线程
    DWORD tidTearing = 0;            // 首个 ALLOW_TEARING 呈现线程
    unsigned long long firstMs = 0;
    unsigned long long lastMs = 0;
};

ChainRecord g_chains[kMaxChains];
size_t g_chainCount = 0u;
SRWLOCK g_chainLock = SRWLOCK_INIT;

// WrapOnFrame 的调用/跳过原因（阶段 1 的"1 Prepare ↔ 1 Present 有没有被破坏"）
volatile LONG64 g_wrapFrames = 0;        // 进了 WrapOnFrame 的应用级 Present 次数
volatile LONG64 g_wrapSkipNested = 0;    // 嵌套在 FFX Present 内部的二次 Present

/// 逐帧呈现轨迹（ZFG_FG_TRACE=1）：把每一次应用级呈现的毫秒时间戳打出来，
/// 用来判定"呈现次数 vs 渲染帧数"的比值从哪来（阶段 1 的核心口径问题）。
bool TraceEnabled()
{
    static int s_state = -1;
    if (s_state < 0) {
        char value[8] = {};
        // B-2（TASK-R10-05）：返回值三态检查。历史缺陷：不看返回值——值 ≥ 8 字符
        // 时缓冲无 NUL，strcmp 越界读。Truncated → 视为"无效值"（trace 关）。
        const zfg::EnvRead read = zfg::ReadEnvA("ZFG_FG_TRACE", value, sizeof(value));
        s_state = (read == zfg::EnvRead::Set && std::strcmp(value, "0") != 0) ? 1 : 0;
    }
    return s_state == 1;
}

void LogLine(const char* fmt, ...)
{
    char body[1024];
    va_list args;
    va_start(args, fmt);
    const int written = std::vsnprintf(body, sizeof(body) - 2u, fmt, args);
    va_end(args);
    if (written < 0) {
        return;
    }
    const size_t len = static_cast<size_t>(written) < sizeof(body) - 2u
        ? static_cast<size_t>(written)
        : sizeof(body) - 2u;
    body[len] = '\n';
    body[len + 1u] = '\0';

    ::OutputDebugStringA(body);
    if (g_logFile == INVALID_HANDLE_VALUE) {
        return;
    }
    ::AcquireSRWLockExclusive(&g_logLock);
    DWORD wrote = 0;
    (void)::WriteFile(g_logFile, body, static_cast<DWORD>(len + 1u), &wrote, nullptr);
    ::ReleaseSRWLockExclusive(&g_logLock);
}

/// 时间戳（系统启动至今毫秒），让"创建时机"可对齐 Unity 日志
unsigned long long NowMs()
{
    return static_cast<unsigned long long>(::GetTickCount64());
}

// ---- 安装守卫的原子读写 / 放大器 -------------------------------------------

LONG ReadInstallState()
{
    // InterlockedCompareExchange(dst, 期望写入值, 比较值)：比较值等于当前值时
    // 写入的比较值与期望值相同 → 等价于一次原子读，且不会被优化成普通 load。
    return ::InterlockedCompareExchange(&g_installState, kInstallIdle, kInstallIdle);
}

bool InstallAbandoned()
{
    return ::InterlockedCompareExchange(&g_installAbandoned, 1, 1) == 1;
}

/// 测试专用竞争放大器（TASK-B-R9-05 要求 5）。默认 0 = **完全不改变行为**：
/// 不 Sleep、日志里一行都不多。设成 N>0 时：
///   ① 抢占安装权**之前** Sleep(N)：把多个入口挤到同一时刻（窗口放大）；
///   ② 抢到之后在"安装中"状态里再保持 N 毫秒：让竞争者**必然**走到自旋等待分支，
///      而不是等赢家装完才发现状态已是 2。
/// 只有 ① 是不够的：两个入口本身相差 50ms（DllMain 兜底线程先 Sleep(50)），
/// 若赢家的临界区只有 1ms，后来者永远看不到状态 1，自旋分支就无法被证明。
LONG InstallStaggerMs()
{
    LONG cached = ::InterlockedCompareExchange(&g_staggerMsCache, -1, -1);
    if (cached >= 0) {
        return cached;
    }
    char value[16] = {};
    // B-2（TASK-R10-05）：三态检查；Truncated（≥16 字符）→ 视为未设置（0）。
    const zfg::EnvRead read
        = zfg::ReadEnvA("ZFG_PROBE_INSTALL_STAGGER_MS", value, sizeof(value));
    unsigned long ms = 0ul;
    if (read == zfg::EnvRead::Set) {
        ms = std::strtoul(value, nullptr, 10);
    }
    if (ms > 10000ul) {
        ms = 10000ul; // 上限：再大就只是拖慢启动，无额外证据价值
    }
    const LONG resolved = static_cast<LONG>(ms);
    ::InterlockedExchange(&g_staggerMsCache, resolved);
    return resolved;
}

/// 打开日志文件（幂等、每次运行只打开一次）。所有安装入口都要能写自己的
/// "zfg probe install" 头行——那是 installs 计数的来源，不能因为没抢到安装权就不记。
bool EnsureLogOpen(const char* logPath)
{
    ::AcquireSRWLockExclusive(&g_logOpenLock);
    bool ok = (g_logFile != INVALID_HANDLE_VALUE);
    if (!ok) {
        if (logPath != nullptr && logPath[0] != '\0') {
            (void)std::snprintf(g_logPath, sizeof(g_logPath), "%s", logPath);
        } else {
            char tempDir[MAX_PATH] = {};
            const DWORD tempLen = ::GetTempPathA(static_cast<DWORD>(sizeof(tempDir)), tempDir);
            if (tempLen == 0u || tempLen >= sizeof(tempDir)) {
                (void)std::snprintf(g_logPath, sizeof(g_logPath), "zfg_probe.log");
            } else {
                (void)std::snprintf(g_logPath, sizeof(g_logPath), "%szfg_probe.log", tempDir);
            }
        }
        g_logFile = ::CreateFileA(g_logPath, FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        ok = (g_logFile != INVALID_HANDLE_VALUE);
    }
    ::ReleaseSRWLockExclusive(&g_logOpenLock);
    return ok;
}

/// 记录窗口身份（Unity 的 Game View / Player 窗口名对定位问题很关键）
void LogWindowIdentity(const char* tag, HWND hwnd)
{
    if (hwnd == nullptr) {
        LogLine("  %s hwnd=null", tag);
        return;
    }
    char className[128] = {};
    char title[256] = {};
    (void)::GetClassNameA(hwnd, className, static_cast<int>(sizeof(className)));
    (void)::GetWindowTextA(hwnd, title, static_cast<int>(sizeof(title)));
    RECT rc {};
    (void)::GetClientRect(hwnd, &rc);
    LogLine("  %s hwnd=0x%p class='%s' title='%s' client=%ldx%ld", tag,
        reinterpret_cast<void*>(hwnd), className, title, rc.right - rc.left,
        rc.bottom - rc.top);
}

void EnsureSwapchainHooks(IDXGISwapChain1* swapchain);

// ---- 链注册表：建档 / 查档 / 计数 / 汇总 -------------------------------------

/// 创建调用栈的模块归属（可证伪的"谁建的"证据）：跳过本模块帧，取前 5 个
/// 外部帧的 `模块名+偏移`。不需要 PDB——只要模块名，就足以区分
/// amd_fidelityfx_framegeneration_dx12.dll / UnityPlayer.dll / d3d12.dll。
void DescribeCaller(char* out, size_t cap)
{
    if (out == nullptr || cap == 0u) {
        return;
    }
    out[0] = '\0';
    void* frames[24] = {};
    const USHORT captured = ::RtlCaptureStackBackTrace(1u, 24u, frames, nullptr);
    size_t used = 0u;
    int shown = 0;
    for (USHORT i = 0u; i < captured && shown < 5; ++i) {
        HMODULE mod = nullptr;
        if (::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                static_cast<LPCSTR>(frames[i]), &mod)
                == FALSE
            || mod == nullptr) {
            continue;
        }
        char path[MAX_PATH] = {};
        if (::GetModuleFileNameA(mod, path, static_cast<DWORD>(sizeof(path))) == 0u) {
            continue;
        }
        const char* base = std::strrchr(path, '\\');
        base = (base != nullptr) ? base + 1 : path;
        if (_stricmp(base, "zfg_hook.dll") == 0 || _stricmp(base, "zfg_probe.dll") == 0) {
            continue; // 本模块自身的帧
        }
        const uintptr_t offset
            = reinterpret_cast<uintptr_t>(frames[i]) - reinterpret_cast<uintptr_t>(mod);
        const int written = std::snprintf(out + used, cap - used, "%s%s+0x%llX",
            (used != 0u) ? " <- " : "", base, static_cast<unsigned long long>(offset));
        if (written <= 0 || static_cast<size_t>(written) >= cap - used) {
            break;
        }
        used += static_cast<size_t>(written);
        ++shown;
    }
}

ChainRecord* NewChainRecord(void* sc, bool wrapped)
{
    ::AcquireSRWLockExclusive(&g_chainLock);
    ChainRecord* rec = nullptr;
    if (g_chainCount < kMaxChains) {
        rec = &g_chains[g_chainCount];
        g_chainCount += 1u;
        *rec = ChainRecord {};
        rec->sc = sc;
        rec->index = static_cast<int>(g_chainCount);
        rec->wrapped = wrapped;
        rec->createTid = ::GetCurrentThreadId();
        rec->createMs = NowMs();
        DescribeCaller(rec->creator, sizeof(rec->creator));
    }
    ::ReleaseSRWLockExclusive(&g_chainLock);
    return rec;
}

ChainRecord* FindChain(const void* sc)
{
    if (sc == nullptr) {
        return nullptr;
    }
    ::AcquireSRWLockShared(&g_chainLock);
    ChainRecord* found = nullptr;
    for (size_t i = 0u; i < g_chainCount; ++i) {
        if (g_chains[i].sc == sc) {
            found = &g_chains[i];
            break;
        }
    }
    ::ReleaseSRWLockShared(&g_chainLock);
    return found;
}

/// 每次 Present/Present1 都记账（跨线程 → 原子）。`appLevel` = 代理链上、非嵌套：
/// 只有这种才是宿主自己发起的呈现。
void AccountPresent(void* sc, bool present1, UINT flags, HRESULT hr, bool appLevel)
{
    ChainRecord* rec = FindChain(sc);
    if (rec == nullptr) {
        return;
    }
    if (present1) {
        (void)::InterlockedIncrement64(&rec->nPresent1);
    } else {
        (void)::InterlockedIncrement64(&rec->nPresent);
    }
    if (hr == S_OK) {
        (void)::InterlockedIncrement64(&rec->nOk);
    } else {
        (void)::InterlockedIncrement64(&rec->nNotOk);
    }
    if ((flags & DXGI_PRESENT_ALLOW_TEARING) != 0u) {
        (void)::InterlockedIncrement64(&rec->nTearing);
        if (rec->tidTearing == 0u) {
            rec->tidTearing = ::GetCurrentThreadId();
        }
    } else if (appLevel) {
        (void)::InterlockedIncrement64(&rec->nApp);
        if (rec->tidApp == 0u) {
            rec->tidApp = ::GetCurrentThreadId();
        }
    }
    const unsigned long long now = NowMs();
    if (rec->firstMs == 0u) {
        rec->firstMs = now;
    }
    rec->lastMs = now;
}

/// 汇总表（ZFG_FgAnalyzeCaptures 调用；同时打 DXGI 自己的 GetLastPresentCount）
void DumpSummaryImpl(const char* reason)
{
    LogLine("==== zfg chain summary (%s): chains=%zu wrapFrames=%lld skipNested=%lld "
            "globalPresents=%lld ====",
        (reason != nullptr) ? reason : "", g_chainCount,
        static_cast<long long>(g_wrapFrames), static_cast<long long>(g_wrapSkipNested),
        static_cast<long long>(g_presentCount));
    ::AcquireSRWLockShared(&g_chainLock);
    for (size_t i = 0u; i < g_chainCount; ++i) {
        ChainRecord& rec = g_chains[i];
        UINT dxgiCount = 0u;
        HRESULT hrCount = E_NOINTERFACE;
        if (rec.sc != nullptr) {
            hrCount = static_cast<IDXGISwapChain*>(rec.sc)->GetLastPresentCount(&dxgiCount);
        }
        const unsigned long long spanMs
            = (rec.lastMs > rec.createMs) ? (rec.lastMs - rec.createMs) : 0u;
        const long long total = rec.nPresent + rec.nPresent1;
        const double rate = (spanMs > 0u)
            ? (1000.0 * static_cast<double>(total) / static_cast<double>(spanMs))
            : 0.0;
        LogLine("  chain[%d] sc=0x%p wrapped=%d hwnd=0x%p %ux%u fmt=%d buffers=%u usage=0x%X "
                "effect=%d flags=0x%X",
            rec.index, rec.sc, rec.wrapped ? 1 : 0, reinterpret_cast<void*>(rec.hwnd), rec.width,
            rec.height, rec.format, rec.buffers, rec.usage, rec.effect, rec.flags);
        LogLine("    creator: %s", rec.creator);
        LogLine("    presents: total=%lld (Present=%lld Present1=%lld) ok=%lld notOk=%lld "
                "tearing=%lld app=%lld rate=%.1f/s span=%llums",
            total, static_cast<long long>(rec.nPresent), static_cast<long long>(rec.nPresent1),
            static_cast<long long>(rec.nOk), static_cast<long long>(rec.nNotOk),
            static_cast<long long>(rec.nTearing), static_cast<long long>(rec.nApp), rate,
            spanMs);
        LogLine("    threads: appTid=%lu tearingTid=%lu createTid=%lu | DXGI GetLastPresentCount "
                "hr=0x%08X count=%u",
            rec.tidApp, rec.tidTearing, rec.createTid, static_cast<unsigned>(hrCount), dxgiCount);
    }
    ::ReleaseSRWLockShared(&g_chainLock);
}


/// 找 self 所属类的 hook 表项（原生类与 FFX 包装类是两个 vtable，必须分派）
SwapchainHooks* FindHooks(const void* self)
{
    if (self == nullptr) {
        return nullptr;
    }
    void** vtbl = *reinterpret_cast<void***>(const_cast<void*>(self));
    for (size_t i = 0u; i < g_scHookCount; ++i) {
        if (g_scHooks[i].vtbl == vtbl) {
            return &g_scHooks[i];
        }
    }
    return nullptr;
}

// ---- 设备门控（TASK-B-R9-05 G3）--------------------------------------------
//
// B-R9-04 验收 R2：不带 -force-d3d12 时 Unity 走 D3D11，在
// CreateSwapChainForHwnd 里被 wrap 后**当场崩**（该轮 patches=1，与竞态无关）——
// 在 D3D11 设备上调 FFX 代理链必崩。所以"能不能默认带着代理 dll 发布"取决于
// 这里：非 D3D12（或无法确认）→ 一条 NOTICE → **纯透传**，一个 FFX API 都不碰。
//
// 判定方式：对 CreateSwapChainForHwnd 的 pDevice 做 QI 试探。
//   D3D12：pDevice 是 ID3D12CommandQueue（Unity/D3D12 都这么传）或 ID3D12Device；
//   D3D11：pDevice 是 ID3D11Device，两者都拿不到。
// 只用 IUnknown::QueryInterface，不引 d3d12.h、不加链接依赖。
const GUID kIidD3D12Device = {0x189819f1u, 0x1db6u, 0x4b57u,
    {0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7}};
const GUID kIidD3D12CommandQueue = {0x0ec870a6u, 0x5d7eu, 0x4c22u,
    {0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed}};

bool DeviceIsD3D12(IUnknown* device)
{
    if (device == nullptr) {
        return false;
    }
    void* probe = nullptr;
    if (SUCCEEDED(device->QueryInterface(kIidD3D12Device, &probe)) && probe != nullptr) {
        static_cast<IUnknown*>(probe)->Release();
        return true;
    }
    probe = nullptr;
    if (SUCCEEDED(device->QueryInterface(kIidD3D12CommandQueue, &probe)) && probe != nullptr) {
        static_cast<IUnknown*>(probe)->Release();
        return true;
    }
    return false;
}

/// 本帧是否允许派发 PrepareV2（wantFrameGen）：
///   - fg 模式：始终允许（托管侧开关只做"抑制"，语义在 WrapOnFrame 的 override 里）；
///   - wrap 模式：仅当托管侧显式调过 ZFG_FgSetEnabled(1)（TASK-B-R9-06）。默认 -1
///     （跟随模式）仍是纯直通，所以 B-R9-04/05 的"wrap = 透明"回归面不变，
///     而设置页开关在 wrap 部署下也不会变成"能点不生效"的假开关。
bool ModeWantsFrameGen()
{
    if (g_mode == Mode::Fg) {
        return true;
    }
    return (g_mode == Mode::Wrap) && WrapFrameGenAuthorized();
}

/// 返回 true = 允许建 FFX 代理链；false = 非 D3D12，调用方必须纯透传。
/// NOTICE 只打一次（两类各自一次），不会刷屏。
bool GateAllowsFrameGen(IUnknown* device){
    if (DeviceIsD3D12(device)) {
        if (::InterlockedCompareExchange(&g_gateLoggedD3D12, 1, 0) == 0) {
            LogLine("  zfg gate: device has ID3D12Device/ID3D12CommandQueue -> FG proxy allowed");
        }
        return true;
    }
    if (::InterlockedCompareExchange(&g_gateLoggedOther, 1, 0) == 0) {
        LogLine("  zfg gate: NOTICE non-D3D12, passthrough (no FFX context, no proxy chain; "
                "logged once) — G3");
    }
    return false;
}

// ---- 记录 + 转发 -------------------------------------------------------------

HRESULT STDMETHODCALLTYPE HookCreateSwapChain(IDXGIFactory* self, IUnknown* device,
    DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** outSwapChain)
{
    (void)::InterlockedIncrement64(&g_createCalls);
    LogLine("[+%llums] IDXGIFactory::CreateSwapChain (LEGACY path) tid=%lu desc=%s", NowMs(),
        ::GetCurrentThreadId(), (desc != nullptr) ? "yes" : "null");
    if (desc != nullptr) {
        LogLine("  legacy buffers=%u %ux%u fmt=%d windowed=%d effect=%d flags=0x%X",
            desc->BufferCount, desc->BufferDesc.Width, desc->BufferDesc.Height,
            static_cast<int>(desc->BufferDesc.Format), desc->Windowed ? 1 : 0,
            static_cast<int>(desc->SwapEffect), desc->Flags);
        LogWindowIdentity("legacy", desc->OutputWindow);
    }
    const HRESULT hr = g_origCreateSwapChain(self, device, desc, outSwapChain);
    LogLine("  -> hr=0x%08X swapchain=0x%p", static_cast<unsigned>(hr),
        (outSwapChain != nullptr) ? static_cast<void*>(*outSwapChain) : nullptr);
    if (SUCCEEDED(hr) && outSwapChain != nullptr && *outSwapChain != nullptr) {
        ChainRecord* rec = NewChainRecord(static_cast<void*>(*outSwapChain), false);
        if (rec != nullptr && desc != nullptr) {
            rec->hwnd = desc->OutputWindow;
            rec->width = desc->BufferDesc.Width;
            rec->height = desc->BufferDesc.Height;
            rec->buffers = desc->BufferCount;
            rec->format = static_cast<int>(desc->BufferDesc.Format);
            rec->effect = static_cast<int>(desc->SwapEffect);
            rec->flags = desc->Flags;
            LogLine("  chain[%d] registered (legacy) creator: %s", rec->index, rec->creator);
        }
        IDXGISwapChain1* sc1 = nullptr;
        if (SUCCEEDED((*outSwapChain)->QueryInterface(IID_PPV_ARGS(&sc1))) && sc1 != nullptr) {
            EnsureSwapchainHooks(sc1);
            sc1->Release();
        }
        if (desc != nullptr) {
            WrapNoteSwapchainSize(desc->BufferDesc.Width, desc->BufferDesc.Height);
        }
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookCreateSwapChainForCoreWindow(IDXGIFactory2* self, IUnknown* device,
    IUnknown* coreWindow, const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain)
{
    (void)::InterlockedIncrement64(&g_createCalls);
    LogLine("[+%llums] IDXGIFactory2::CreateSwapChainForCoreWindow tid=%lu", NowMs(),
        ::GetCurrentThreadId());
    const HRESULT hr = g_origCreateSwapChainForCoreWindow(self, device, coreWindow, desc,
        restrictToOutput, outSwapChain);
    LogLine("  -> hr=0x%08X swapchain=0x%p", static_cast<unsigned>(hr),
        (outSwapChain != nullptr) ? static_cast<void*>(*outSwapChain) : nullptr);
    if (SUCCEEDED(hr) && outSwapChain != nullptr && *outSwapChain != nullptr) {
        ChainRecord* rec = NewChainRecord(static_cast<void*>(*outSwapChain), false);
        if (rec != nullptr) {
            if (desc != nullptr) {
                rec->width = desc->Width;
                rec->height = desc->Height;
                rec->buffers = desc->BufferCount;
                rec->usage = desc->BufferUsage;
                rec->format = static_cast<int>(desc->Format);
                rec->effect = static_cast<int>(desc->SwapEffect);
                rec->flags = desc->Flags;
            }
            LogLine("  chain[%d] registered (coreWindow) creator: %s", rec->index, rec->creator);
        }
        EnsureSwapchainHooks(*outSwapChain);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookCreateSwapChainForComposition(IDXGIFactory2* self, IUnknown* device,
    const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain)
{
    (void)::InterlockedIncrement64(&g_createCalls);
    LogLine("[+%llums] IDXGIFactory2::CreateSwapChainForComposition tid=%lu", NowMs(),
        ::GetCurrentThreadId());
    if (desc != nullptr) {
        LogLine("  desc %ux%u fmt=%d buffers=%u usage=0x%X effect=%d flags=0x%X", desc->Width,
            desc->Height, static_cast<int>(desc->Format), desc->BufferCount, desc->BufferUsage,
            static_cast<int>(desc->SwapEffect), desc->Flags);
    }
    const HRESULT hr = g_origCreateSwapChainForComposition(self, device, desc, restrictToOutput,
        outSwapChain);
    LogLine("  -> hr=0x%08X swapchain=0x%p", static_cast<unsigned>(hr),
        (outSwapChain != nullptr) ? static_cast<void*>(*outSwapChain) : nullptr);
    if (SUCCEEDED(hr) && outSwapChain != nullptr && *outSwapChain != nullptr) {
        ChainRecord* rec = NewChainRecord(static_cast<void*>(*outSwapChain), false);
        if (rec != nullptr) {
            if (desc != nullptr) {
                rec->width = desc->Width;
                rec->height = desc->Height;
                rec->buffers = desc->BufferCount;
                rec->usage = desc->BufferUsage;
                rec->format = static_cast<int>(desc->Format);
                rec->effect = static_cast<int>(desc->SwapEffect);
                rec->flags = desc->Flags;
            }
            LogLine("  chain[%d] registered (composition) creator: %s", rec->index, rec->creator);
        }
        EnsureSwapchainHooks(*outSwapChain);
    }
    return hr;
}

HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* self, UINT syncInterval, UINT flags)
{
    const LONG64 n = ::InterlockedIncrement64(&g_presentCount);
    const bool isWrapped = (self != nullptr && static_cast<void*>(self) == WrapSwapchain());
    const bool nested = t_inWrappedPresent;
    // 应用级呈现 = 宿主自己发起的那一次。嵌套在 FFX Present1 内部的二次 Present
    // 是 SDK 的内部转发，不是应用帧（阶段 1 实测：每个应用 Present1 恰好带一次）。
    const bool appLevel = isWrapped && !nested;
    if (isWrapped && nested) {
        (void)::InterlockedIncrement64(&g_wrapSkipNested);
    }
    if (appLevel && TraceEnabled()) {
        LogLine("[+%llums] TRACE app Present sc=0x%p sync=%u flags=0x%X #%lld", NowMs(),
            static_cast<void*>(self), syncInterval, flags, static_cast<long long>(n));
    }
    // 前 3 次 + 每 60 次一条：足够证明 Present 走这里且节奏正常，又不写爆日志
    if (n <= 3 || (n % 60) == 0) {
        LogLine("[+%llums] IDXGISwapChain::Present #%lld tid=%lu sync=%u flags=0x%X sc=0x%p "
                "wrapped=%d nested=%d",
            NowMs(), static_cast<long long>(n), ::GetCurrentThreadId(), syncInterval, flags,
            static_cast<void*>(self), isWrapped ? 1 : 0, nested ? 1 : 0);
    }
    // 代理交换链：Present 前必须 Configure 本帧（frameID 严格 +1），fg 模式再派发
    // PrepareV2。任何一步失败都只记录——Present 继续走（宿主画面优先）。
    // 只有应用级呈现才推进 FG 契约（1 Configure + 1 Prepare 对 1 应用 Present）。
    // 嵌套调用是 FFX 的内部转发，它若也跑一遍 Configure，就会把"刚配置好的
    // enabled=true"用 enabled=false 覆盖掉（B-R9-03 阶段 C 恒 generated≈0 的根因）。
    if (g_mode != Mode::LogOnly && appLevel) {
        (void)::InterlockedIncrement64(&g_wrapFrames);
        if (!WrapOnFrame(self, ModeWantsFrameGen())) {
            LogLine("  wrap: frame sync FAILED (%s)", WrapLastError());
        }
    }
    SwapchainHooks* hooks = FindHooks(self);
    if (hooks == nullptr || hooks->present == nullptr) {
        return DXGI_ERROR_INVALID_CALL;
    }
    // 非代理链 = FFX 自建的底层链（真正呈现到屏幕的那条）：Present 前采样读回，
    // 作为"副链透传与基线一致"的像素证据（采样 stride 内部决定，失败即停用）。
    if (g_mode != Mode::LogOnly && !isWrapped && WrapSwapchain() != nullptr) {
        WrapCaptureNativePresent(self);
    }
    const bool outerWrapped = isWrapped;
    // B-1（TASK-R10-05）：嵌套标志保存/恢复（不再硬清 false）。判定与恢复逻辑
    // 在 zfg_present_reentry.h（产品与单测共用）；2 层以上嵌套时内层退出恢复
    // "进入前"的标志值，外层余下的嵌套 Present 不会被误判成应用级。
    const PresentReentry reentry = PresentReenter(outerWrapped, t_inWrappedPresent);
    if (reentry.setNested) {
        t_inWrappedPresent = true;
    }
    const HRESULT hr = hooks->present(self, syncInterval, flags);
    PresentReexit(reentry, t_inWrappedPresent);
    if (g_mode != Mode::LogOnly && !isWrapped && WrapSwapchain() != nullptr) {
        WrapNativeCaptureAfterPresent();
    }
    AccountPresent(self, false, flags, hr, appLevel);
    return hr;
}

HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* self, UINT syncInterval, UINT flags,
    const DXGI_PRESENT_PARAMETERS* params)
{
    const LONG64 n = ::InterlockedIncrement64(&g_presentCount);
    const bool isWrapped = (self != nullptr && static_cast<void*>(self) == WrapSwapchain());
    const bool nested = t_inWrappedPresent;
    const bool appLevel = isWrapped && !nested;
    if (isWrapped && nested) {
        (void)::InterlockedIncrement64(&g_wrapSkipNested);
    }
    if (appLevel && TraceEnabled()) {
        LogLine("[+%llums] TRACE app Present1 sc=0x%p sync=%u flags=0x%X #%lld", NowMs(),
            static_cast<void*>(self), syncInterval, flags, static_cast<long long>(n));
    }
    if (n <= 3 || (n % 60) == 0) {
        LogLine("[+%llums] IDXGISwapChain1::Present1 #%lld tid=%lu sync=%u flags=0x%X sc=0x%p "
                "dirtyRects=%lu wrapped=%d nested=%d",
            NowMs(), static_cast<long long>(n), ::GetCurrentThreadId(), syncInterval, flags,
            static_cast<void*>(self),
            static_cast<unsigned long>((params != nullptr) ? params->DirtyRectsCount : 0u),
            isWrapped ? 1 : 0, nested ? 1 : 0);
    }
    // 只有应用级呈现才推进 FG 契约（1 Configure + 1 Prepare 对 1 应用 Present）。
    // 嵌套调用是 FFX 的内部转发，它若也跑一遍 Configure，就会把"刚配置好的
    // enabled=true"用 enabled=false 覆盖掉（B-R9-03 阶段 C 恒 generated≈0 的根因）。
    if (g_mode != Mode::LogOnly && appLevel) {
        (void)::InterlockedIncrement64(&g_wrapFrames);
        if (!WrapOnFrame(self, ModeWantsFrameGen())) {
            LogLine("  wrap: frame sync FAILED (%s)", WrapLastError());
        }
    }
    SwapchainHooks* hooks = FindHooks(self);
    if (hooks == nullptr || hooks->present1 == nullptr) {
        return DXGI_ERROR_INVALID_CALL;
    }
    const bool outerWrapped = isWrapped;
    // B-1（TASK-R10-05）：同 HookPresent——保存/恢复嵌套标志（不硬清 false），
    // 嵌套 >1 层时内层退出不破坏外层状态。逻辑在 zfg_present_reentry.h。
    const PresentReentry reentry = PresentReenter(outerWrapped, t_inWrappedPresent);
    if (reentry.setNested) {
        t_inWrappedPresent = true;
    }
    const HRESULT hr = hooks->present1(self, syncInterval, flags, params);
    PresentReexit(reentry, t_inWrappedPresent);
    AccountPresent(self, true, flags, hr, appLevel);
    return hr;
}

HRESULT STDMETHODCALLTYPE HookResizeBuffers(IDXGISwapChain* self, UINT bufferCount, UINT width,
    UINT height, DXGI_FORMAT newFormat, UINT swapChainFlags)
{
    LogLine("[+%llums] IDXGISwapChain::ResizeBuffers buffers=%u %ux%u fmt=%d flags=0x%X sc=0x%p",
        NowMs(), bufferCount, width, height, static_cast<int>(newFormat), swapChainFlags,
        static_cast<void*>(self));
    SwapchainHooks* hooks = FindHooks(self);
    if (hooks == nullptr || hooks->resizeBuffers == nullptr) {
        return DXGI_ERROR_INVALID_CALL;
    }
    const HRESULT hr = hooks->resizeBuffers(self, bufferCount, width, height, newFormat,
        swapChainFlags);
    LogLine("  -> hr=0x%08X", static_cast<unsigned>(hr));
    if (SUCCEEDED(hr)) {
        // TASK-R10-03 A-4：重建成功后推进显示状态（尺寸 + 采集作废 + 输入复用额度）。
        // 历史缺陷：这里只转发不通知 wrap → 显示尺寸永远停在建链时的值，
        // 托管侧提交新尺寸后被判"尺寸不符"→ 插帧永久静默失效。
        WrapOnSwapchainResized(width, height);
        // 链档案也更新：否则汇总表里的尺寸永远是建链时的值（取证失真）
        ChainRecord* rec = FindChain(static_cast<void*>(self));
        if (rec != nullptr && width != 0u && height != 0u) {
            rec->width = width;
            rec->height = height;
        }
    }
    return hr;
}

// ---- 安装 -------------------------------------------------------------------

/// 已落地的 patch 记录（半状态清理用，TASK-B-R9-05 要求 2）：失败时按相反顺序
/// 把每一个真正写过的槽位还原成 patch 前的原值。
struct PatchRecord {
    void** vtbl = nullptr;
    size_t slot = 0u;
    void* original = nullptr;
};

constexpr size_t kMaxPatchJournal = 8u;

/// 地址所属模块的完整路径（"这个函数到底是谁的"取证；失败返回 false）
bool ModulePathOfAddress(const void* address, char* out, size_t cap)
{
    if (out == nullptr || cap == 0u) {
        return false;
    }
    out[0] = '\0';
    HMODULE module = nullptr;
    if (address == nullptr
        || ::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                static_cast<LPCSTR>(address), &module)
            == FALSE
        || module == nullptr) {
        return false;
    }
    char path[MAX_PATH] = {};
    if (::GetModuleFileNameA(module, path, static_cast<DWORD>(sizeof(path))) == 0u) {
        return false;
    }
    (void)std::snprintf(out, cap, "%s", path);
    return true;
}

/// 地址是否落在**系统目录**下的 dxgi.dll 内。
///
/// 不能只按模块基名判断：本仓的 dxgi 代理自己的输出名也叫 dxgi.dll（同名代理），
/// `GetModuleHandleA("dxgi.dll")` 可能返回代理自身。故按"完整路径目录 ==
/// GetSystemDirectoryA()"判定 —— 这是"槽里躺的是真 dxgi 实现"的可证伪判据，
/// 比原来的"不是自家代码"强一档（R10-02 A-2 的槽 17 也能过原来那道门）。
bool IsSystemDxgiAddress(const void* address)
{
    char path[MAX_PATH] = {};
    if (!ModulePathOfAddress(address, path, sizeof(path))) {
        return false;
    }
    char sysDir[MAX_PATH] = {};
    const UINT sysLen = ::GetSystemDirectoryA(sysDir, static_cast<UINT>(sizeof(sysDir)));
    if (sysLen == 0u || sysLen >= sizeof(sysDir)) {
        return false;
    }
    const size_t dirLen = std::strlen(sysDir);
    if (_strnicmp(path, sysDir, dirLen) != 0 || path[dirLen] != '\\') {
        return false;
    }
    const char* base = std::strrchr(path, '\\');
    return (base != nullptr) && (_stricmp(base + 1, "dxgi.dll") == 0);
}

/// 工厂槽位结构校验（TASK-R10-03 A-2）：**patch 之前**跑，任何一条不成立就整体
/// 放弃安装（宁可保持原生，也不要"装错槽还打印成功"）。逐槽判据：
///   ① 槽号 < kIdxgiFactory2VtblCount（越界 = 常量写飞）
///   ② 槽内指针非空、不在本模块、且落在**系统目录**的 dxgi.dll 内
///   ③ lastOfInterface 的槽必须正好是结构体最后一项（Composition = 24/25）
///   ④ 被 patch 的槽两两不同（同一函数被当成两个方法 = 槽位错位）
/// 通过后打一行 `slot guard: ... -> PASS` 与一行总结；失败逐条打原因。
/// 注意：这是**结构**校验，不能单独证明"槽号语义正确"——语义由
/// Native/tests/test_zfg_factory_slots.cpp 用 SDK 头的 Vtbl 布局反算证明。
bool VerifyFactorySlots(void** vtbl)
{
    if (vtbl == nullptr) {
        LogLine("  slot guard: FAIL (factory vtable is null)");
        return false;
    }
    bool ok = true;
    size_t checked = 0u;
    const void* seen[kVtblSlotCount] = {};
    size_t seenCount = 0u;
    for (size_t i = 0u; i < kVtblSlotCount; ++i) {
        const VtblSlotSpec& spec = kVtblSlots[i];
        if (spec.family != VtblFamily::Factory2 || !spec.patched) {
            continue;
        }
        ++checked;
        const void* fn = vtbl[spec.slot];
        char path[MAX_PATH] = {};
        (void)ModulePathOfAddress(fn, path, sizeof(path));
        const char* why = "ok";
        if (spec.slot >= kIdxgiFactory2VtblCount) {
            why = "slot index >= IDXGIFactory2Vtbl count";
        } else if (fn == nullptr) {
            why = "slot is null";
        } else if (IsProbeCode(const_cast<void*>(fn))) {
            why = "slot points into the probe module";
        } else if (!IsSystemDxgiAddress(fn)) {
            why = "slot does not point into the system dxgi.dll";
        } else if (spec.lastOfInterface && (spec.slot + 1u != kIdxgiFactory2VtblCount)) {
            why = "last-of-interface invariant broken (slot+1 != vtbl count)";
        } else {
            for (size_t k = 0u; k < seenCount; ++k) {
                if (seen[k] == fn) {
                    why = "same function as another patched slot";
                    break;
                }
            }
            if (std::strcmp(why, "ok") == 0 && seenCount < kVtblSlotCount) {
                seen[seenCount] = fn;
                ++seenCount;
            }
        }
        if (std::strcmp(why, "ok") == 0) {
            LogLine("  slot guard: %s slot=%zu fn=0x%p module='%s' -> PASS", spec.method,
                spec.slot, fn, path);
        } else {
            ok = false;
            LogLine("  slot guard: %s slot=%zu fn=0x%p module='%s' -> FAIL (%s)", spec.method,
                spec.slot, fn, path, why);
        }
    }
    if (ok) {
        LogLine("  slot guard: VERIFY PASS (%zu factory slot(s); composition=%zu == "
                "IDXGIFactory2Vtbl[%zu]-1)",
            checked, kSlotCreateSwapChainForComposition, kIdxgiFactory2VtblCount);
    } else {
        LogLine("  slot guard: VERIFY FAIL (%zu slot(s) checked) -> install aborted, native "
                "remains",
            checked);
    }
    return ok;
}

/// 交换链类槽位的结构校验（同样在 patch 之前跑）：Present/ResizeBuffers/Present1 三个
/// 槽号必须落在 IDXGISwapChain1 的声明顺序上且互不相同。语义同样由单测按
/// IDXGISwapChain1Vtbl 反算证明。
bool VerifySwapchainSlots(void** vtbl, size_t classIndex)
{
    const bool ordered = (kSlotPresent < kSlotResizeBuffers) && (kSlotResizeBuffers < kSlotPresent1);
    const bool distinct = (kSlotPresent != kSlotResizeBuffers)
        && (kSlotPresent != kSlotPresent1) && (kSlotResizeBuffers != kSlotPresent1);
    const bool nonEmpty = (vtbl != nullptr) && (vtbl[kSlotPresent] != nullptr)
        && (vtbl[kSlotResizeBuffers] != nullptr) && (vtbl[kSlotPresent1] != nullptr);
    const bool ok = ordered && distinct && nonEmpty;
    LogLine("  slot guard: swapchain class[%zu] vtable=0x%p present=%zu resizeBuffers=%zu "
            "present1=%zu -> %s",
        classIndex, static_cast<void*>(vtbl), kSlotPresent, kSlotResizeBuffers, kSlotPresent1,
        ok ? "PASS" : "FAIL");
    return ok;
}

/// 打补丁（带自递归兜底 + 回滚记账）。
///
/// G2（要求 3）：写之前先看**拿到的原始函数**落在哪个模块。若它已经在
/// zfg_probe.dll / zfg_hook.dll 里，说明这个槽位里躺着的是我们自己（或同源的另一份
/// 副本）的 hook——再 patch 一次就会让 hook 调自己：
///   HookCreateSwapChainForHwnd → 自家函数 → 又进 HookCreateSwapChainForHwnd → …
/// 直接拒绝并告警，绝不让这条路径成立。G1 的三态机保证"正常情况不会有两个安装者"，
/// G2 保证"即使 G1 将来被改坏/被绕过，也不会递归"——两者是两道独立的门。
bool PatchSlotTracked(void** vtbl, size_t slot, void* replacement, void** outOriginal,
    PatchRecord* journal, size_t journalCap, size_t* journalCount)
{
    if (vtbl == nullptr || replacement == nullptr || outOriginal == nullptr) {
        return false;
    }
    void** slotPtr = vtbl + slot;
    void* original = *slotPtr;
    if (original == nullptr) {
        LogLine("  patch slot %zu REFUSED: slot is empty", slot);
        return false;
    }
    if (original == replacement) {
        // 已经是我们的函数：既不能重复记原值（会把自己的 hook 记成"原始函数"），
        // 也不需要再写一次。
        LogLine("  patch slot %zu REFUSED: slot already points at this module's hook "
                "(0x%p) — re-patch would self-recurse", slot, original);
        return false;
    }
    if (IsProbeCode(original)) {
        LogLine("  patch slot %zu REFUSED: original 0x%p is inside the probe module "
                "('zfg_hook.dll'/'zfg_probe.dll') — refusing to treat our own hook as the "
                "original (would self-recurse)",
            slot, original);
        return false;
    }
    DWORD oldProtect = 0;
    if (::VirtualProtect(slotPtr, sizeof(void*), PAGE_READWRITE, &oldProtect) == FALSE) {
        LogLine("  patch slot %zu FAILED (VirtualProtect err=%lu)", slot, ::GetLastError());
        return false;
    }
    *outOriginal = original;
    *slotPtr = replacement;
    DWORD ignored = 0;
    (void)::VirtualProtect(slotPtr, sizeof(void*), oldProtect, &ignored);
    (void)::FlushInstructionCache(::GetCurrentProcess(), slotPtr, sizeof(void*));
    if (journal != nullptr && journalCount != nullptr && *journalCount < journalCap) {
        journal[*journalCount].vtbl = vtbl;
        journal[*journalCount].slot = slot;
        journal[*journalCount].original = original;
        *journalCount += 1u;
    }
    // "patch 成功"与"槽位校验通过"是两件事（TASK-R10-03 A-2）：本行只说明内存里
    // 那个槽被写成了我们的 hook；槽号语义对不对由 slot guard 那一行负责。
    LogLine("  patch ok: slot=%zu 0x%p -> 0x%p", slot, original, replacement);
    return true;
}

/// 半状态清理：把 journal 里记下的槽位按倒序还原（后写的先还原）。
void RollbackPatches(PatchRecord* journal, size_t count)
{
    if (journal == nullptr) {
        return;
    }
    for (size_t i = count; i > 0u; --i) {
        const PatchRecord& rec = journal[i - 1u];
        void** slotPtr = rec.vtbl + rec.slot;
        if (*slotPtr == rec.original) {
            continue; // 已经被别人还原过
        }
        DWORD oldProtect = 0;
        if (::VirtualProtect(slotPtr, sizeof(void*), PAGE_READWRITE, &oldProtect) == FALSE) {
            LogLine("  rollback slot %zu FAILED (VirtualProtect err=%lu)", rec.slot,
                ::GetLastError());
            continue;
        }
        *slotPtr = rec.original;
        DWORD ignored = 0;
        (void)::VirtualProtect(slotPtr, sizeof(void*), oldProtect, &ignored);
        (void)::FlushInstructionCache(::GetCurrentProcess(), slotPtr, sizeof(void*));
        LogLine("  rollback slot %zu -> 0x%p", rec.slot, rec.original);
    }
}

void RestoreSlot(void** vtbl, size_t slot, void* original)
{
    if (vtbl == nullptr || original == nullptr) {
        return;
    }
    void** slotPtr = vtbl + slot;
    DWORD oldProtect = 0;
    if (::VirtualProtect(slotPtr, sizeof(void*), PAGE_READWRITE, &oldProtect) == FALSE) {
        return;
    }
    *slotPtr = original;
    DWORD ignored = 0;
    (void)::VirtualProtect(slotPtr, sizeof(void*), oldProtect, &ignored);
    (void)::FlushInstructionCache(::GetCurrentProcess(), slotPtr, sizeof(void*));
}

/// 工厂 vtable 基址（安装/卸载共用）
void** g_factoryVtbl = nullptr;

/// 交换链类的安装也要防并发：两处创建（宿主链 + FFX 内部链）虽在同一线程，但
/// 语义上同属"安装"，用同一把锁 + 同一套"拒绝自递归 + 回滚"逻辑。
SRWLOCK g_scHookLock = SRWLOCK_INIT;

void EnsureSwapchainHooks(IDXGISwapChain1* swapchain)
{
    if (swapchain == nullptr) {
        return;
    }
    void** vtbl = *reinterpret_cast<void***>(swapchain);
    if (vtbl == nullptr) {
        return;
    }
    ::AcquireSRWLockExclusive(&g_scHookLock);
    bool done = false;
    if (g_scHookCount >= kMaxSwapchainClasses) {
        done = true;
    } else {
        for (size_t i = 0u; i < g_scHookCount; ++i) {
            if (g_scHooks[i].vtbl == vtbl) {
                done = true; // 该类已挂过
                break;
            }
        }
    }
    if (done) {
        ::ReleaseSRWLockExclusive(&g_scHookLock);
        return;
    }
    SwapchainHooks& entry = g_scHooks[g_scHookCount];
    if (!VerifySwapchainSlots(vtbl, g_scHookCount)) {
        // 结构校验不通过 = 槽号常量或对象本身有问题 → 一个槽都不动，保持原生。
        LogLine("  swapchain class install skipped (slot guard failed)");
        ::ReleaseSRWLockExclusive(&g_scHookLock);
        return;
    }
    PatchRecord journal[kMaxPatchJournal] = {};
    size_t journalCount = 0u;
    const bool presentOk = PatchSlotTracked(vtbl, kSlotPresent,
        reinterpret_cast<void*>(&HookPresent), reinterpret_cast<void**>(&entry.present), journal,
        kMaxPatchJournal, &journalCount);
    const bool present1Ok = PatchSlotTracked(vtbl, kSlotPresent1,
        reinterpret_cast<void*>(&HookPresent1), reinterpret_cast<void**>(&entry.present1), journal,
        kMaxPatchJournal, &journalCount);
    const bool resizeOk = PatchSlotTracked(vtbl, kSlotResizeBuffers,
        reinterpret_cast<void*>(&HookResizeBuffers), reinterpret_cast<void**>(&entry.resizeBuffers),
        journal, kMaxPatchJournal, &journalCount);
    LogLine("  swapchain class[%zu] vtable=0x%p present=%s present1=%s resizeBuffers=%s",
        g_scHookCount, static_cast<void*>(vtbl), presentOk ? "ok" : "FAILED",
        present1Ok ? "ok" : "FAILED", resizeOk ? "ok" : "FAILED");
    if (presentOk && present1Ok && resizeOk) {
        entry.vtbl = vtbl;
        ::MemoryBarrier(); // 表项写全了再让 FindHooks 看到它
        g_scHookCount += 1u;
    } else {
        // 半状态清理：三个槽位要么全挂，要么全不挂（否则 Present 与 Present1 之间
        // 会只剩一半走我们的记录函数，计数与 appLevel 判定都会失真）。
        LogLine("  swapchain class install incomplete -> rolling back %zu slot(s)", journalCount);
        RollbackPatches(journal, journalCount);
        entry = SwapchainHooks {};
    }
    ::ReleaseSRWLockExclusive(&g_scHookLock);
}


} // namespace

HRESULT STDMETHODCALLTYPE HookCreateSwapChainForHwnd(IDXGIFactory2* self, IUnknown* device,
    HWND hwnd, const DXGI_SWAP_CHAIN_DESC1* desc,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc, IDXGIOutput* restrictToOutput,
    IDXGISwapChain1** outSwapChain)
{
    (void)::InterlockedIncrement64(&g_createCalls);
    LogLine("[+%llums] IDXGIFactory2::CreateSwapChainForHwnd tid=%lu", NowMs(),
        ::GetCurrentThreadId());

    // B-7（TASK-R10-05）：空 out 参 = 宿主契约违例。历史缺陷：不校验——wrap 成功后
    // 已建好整条 FFX 代理链却无处可写，仍返回 S_OK（原生语义应 E_INVALIDARG）；
    // 后果是代理链泄漏 + g_fg 非空后第二条链再也 wrap 不了。入口直接拒绝：
    // 既不建代理链，也不转发原生创建（原生同样返回 E_INVALIDARG）。
    if (outSwapChain == nullptr) {
        LogLine("  -> outSwapChain=nullptr -> E_INVALIDARG (B-7; no proxy chain created)");
        return E_INVALIDARG;
    }
    if (desc != nullptr) {
        LogLine("  desc %ux%u fmt=%d buffers=%u usage=0x%X effect=%d flags=0x%X "
                "scaling=%d alpha=%d",
            desc->Width, desc->Height, static_cast<int>(desc->Format), desc->BufferCount,
            desc->BufferUsage, static_cast<int>(desc->SwapEffect), desc->Flags,
            static_cast<int>(desc->Scaling), static_cast<int>(desc->AlphaMode));
    }
    LogLine("  fullscreenDesc=%s factory=0x%p", (fullscreenDesc != nullptr) ? "non-null" : "null",
        static_cast<void*>(self));
    LogWindowIdentity("hwnd", hwnd);

    // ---- wrap 模式：换 FFX 代理交换链（失败立即回退原生）---------------------
    // G3 门控（TASK-B-R9-05）：先确认设备是 D3D12 才允许碰 FFX。非 D3D12（D3D11 /
    // 无法确认）→ 一条 NOTICE（只打一次）→ 直接落到下面的原生创建，纯透传，
    // 不建 FFX 上下文、不调任何 FFX API。
    if (g_mode != Mode::LogOnly && desc != nullptr && !t_inWrap && GateAllowsFrameGen(device)) {
        void* wrapped = nullptr;
        t_inWrap = true;
        // A-3（TASK-R10-05）：宿主 desc 全量透传（不再只传 Width/Height/Format/
        // BufferCount）。历史缺陷：usage/effect/flags/scaling/alpha/sampleDesc 在
        // 边界被丢弃，FFX 用自造 desc 建链却返回 S_OK——本仓日志实测 Unity 请求
        // usage=0x30 effect=3 flags=0x842，建成 usage=0x20 effect=4 flags=0x840。
        // wrap 内部：先按宿主 desc 建链（只补 FG 必需位），FFX 拒绝再回退自造 desc
        //（不回归 B-R9-04 的真机插帧链路），并打"请求 vs 实际"对账日志。
        const bool wrapOk = WrapCreateSwapChainForHwnd(self, device, hwnd, desc->Width,
            desc->Height, static_cast<unsigned>(desc->Format), desc->BufferCount, &wrapped, desc,
            fullscreenDesc);
        t_inWrap = false;
        if (wrapOk) {
            LogLine("  -> WRAPPED swapchain=0x%p (native creation skipped)", wrapped);
            ChainRecord* rec = NewChainRecord(wrapped, true);
            if (rec != nullptr) {
                rec->hwnd = hwnd;
                rec->width = desc->Width;
                rec->height = desc->Height;
                rec->buffers = desc->BufferCount;
                rec->usage = desc->BufferUsage;
                rec->format = static_cast<int>(desc->Format);
                rec->effect = static_cast<int>(desc->SwapEffect);
                rec->flags = desc->Flags;
                LogLine("  chain[%d] registered (wrapped) creator: %s", rec->index, rec->creator);
            }
            auto* sc1 = static_cast<IDXGISwapChain1*>(wrapped);
            EnsureSwapchainHooks(sc1);
            if (outSwapChain != nullptr) {
                *outSwapChain = sc1;
                sc1->AddRef();
            }
            return S_OK;
        }
        LogLine("  wrap FAILED (%s); falling back to native swapchain", WrapLastError());
    }

    const HRESULT hr = g_origCreateSwapChainForHwnd(self, device, hwnd, desc, fullscreenDesc,
        restrictToOutput, outSwapChain);
    LogLine("  -> hr=0x%08X swapchain=0x%p", static_cast<unsigned>(hr),
        (outSwapChain != nullptr) ? static_cast<void*>(*outSwapChain) : nullptr);
    if (SUCCEEDED(hr) && outSwapChain != nullptr && *outSwapChain != nullptr) {
        ChainRecord* rec = NewChainRecord(static_cast<void*>(*outSwapChain), false);
        if (rec != nullptr) {
            rec->hwnd = hwnd;
            if (desc != nullptr) {
                rec->width = desc->Width;
                rec->height = desc->Height;
                rec->buffers = desc->BufferCount;
                rec->usage = desc->BufferUsage;
                rec->format = static_cast<int>(desc->Format);
                rec->effect = static_cast<int>(desc->SwapEffect);
                rec->flags = desc->Flags;
            }
            LogLine("  chain[%d] registered (native, inWrap=%d) creator: %s", rec->index,
                t_inWrap ? 1 : 0, rec->creator);
        }
        EnsureSwapchainHooks(*outSwapChain);
        if (desc != nullptr) {
            // 显示尺寸的唯一事实来源（TASK-R10-03 A-4）：原生链也要登记，
            // 否则 log 模式下 ResizeBuffers 的比较基准是 0x0。
            WrapNoteSwapchainSize(desc->Width, desc->Height);
        }
    }
    return hr;
}

/// 交换链汇总（TASK-B-R9-04 阶段 1）：每条链的创建者/desc/呈现计数 + WrapOnFrame
/// 的调用与跳过原因。供 WrapAnalyzeCaptures 在退出前 dump。
void DumpSwapchainSummary(const char* reason)
{
    DumpSummaryImpl(reason);
}

/// 地址是否落在某个 zfg_probe.dll 模块内——用于识别"本进程里已经有另一份副本
/// 装过 hook"（Unity 会从 Data/Plugins 再加载一份，和我们注入的那份不是同一个
/// 模块实例，各自有独立全局变量）。重复 patch 会把对方的 hook 当成原始函数。
bool IsProbeCode(void* address)
{
    if (address == nullptr) {
        return false;
    }
    HMODULE module = nullptr;
    if (::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
            | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            static_cast<LPCSTR>(address), &module)
        == FALSE) {
        return false;
    }
    char path[MAX_PATH] = {};
    if (::GetModuleFileNameA(module, path, static_cast<DWORD>(sizeof(path))) == 0u) {
        return false;
    }
    const char* name = std::strrchr(path, '\\');
    name = (name != nullptr) ? name + 1 : path;
    // 本仓有两份同源模块：spike 的 zfg_probe.dll（注入式）与本任务的 zfg_hook.dll
    // （dxgi 代理惰性加载）。两者代码相同、全局变量各自独立，所以都要认。
    return _stricmp(name, "zfg_probe.dll") == 0 || _stricmp(name, "zfg_hook.dll") == 0;
}

bool IsInstalled()
{
    return ReadInstallState() == kInstallDone;
}

const char* LogPath()
{
    return g_logPath;
}

/// 地址所属模块的基名（G2 自证的证据用）
void DescribeModuleName(const void* address, char* out, size_t cap)
{
    if (out == nullptr || cap == 0u) {
        return;
    }
    out[0] = '\0';
    HMODULE module = nullptr;
    if (address == nullptr
        || ::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                static_cast<LPCSTR>(address), &module)
            == FALSE
        || module == nullptr) {
        return;
    }
    char path[MAX_PATH] = {};
    if (::GetModuleFileNameA(module, path, static_cast<DWORD>(sizeof(path))) == 0u) {
        return;
    }
    const char* base = std::strrchr(path, '\\');
    base = (base != nullptr) ? base + 1 : path;
    (void)std::snprintf(out, cap, "%s", base);
}

/// G2 的函数级自证（只有 ZFG_PROBE_GUARD_SELFTEST=1 时执行；默认零影响）。
///
/// 为什么不在 Unity 测试里证明：G2 的判据是"这个函数地址落在哪个**已加载模块**
/// 内"，Unity 测试进程里根本没有 zfg_hook.dll / zfg_probe.dll，构造不出这个前提。
/// 因此在安装期用一块**栈上的假 vtable** 直接调生产代码用的同一个
/// `PatchSlotTracked`：
///   ① 负例：槽位里放本模块内的地址（= 守卫被改坏 / 另一份同源副本 patch 过之后
///            的真实形态）→ 必须 REFUSED，且槽位一个字节都不被改 → 不可能递归；
///   ② 正例：槽位里放真实 dxgi.dll 的导出地址 → 必须允许（证明不是"一律拒绝"）。
void RunGuardSelfTest()
{
    char flag[8] = {};
    // B-2（TASK-R10-05）：三态检查；Truncated → 视为未设置（默认零影响）。
    const zfg::EnvRead read = zfg::ReadEnvA("ZFG_PROBE_GUARD_SELFTEST", flag, sizeof(flag));
    if (read != zfg::EnvRead::Set || std::strcmp(flag, "0") == 0) {
        return;
    }

    // ① 负例：本模块内的函数地址
    void* fakeVtbl[kSlotPresent1 + 1u] = {};
    void* const selfHook = reinterpret_cast<void*>(&HookPresent);
    fakeVtbl[kSlotPresent] = selfHook;
    void* original = nullptr;
    PatchRecord journal[kMaxPatchJournal] = {};
    size_t journalCount = 0u;
    const bool appliedSelf = PatchSlotTracked(fakeVtbl, kSlotPresent,
        reinterpret_cast<void*>(&HookPresent1), &original, journal, kMaxPatchJournal,
        &journalCount);
    const bool unchanged = (fakeVtbl[kSlotPresent] == selfHook);
    char module[96] = {};
    DescribeModuleName(selfHook, module, sizeof(module));
    LogLine("GUARD SELFTEST[negative] slot=%zu target=0x%p module='%s' applied=%d "
            "slotUnchanged=%d verdict=%s",
        kSlotPresent, selfHook, module, appliedSelf ? 1 : 0, unchanged ? 1 : 0,
        (!appliedSelf && unchanged) ? "REFUSED-no-recursion" : "FAILED");

    // ② 正例：真实 dxgi.dll 的导出（不在本模块）
    HMODULE dxgi = ::GetModuleHandleA("dxgi.dll");
    void* realFn = (dxgi != nullptr)
        ? reinterpret_cast<void*>(::GetProcAddress(dxgi, "CreateDXGIFactory1"))
        : nullptr;
    bool allowed = false;
    bool restored = false;
    char realModule[96] = {};
    if (realFn != nullptr) {
        void* fakeVtbl2[kSlotPresent1 + 1u] = {};
        fakeVtbl2[kSlotPresent] = realFn;
        void* original2 = nullptr;
        PatchRecord journal2[kMaxPatchJournal] = {};
        size_t count2 = 0u;
        allowed = PatchSlotTracked(fakeVtbl2, kSlotPresent,
            reinterpret_cast<void*>(&HookPresent1), &original2, journal2, kMaxPatchJournal,
            &count2);
        if (allowed) {
            // 只动了栈上的假 vtable，写回即可（不涉及任何真实对象）
            fakeVtbl2[kSlotPresent] = original2;
            restored = (fakeVtbl2[kSlotPresent] == realFn);
        }
        DescribeModuleName(realFn, realModule, sizeof(realModule));
    }
    LogLine("GUARD SELFTEST[positive] target=0x%p module='%s' applied=%d restored=%d "
            "verdict=%s",
        realFn, realModule, allowed ? 1 : 0, restored ? 1 : 0,
        (allowed && restored) ? "ALLOWED" : "FAILED");

    // ③ 半状态清理（要求 2）：两个假槽位，第一个可打（dxgi 地址）、第二个必须被
    //    拒绝（本模块地址）——模拟"patch 到一半失败"，验证 RollbackPatches 把已经
    //    写下去的那个槽位恢复原样（不留"一半被 hook"的内存）。
    bool halfRolledBack = false;
    if (realFn != nullptr) {
        void* fakeVtbl3[2] = {};
        fakeVtbl3[0] = realFn;
        fakeVtbl3[1] = selfHook;
        void* original3a = nullptr;
        void* original3b = nullptr;
        PatchRecord journal3[2] = {};
        size_t count3 = 0u;
        const bool applied3a = PatchSlotTracked(fakeVtbl3, 0u,
            reinterpret_cast<void*>(&HookPresent1), &original3a, journal3, 2u, &count3);
        const bool applied3b = PatchSlotTracked(fakeVtbl3, 1u,
            reinterpret_cast<void*>(&HookPresent1), &original3b, journal3, 2u, &count3);
        RollbackPatches(journal3, count3);
        halfRolledBack = applied3a && !applied3b && (fakeVtbl3[0] == realFn)
            && (fakeVtbl3[1] == selfHook);
        LogLine("GUARD SELFTEST[rollback] first=%d second=%d journal=%zu slot0Restored=%d "
                "slot1Untouched=%d verdict=%s",
            applied3a ? 1 : 0, applied3b ? 1 : 0, count3, (fakeVtbl3[0] == realFn) ? 1 : 0,
            (fakeVtbl3[1] == selfHook) ? 1 : 0, halfRolledBack ? "ROLLBACK-OK" : "FAILED");
    }

    const bool pass = (!appliedSelf && unchanged && allowed && restored && halfRolledBack);
    LogLine("GUARD SELFTEST: %s (negative refused=%d, positive allowed=%d, rollback ok=%d)",
        pass ? "PASS" : "FAIL", (!appliedSelf && unchanged) ? 1 : 0, allowed ? 1 : 0,
        halfRolledBack ? 1 : 0);
}

/// 状态 1 的竞争者：自旋等待（带超时）。**任何情况下都不碰 vtable**。
ProbeInstall WaitForPeerInstall()
{
    const unsigned long long start = NowMs();
    for (;;) {
        const unsigned long long waited = NowMs() - start;
        const LONG state = ReadInstallState();
        if (state == kInstallDone) {
            LogLine("  install guard: peer install finished (waited=%llums); reusing it "
                    "(no re-patch)",
                waited);
            return ProbeInstall::Installed;
        }
        if (state == kInstallIdle) {
            LogLine("  install guard: peer install failed and rolled back (waited=%llums); "
                    "staying native",
                waited);
            return ProbeInstall::Failed;
        }
        if (waited >= kInstallWaitTimeoutMs) {
            LogLine("  install guard: TIMEOUT waiting for in-flight install (waited=%llums >= "
                    "%llums); giving up, staying native (no second patch)",
                waited, kInstallWaitTimeoutMs);
            return ProbeInstall::Failed;
        }
        ::Sleep((waited < 50ull) ? 0u : 1u);
    }
}

ProbeInstall Install(const char* logPath)
{
    if (ReadInstallState() == kInstallDone) {
        return ProbeInstall::Installed;
    }

    // 每个入口都留一条头行：installs 计数（= 真实安装入口数）是 G1 的主要证据，
    // 竞争者即使最终不 patch 也必须记录"我来过"。
    if (!EnsureLogOpen(logPath)) {
        return ProbeInstall::Failed;
    }
    LogLine("==== zfg probe install: pid=%lu tid=%lu mode=%s path='%s' ====",
        ::GetCurrentProcessId(), ::GetCurrentThreadId(), ModeName(g_mode), g_logPath);

    if (InstallAbandoned()) {
        LogLine("  install skipped: a previous attempt failed and rolled back; staying native "
                "(no retry)");
        return ProbeInstall::Failed;
    }

    // ---- 测试用竞争放大器（默认 0 = 零影响）----
    const LONG stagger = InstallStaggerMs();
    if (stagger > 0) {
        LogLine("  install guard: TEST AMPLIFIER ZFG_PROBE_INSTALL_STAGGER_MS=%ld → sleep %ldms "
                "before acquiring",
            stagger, stagger);
        ::Sleep(static_cast<DWORD>(stagger));
    }

    // ---- 抢占安装权：0 → 1（唯一能改 vtable 的路径）----
    const LONG prev = ::InterlockedCompareExchange(&g_installState, kInstallBusy, kInstallIdle);
    if (prev == kInstallDone) {
        LogLine("  install guard: already installed by another entry; reuse (no re-patch)");
        return ProbeInstall::Installed;
    }
    if (prev == kInstallBusy) {
        LogLine("  install guard: another entry holds the install right (state=1); spin-wait, "
                "timeout=%llums",
            kInstallWaitTimeoutMs);
        return WaitForPeerInstall();
    }
    LogLine("  install guard: acquired install right (tid=%lu, state 0->1)",
        ::GetCurrentThreadId());
    if (stagger > 0) {
        // 仅测试：把"安装中"这段敞开同一时长，保证竞争者一定走到自旋分支
        ::Sleep(static_cast<DWORD>(stagger));
    }
    RunGuardSelfTest();

    // 失败收尾的唯一出口：先由调用方回滚，再置终态（本进程不再重试，保持原生）。
    auto giveUp = [](const char* reason) -> ProbeInstall {
        ::InterlockedExchange(&g_installAbandoned, 1);
        ::InterlockedExchange(&g_installState, kInstallIdle);
        LogLine("install FAILED: %s", reason);
        LogLine("==== zfg probe install aborted (rolled back; native remains) ====");
        return ProbeInstall::Failed;
    };

    // ---- 取工厂 vtable ----
    IDXGIFactory2* factory = nullptr;
    const HRESULT hr = ::CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr) || factory == nullptr) {
        LogLine("  CreateDXGIFactory1 hr=0x%08X", static_cast<unsigned>(hr));
        return giveUp("cannot create DXGI factory");
    }
    g_factoryVtbl = *reinterpret_cast<void***>(factory);
    LogLine("  factory=0x%p vtable=0x%p", static_cast<void*>(factory),
        static_cast<void*>(g_factoryVtbl));

    // ---- 已有另一份副本装过 hook → 直接复用，不回读它写下的指针 ----------
    if (IsProbeCode(g_factoryVtbl[kSlotCreateSwapChainForHwnd])) {
        LogLine("  another probe copy already hooked this process; skip re-patch");
        factory->Release();
        ::InterlockedExchange(&g_installState, kInstallDone);
        LogLine("==== zfg probe installed (reused existing hook) ====");
        return ProbeInstall::Installed;
    }

    // ---- 槽位结构校验（TASK-R10-03 A-2）：先证明"这些槽里躺的是真 dxgi 实现、
    //      且 Composition 是接口最后一项"，再动手 patch。校验不过就整体放弃。----
    if (!VerifyFactorySlots(g_factoryVtbl)) {
        factory->Release();
        return giveUp("factory vtable slot guard failed (see slot guard lines above)");
    }

    // ---- patch 工厂槽位（自递归兜底 + 回滚记账）----
    PatchRecord journal[kMaxPatchJournal] = {};
    size_t journalCount = 0u;
    bool ok = true;
    ok = PatchSlotTracked(g_factoryVtbl, kSlotCreateSwapChain,
             reinterpret_cast<void*>(&HookCreateSwapChain),
             reinterpret_cast<void**>(&g_origCreateSwapChain), journal, kMaxPatchJournal,
             &journalCount)
        && ok;
    ok = PatchSlotTracked(g_factoryVtbl, kSlotCreateSwapChainForHwnd,
             reinterpret_cast<void*>(&HookCreateSwapChainForHwnd),
             reinterpret_cast<void**>(&g_origCreateSwapChainForHwnd), journal, kMaxPatchJournal,
             &journalCount)
        && ok;
    ok = PatchSlotTracked(g_factoryVtbl, kSlotCreateSwapChainForCoreWindow,
             reinterpret_cast<void*>(&HookCreateSwapChainForCoreWindow),
             reinterpret_cast<void**>(&g_origCreateSwapChainForCoreWindow), journal,
             kMaxPatchJournal, &journalCount)
        && ok;
    ok = PatchSlotTracked(g_factoryVtbl, kSlotCreateSwapChainForComposition,
             reinterpret_cast<void*>(&HookCreateSwapChainForComposition),
             reinterpret_cast<void**>(&g_origCreateSwapChainForComposition), journal,
             kMaxPatchJournal, &journalCount)
        && ok;
    factory->Release();
    if (!ok) {
        LogLine("  factory vtable patch incomplete -> rolling back %zu applied slot(s)",
            journalCount);
        RollbackPatches(journal, journalCount);
        return giveUp("factory vtable patch incomplete");
    }

    ::InterlockedExchange(&g_installState, kInstallDone);
    LogLine("  factory vtable patched (slots: createSwapChain=%zu forHwnd=%zu coreWindow=%zu "
            "composition=%zu): createSwapChain=0x%p forHwnd=0x%p coreWindow=0x%p "
            "composition=0x%p",
        kSlotCreateSwapChain, kSlotCreateSwapChainForHwnd, kSlotCreateSwapChainForCoreWindow,
        kSlotCreateSwapChainForComposition, reinterpret_cast<void*>(g_origCreateSwapChain),
        reinterpret_cast<void*>(g_origCreateSwapChainForHwnd),
        reinterpret_cast<void*>(g_origCreateSwapChainForCoreWindow),
        reinterpret_cast<void*>(g_origCreateSwapChainForComposition));
    LogLine("==== zfg probe installed ====");
    return ProbeInstall::Installed;
}

ProbeInstall InstallFromEnv()
{
    // B-2（TASK-R10-05）：三态读取。历史缺陷：char mode[32] 不看返回值——值 ≥ 32
    // 字符时缓冲无 NUL，strcmp 越界读。Truncated → 视为"未知模式"（LogOnly：
    // 保留取证行，不 wrap/不插帧——与"未知值"的原语义一致，只是不再 UB）。
    char mode[32] = {};
    const zfg::EnvRead modeRead = zfg::ReadEnvA("ZFG_HOOK", mode, sizeof(mode));
    const char* source = "ZFG_HOOK";
    zfg::EnvRead probeRead = modeRead;
    if (modeRead == zfg::EnvRead::Unset) {
        // 兼容 B-R9-01 的注入式 spike（ZFG_PROBE）；两者都未设 → 完全不干预
        probeRead = zfg::ReadEnvA("ZFG_PROBE", mode, sizeof(mode));
        source = "ZFG_PROBE";
    }
    if (probeRead == zfg::EnvRead::Unset
        || (probeRead == zfg::EnvRead::Set && std::strcmp(mode, "0") == 0)) {
        return ProbeInstall::Disabled;
    }
    if (probeRead == zfg::EnvRead::Set && std::strcmp(mode, "wrap") == 0) {
        g_mode = Mode::Wrap;
    } else if (probeRead == zfg::EnvRead::Set && std::strcmp(mode, "fg") == 0) {
        g_mode = Mode::Fg;
    } else {
        g_mode = Mode::LogOnly;
    }
    // 日志路径：动态读取（B-2 用例"日志路径正确"）。历史缺陷：char[MAX_PATH] 不看
    // 返回值——值 ≥ MAX_PATH 时缓冲无 NUL → CreateFileA 打开乱码路径 → 证据静默
    // 丢失。现在：动态量长 → 明确拒绝超长值（CreateFileA 的 ANSI MAX_PATH 硬限制
    // 使"保真打开"不可行）→ 回落默认 %TEMP%\zfg_probe.log 并留痕（证据不静默丢）。
    std::unique_ptr<char[]> hookLogPath;
    const size_t hookLogLen = zfg::ReadEnvDynamicA("ZFG_HOOK_LOG", &hookLogPath, 8192u);
    const char* logSource = "ZFG_HOOK_LOG";
    std::unique_ptr<char[]> probeLogPath;
    size_t logLen = hookLogLen;
    if (logLen == 0u) {
        logLen = zfg::ReadEnvDynamicA("ZFG_PROBE_LOG", &probeLogPath, 8192u);
        logSource = "ZFG_PROBE_LOG";
    }
    const char* logPathArg = nullptr;
    if (logLen > 0u) {
        const std::unique_ptr<char[]>& resolved = (hookLogLen != 0u) ? hookLogPath : probeLogPath;
        if (resolved == nullptr) {
            // 超过 8 KB 安全上限（或 OOM）：明确拒绝，不猜内容
            LogLine("  install: %s rejected (len=%zu over limit / alloc failed; B-2) — "
                    "default log path",
                logSource, logLen);
        } else if (logLen >= MAX_PATH) {
            LogLine("  install: %s is %zu chars (>= MAX_PATH) — CreateFileA(ANSI) cannot open "
                    "it reliably; rejected (B-2) — default log path",
                logSource, logLen);
        } else {
            logPathArg = resolved.get();
        }
    }
    (void)source;
    return Install(logPathArg);
}

void Uninstall()
{
    if (ReadInstallState() != kInstallDone) {
        return;
    }
    RestoreSlot(g_factoryVtbl, kSlotCreateSwapChain,
        reinterpret_cast<void*>(g_origCreateSwapChain));
    RestoreSlot(g_factoryVtbl, kSlotCreateSwapChainForHwnd,
        reinterpret_cast<void*>(g_origCreateSwapChainForHwnd));
    RestoreSlot(g_factoryVtbl, kSlotCreateSwapChainForCoreWindow,
        reinterpret_cast<void*>(g_origCreateSwapChainForCoreWindow));
    RestoreSlot(g_factoryVtbl, kSlotCreateSwapChainForComposition,
        reinterpret_cast<void*>(g_origCreateSwapChainForComposition));
    ::AcquireSRWLockExclusive(&g_scHookLock);
    for (size_t i = 0u; i < g_scHookCount; ++i) {
        RestoreSlot(g_scHooks[i].vtbl, kSlotPresent,
            reinterpret_cast<void*>(g_scHooks[i].present));
        RestoreSlot(g_scHooks[i].vtbl, kSlotPresent1,
            reinterpret_cast<void*>(g_scHooks[i].present1));
        RestoreSlot(g_scHooks[i].vtbl, kSlotResizeBuffers,
            reinterpret_cast<void*>(g_scHooks[i].resizeBuffers));
    }
    g_scHookCount = 0u;
    ::ReleaseSRWLockExclusive(&g_scHookLock);
    ::InterlockedExchange(&g_installState, kInstallIdle);
    LogLine("==== zfg probe uninstalled ====");
    if (g_logFile != INVALID_HANDLE_VALUE) {
        // 卸载即关闭日志句柄：让外部工具（自测/验收）能独占读取
        ::CloseHandle(g_logFile);
        g_logFile = INVALID_HANDLE_VALUE;
    }
}

/// 供同模块其它编译单元（zfg_probe_wrap.cpp）使用的日志出口：单一文件句柄、
/// 单一锁，保证代理/wrap 的日志与 hook 的行序一致。
void ProbeLog(const char* fmt, ...)
{
    char body[512];
    va_list args;
    va_start(args, fmt);
    const int written = std::vsnprintf(body, sizeof(body), fmt, args);
    va_end(args);
    if (written < 0) {
        return;
    }
    LogLine("%s", body);
}

} // namespace zfg
