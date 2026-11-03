// =============================================================================
// zfg_probe_resize.h —— 交换链尺寸/采集状态的纯逻辑（TASK-R10-03 A-4）
//
// 为什么抽出来：R10-02 A-4 的缺陷是"ResizeBuffers 之后状态不更新"——
//   · g_displayWidth/Height 全仓只在建链时写过一次（旧尺寸永远不更新）；
//   · g_captureReady 没有重置点（读回槽位仍是旧尺寸）；
//   · WrapOnFrame 的尺寸比较仍拿旧值 → 托管侧提交新尺寸后 enabled=false，
//     插帧**永久静默失效**；
//   · CaptureObserver 按旧尺寸对新后台缓冲 CopyTextureRegion → 窗口变小越界。
//
// 这里只放"状态怎么变"的判定（无 D3D12/DXGI 依赖），产品代码与单测共用：
// 单测直接驱动"1920x1080 → 1280x720"这条窗口变小路径，断言尺寸已更新、
// 采集需失效、旧尺寸输入被拒（FG 安全停用）而新尺寸输入被接受（FG 恢复）。
// 真正动资源的部分仍在 zfg_probe_wrap.cpp（ReleaseCaptureBank 等）。
// =============================================================================
#pragma once

namespace zfg {

/// 显示（交换链）侧状态：尺寸 + 读回槽位是否可用 + 已消费的输入戳
struct DisplayState {
    unsigned width = 0u;
    unsigned height = 0u;
    bool captureValid = false;             // 读回槽位是否按当前尺寸建好
    unsigned long long consumedStamp = 0u; // 已消费的输入戳（复用额度判定）
    unsigned long long resizeSerial = 0u;  // 尺寸变更次数（日志/取证）
};

/// 一次 ResizeBuffers 的结论（调用方据此决定要做什么）
struct ResizeOutcome {
    bool accepted = false;         // false = 新尺寸未知（DXGI 的 0,0 语义），尺寸保持不变
    bool sizeChanged = false;      // 显示尺寸真的变了
    bool captureInvalidated = false; // 读回槽位必须作废（旧尺寸/未知尺寸）
    bool inputsInvalidated = true;   // 旧输入不得再被复用（复用额度清零）
    unsigned oldWidth = 0u;
    unsigned oldHeight = 0u;
};

/// 交换链重建后的状态推进。
///
/// `latestInputStamp` 是调用时刻托管侧最新提交的输入戳：把它记成"已消费"，下一次
/// 呈现就必须拿到**新提交**才允许插帧（旧尺寸的 depth/MV 不会被当成有效输入）。
///
/// DXGI 语义：`ResizeBuffers(..., 0, 0, ...)` 表示"沿用当前客户区尺寸"，实际值
/// 未知 → accepted=false，尺寸保持原值（宁可继续用旧值，也不写一个假尺寸），但
/// 采集照样作废、输入照样失效——保守方向（安全停用）优先。
inline ResizeOutcome ApplySwapchainResize(DisplayState& state, unsigned long long latestInputStamp,
    unsigned width, unsigned height)
{
    ResizeOutcome out {};
    out.oldWidth = state.width;
    out.oldHeight = state.height;
    out.captureInvalidated = state.captureValid;
    state.captureValid = false;
    state.consumedStamp = latestInputStamp;
    out.inputsInvalidated = true;

    if (width == 0u || height == 0u) {
        out.accepted = false;
        out.sizeChanged = false;
        return out;
    }
    out.accepted = true;
    out.sizeChanged = (width != state.width) || (height != state.height);
    state.width = width;
    state.height = height;
    if (out.sizeChanged) {
        state.resizeSerial += 1u;
    }
    return out;
}

/// 本帧提交的 depth/MV 尺寸是否与当前显示尺寸一致（不一致 = 不能用这帧输入插帧）
inline bool FrameInputMatchesDisplay(const DisplayState& state, unsigned inputWidth,
    unsigned inputHeight)
{
    return state.width != 0u && state.height != 0u && inputWidth == state.width
        && inputHeight == state.height;
}

/// 拷贝/读回时取"源与目的都能容纳"的尺寸（R10-02 A-4 的越界根因：box 用了旧显示
/// 尺寸）。任一参数为 0 时返回 0（调用方应放弃本次拷贝）。
inline unsigned ClampCopyExtent(unsigned requested, unsigned available)
{
    if (requested == 0u || available == 0u) {
        return 0u;
    }
    return (requested < available) ? requested : available;
}

} // namespace zfg
