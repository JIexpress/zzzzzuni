// =============================================================================
// zfg_present_reentry.h —— Present 重入账目的纯逻辑（TASK-R10-05 B-1）
//
// 历史缺陷（R10-02 B-1）：hook 的 Present/Present1 对嵌套标志只做
//   `if (outerWrapped) { t_inWrappedPresent = true; } … = false;`
// 即"进时置位、出时硬清 false"。FFX 的 Present1 内部会再调一次同一个代理对象的
// Present（1 层嵌套，B-R9-04 实测）；一旦出现 **2 层以上**嵌套（Present1→Present→
// Present，SDK 版本变化或将来接入方式变化都可能触发），内层退出时把标志清成
// false，外层余下的嵌套 Present 就被误判成应用级 → WrapOnFrame 再跑一遍 →
// frameID 双推进 / 一次 Prepare 对两次 Present（B-R9-03 阶段 C "generated≈0" 的
// 同类症状）。
//
// 修复：保存/恢复旧值（`const bool prev = flag; … flag = prev;`）。判定与恢复
// 逻辑收敛在本头文件的纯函数里——产品代码（zfg_probe_hook.cpp）与单测共用，
// 单测直接模拟 Present1→Present→Present 三层序列断言分类与展开；
// "改回硬清 false"即红（变异验证）。
// =============================================================================
#pragma once

namespace zfg {

/// 进入一次被 hook 的 Present/Present1 的判定（调用时 flag = 当前嵌套标志值）
struct PresentReentry {
    bool appLevel = false;   ///< 本次是否应用级呈现（只有它推进 FG 契约：Configure/Prepare）
    bool setNested = false;  ///< 调真实 Present 前是否需要置嵌套标志（代理链才置）
    bool restoreValue = false; ///< 退出时恢复到该值（= 进入前的标志值；B-1 修复点）
};

/// 进入判定（纯函数）：
///   · 应用级 = 本次调用落在**代理链**上、且当前不处于嵌套（外层）；
///   · 代理链的调用要置嵌套标志（FFX 内部转发对它可见）；
///   · 非代理链（FFX 自建的底层链）不置标志——它内部不会再进我们的 hook 判定域。
inline PresentReentry PresentReenter(bool isWrappedChain, bool nestedFlagNow)
{
    PresentReentry r;
    r.appLevel = isWrappedChain && !nestedFlagNow;
    r.setNested = isWrappedChain;
    r.restoreValue = nestedFlagNow; // 保存旧值：内层退出恢复"进入前"的状态
    return r;
}

/// 退出恢复（纯函数）。旧实现在这里硬清 false —— 嵌套 >1 层即失效（B-1）。
inline void PresentReexit(const PresentReentry& r, bool& nestedFlag)
{
    if (r.setNested) {
        nestedFlag = r.restoreValue;
    }
}

} // namespace zfg
