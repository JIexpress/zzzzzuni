// =============================================================================
// test_zfg_input_policy.cpp —— FG 每帧输入的消费账目与引用退休（TASK-R10-05 A-7）
//
// 历史缺陷（R10-02 A-7）：托管侧提交的 depth/MV 是裸 ID3D12Resource*，wrap 只做
// memcpy，还允许同一份输入被复用 2 次——托管侧销毁重建纹理后，复用窗口内的
// PrepareV2 引用已释放资源（设备移除/花屏）。
//
// 修复的两层判定都在 zfgprobe/zfg_fg_input_policy.h（产品 zfg_probe_wrap.cpp 即用
// 这份）：
//   · ConsumeFrameInput：fresh/reuse/stale 三分账目（语义与 B-R9-04 逐位一致）；
//   · RetiredInputReleasable：被替换输入的引用何时可安全 Release（等 fence 追平）。
// 用例（简报验收点：reuse 分支 + 资源释放序列不再引用已释放指针）：
//   1. fresh → reuse×2 → stale 的完整额度序列；
//   2. stale 帧不引用任何输入（verdict=Stale，wrap 侧不取 g_inputs）；
//   3. 释放序列：fence 未追平 → 不得释放；追平（含恰好等于）→ 必须释放；
//      无 fence（从未派发过 PrepareV2）→ 立即可释放。
// 变异验证：把 kMaxInputReuse 改回旧实现语义（fresh 不重置额度 / 或把 Reuse 分支
// 删掉）→ 额度序列用例红；把 RetiredInputReleasable 改成"不等 fence"→ 释放序列
// 用例红。
// =============================================================================
#include "doctest/doctest.h"

#include "zfgprobe/zfg_fg_input_policy.h"

TEST_CASE("zfg input: fresh -> reuse -> reuse -> stale quota sequence (A-7 reuse branch)")
{
    zfg::FgInputAccount account {};
    // 从未提交过 → None（不引用任何输入）
    CHECK(zfg::ConsumeFrameInput(account, 0u).verdict == zfg::FgInputVerdict::None);

    // 托管侧已提交 #1（stamp 1）→ Fresh，复用额度重置
    account.hasInputs = true;
    zfg::FgInputConsumption c = zfg::ConsumeFrameInput(account, 1u);
    CHECK(c.verdict == zfg::FgInputVerdict::Fresh);
    CHECK(c.consumedStamp == 1u);
    CHECK(c.reuseLeft == zfg::kMaxInputReuse);
    account.consumedStamp = c.consumedStamp;
    account.hasInputs = true;
    account.reuseLeft = c.reuseLeft;

    // 同一渲染帧的重复呈现（stamp 不动）→ Reuse，额度递减（A-7 后安全：资源被
    // 本模块 AddRef 持有）
    c = zfg::ConsumeFrameInput(account, 1u);
    CHECK(c.verdict == zfg::FgInputVerdict::Reuse);
    CHECK(c.reuseLeft == zfg::kMaxInputReuse - 1);
    account.reuseLeft = c.reuseLeft;

    c = zfg::ConsumeFrameInput(account, 1u);
    CHECK(c.verdict == zfg::FgInputVerdict::Reuse);
    CHECK(c.reuseLeft == 0);
    account.reuseLeft = c.reuseLeft;

    // 额度耗尽 → Stale（本帧不插帧：wrap 侧按"无输入"处理，不引用 g_inputs）
    c = zfg::ConsumeFrameInput(account, 1u);
    CHECK(c.verdict == zfg::FgInputVerdict::Stale);
    CHECK(c.reuseLeft == 0);
}

TEST_CASE("zfg input: a new submit ends the reuse window (stale inputs are dropped)")
{
    zfg::FgInputAccount account {true, 5u, 0}; // 旧输入已耗尽复用额度
    // 托管侧新提交（stamp 6）→ Fresh（不是 Stale）——新渲染帧重新可插帧
    const zfg::FgInputConsumption c = zfg::ConsumeFrameInput(account, 6u);
    CHECK(c.verdict == zfg::FgInputVerdict::Fresh);
    CHECK(c.consumedStamp == 6u);
    CHECK(c.reuseLeft == zfg::kMaxInputReuse);
}

TEST_CASE("zfg input: stale verdict must not read g_inputs (wrap drops the frame)")
{
    // Stale 的语义契约：verdict != Fresh/Reuse 时 wrap 侧不取 g_inputs（简报验收
    // 点"不再引用已释放指针"的账目面）。这里把判定矩阵钉死。
    const zfg::FgInputAccount exhausted {true, 9u, 0};
    CHECK(zfg::ConsumeFrameInput(exhausted, 9u).verdict == zfg::FgInputVerdict::Stale);
    const zfg::FgInputAccount never {};
    CHECK(zfg::ConsumeFrameInput(never, 0u).verdict == zfg::FgInputVerdict::None);
    CHECK(zfg::ConsumeFrameInput(never, 1u).verdict == zfg::FgInputVerdict::None); // 未提交恒 None
    // 已提交但未消费（stamp 未推进）→ Fresh（只有 Fresh/Reuse 才允许引用输入）
    const zfg::FgInputAccount submitted {true, 0u, 0};
    CHECK(zfg::ConsumeFrameInput(submitted, 1u).verdict == zfg::FgInputVerdict::Fresh);
}

TEST_CASE("zfg input: retired input is releasable only after the fence catches up (A-7 release sequence)")
{
    // 退休时快照 fence=10：使用这份输入的所有 PrepareV2 已在 10 号 signal 前派发
    // → fence 追平 10 才能安全 Release。
    CHECK_FALSE(zfg::RetiredInputReleasable(10u, true, 9u)); // GPU 未追平 → 不许释放
    CHECK(zfg::RetiredInputReleasable(10u, true, 10u));      // 恰好追平 → 可释放
    CHECK(zfg::RetiredInputReleasable(10u, true, 11u));      // 已越过 → 可释放
    // 从未派发过 PrepareV2（无 fence）→ 立即可释放（提交后从未进 GPU）
    CHECK(zfg::RetiredInputReleasable(0u, false, 0u));
    CHECK(zfg::RetiredInputReleasable(42u, false, 0u));
}

TEST_CASE("zfg input: monotonic stamps keep the accounting consistent")
{
    zfg::FgInputAccount account {};
    account.hasInputs = true; // 托管侧在持续提交
    unsigned long long stamp = 0u;
    for (int frame = 0; frame < 5; ++frame) {
        ++stamp; // 托管侧每渲染帧提交一次
        const zfg::FgInputConsumption c = zfg::ConsumeFrameInput(account, stamp);
        CHECK(c.verdict == zfg::FgInputVerdict::Fresh);
        account.consumedStamp = c.consumedStamp;
        account.hasInputs = true;
        account.reuseLeft = c.reuseLeft;
        // 本帧的第二次呈现（B-R9-04 实测的"同一毫秒两次 Present1"）→ Reuse
        const zfg::FgInputConsumption r = zfg::ConsumeFrameInput(account, stamp);
        CHECK(r.verdict == zfg::FgInputVerdict::Reuse);
        account.reuseLeft = r.reuseLeft;
        // 本帧的第三次呈现 → 额度还有 1
        const zfg::FgInputConsumption r2 = zfg::ConsumeFrameInput(account, stamp);
        CHECK(r2.verdict == zfg::FgInputVerdict::Reuse);
        account.reuseLeft = r2.reuseLeft;
        // 第四次 → Stale
        CHECK(zfg::ConsumeFrameInput(account, stamp).verdict == zfg::FgInputVerdict::Stale);
    }
}
