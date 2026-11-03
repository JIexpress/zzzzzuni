// =============================================================================
// zfg_probe_wrap.h —— FFX 代理交换链（TASK-B-R9-01 spike；TASK-B-R9-03 扩为
// 每帧 FG 契约：Configure(frameID 严格 +1) → PrepareV2 → Present）
//
// 只在 ZFG_HOOK=wrap|fg 时启用：hook 拦截 Unity 的 CreateSwapChainForHwnd，
// 让 FFX 自建底层交换链并包装，把包装对象返回给 Unity。任何一步失败都返回
// false，调用方必须回退原生 path（failsafe）。
//
// 两种模式：
//   wrap = 代理链直通（frameGenerationEnabled=false），用于验证"宿主能在代理链上
//          正常跑且画面正确"；
//   fg   = 真插帧（frameGenerationEnabled=true + 每帧 PrepareV2，输入由托管侧
//          经 ZFG_FgSubmitInputs 每帧提交）。
// =============================================================================
#pragma once

#include <cstdint>

#include "zfgprobe/zfg_fg_input.h"

namespace zfg {

/// 用 FFX 代理交换链替换 Unity 请求的交换链。成功返回 true 并写 *outWrapped。
/// 失败返回 false（调用方回退原生创建），原因见 WrapLastError()。
/// A-3（TASK-R10-05）：`requestedDesc1`/`requestedFullscreenDesc1` 是宿主原始
/// desc（DXGI_SWAP_CHAIN_DESC1* / DXGI_SWAP_CHAIN_FULLSCREEN_DESC1*，以 const void*
/// 携带、可空）。非空时先按宿主 desc 建链（只强制 FG 必需位 FRAME_LATENCY_WAITABLE_
/// OBJECT），FFX 拒绝再回退自造 desc（不回归 B-R9-04 真机链路），并打对账日志。
bool WrapCreateSwapChainForHwnd(void* dxgiFactory, void* gameQueue, void* hwnd,
    unsigned width, unsigned height, unsigned dxgiFormat, unsigned bufferCount,
    void** outWrapped, const void* requestedDesc1 = nullptr,
    const void* requestedFullscreenDesc1 = nullptr);

/// 每帧（Present 前）同步：Configure(frameId) →（wantFrameGen 且输入有效时）
/// PrepareV2 派发。返回 false 表示本帧 FG 契约未走通（宿主照常 Present）。
bool WrapOnFrame(void* wrapped, bool wantFrameGen);

/// 原生（非代理）链创建时登记显示尺寸（TASK-R10-03 A-4）：让"显示尺寸"这条事实
/// 在 log 模式与 wrap 模式都成立，ResizeBuffers 的比较基准才不是 0x0。
void WrapNoteSwapchainSize(unsigned width, unsigned height);

/// 交换链重建后推进显示状态（TASK-R10-03 A-4）：更新尺寸 → 作废读回槽位 →
/// 作废输入复用额度（下一次呈现必须拿到新提交才允许插帧）。
/// `width/height` 允许为 0（DXGI 的"沿用当前客户区"语义）→ 尺寸保持原值但采集照样作废。
void WrapOnSwapchainResized(unsigned width, unsigned height);

/// 托管侧每帧提交的输入（拷贝存本模块；返回 false = 参数非法）
bool WrapSubmitInputs(const ZfgFgFrameInputs* inputs);

/// 统计读数（原子；返回 true 表示代理链已建立）
bool WrapGetStats(ZfgFgStats* out);

/// 运行期开关（0 = 不插帧；1 = 插帧）；返回 1 = 已受理
int WrapSetFrameGenEnabled(int enabled);

/// 托管侧是否显式要求插帧（= 最近一次 WrapSetFrameGenEnabled(1)）。
/// 供 hook 判定 wrap 模式下"用户把开关拨到开"时的 wantFrameGen；
/// 默认（-1，跟随模式）返回 false，wrap 模式仍是纯直通。
bool WrapFrameGenAuthorized();

/// 把已抓到的呈现帧（真帧/插帧）GPU 追平后读回并打统计；返回 1 = 已执行。
/// 探针在退出前调用（进程退出路径上没有可靠的关停时机）。
int WrapAnalyzeCaptures();

// -----------------------------------------------------------------------------
// 读回银行（B-6 / TASK-R10-07）：创建接缝 + 失败计数 + 启用入口
// -----------------------------------------------------------------------------

/// 一次建 slotCount 个读回资源的接缝（生产默认实现走 D3D12 设备）。
/// 契约：成功 → outResources[0..slotCount-1] 全部非空、*outBytes 给每槽字节数；
/// 失败 → *failedSlot 给失败槽号（-1 = 设备/几何层失败），且 0..failedSlot-1 已
/// 写入 outResources（由调用方统一释放）。**存在意义**：`CreateCommittedResource`
/// 只在显存不足时失败，没有注入点就无法在测试里执行失败路径。
using ZfgCaptureBuildFn = bool (*)(void* user, int slotCount, unsigned width, unsigned height,
    void** outResources, uint64_t* outBytes, int* failedSlot);

struct ZfgCaptureSeam {
    void* user = nullptr;
    ZfgCaptureBuildFn build = nullptr;  ///< 资源创建（nullptr = 用默认设备实现）
    void (*release)(void* user, void* resource) = nullptr; ///< 释放（nullptr = Release()）
};

/// 注入读回创建接缝（nullptr = 恢复默认）。测试/自检专用：只替换"资源从哪来/怎么
/// 释放"，不改变采集策略。生产路径必须保持默认实现。
void WrapSetCaptureSeam(const ZfgCaptureSeam* seam);

/// 读回银行诊断（B-6）：失败计数 + 槽位占用（"不留半就绪"的判据）
struct ZfgCaptureDiag {
    uint64_t allocFailures = 0; ///< 整组构建失败次数（每次失败 +1）
    int liveSlots = 0;          ///< 当前槽位中非空资源数（0..kCaptureSlots）
    bool captureValid = false;  ///< 银行是否整组就绪
};
bool WrapGetCaptureDiag(ZfgCaptureDiag* out);

/// 读回银行启用（幂等，等价于 present 回调内部那次 EnsureCapture）。
/// 生产由 CaptureObserver 调用；导出是为了让自检/单测能直接驱动构建与失败路径。
/// 返回 false = 未就绪（尺寸/设备缺失，或整组构建失败——见 ZfgCaptureDiag）。
bool WrapEnsureCaptureBank();

/// 底层链（FFX 自建、真正呈现到屏幕的那条）Present 前的像素读回采样。
/// 只对**非代理链**调用；采样 stride 由实现决定，任何一步失败即永久停用，
/// 绝不影响呈现（ZFG_NATIVE_CAPTURE=0 可整体关闭）。
void WrapCaptureNativePresent(void* swapchain);

/// 与 WrapCaptureNativePresent 配对：Present **返回后**调用，真正记录拷贝命令
/// （Present 前只选定后台缓冲下标；内容在 Present 返回时才确定）。
void WrapNativeCaptureAfterPresent();

/// 当前被替换的代理交换链（无替换返回 nullptr）
void* WrapSwapchain();

/// 关停（销毁 FG 上下文与交换链上下文；幂等）
void WrapShutdown();

/// 最后一次失败原因（诊断用；未失败为空串）
const char* WrapLastError();

} // namespace zfg
