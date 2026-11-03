// =============================================================================
// zfg_fg_input_policy.h —— FG 每帧输入的消费账目 + 引用退休判定（TASK-R10-05 A-7）
//
// 历史缺陷（R10-02 A-7）：托管侧每帧提交的 depth/MV 是裸 ID3D12Resource*，
// WrapSubmitInputs 只做 memcpy；WrapOnFrame 还允许同一份输入被连续呈现复用
// kMaxInputReuse 次。托管侧某帧没提交而宿主继续 Present 时，若 Unity 已因切
// 画质档/切分辨率销毁重建了 depth/MV 纹理，PrepareV2 拿到的就是已释放资源 →
// 设备移除/花屏。
//
// 修复（两层，都在产品代码里生效；本头文件是可单测的判定部分）：
//   1. **引用配对**：WrapSubmitInputs 对两个资源 AddRef；被新提交覆盖/关停时
//      不直接 Release，而是进"退休队列"，等游戏队列 fence 追平（最后引用它的
//      PrepareV2 已在 GPU 完成）再 Release——悬垂在结构上不可能，而不仅是
//      概率变小。
//   2. **消费账目**：fresh（本帧新提交）/ reuse（同渲染帧的重复呈现，额度内）/
//      stale（额度耗尽或无输入）三分判定收敛为纯函数，产品与单测共用。
//
// 线程口径：账目结构只在持有 zfg_probe_wrap.cpp 的 g_inputLock 时读写；
// fence 判定允许读到略旧的 completed 值（只会让 Release 晚一点，不会早放）。
// =============================================================================
#pragma once

#include <cstdint>

namespace zfg {

/// 同一份输入最多被几次连续呈现复用（宿主一次渲染可能呈现多次；B-R9-04 实测
/// "同一毫秒内两次 Present1"）。A-7 后复用是安全的：输入资源被本模块 AddRef 持有，
/// 不会被托管侧释放。
constexpr int kMaxInputReuse = 2;

/// 消费判定结论
enum class FgInputVerdict {
    None = 0,  ///< 从未收到过合法输入
    Fresh = 1, ///< 本帧新提交（stamp 前进）→ 直接使用，复用额度重置
    Reuse = 2, ///< 同一渲染帧的重复呈现（额度内）→ 复用旧输入
    Stale = 3, ///< 复用额度耗尽 → 本帧不插帧（不引用任何输入）
};

/// 账目状态（wrap 侧全局状态的镜像；字段含义见 zfg_probe_wrap.cpp）
struct FgInputAccount {
    bool hasInputs = false;
    unsigned long long consumedStamp = 0;
    int reuseLeft = 0;
};

/// 一次消费的结论（含账目推进后的新值）
struct FgInputConsumption {
    FgInputVerdict verdict = FgInputVerdict::None;
    unsigned long long consumedStamp = 0; ///< 账目推进后的已消费戳
    int reuseLeft = 0;                    ///< 账目推进后的复用余额
};

/// 消费判定（纯函数；newestStamp = 调用时刻托管侧最新提交的输入戳）。
/// 语义与 B-R9-04 的实现逐位一致（fresh 重置额度 → reuse 递减 → stale），
/// A-7 只把"引用谁持有"改成显式配对，不改消费节奏。
inline FgInputConsumption ConsumeFrameInput(const FgInputAccount& account,
    unsigned long long newestStamp)
{
    FgInputConsumption r;
    r.consumedStamp = account.consumedStamp;
    r.reuseLeft = account.reuseLeft;
    if (!account.hasInputs) {
        r.verdict = FgInputVerdict::None;
        return r;
    }
    if (newestStamp != account.consumedStamp) {
        r.verdict = FgInputVerdict::Fresh;
        r.consumedStamp = newestStamp;
        r.reuseLeft = kMaxInputReuse;
        return r;
    }
    if (account.reuseLeft > 0) {
        r.verdict = FgInputVerdict::Reuse;
        r.reuseLeft = account.reuseLeft - 1;
        return r;
    }
    r.verdict = FgInputVerdict::Stale;
    return r;
}

/// 退休引用是否已可安全 Release（纯函数）：
///   · 无 fence（从未派发过 PrepareV2）→ 立即可释放；
///   · 否则等游戏队列 fence 追平退休时记录的值——比它早的所有派发（含使用这
///     份输入的那次 PrepareV2）都已在 GPU 完成，命令表不再引用该资源。
/// completed 读到略旧值只会推迟释放（保守方向正确）。
inline bool RetiredInputReleasable(
    unsigned long long retiredFenceValue, bool fenceValid, unsigned long long completedValue)
{
    if (!fenceValid) {
        return true;
    }
    return completedValue >= retiredFenceValue;
}

} // namespace zfg
