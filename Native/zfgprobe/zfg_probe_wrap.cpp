// =============================================================================
// zfg_probe_wrap.cpp —— FFX 代理交换链 + 每帧 FG 契约（TASK-B-R9-01 spike，
// TASK-B-R9-03 扩为可插帧）
//
// 复用 Native/zupscaler/fg_ffx（FrameGenerator）——与窗口程序 --fg 模式同一份
// 已跑通的封装，避免另写一套 FFX 调用。
//
// 与窗口程序的关键差异（Unity 路径的三个硬约束）：
//   1. **jitterCancellation = false（F2）**：URP 的 MV 用 _NonJitteredViewProjMatrix
//      算，不含 TAA 抖动。硬编码 true 会让 FFX 反解一份并不存在的抖动（每帧 0.5px
//      级重投影误差）。窗口程序 --fg 自己置 true，行为不受影响。
//   2. **depthInverted = true**：Unity 本机是 reversed-Z（SystemInfo.usesReversedZBuffer），
//      深度值 1=近。FFX 需要 DEPTH_INVERTED 标志才按同一口径解释。
//   3. **PrepareV2 的命令表自己建**：hook 在 Present1 里，拿不到 Unity 的命令表。
//      用游戏队列（CreateSwapChainForHwnd 的 pDevice 就是 ID3D12CommandQueue*）
//      在 Present 前 ExecuteCommandLists 一份只含 Prepare 的表——同一队列 FIFO，
//      顺序天然满足"Prepare 先于插帧采样"。
// =============================================================================
#include "zfgprobe/zfg_probe_wrap.h"

#include "zfgprobe/zfg_probe_hook.h"
#include "zfgprobe/zfg_env.h"
#include "zfgprobe/zfg_fg_input_policy.h"
#include "zfgprobe/zfg_probe_resize.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <d3d12.h>
#include <dxgi1_6.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <memory>
#include <new>

#include "zupscaler/fg_ffx.h"

// 官方 FFX 表面格式映射（inline 纯函数）；第三方头按仓库约定放进 /WX 豁免段。
#pragma warning(push)
#pragma warning(disable: 4100 4127 4189 4201 4324 4456 4457 4458 4514 4623 4625 4626 5045)
#include "dx12/ffx_api_dx12.h"
#pragma warning(pop)

namespace zfg {
namespace {

zr::FrameGenerator* g_fg = nullptr;
void* g_wrapped = nullptr;
void* g_waitable = nullptr;
uint64_t g_frameId = 0;
char g_lastError[256] = {};

ID3D12Device* g_device = nullptr;      // AddRef 持有
ID3D12CommandQueue* g_queue = nullptr; // AddRef 持有（Unity 的游戏队列）

/// 显示尺寸 + 读回状态（TASK-R10-03 A-4）：**唯一**的尺寸事实来源。
/// 历史缺陷：尺寸只在建链时写一次，ResizeBuffers 后既不更新也不失效采集。
/// 状态推进逻辑在 zfg_probe_resize.h（纯逻辑，单测直接驱动）。
DisplayState g_display;

// ---- 托管侧提交的输入（每帧覆盖）-----------------------------------------------
// A-7（TASK-R10-05）：本模块对提交的 depth/MV 资源持有**显式引用**（提交时
// AddRef），历史缺陷是只 memcpy 裸指针——托管侧销毁重建纹理后，复用/尚未消费的
// 指针就是悬垂（PrepareV2 引用已释放资源 → 设备移除）。引用的释放不随覆盖立刻
// 发生：先进"退休队列"，等游戏队列 fence 追平（最后引用它的 PrepareV2 已在 GPU
// 完成）再 Release——"不再引用已释放指针"在结构上成立，而非概率变小。
SRWLOCK g_inputLock = SRWLOCK_INIT;
ZfgFgFrameInputs g_inputs = {};
uint64_t g_inputStamp = 0;      // 每次提交 +1
uint64_t g_consumedStamp = 0;   // 上一次被 WrapOnFrame 消费的 stamp
bool g_hasInputs = false;

// ---- 退休队列（A-7）：等待 GPU 追平后释放的旧输入引用 ---------------------------
constexpr size_t kMaxRetiredInputs = 8;
struct RetiredInput {
    void* depth = nullptr;   // 已 AddRef 的 ID3D12Resource*
    void* mv = nullptr;      // 已 AddRef 的 ID3D12Resource*
    uint64_t fenceValue = 0; // 退休时的队列 fence 快照（追平即可释放）
};
RetiredInput g_retired[kMaxRetiredInputs];
size_t g_retiredCount = 0;

// ---- PrepareV2 的私有命令表（3 环）-------------------------------------------
constexpr int kPrepRing = 3;
ID3D12CommandAllocator* g_alloc[kPrepRing] = {};
ID3D12GraphicsCommandList* g_list[kPrepRing] = {};
uint64_t g_allocDone[kPrepRing] = {};
int g_allocIndex = 0;
ID3D12Fence* g_fence = nullptr;
uint64_t g_fenceValue = 0;
bool g_prepFailed = false;

// ---- 统计 / 开关 ---------------------------------------------------------------
std::atomic<uint64_t> g_real{0};
std::atomic<uint64_t> g_generated{0};
std::atomic<uint64_t> g_prepareOk{0};
std::atomic<uint64_t> g_prepareFail{0};
std::atomic<uint32_t> g_lastRc{0};
std::atomic<int> g_enabledOverride{-1}; // -1 = 跟随模式；0/1 = 托管侧指定
std::atomic<int> g_enabledNow{0};
std::atomic<uint64_t> g_onFrameCalls{0};   // WrapOnFrame 进入次数（= 应用级 Present 数）
std::atomic<uint64_t> g_onFrameInputs{0};  // 其中拿到"本帧新输入"的次数
std::atomic<uint64_t> g_onFrameReused{0};  // 复用同一渲染帧输入的次数（重复呈现）
std::atomic<uint64_t> g_onFrameStale{0};   // 无可用输入的次数（不插帧）

// 同一份输入最多被几次连续呈现复用：常量与判定见 zfgprobe/zfg_fg_input_policy.h
// （A-7 收敛：产品与单测共用；复用安全由"本模块显式持有输入引用"保证）
int g_reuseLeft = 0; // 复用余额（消费账目推进；受 g_inputLock 保护）

/// 本线程上一次呈现是否因"输入尺寸 != 显示尺寸"停用了插帧（只在切换时打日志）
thread_local bool t_sizeMismatchLogged = false;

/// GPU 追平（定义在下面；RetireCaptureBank 需要它先声明）
bool FlushGameQueue(uint32_t timeoutMs);

// ---- A-7 引用管理（三个函数都要求调用方已持有 g_inputLock 独占）-----------------

/// 释放一份退休引用（调用方保证 GPU 侧已不再引用）
void ReleaseRetiredInput(RetiredInput& r)
{
    if (r.depth != nullptr) {
        static_cast<IUnknown*>(r.depth)->Release();
    }
    if (r.mv != nullptr) {
        static_cast<IUnknown*>(r.mv)->Release();
    }
    r = RetiredInput {};
}

/// 清理所有"fence 已追平"的退休引用（swap-remove）。completed 读到略旧值只会
/// 推迟释放（保守方向正确，见 zfg_fg_input_policy.h 的 RetiredInputReleasable）。
void FlushRetiredInputsLocked()
{
    const bool fenceValid = (g_fence != nullptr);
    const uint64_t completed = fenceValid ? g_fence->GetCompletedValue() : 0u;
    for (size_t i = 0; i < g_retiredCount;) {
        if (RetiredInputReleasable(g_retired[i].fenceValue, fenceValid, completed)) {
            ReleaseRetiredInput(g_retired[i]);
            g_retired[i] = g_retired[g_retiredCount - 1u];
            g_retiredCount -= 1u;
            continue;
        }
        ++i;
    }
}

/// 把当前生效输入的引用移入退休队列（新提交覆盖它之前调用；锁内）。
/// fence 快照：此刻之前派发的所有 PrepareV2 的 signal 值 ≤ g_fenceValue（它由
/// DispatchPrepare 在派发后单调递增；锁外写/锁内读为对齐 8 字节读，撕裂不成立），
/// 追平该值即"使用这份旧输入的 GPU 工作已全部完成"。
void RetireCurrentInputsLocked()
{
    if (!g_hasInputs || (g_inputs.depthResource == nullptr && g_inputs.mvResource == nullptr)) {
        return;
    }
    if (g_retiredCount >= kMaxRetiredInputs) {
        // 队列满：fence 正常一帧内追平，8 个槽位远超合理节奏；真到这里说明 GPU
        // 长期不追平——告警后强制释放最老一组（防泄漏无限放大），不再引用它即可。
        ProbeLog("  zfg wrap: retired-input ring full (%zu) — force-releasing oldest "
                 "(fence may not have completed)",
            g_retiredCount);
        ReleaseRetiredInput(g_retired[0]);
        g_retired[0] = g_retired[g_retiredCount - 1u];
        g_retiredCount -= 1u;
    }
    RetiredInput& slot = g_retired[g_retiredCount];
    slot.depth = g_inputs.depthResource;
    slot.mv = g_inputs.mvResource;
    slot.fenceValue = g_fenceValue;
    g_retiredCount += 1u;
    g_inputs.depthResource = nullptr;
    g_inputs.mvResource = nullptr;
}

// ---- 插帧/真帧像素读回（TASK-B-R9-03 阶段 C 的"非黑非花"证据）------------------
// FFX 的 present 回调每个呈现帧都会把最终画面合成到 output（COPY_DEST）。我们在
// 那里挂一个旁路：每 kCaptureStride 个呈现帧把 output 拷进 READBACK 堆的资源，
// 关停前统一 GPU 栅栏 + Map，算均值/方差/黑像素占比/哈希。
// 为什么不在 Present 之后直接读：插帧是 SDK 的 pacing 线程异步合成的，只有它的
// 回调里 output 才是"这一帧的最终画面"。
constexpr int kCaptureSlots = 6;
constexpr int kCaptureStride = 8;

struct CaptureSlot {
    ID3D12Resource* resource = nullptr;
    uint64_t serial = 0;
    uint32_t frameId = 0;
    int generated = 0;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    uint64_t bytes = 0;
};

CaptureSlot g_capture[kCaptureSlots];
/// "退休"槽位（TASK-R10-03 A-4）：resize 时把当前槽位挪到这里，**不立即 Release**。
/// 理由：present 回调在 FFX 的 pacing 线程上跑，可能正在往这些读回资源里记拷贝命令；
/// 立刻释放就是 UAF。退休槽在下一次 EnsureCapture（至少又过了一帧）或关停时释放。
CaptureSlot g_captureRetired[kCaptureSlots];
bool g_captureRetiredValid = false;
int g_captureNext = 0;
long g_presentSeen = 0;
SRWLOCK g_captureLock = SRWLOCK_INIT;
std::atomic<uint64_t> g_captureTotal{0};      // 实际抓进槽位的呈现帧数
std::atomic<uint64_t> g_captureGenerated{0};  // 其中插帧帧数
std::atomic<uint64_t> g_captureReal{0};       // 其中真帧数
std::atomic<uint64_t> g_captureAllocFailures{0}; // 读回银行整组构建失败次数（B-6）
std::atomic<uint64_t> g_resizeCount{0};       // 观察到的交换链尺寸变更次数
std::atomic<uint64_t> g_sizeMismatchFrames{0}; // 因"输入尺寸 != 显示尺寸"而停用插帧的帧数

/// 读回资源创建接缝（B-6 / TASK-R10-07）：默认空 = 走设备；注入点让"第 N 个
/// CreateCommittedResource 返回 E_OUTOFMEMORY"这条路径在测试里真的被执行。
ZfgCaptureSeam g_captureSeam;

void SetError(const char* what, long value)
{
    (void)std::snprintf(g_lastError, sizeof(g_lastError), "%s (0x%08lX)", what,
        static_cast<unsigned long>(value));
}

void CountPresent(bool isGeneratedFrame, void*)
{
    if (isGeneratedFrame) {
        g_generated.fetch_add(1u, std::memory_order_relaxed);
    } else {
        g_real.fetch_add(1u, std::memory_order_relaxed);
    }
}

/// 释放单个读回资源（B-6）：接缝注入时走注入的 release，生产走 Release()
void ReleaseCaptureResource(void* resource)
{
    if (resource == nullptr) {
        return;
    }
    const ZfgCaptureSeam seam = g_captureSeam;
    if (seam.release != nullptr) {
        seam.release(seam.user, resource);
        return;
    }
    static_cast<ID3D12Resource*>(resource)->Release();
}

/// 读回几何（footprints）。几何依赖设备，故默认实现与生产路径共用这一处。
bool ComputeCaptureFootprints(ID3D12Device* device, unsigned width, unsigned height,
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT* placed, uint64_t* total)
{
    if (device == nullptr || width == 0u || height == 0u) {
        return false;
    }
    D3D12_RESOURCE_DESC desc {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = width;
    desc.Height = height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    *placed = D3D12_PLACED_SUBRESOURCE_FOOTPRINT {};
    *total = 0;
    device->GetCopyableFootprints(&desc, 0, 1, 0, placed, nullptr, nullptr, total);
    return *total != 0u;
}

/// 默认接缝实现（= 生产路径）：整组读回资源按同一几何建到 READBACK 堆。
/// 中途失败时 *failedSlot = i，已建的 0..i-1 由调用方释放（B-6 的清理点）。
bool BuildCaptureBankDefault(void* user, int slotCount, unsigned width, unsigned height,
    void** outResources, uint64_t* outBytes, int* failedSlot)
{
    auto* device = static_cast<ID3D12Device*>(user);
    *failedSlot = -1;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    uint64_t total = 0;
    if (!ComputeCaptureFootprints(device, width, height, &placed, &total)) {
        return false; // 几何层失败：与槽位无关，不计入分配失败
    }
    if (slotCount <= 0 || outResources == nullptr) {
        return false;
    }
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC bufDesc {};
    bufDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bufDesc.Width = total;
    bufDesc.Height = 1;
    bufDesc.DepthOrArraySize = 1;
    bufDesc.MipLevels = 1;
    bufDesc.Format = DXGI_FORMAT_UNKNOWN;
    bufDesc.SampleDesc.Count = 1;
    bufDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    for (int i = 0; i < slotCount; ++i) {
        ID3D12Resource* res = nullptr;
        const HRESULT hr = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE,
            &bufDesc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&res));
        if (FAILED(hr) || res == nullptr) {
            *failedSlot = i;
            return false;
        }
        outResources[i] = res;
    }
    *outBytes = total;
    return true;
}

/// 释放一组读回槽位（调用方保证 GPU 侧已不再引用它们）
void ReleaseCaptureBank(CaptureSlot* bank)
{
    if (bank == nullptr) {
        return;
    }
    for (int i = 0; i < kCaptureSlots; ++i) {
        if (bank[i].resource != nullptr) {
            ReleaseCaptureResource(bank[i].resource);
        }
        bank[i] = CaptureSlot {};
    }
}

/// resize 后作废当前采集银行（TASK-R10-03 A-4）。
/// 顺序很关键：先把 GPU 追平（游戏队列）→ 再在锁内把槽位挪进退休槽 → 置
/// captureValid=false。退休槽留到下一次 EnsureCapture 释放（那时至少又过了一帧，
/// FFX pacing 线程上的拷贝命令已落地），避免"释放正在被写入的读回资源"。
void RetireCaptureBank(const char* why)
{
    (void)FlushGameQueue(1000u);
    bool had = false;
    ::AcquireSRWLockExclusive(&g_captureLock);
    // 判"有没有槽位要退休"只能看槽位本身：调用方（ApplySwapchainResize）已经把
    // captureValid 清成 false，用标志位判断会让整组读回资源永远留在原位、下一次
    // 重建时被直接覆盖 → 每次 resize 漏 kCaptureSlots 个资源（B-6 同族泄漏）。
    for (int i = 0; i < kCaptureSlots; ++i) {
        had = had || (g_capture[i].resource != nullptr);
    }
    g_display.captureValid = false;
    g_captureNext = 0;
    g_presentSeen = 0;
    if (had) {
        if (g_captureRetiredValid) {
            ReleaseCaptureBank(g_captureRetired);
            g_captureRetiredValid = false;
        }
        for (int i = 0; i < kCaptureSlots; ++i) {
            g_captureRetired[i] = g_capture[i];
            g_capture[i] = CaptureSlot {};
        }
        g_captureRetiredValid = true;
    }
    ::ReleaseSRWLockExclusive(&g_captureLock);
    if (had) {
        ProbeLog("  zfg capture: bank retired (%s) — slots will be rebuilt at %ux%u", why,
            g_display.width, g_display.height);
    }
}

bool EnsureCapture()
{
    if (g_display.captureValid) {
        return true;
    }
    if (g_display.width == 0u || g_display.height == 0u) {
        return false;
    }
    // 接缝快照：注入只在测试期发生，生产恒为默认（设备）实现
    const ZfgCaptureSeam seam = g_captureSeam;
    const bool injected = seam.build != nullptr;
    if (!injected && g_device == nullptr) {
        return false;
    }
    // 上一代退休槽位在这里释放（resize 后至少又过了一帧）
    ::AcquireSRWLockExclusive(&g_captureLock);
    if (g_captureRetiredValid) {
        ReleaseCaptureBank(g_captureRetired);
        g_captureRetiredValid = false;
    }
    ::ReleaseSRWLockExclusive(&g_captureLock);

    // 生产路径先把几何算出来（槽位 placed 要留到拷贝时用；几何失败与旧行为一致，
    // 直接返回 false、不计入分配失败）
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    if (!injected) {
        uint64_t geometryBytes = 0;
        if (!ComputeCaptureFootprints(g_device, g_display.width, g_display.height, &placed,
                &geometryBytes)) {
            return false;
        }
    }

    // B-6：整组先建到临时数组，全部成功才写入 g_capture —— "半就绪"在结构上不存在
    void* made[kCaptureSlots] = {};
    uint64_t bytes = 0;
    int failedSlot = -1;
    const ZfgCaptureBuildFn build = injected ? seam.build : &BuildCaptureBankDefault;
    void* buildUser = injected ? seam.user : static_cast<void*>(g_device);
    const bool built = build(buildUser, kCaptureSlots, g_display.width, g_display.height, made,
        &bytes, &failedSlot);
    if (!built) {
        // 失败：把已建的 0..N-1 释放并置空，下一次调用重建 kCaptureSlots 个（不叠加）
        int released = 0;
        for (int i = 0; i < kCaptureSlots; ++i) {
            if (made[i] != nullptr) {
                ReleaseCaptureResource(made[i]);
                made[i] = nullptr;
                ++released;
            }
        }
        const uint64_t failures
            = g_captureAllocFailures.fetch_add(1u, std::memory_order_relaxed) + 1u;
        ProbeLog("  zfg capture: bank build FAILED at slot %d (released %d slot(s); retry "
                 "rebuilds %d; failures=%llu)",
            failedSlot, released, kCaptureSlots,
            static_cast<unsigned long long>(failures));
        return false;
    }

    // 提交：锁内一次性写满全部槽位并置 captureValid（无中间态）
    ::AcquireSRWLockExclusive(&g_captureLock);
    for (int i = 0; i < kCaptureSlots; ++i) {
        g_capture[i].resource = static_cast<ID3D12Resource*>(made[i]);
        g_capture[i].placed = placed;
        g_capture[i].bytes = bytes;
    }
    g_display.captureValid = true;
    ::ReleaseSRWLockExclusive(&g_captureLock);
    ProbeLog("  zfg capture: bank ready at %ux%u (slots=%d)", g_display.width, g_display.height,
        kCaptureSlots);
    return true;
}

/// present 回调旁路：把 output（已是本帧最终画面）拷进读回槽位
void CaptureObserver(bool isGenerated, void* cmdList, void* output, void*)
{
    const long seen = ::InterlockedIncrement(&g_presentSeen);
    // 采样策略：**每一个插帧帧都抓**（它们才是"非黑非花"要判的对象，且占比低），
    // 真帧只每 kCaptureStride 个抓一个（当"参考画面"用，避免每帧一次拷贝）。
    // 早先只按 stride 抽签时，6 个槽位全落在真帧上 → 插帧像素无证据。
    if (!isGenerated && (seen % kCaptureStride) != 0) {
        return;
    }
    if (cmdList == nullptr || output == nullptr) {
        return;
    }
    if (!EnsureCapture()) {
        return;
    }
    auto* cmd = static_cast<ID3D12GraphicsCommandList*>(cmdList);
    auto* src = static_cast<ID3D12Resource*>(output);
    // 与 RetireCaptureBank 串行：resize 不能在我们正取槽位/记拷贝的当口把资源挪走
    // （TASK-R10-03 A-4：读回资源现在会随尺寸变更整组重建）
    ::AcquireSRWLockExclusive(&g_captureLock);
    if (!g_display.captureValid) {
        ::ReleaseSRWLockExclusive(&g_captureLock);
        return;
    }
    const int slot = g_captureNext;
    g_captureNext = (g_captureNext + 1) % kCaptureSlots;
    auto* dst = g_capture[slot].resource;
    const D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed = g_capture[slot].placed;
    // 拷贝范围取"源资源实际尺寸"与"读回槽位容量"的较小值。历史缺陷：这里直接用
    // 全局显示尺寸 → 窗口变小后 box 超出新后台缓冲（D3D12 未定义行为）。
    const D3D12_RESOURCE_DESC srcDesc = src->GetDesc();
    const unsigned copyW = ClampCopyExtent(static_cast<unsigned>(srcDesc.Width),
        placed.Footprint.Width);
    const unsigned copyH = ClampCopyExtent(srcDesc.Height, placed.Footprint.Height);
    if (dst == nullptr || copyW == 0u || copyH == 0u) {
        ::ReleaseSRWLockExclusive(&g_captureLock);
        return;
    }

    D3D12_RESOURCE_BARRIER barrier {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = src;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    cmd->ResourceBarrier(1u, &barrier);
    // 简单拷贝（不使用 FFX 的 ffxApiGetResourceDX12 包装，避免依赖其内部句柄）
    const D3D12_BOX box {0u, 0u, 0u, copyW, copyH, 1u};
    D3D12_TEXTURE_COPY_LOCATION dstLoc {};
    dstLoc.pResource = dst;
    dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dstLoc.PlacedFootprint = placed;
    D3D12_TEXTURE_COPY_LOCATION srcLoc {};
    srcLoc.pResource = src;
    srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    srcLoc.SubresourceIndex = 0;
    cmd->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, &box);
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    cmd->ResourceBarrier(1u, &barrier);

    (void)g_captureTotal.fetch_add(1u, std::memory_order_relaxed);
    if (isGenerated) {
        (void)g_captureGenerated.fetch_add(1u, std::memory_order_relaxed);
    } else {
        (void)g_captureReal.fetch_add(1u, std::memory_order_relaxed);
    }
    g_capture[slot].serial = static_cast<uint64_t>(seen);
    g_capture[slot].frameId = static_cast<uint32_t>(g_frameId);
    g_capture[slot].generated = isGenerated ? 1 : 0;
    ::ReleaseSRWLockExclusive(&g_captureLock);
}

/// 关停前把 GPU 追平（游戏队列上补一个 Signal 等它完成）
bool FlushGameQueue(uint32_t timeoutMs)
{
    if (g_queue == nullptr || g_device == nullptr) {
        return false;
    }
    if (g_fence == nullptr
        && FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)))) {
        return false;
    }
    g_fenceValue += 1u;
    if (FAILED(g_queue->Signal(g_fence, g_fenceValue))) {
        return false;
    }
    const HANDLE ev = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (ev == nullptr) {
        return false;
    }
    bool ok = false;
    if (SUCCEEDED(g_fence->SetEventOnCompletion(g_fenceValue, ev))) {
        ok = (::WaitForSingleObject(ev, timeoutMs) == WAIT_OBJECT_0);
    }
    (void)::CloseHandle(ev);
    return ok;
}

/// 读回缓冲的像素统计（8×8 抽样：亮度均值/方差/暗像素占比/R 通道动态范围/FNV 哈希）
struct PixelStats {
    double mean = 0.0;
    double variance = 0.0;
    double blackPct = 0.0;
    unsigned rMin = 0u;
    unsigned rMax = 0u;
    unsigned long long hash = 0u;
};

bool ComputePixelStats(ID3D12Resource* resource,
    const D3D12_PLACED_SUBRESOURCE_FOOTPRINT& placed, uint64_t bytes, PixelStats* out)
{
    if (resource == nullptr || out == nullptr) {
        return false;
    }
    void* mapped = nullptr;
    D3D12_RANGE range {0u, static_cast<SIZE_T>(bytes)};
    if (FAILED(resource->Map(0, &range, &mapped)) || mapped == nullptr) {
        return false;
    }
    const auto* base = static_cast<const uint8_t*>(mapped);
    const uint32_t pitch = placed.Footprint.RowPitch;
    // 采样范围取槽位自身容量（TASK-R10-03 A-4）：不再依赖全局显示尺寸，
    // 否则尺寸变更后按旧尺寸扫描会读到读回缓冲之外。
    const uint32_t sampleW = placed.Footprint.Width;
    const uint32_t sampleH = placed.Footprint.Height;
    uint64_t hash = 1469598103934665603ull;
    double sum = 0.0;
    double sumSq = 0.0;
    uint64_t dark = 0u;
    uint64_t count = 0u;
    uint8_t minC = 255u;
    uint8_t maxC = 0u;
    for (uint32_t y = 0; y < sampleH; y += 8u) {
        const uint8_t* row = base + static_cast<size_t>(y) * pitch;
        for (uint32_t x = 0; x < sampleW; x += 8u) {
            const uint8_t* p = row + static_cast<size_t>(x) * 4u;
            const double lum = (0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]) / 255.0;
            sum += lum;
            sumSq += lum * lum;
            hash = (hash ^ p[0]) * 1099511628211ull;
            hash = (hash ^ p[1]) * 1099511628211ull;
            hash = (hash ^ p[2]) * 1099511628211ull;
            if (lum < 0.02) {
                ++dark;
            }
            if (p[0] < minC) {
                minC = p[0];
            }
            if (p[0] > maxC) {
                maxC = p[0];
            }
            ++count;
        }
    }
    D3D12_RANGE written {0u, 0u};
    resource->Unmap(0, &written);
    const double mean = (count > 0u) ? sum / static_cast<double>(count) : 0.0;
    out->mean = mean;
    out->variance = (count > 0u) ? (sumSq / static_cast<double>(count)) - mean * mean : 0.0;
    out->blackPct = (count > 0u)
        ? (100.0 * static_cast<double>(dark) / static_cast<double>(count))
        : 0.0;
    out->rMin = static_cast<unsigned>(minC);
    out->rMax = static_cast<unsigned>(maxC);
    out->hash = static_cast<unsigned long long>(hash);
    return true;
}

// =============================================================================
// 底层链像素读回（TASK-B-R9-04 阶段 3）——"两条链都读回"
//
// 宿主看到的是代理链（chain[2]）的后台缓冲；**屏幕上真正呈现的是 FFX 自建的
// chain[1]**。要判"副链透传与基线一致"，必须读 chain[1] 自己的后台缓冲。
// 做法：在 chain[1] 的 Present 入口取 GetCurrentBackBufferIndex 对应的 buffer
// （flip 模型下这就是即将呈现的那一张，Present 要求它处于 PRESENT 状态），
// 插一条 PRESENT→COPY_SOURCE→PRESENT 的拷贝到 READBACK 堆；GPU 由 analyze
// 时的队列栅栏追平后 Map。采样 stride 大，代价可忽略；任何一步失败永久停用。
// =============================================================================
constexpr int kNativeSlots = 3;
constexpr int kNativeStride = 16;

struct NativeSlot {
    ID3D12Resource* resource = nullptr;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    uint64_t bytes = 0;
    uint64_t serial = 0;
    uint32_t bufferIndex = 0;
};

NativeSlot g_nativeSlots[kNativeSlots];
int g_nativeNext = 0;
long g_nativeSeen = 0;
uint64_t g_nativeFenceValue = 0;
volatile LONG64 g_nativeSkipped = 0; // 因栅栏未追平而跳过的采样数
ID3D12CommandAllocator* g_nativeAlloc = nullptr;
ID3D12GraphicsCommandList* g_nativeList = nullptr;
ID3D12Fence* g_nativeFence = nullptr;
bool g_nativeReady = false;
bool g_nativeDisabled = false;

bool NativeCaptureEnabled()
{
    static int s_state = -1;
    if (s_state < 0) {
        char value[8] = {};
        // B-2（TASK-R10-05）：三态读取。历史缺陷：不看返回值（值 ≥ 8 字符时 strcmp
        // 越界读）。语义保持"只有 '0' 关闭"：Truncated（异常长值）→ 不等于 "0" → 开。
        const zfg::EnvRead read = zfg::ReadEnvA("ZFG_NATIVE_CAPTURE", value, sizeof(value));
        s_state = (read == zfg::EnvRead::Set && std::strcmp(value, "0") == 0) ? 0 : 1;
    }
    return s_state == 1;
}

bool EnsureNativeCapture()
{
    if (g_nativeReady) {
        return true;
    }
    if (g_nativeDisabled || g_device == nullptr || g_queue == nullptr
        || g_display.width == 0u || g_display.height == 0u) {
        return false;
    }
    D3D12_RESOURCE_DESC desc {};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = g_display.width;
    desc.Height = g_display.height;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    UINT64 total = 0;
    g_device->GetCopyableFootprints(&desc, 0, 1, 0, &placed, nullptr, nullptr, &total);
    if (total == 0u) {
        g_nativeDisabled = true;
        return false;
    }
    D3D12_HEAP_PROPERTIES heap {};
    heap.Type = D3D12_HEAP_TYPE_READBACK;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC bufDesc {};
    bufDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    bufDesc.Width = total;
    bufDesc.Height = 1;
    bufDesc.DepthOrArraySize = 1;
    bufDesc.MipLevels = 1;
    bufDesc.Format = DXGI_FORMAT_UNKNOWN;
    bufDesc.SampleDesc.Count = 1;
    bufDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    for (int i = 0; i < kNativeSlots; ++i) {
        ID3D12Resource* res = nullptr;
        if (FAILED(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &bufDesc,
                D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&res)))
            || res == nullptr) {
            g_nativeDisabled = true;
            return false;
        }
        g_nativeSlots[i].resource = res;
        g_nativeSlots[i].placed = placed;
        g_nativeSlots[i].bytes = total;
    }
    if (FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
            IID_PPV_ARGS(&g_nativeAlloc)))
        || g_nativeAlloc == nullptr) {
        g_nativeDisabled = true;
        return false;
    }
    if (FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_nativeAlloc,
            nullptr, IID_PPV_ARGS(&g_nativeList)))
        || g_nativeList == nullptr) {
        g_nativeDisabled = true;
        return false;
    }
    (void)g_nativeList->Close();
    if (FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_nativeFence)))
        || g_nativeFence == nullptr) {
        g_nativeDisabled = true;
        return false;
    }
    g_nativeReady = true;
    ProbeLog("  zfg native capture: ready (%ux%u, %d slots, stride=%d)", g_display.width,
        g_display.height, kNativeSlots, kNativeStride);
    return true;
}

// 采样分两步：Present **前**只记下"要读哪个后台缓冲"，Present **后**才真正
// 记录拷贝命令。原因：FFX 在自己的队列上做合成，我们在游戏队列上插拷贝，
// 若在 Present 前拷贝，可能读到该缓冲上一次的内容（实测哈希与观察者不一致，
// 统计量却一致——正是"落后一拍"的特征）。Present 返回时内容已确定。
thread_local int t_nativePendingIndex = -1;
thread_local void* t_nativePendingSc = nullptr;
thread_local long t_nativePendingSeen = 0;

void RecordNativeCaptureBefore(void* swapchain)
{
    t_nativePendingIndex = -1;
    t_nativePendingSc = nullptr;
    if (swapchain == nullptr || !NativeCaptureEnabled()) {
        return;
    }
    const long seen = ::InterlockedIncrement(&g_nativeSeen);
    if ((seen % kNativeStride) != 0) {
        return;
    }
    if (!EnsureNativeCapture() || g_nativeDisabled) {
        return;
    }
    auto* sc3 = static_cast<IDXGISwapChain3*>(swapchain);
    t_nativePendingIndex = static_cast<int>(sc3->GetCurrentBackBufferIndex());
    t_nativePendingSc = swapchain;
    t_nativePendingSeen = seen;
}

void RecordNativeCaptureAfter()
{
    const int pendingIndex = t_nativePendingIndex;
    void* pendingSc = t_nativePendingSc;
    const long seen = t_nativePendingSeen;
    t_nativePendingIndex = -1;
    t_nativePendingSc = nullptr;
    if (pendingIndex < 0 || pendingSc == nullptr || g_nativeDisabled || !g_nativeReady) {
        return;
    }
    auto* sc3 = static_cast<IDXGISwapChain3*>(pendingSc);
    const UINT index = static_cast<UINT>(pendingIndex);
    ID3D12Resource* buffer = nullptr;
    if (FAILED(sc3->GetBuffer(index, IID_PPV_ARGS(&buffer))) || buffer == nullptr) {
        g_nativeDisabled = true;
        ProbeLog("  zfg native capture: GetBuffer FAILED -> disabled");
        return;
    }
    const int slot = g_nativeNext;
    // 只有一个分配器：Reset 前必须等"上一次提交"整体完成（否则就是重置仍在
    // GPU 上执行的分配器）。超时则跳过本次采样——绝不阻塞呈现。
    if (g_nativeFenceValue != 0u && g_nativeFence->GetCompletedValue() < g_nativeFenceValue) {
        const HANDLE ev = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
        bool done = false;
        if (ev != nullptr) {
            if (SUCCEEDED(g_nativeFence->SetEventOnCompletion(g_nativeFenceValue, ev))) {
                done = (::WaitForSingleObject(ev, 200u) == WAIT_OBJECT_0);
            }
            (void)::CloseHandle(ev);
        }
        if (!done) {
            buffer->Release();
            (void)::InterlockedIncrement64(&g_nativeSkipped);
            return;
        }
    }
    g_nativeNext = (g_nativeNext + 1) % kNativeSlots;
    if (FAILED(g_nativeAlloc->Reset()) || FAILED(g_nativeList->Reset(g_nativeAlloc, nullptr))) {
        buffer->Release();
        return;
    }
    D3D12_RESOURCE_BARRIER barrier {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = buffer;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    g_nativeList->ResourceBarrier(1u, &barrier);
    D3D12_TEXTURE_COPY_LOCATION dstLoc {};
    dstLoc.pResource = g_nativeSlots[slot].resource;
    dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dstLoc.PlacedFootprint = g_nativeSlots[slot].placed;
    D3D12_TEXTURE_COPY_LOCATION srcLoc {};
    srcLoc.pResource = buffer;
    srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    srcLoc.SubresourceIndex = 0;
    // 拷贝范围取"源后台缓冲实际尺寸"与"读回槽位容量"的较小值（同 CaptureObserver：
    // 尺寸变更后不得按旧尺寸越界拷贝）
    const D3D12_RESOURCE_DESC srcDesc = buffer->GetDesc();
    const unsigned copyW = ClampCopyExtent(static_cast<unsigned>(srcDesc.Width),
        g_nativeSlots[slot].placed.Footprint.Width);
    const unsigned copyH = ClampCopyExtent(srcDesc.Height,
        g_nativeSlots[slot].placed.Footprint.Height);
    if (copyW == 0u || copyH == 0u) {
        buffer->Release();
        return;
    }
    const D3D12_BOX box {0u, 0u, 0u, copyW, copyH, 1u};
    g_nativeList->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, &box);
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    g_nativeList->ResourceBarrier(1u, &barrier);
    const HRESULT closeHr = g_nativeList->Close();
    if (FAILED(closeHr)) {
        buffer->Release();
        g_nativeDisabled = true;
        return;
    }
    ID3D12CommandList* lists[1] = {g_nativeList};
    g_queue->ExecuteCommandLists(1u, lists);
    g_nativeFenceValue += 1u;
    if (FAILED(g_queue->Signal(g_nativeFence, g_nativeFenceValue))) {
        buffer->Release();
        g_nativeDisabled = true;
        return;
    }
    g_nativeSlots[slot].serial = static_cast<uint64_t>(seen);
    g_nativeSlots[slot].bufferIndex = static_cast<uint32_t>(index);
    buffer->Release();
}

/// 底层链读回统计（"副链透传与基线一致"的像素证据）
void AnalyzeNativeCaptures()
{
    if (!g_nativeReady) {
        ProbeLog("  zfg native capture: none (ready=0 disabled=%d)", g_nativeDisabled ? 1 : 0);
        return;
    }
    for (int i = 0; i < kNativeSlots; ++i) {
        NativeSlot& slot = g_nativeSlots[i];
        if (slot.resource == nullptr || slot.serial == 0u) {
            continue;
        }
        PixelStats st {};
        if (!ComputePixelStats(slot.resource, slot.placed, slot.bytes, &st)) {
            ProbeLog("  zfg native[%d]: map FAILED", i);
            continue;
        }
        ProbeLog("  zfg native[%d]: presentSerial=%llu bufIndex=%u mean=%.4f var=%.5f "
                 "black=%.1f%% rMin=%u rMax=%u hash=0x%016llX",
            i, static_cast<unsigned long long>(slot.serial), slot.bufferIndex, st.mean,
            st.variance, st.blackPct, st.rMin, st.rMax, st.hash);
    }
    ProbeLog("  zfg native capture: samples=%lld skipped=%lld", static_cast<long long>(g_nativeSeen),
        static_cast<long long>(g_nativeSkipped));
}

/// 读回槽位统计（非黑/非均匀/哈希）：插帧帧的"不是黑屏也不是花屏"由这里判定
void AnalyzeCaptures()
{
    if (!g_display.captureValid) {
        ProbeLog("  zfg capture: no frames captured (bank invalid/retired)");
        return;
    }
    ProbeLog("  zfg capture: slots=%d total=%llu generated=%llu real=%llu", kCaptureSlots,
        static_cast<unsigned long long>(g_captureTotal.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_captureGenerated.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_captureReal.load(std::memory_order_relaxed)));
    for (int i = 0; i < kCaptureSlots; ++i) {
        CaptureSlot& slot = g_capture[i];
        if (slot.resource == nullptr || slot.serial == 0u) {
            continue;
        }
        PixelStats st {};
        if (!ComputePixelStats(slot.resource, slot.placed, slot.bytes, &st)) {
            ProbeLog("  zfg capture[%d]: map FAILED", i);
            continue;
        }
        ProbeLog("  zfg capture[%d]: gen=%d frameId=%u serial=%llu mean=%.4f var=%.5f black=%.1f%% "
                 "rMin=%u rMax=%u hash=0x%016llX",
            i, slot.generated, slot.frameId, static_cast<unsigned long long>(slot.serial), st.mean,
            st.variance, st.blackPct, st.rMin, st.rMax, st.hash);
    }
    AnalyzeNativeCaptures();
}

bool EnsurePrepObjects()
{
    if (g_device == nullptr || g_queue == nullptr) {
        return false;
    }
    if (g_fence == nullptr) {
        if (FAILED(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)))
            || g_fence == nullptr) {
            SetError("CreateFence", 0);
            return false;
        }
    }
    for (int i = 0; i < kPrepRing; ++i) {
        if (g_alloc[i] == nullptr) {
            if (FAILED(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                    IID_PPV_ARGS(&g_alloc[i])))
                || g_alloc[i] == nullptr) {
                SetError("CreateCommandAllocator", 0);
                return false;
            }
        }
        if (g_list[i] == nullptr) {
            if (FAILED(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_alloc[i],
                    nullptr, IID_PPV_ARGS(&g_list[i])))
                || g_list[i] == nullptr) {
                SetError("CreateCommandList", 0);
                return false;
            }
            // 立刻 Close：D3D12 要求命令表以关闭态交付，Reset 时再开
            (void)g_list[i]->Close();
        }
    }
    return true;
}

/// 等待某个分配器在 GPU 侧完成（上限 100ms；超时返回 false → 本帧不派发）
bool WaitAllocatorIdle(int index)
{
    if (g_fence == nullptr || g_allocDone[index] == 0u) {
        return true;
    }
    if (g_fence->GetCompletedValue() >= g_allocDone[index]) {
        return true;
    }
    const HANDLE ev = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (ev == nullptr) {
        return false;
    }
    bool ok = false;
    if (SUCCEEDED(g_fence->SetEventOnCompletion(g_allocDone[index], ev))) {
        ok = (::WaitForSingleObject(ev, 100u) == WAIT_OBJECT_0)
            && (g_fence->GetCompletedValue() >= g_allocDone[index]);
    }
    (void)::CloseHandle(ev);
    return ok;
}

/// 派发 PrepareV2（在游戏队列上，Present 之前）
bool DispatchPrepare(uint64_t frameId, const ZfgFgFrameInputs& in)
{
    if (g_fg == nullptr || !EnsurePrepObjects()) {
        return false;
    }
    const int index = g_allocIndex;
    if (!WaitAllocatorIdle(index)) {
        SetError("prepare allocator still in flight", 0);
        return false;
    }
    if (FAILED(g_alloc[index]->Reset()) || FAILED(g_list[index]->Reset(g_alloc[index], nullptr))) {
        SetError("command list reset", 0);
        return false;
    }
    zr::FrameGenPrepareArgs args {};
    args.nativeCmdList = g_list[index];
    args.nativeDepth = in.depthResource;
    args.nativeMotionVectors = in.mvResource;
    // 0 = 用默认（像素+计算可读，等价 Unity 采样后的资源状态）
    args.depthState = (in.depthState != 0u) ? in.depthState : FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ;
    args.mvState = (in.mvState != 0u) ? in.mvState : FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ;
    args.jitterX = in.jitterX;
    args.jitterY = in.jitterY;
    args.motionVectorScaleX = in.mvScaleX;
    args.motionVectorScaleY = in.mvScaleY;
    args.frameTimeDeltaMs = in.frameTimeMs;
    args.cameraFovY = in.fovY;
    args.cameraNear = in.nearZ;
    args.cameraFar = in.farZ;
    args.reset = (in.reset != 0);
    args.frameId = frameId;
    std::memcpy(args.cameraPosition, in.camPos, sizeof(args.cameraPosition));
    std::memcpy(args.cameraUp, in.camUp, sizeof(args.cameraUp));
    std::memcpy(args.cameraRight, in.camRight, sizeof(args.cameraRight));
    std::memcpy(args.cameraForward, in.camForward, sizeof(args.cameraForward));
    const bool ok = g_fg->DispatchPrepare(args);
    g_lastRc.store(g_fg->LastReturnCode(), std::memory_order_relaxed);
    if (FAILED(g_list[index]->Close())) {
        SetError("command list close", 0);
        return false;
    }
    ID3D12CommandList* lists[1] = {g_list[index]};
    g_queue->ExecuteCommandLists(1u, lists);
    g_fenceValue += 1u;
    if (FAILED(g_fence->Signal(g_fenceValue))) {
        SetError("fence signal", 0);
        return false;
    }
    g_allocDone[index] = g_fenceValue;
    g_allocIndex = (index + 1) % kPrepRing;
    return ok;
}

void ReleasePrepObjects()
{
    for (int i = 0; i < kPrepRing; ++i) {
        if (g_list[i] != nullptr) {
            g_list[i]->Release();
            g_list[i] = nullptr;
        }
        if (g_alloc[i] != nullptr) {
            g_alloc[i]->Release();
            g_alloc[i] = nullptr;
        }
        g_allocDone[i] = 0u;
    }
    if (g_fence != nullptr) {
        g_fence->Release();
        g_fence = nullptr;
    }
    g_fenceValue = 0u;
    g_allocIndex = 0;
}

/// 释放底层链（FFX 自建、真正上屏那条）的读回槽位与命令对象。
/// 尺寸变更后必须整组作废：否则下一次 RecordNativeCaptureAfter 会按旧槽位容量
/// 与新后台缓冲做拷贝（TASK-R10-03 A-4 的同类越界面）。
void ReleaseNativeCapture()
{
    const bool had = g_nativeReady;
    g_nativeReady = false;
    g_nativeDisabled = false; // 允许按新尺寸重建
    g_nativeNext = 0;
    g_nativeSeen = 0;
    g_nativeFenceValue = 0u;
    for (int i = 0; i < kNativeSlots; ++i) {
        if (g_nativeSlots[i].resource != nullptr) {
            g_nativeSlots[i].resource->Release();
        }
        g_nativeSlots[i] = NativeSlot {};
    }
    if (g_nativeList != nullptr) {
        g_nativeList->Release();
        g_nativeList = nullptr;
    }
    if (g_nativeAlloc != nullptr) {
        g_nativeAlloc->Release();
        g_nativeAlloc = nullptr;
    }
    if (g_nativeFence != nullptr) {
        g_nativeFence->Release();
        g_nativeFence = nullptr;
    }
    if (had) {
        ProbeLog("  zfg native capture: bank released (size change)");
    }
}

} // namespace

// ---- B-6（TASK-R10-07）：读回银行接缝 / 诊断 / 启用入口 ------------------------
void WrapSetCaptureSeam(const ZfgCaptureSeam* seam)
{
    g_captureSeam = (seam != nullptr) ? *seam : ZfgCaptureSeam {};
}

bool WrapGetCaptureDiag(ZfgCaptureDiag* out)
{
    if (out == nullptr) {
        return false;
    }
    ::AcquireSRWLockExclusive(&g_captureLock);
    int live = 0;
    for (int i = 0; i < kCaptureSlots; ++i) {
        if (g_capture[i].resource != nullptr) {
            ++live;
        }
    }
    out->liveSlots = live;
    out->captureValid = g_display.captureValid;
    ::ReleaseSRWLockExclusive(&g_captureLock);
    out->allocFailures = g_captureAllocFailures.load(std::memory_order_relaxed);
    return true;
}

bool WrapEnsureCaptureBank()
{
    return EnsureCapture();
}

bool WrapCreateSwapChainForHwnd(void* dxgiFactory, void* gameQueue, void* hwnd,
    unsigned width, unsigned height, unsigned dxgiFormat, unsigned bufferCount, void** outWrapped,
    const void* requestedDesc1, const void* requestedFullscreenDesc1)
{
    if (g_fg != nullptr) {
        (void)std::snprintf(g_lastError, sizeof(g_lastError), "already wrapped");
        return false;
    }
    if (outWrapped == nullptr || hwnd == nullptr || gameQueue == nullptr || dxgiFactory == nullptr
        || width == 0u || height == 0u) {
        SetError("invalid argument", 0);
        return false;
    }
    auto* queue = static_cast<ID3D12CommandQueue*>(gameQueue);
    ID3D12Device* device = nullptr;
    HRESULT hr = queue->GetDevice(IID_PPV_ARGS(&device));
    if (FAILED(hr) || device == nullptr) {
        SetError("ID3D12CommandQueue::GetDevice", hr);
        return false;
    }

    auto* fg = new (std::nothrow) zr::FrameGenerator();
    if (fg == nullptr) {
        device->Release();
        SetError("out of memory", 0);
        return false;
    }

    const zr::FrameGenProbe probe = zr::FrameGenerator::QuerySupport(device);
    ProbeLog("  zfg wrap: probe support=%u rc=%u provider='%s'",
        static_cast<unsigned>(probe.support), probe.returnCode, probe.providerName);
    if (probe.support != zr::FrameGenSupport::Available) {
        delete fg;
        device->Release();
        SetError("FG not available", static_cast<long>(probe.returnCode));
        return false;
    }

    zr::FrameGenCreateDesc desc {};
    desc.displayWidth = width;
    desc.displayHeight = height;
    desc.renderWidth = width; // 本集成无上采样：render == display
    desc.renderHeight = height;
    desc.backBufferFfxFormat = 0; // 由 fg_ffx 内部按 DXGI 格式换算（见下）
    desc.displayResolutionMv = true;
    // F2（TASK-B-R9-03 必修）：URP 的 MV 不含 jitter → Unity/代理路径必须 false
    desc.jitterCancellation = false;
    // Unity 是 reversed-Z（深度 1=近），FFX 需要同一口径
    desc.depthInverted = true;
    desc.debugChecking = false;
    desc.nativeDevice = device;
    // 官方映射：实测 Unity 2022.3/D3D12 的后台缓冲是 R8G8B8A8_UNORM（不是窗口
    // 程序 --fg 用的 B8G8R8A8），因此按 DXGI 格式查表而不是硬编码。
    const uint32_t ffxFormat = ffxApiGetSurfaceFormatDX12(static_cast<DXGI_FORMAT>(dxgiFormat));
    if (ffxFormat == 0u) {
        delete fg;
        device->Release();
        SetError("unsupported backbuffer format", static_cast<long>(dxgiFormat));
        return false;
    }
    desc.backBufferFfxFormat = ffxFormat;
    if (!fg->Create(desc)) {
        const long rc = static_cast<long>(fg->LastReturnCode());
        delete fg;
        device->Release();
        SetError("ffxCreateContext(FRAMEGENERATION)", rc);
        return false;
    }

    // ---- A-3（TASK-R10-05）：先按宿主 desc 建链（只补 FG 必需位），FFX 拒绝再回退
    // 自造 desc。历史缺陷：宿主请求的 usage/effect/flags/scaling/alpha/sampleDesc
    // 在边界被丢弃、FFX 用自造 desc 建链却返回 S_OK（本仓日志实测：Unity 请求
    // usage=0x30 effect=3 flags=0x842，建成 usage=0x20 effect=4 flags=0x840——
    // 后台缓冲 SHADER_INPUT 位、ALLOW_TEARING 位、FLIP_SEQUENTIAL 语义被静默丢掉）。
    // 回退不是给错误让路，而是保住 B-R9-04 的真机插帧链路：若某 FFX 版本拒绝宿主
    // 的 SwapEffect/usage 组合，宁可按旧口径建链并在日志里如实对账，也不让 FG 整体
    // 失效（hook 会落到原生链 = 插帧直接没有）。
    void* wrapped = nullptr;
    void* waitable = nullptr;
    if (requestedDesc1 != nullptr) {
        if (fg->CreateFgSwapchainForHwnd(hwnd, dxgiFactory, gameQueue, width, height, dxgiFormat,
                bufferCount, &wrapped, &waitable, requestedDesc1, requestedFullscreenDesc1)
            && wrapped != nullptr) {
            ProbeLog("  zfg wrap: proxy swapchain built from host desc (A-3 passthrough)");
        } else {
            const long rc = static_cast<long>(fg->LastReturnCode());
            ProbeLog("  zfg wrap: host-desc swapchain REJECTED by FFX (rc=%ld %s) — falling "
                     "back to legacy synthesized desc (A-3 fallback)",
                rc, zr::ZupFgReturnCodeName(static_cast<uint32_t>(rc)));
            wrapped = nullptr;
            waitable = nullptr;
        }
    }
    if (wrapped == nullptr
        && (!fg->CreateFgSwapchainForHwnd(hwnd, dxgiFactory, gameQueue, width, height, dxgiFormat,
                bufferCount, &wrapped, &waitable)
            || wrapped == nullptr)) {
        const long rc = static_cast<long>(fg->LastReturnCode());
        fg->Shutdown();
        delete fg;
        device->Release();
        SetError("ffxCreateContext(swapchain)", rc);
        return false;
    }

    // A-3 对账证据：宿主请求 desc vs 代理链实际 desc（逐字段；必需位除外必须一致）。
    // 审计 A-3 的验证方式就是"比对 desc 行与请求一致"——这行日志是它的运行期载体。
    if (requestedDesc1 != nullptr) {
        DXGI_SWAP_CHAIN_DESC1 requested {};
        std::memcpy(&requested, requestedDesc1, sizeof(requested));
        auto* wrappedSc = static_cast<IDXGISwapChain1*>(wrapped);
        DXGI_SWAP_CHAIN_DESC1 actual {};
        if (SUCCEEDED(wrappedSc->GetDesc1(&actual))) {
            const bool mismatch = (actual.Width != requested.Width)
                || (actual.Height != requested.Height)
                || (actual.Format != requested.Format)
                || (actual.BufferCount != requested.BufferCount)
                || (actual.BufferUsage != requested.BufferUsage)
                || (actual.SwapEffect != requested.SwapEffect)
                || (actual.Scaling != requested.Scaling)
                || (actual.AlphaMode != requested.AlphaMode)
                || (actual.SampleDesc.Count != requested.SampleDesc.Count)
                || (actual.SampleDesc.Quality != requested.SampleDesc.Quality)
                || (actual.Flags
                    != (requested.Flags | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT));
            ProbeLog("  zfg wrap: desc reconcile (A-3) requested usage=0x%X effect=%d "
                     "flags=0x%X scaling=%d alpha=%d sample=%u || actual usage=0x%X effect=%d "
                     "flags=0x%X scaling=%d alpha=%d sample=%u -> %s",
                requested.BufferUsage, static_cast<int>(requested.SwapEffect), requested.Flags,
                static_cast<int>(requested.Scaling), static_cast<int>(requested.AlphaMode),
                requested.SampleDesc.Count, actual.BufferUsage,
                static_cast<int>(actual.SwapEffect), actual.Flags,
                static_cast<int>(actual.Scaling), static_cast<int>(actual.AlphaMode),
                actual.SampleDesc.Count, mismatch ? "MISMATCH" : "MATCH");
        }
    }

    g_fg = fg;
    g_wrapped = wrapped;
    g_waitable = waitable;
    g_frameId = 0;
    g_display.width = width;
    g_display.height = height;
    g_display.captureValid = false;
    g_display.resizeSerial = 0u;
    g_device = device; // 偷一份引用（Shutdown 时释放）
    g_queue = queue;
    g_queue->AddRef();
    g_lastError[0] = '\0';
    *outWrapped = wrapped;
    // 像素校验旁路（真帧/插帧都抓，输出到日志；对画面零影响）
    fg->SetPresentObserver(&CaptureObserver, nullptr);
    ProbeLog("  zfg wrap: proxy swapchain created wrapped=%p waitable=%p (jitterCancellation=0 "
             "depthInverted=1)",
        wrapped, waitable);
    return true;
}

void WrapNoteSwapchainSize(unsigned width, unsigned height)
{
    if (width == 0u || height == 0u) {
        return;
    }
    if (g_wrapped != nullptr) {
        return; // 代理链：尺寸由 WrapCreateSwapChainForHwnd 写入
    }
    if (g_display.width == width && g_display.height == height) {
        return;
    }
    unsigned long long stamp = 0u;
    ::AcquireSRWLockExclusive(&g_inputLock);
    stamp = g_inputStamp;
    ::ReleaseSRWLockExclusive(&g_inputLock);
    const unsigned oldW = g_display.width;
    const unsigned oldH = g_display.height;
    const ResizeOutcome out = ApplySwapchainResize(g_display, stamp, width, height);
    if (out.captureInvalidated) {
        RetireCaptureBank("chain created");
    }
    ProbeLog("  zfg wrap: display size %ux%u -> %ux%u (native chain created)", oldW, oldH,
        g_display.width, g_display.height);
}

void WrapOnSwapchainResized(unsigned width, unsigned height)
{
    unsigned long long stamp = 0u;
    ::AcquireSRWLockExclusive(&g_inputLock);
    stamp = g_inputStamp;
    ::ReleaseSRWLockExclusive(&g_inputLock);

    const ResizeOutcome out = ApplySwapchainResize(g_display, stamp, width, height);

    // 输入复用额度失效（TASK-R10-03 A-4）：交换链重建后，旧尺寸的 depth/MV 不再是
    // "可安全复用的本帧输入"——下一次呈现必须拿到**新提交**才允许插帧。
    ::AcquireSRWLockExclusive(&g_inputLock);
    g_consumedStamp = g_display.consumedStamp;
    g_reuseLeft = 0;
    ::ReleaseSRWLockExclusive(&g_inputLock);

    if (out.sizeChanged) {
        (void)g_resizeCount.fetch_add(1u, std::memory_order_relaxed);
    }
    if (out.captureInvalidated) {
        RetireCaptureBank("resize");
        ReleaseNativeCapture();
    }
    ProbeLog("  zfg wrap: swapchain resized %ux%u -> %ux%u (accepted=%d sizeChanged=%d "
             "captureReset=%d inputsRevalidated=%d serial=%llu)",
        out.oldWidth, out.oldHeight, g_display.width, g_display.height, out.accepted ? 1 : 0,
        out.sizeChanged ? 1 : 0, out.captureInvalidated ? 1 : 0, out.inputsInvalidated ? 1 : 0,
        static_cast<unsigned long long>(g_display.resizeSerial));
}

bool WrapOnFrame(void* wrapped, bool wantFrameGen)
{
    if (g_fg == nullptr || wrapped == nullptr) {
        return false;
    }
    g_frameId += 1u;
    const uint64_t callIndex = g_onFrameCalls.fetch_add(1u, std::memory_order_relaxed) + 1u;

    // 输入消费口径（TASK-B-R9-04 阶段 2）：
    //   fresh = 本帧新提交（托管侧每渲染帧提交一次）→ 直接用；
    //   reuse = 同一渲染帧的重复呈现（实测宿主会出现"同一毫秒内两次 Present1"，
    //           即一次渲染对应多次呈现）。此时 depth/MV 仍是该帧的，可以安全复用；
    //   stale = 复用额度耗尽（连续 kMaxInputReuse 次以上没有新提交）→ 本帧不插帧，
    //           避免用过期资源插出错误画面。
    bool haveInputs = false;
    bool fresh = false;
    ZfgFgFrameInputs in {};
    ::AcquireSRWLockExclusive(&g_inputLock);
    // A-7（TASK-R10-05）：消费账目（fresh/reuse/stale）收敛到 zfg_fg_input_policy.h
    // ——产品与单测共用同一份判定。输入资源由本模块显式 AddRef 持有（提交时），
    // 复用窗口内的指针在结构上不可能悬垂（历史缺陷：裸指针 + memcpy）。
    const FgInputConsumption consumed = ConsumeFrameInput(
        FgInputAccount {g_hasInputs, g_consumedStamp, g_reuseLeft}, g_inputStamp);
    g_consumedStamp = consumed.consumedStamp;
    g_reuseLeft = consumed.reuseLeft;
    if (consumed.verdict == FgInputVerdict::Fresh || consumed.verdict == FgInputVerdict::Reuse) {
        in = g_inputs;
        haveInputs = true;
        fresh = (consumed.verdict == FgInputVerdict::Fresh);
    }
    ::ReleaseSRWLockExclusive(&g_inputLock);
    if (fresh) {
        (void)g_onFrameInputs.fetch_add(1u, std::memory_order_relaxed);
    } else if (haveInputs) {
        (void)g_onFrameReused.fetch_add(1u, std::memory_order_relaxed);
    } else {
        (void)g_onFrameStale.fetch_add(1u, std::memory_order_relaxed);
    }

    int overrideEnabled = g_enabledOverride.load(std::memory_order_relaxed);
    bool want = wantFrameGen && (overrideEnabled != 0);
    // 尺寸校验（TASK-R10-03 A-4）：显示尺寸现在会随 ResizeBuffers 更新，所以这里
    // 是"本帧输入是否属于当前这条链"的实时判定，而不是一次性告警。只在状态切换时
    // 打日志（停用/恢复各一条），避免 resize 后每帧刷屏。
    if (want && haveInputs && !FrameInputMatchesDisplay(g_display, in.width, in.height)) {
        if (!t_sizeMismatchLogged) {
            t_sizeMismatchLogged = true;
            (void)g_sizeMismatchFrames.fetch_add(1u, std::memory_order_relaxed);
            ProbeLog("  zfg wrap: FG disabled (input %ux%u != display %ux%u) — waiting for a "
                     "fresh input at the new size",
                in.width, in.height, g_display.width, g_display.height);
        }
        haveInputs = false;
    } else if (!t_sizeMismatchLogged) {
        // 尺寸一致（含从未错配）：无需处理
    } else {
        t_sizeMismatchLogged = false;
        ProbeLog("  zfg wrap: FG input size matches display again (%ux%u) — frame gen resumed",
            g_display.width, g_display.height);
    }
    const bool enabled = want && haveInputs;

    // 取证：每 60 个应用帧一条（frameId / 输入口径 / 是否插帧 / 宿主呈现计数）
    if ((callIndex % 60u) == 0u) {
        ProbeLog("  zfg wrap: onFrame #%llu frameId=%llu fresh=%d reused=%d stale=%d "
                 "enabled=%d prepared=%llu generated=%llu real=%llu",
            static_cast<unsigned long long>(callIndex),
            static_cast<unsigned long long>(g_frameId), fresh ? 1 : 0,
            (!fresh && haveInputs) ? 1 : 0, (!haveInputs) ? 1 : 0, enabled ? 1 : 0,
            static_cast<unsigned long long>(g_prepareOk.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(g_generated.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(g_real.load(std::memory_order_relaxed)));
    }

    const bool cfgOk = g_fg->ConfigureFrame(wrapped, g_frameId, enabled, &CountPresent, nullptr);
    g_lastRc.store(g_fg->LastReturnCode(), std::memory_order_relaxed);
    g_enabledNow.store(enabled ? 1 : 0, std::memory_order_relaxed);
    if (!cfgOk) {
        g_prepareFail.fetch_add(1u, std::memory_order_relaxed);
        ProbeLog("  zfg wrap: ConfigureFrame FAILED frameId=%llu enabled=%d",
            static_cast<unsigned long long>(g_frameId), enabled ? 1 : 0);
        return false;
    }
    if (!enabled) {
        return true;
    }
    if (!DispatchPrepare(g_frameId, in)) {
        g_prepareFail.fetch_add(1u, std::memory_order_relaxed);
        ProbeLog("  zfg wrap: PrepareV2 FAILED frameId=%llu (%s)",
            static_cast<unsigned long long>(g_frameId), g_lastError);
        return false;
    }
    g_prepareOk.fetch_add(1u, std::memory_order_relaxed);
    return true;
}

bool WrapSubmitInputs(const ZfgFgFrameInputs* inputs)
{
    if (inputs == nullptr || inputs->structSize != sizeof(ZfgFgFrameInputs)
        || inputs->version != kZfgFgInputsVersion || inputs->depthResource == nullptr
        || inputs->mvResource == nullptr || inputs->width == 0u || inputs->height == 0u) {
        return false;
    }
    // A-7（TASK-R10-05）：先 AddRef 再入账——校验通过后引用归本模块持有，托管侧
    // 何时释放纹理都不再影响我们的消费窗口（历史缺陷：裸指针 memcpy）。
    static_cast<IUnknown*>(inputs->depthResource)->AddRef();
    static_cast<IUnknown*>(inputs->mvResource)->AddRef();
    ::AcquireSRWLockExclusive(&g_inputLock);
    RetireCurrentInputsLocked(); // 旧输入引用进退休队列（等 fence 追平再 Release）
    g_inputs = *inputs;
    g_inputStamp += 1u;
    g_hasInputs = true;
    FlushRetiredInputsLocked();  // 顺手清掉 fence 已追平的退休引用
    ::ReleaseSRWLockExclusive(&g_inputLock);
    return true;
}

bool WrapGetStats(ZfgFgStats* out)
{
    if (out == nullptr) {
        return false;
    }
    out->realPresents = g_real.load(std::memory_order_relaxed);
    out->generatedPresents = g_generated.load(std::memory_order_relaxed);
    out->prepareCount = g_prepareOk.load(std::memory_order_relaxed);
    out->prepareFailed = g_prepareFail.load(std::memory_order_relaxed);
    out->enabled = g_enabledNow.load(std::memory_order_relaxed);
    out->lastReturnCode = g_lastRc.load(std::memory_order_relaxed);
    uint32_t haveInputs = 0u;
    ::AcquireSRWLockShared(&g_inputLock);
    haveInputs = g_hasInputs ? 1u : 0u;
    ::ReleaseSRWLockShared(&g_inputLock);
    out->haveInputs = haveInputs;
    out->reserved = 0u;
    return (g_fg != nullptr);
}

bool WrapFrameGenAuthorized()
{
    // 托管侧显式要过插帧（ZFG_FgSetEnabled(1)）才算授权。默认 -1（跟随模式）
    // 不算——ZFG_HOOK=wrap 的"透明直通"语义因此保持不变（B-R9-04/05 的回归面）。
    return g_enabledOverride.load(std::memory_order_relaxed) == 1;
}

int WrapSetFrameGenEnabled(int enabled)
{
    g_enabledOverride.store((enabled != 0) ? 1 : 0, std::memory_order_relaxed);
    return 1;
}

int WrapAnalyzeCaptures()
{
    // 交换链账目与模式无关（log 模式没有代理链，但"宿主到底建了几条链"照样要留证）
    DumpSwapchainSummary("analyze");
    if (g_fg == nullptr) {
        return 0;
    }
    const bool flushed = FlushGameQueue(3000u);
    ProbeLog("  zfg capture: analyze requested (queueFlushed=%d, presents=%ld)", flushed ? 1 : 0,
        static_cast<long>(g_presentSeen));
    ProbeLog("  zfg wrap: onFrame calls=%llu fresh=%llu reused=%llu stale=%llu prepared=%llu "
             "prepareFail=%llu generated=%llu real=%llu",
        static_cast<unsigned long long>(g_onFrameCalls.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_onFrameInputs.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_onFrameReused.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_onFrameStale.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_prepareOk.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_prepareFail.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_generated.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(g_real.load(std::memory_order_relaxed)));
    AnalyzeCaptures();
    return 1;
}

void WrapCaptureNativePresent(void* swapchain)
{
    RecordNativeCaptureBefore(swapchain);
}

void WrapNativeCaptureAfterPresent()
{
    RecordNativeCaptureAfter();
}

void* WrapSwapchain()
{
    return g_wrapped;
}

void WrapShutdown()
{
    // B-3（TASK-R10-05）：一次性闸门。历史缺陷：本函数全仓无调用点；现在有两条
    // 触发路径（托管侧显式 ZFG_FgShutdown / DllMain PROCESS_DETACH 兜底），可能
    // 并发到达——只有第一个进来的人执行，其余直接返回（幂等且无双释放）。
    static volatile LONG s_shutdownGate = 0;
    if (::InterlockedCompareExchange(&s_shutdownGate, 1, 0) != 0) {
        return;
    }
    if (g_fg == nullptr) {
        return;
    }
    g_fg->Shutdown();
    // 关停后 GPU 侧工作已按官方契约刷新；再补一道队列栅栏，保证读回槽位可 Map。
    (void)FlushGameQueue(2000u);
    AnalyzeCaptures();
    delete g_fg;
    g_fg = nullptr;
    g_wrapped = nullptr;
    g_waitable = nullptr;
    g_frameId = 0;
    ReleasePrepObjects();
    // 读回资源（当前槽位 + 退休槽）在这里统一释放（TASK-R10-03 A-4）
    ::AcquireSRWLockExclusive(&g_captureLock);
    ReleaseCaptureBank(g_capture);
    if (g_captureRetiredValid) {
        ReleaseCaptureBank(g_captureRetired);
        g_captureRetiredValid = false;
    }
    g_display.captureValid = false;
    ::ReleaseSRWLockExclusive(&g_captureLock);
    ReleaseNativeCapture();
    g_display.width = 0u;
    g_display.height = 0u;
    ::AcquireSRWLockExclusive(&g_inputLock);
    // A-7：退出前清空输入账目——生效输入与退休队列里的引用全部释放（GPU 已在
    // 上面 FlushGameQueue 追平）。Release 后托管侧持有的引用与其自身生命周期无关。
    g_inputs.depthResource = nullptr;
    g_inputs.mvResource = nullptr;
    g_hasInputs = false;
    for (size_t i = 0; i < g_retiredCount; ++i) {
        ReleaseRetiredInput(g_retired[i]);
    }
    g_retiredCount = 0;
    ::ReleaseSRWLockExclusive(&g_inputLock);
    if (g_queue != nullptr) {
        g_queue->Release();
        g_queue = nullptr;
    }
    if (g_device != nullptr) {
        g_device->Release();
        g_device = nullptr;
    }
}

const char* WrapLastError()
{
    return g_lastError;
}

} // namespace zfg
