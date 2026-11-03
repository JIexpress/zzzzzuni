// =============================================================================
// test_zfg_resize.cpp —— 交换链重建（窗口变小）路径的状态机（TASK-R10-03 A-4）
//
// R10-02 A-4 的缺陷链：`g_displayWidth/Height` 全仓只在建链时写一次、
// `g_captureReady` 没有重置点、ResizeBuffers 只转发 → ① 托管侧提交新尺寸后
// 被判"尺寸不符"，插帧**永久静默失效**；② CaptureObserver 按旧尺寸对新后台
// 缓冲 CopyTextureRegion → 窗口变小即越界。
//
// 本用例直接驱动产品代码用的同一份纯逻辑（zfgprobe/zfg_probe_resize.h），
// 复现"1920x1080 → 1280x720（窗口变小）"这条路径，断言：
//   · 显示尺寸确实被更新（FG 的尺寸比较基准不再是旧值）
//   · 采集银行被作废（读回资源必须按新尺寸重建）
//   · 旧尺寸的 depth/MV 被拒（FG 安全停用），新尺寸输入被接受（FG 恢复）
//   · 拷贝范围按"源与目的取小"钳制（越界根因的结构性堵法）
// =============================================================================
#include "doctest/doctest.h"

#include "zfgprobe/zfg_probe_resize.h"
#include "zfgprobe/zfg_probe_wrap.h"

using zfg::ApplySwapchainResize;
using zfg::ClampCopyExtent;
using zfg::DisplayState;
using zfg::FrameInputMatchesDisplay;
using zfg::ResizeOutcome;

namespace {

/// 建链后的初始状态：1920x1080，读回银行已按该尺寸建好，已消费到第 7 次提交
DisplayState MakeReady1080p()
{
    DisplayState st {};
    st.width = 1920u;
    st.height = 1080u;
    st.captureValid = true;
    st.consumedStamp = 7u;
    st.resizeSerial = 0u;
    return st;
}

} // namespace

TEST_CASE("zfg resize: 窗口变小 1920x1080 -> 1280x720（A-4 的主路径）")
{
    DisplayState st = MakeReady1080p();
    const ResizeOutcome out = ApplySwapchainResize(st, 9u, 1280u, 720u);

    CHECK(out.accepted);
    CHECK(out.sizeChanged);
    CHECK(out.oldWidth == 1920u);
    CHECK(out.oldHeight == 1080u);
    // ① 尺寸必须被更新（历史缺陷：永远停在建链时的值）
    CHECK(st.width == 1280u);
    CHECK(st.height == 720u);
    // ② 读回银行必须作废（旧尺寸槽位不得继续用于新后台缓冲）
    CHECK(out.captureInvalidated);
    CHECK_FALSE(st.captureValid);
    // ③ 旧输入不得复用：已消费戳推到最新提交（9）
    CHECK(out.inputsInvalidated);
    CHECK(st.consumedStamp == 9u);
    CHECK(st.resizeSerial == 1u);
}

TEST_CASE("zfg resize: 窗口变大 1280x720 -> 1920x1080")
{
    DisplayState st {};
    st.width = 1280u;
    st.height = 720u;
    st.captureValid = true;
    const ResizeOutcome out = ApplySwapchainResize(st, 3u, 1920u, 1080u);
    CHECK(out.accepted);
    CHECK(out.sizeChanged);
    CHECK(st.width == 1920u);
    CHECK(st.height == 1080u);
    CHECK(out.captureInvalidated);
    CHECK_FALSE(st.captureValid);
    CHECK(st.resizeSerial == 1u);
}

TEST_CASE("zfg resize: 同尺寸重建 —— 采集仍作废，但不算尺寸变更")
{
    DisplayState st = MakeReady1080p();
    const ResizeOutcome out = ApplySwapchainResize(st, 11u, 1920u, 1080u);
    CHECK(out.accepted);
    CHECK_FALSE(out.sizeChanged);          // 尺寸没变
    CHECK(out.captureInvalidated);         // 但后台缓冲确实重建过 → 采集必须重来
    CHECK_FALSE(st.captureValid);
    CHECK(st.resizeSerial == 0u);          // 不计一次尺寸变更
    CHECK(st.consumedStamp == 11u);
}

TEST_CASE("zfg resize: DXGI 的 (0,0) 语义 —— 尺寸未知则保持原值，采集照样作废")
{
    DisplayState st = MakeReady1080p();
    const ResizeOutcome out = ApplySwapchainResize(st, 5u, 0u, 0u);
    CHECK_FALSE(out.accepted);   // 实际尺寸未知，不能编一个
    CHECK_FALSE(out.sizeChanged);
    CHECK(st.width == 1920u);    // 保持原值
    CHECK(st.height == 1080u);
    CHECK(out.captureInvalidated); // 保守方向：安全停用优先
    CHECK_FALSE(st.captureValid);
    CHECK(st.consumedStamp == 5u);
    CHECK(st.resizeSerial == 0u);
}

TEST_CASE("zfg resize: 窗口变小后旧尺寸输入被拒、新尺寸输入被接受（FG 停用→恢复）")
{
    DisplayState st = MakeReady1080p();
    // 重建前：1920x1080 的输入是合法的
    CHECK(FrameInputMatchesDisplay(st, 1920u, 1080u));

    const ResizeOutcome out = ApplySwapchainResize(st, 20u, 1280u, 720u);
    REQUIRE(out.sizeChanged);

    // 重建后：托管侧还按旧尺寸提交 → 必须被拒（FG 本帧安全停用，而不是拿错尺寸插帧）
    CHECK_FALSE(FrameInputMatchesDisplay(st, 1920u, 1080u));
    // 托管侧按新尺寸重新提交 → 恢复插帧
    CHECK(FrameInputMatchesDisplay(st, 1280u, 720u));
    // 未建链（0x0）时一律不匹配（首帧保护）
    const DisplayState empty {};
    CHECK_FALSE(FrameInputMatchesDisplay(empty, 0u, 0u));
    CHECK_FALSE(FrameInputMatchesDisplay(empty, 1280u, 720u));
}

TEST_CASE("zfg resize: 拷贝范围按源与目的取小（越界根因的结构性堵法）")
{
    // 窗口变小：显示/槽位仍是旧的 1920x1080，源资源已缩到 1280x720 → 只能拷 1280x720
    CHECK(ClampCopyExtent(1920u, 1280u) == 1280u);
    CHECK(ClampCopyExtent(1080u, 720u) == 720u);
    // 窗口变大：源更大，槽位容量是瓶颈
    CHECK(ClampCopyExtent(1920u, 1280u) == 1280u);
    // 相等
    CHECK(ClampCopyExtent(1280u, 1280u) == 1280u);
    // 任一为 0 → 放弃拷贝（不得把 0 当成"全尺寸"）
    CHECK(ClampCopyExtent(0u, 1280u) == 0u);
    CHECK(ClampCopyExtent(1280u, 0u) == 0u);
}

TEST_CASE("zfg resize: 连续 resize 的 serial 与尺寸状态")
{
    DisplayState st = MakeReady1080p();
    (void)ApplySwapchainResize(st, 1u, 1280u, 720u);
    CHECK(st.resizeSerial == 1u);
    (void)ApplySwapchainResize(st, 2u, 1280u, 720u); // 同尺寸重建
    CHECK(st.resizeSerial == 1u);
    (void)ApplySwapchainResize(st, 3u, 1600u, 900u);
    CHECK(st.resizeSerial == 2u);
    CHECK(st.width == 1600u);
    CHECK(st.height == 900u);
    // 每次重建都要求"新提交"才算有效输入
    CHECK(st.consumedStamp == 3u);
    CHECK_FALSE(st.captureValid);
}

// =============================================================================
// B-6（TASK-R10-07）：EnsureCapture 中途失败的清理与"重试不放大"
//
// 真机上 CreateCommittedResource 只在显存不足时失败，所以这里走 zfg_probe_wrap.h
// 暴露的**可注入接缝**（ZfgCaptureSeam）让第 N 个槽位创建失败，验证：
//   · 已建的 0..N-1 被释放（建几个释放几个）
//   · g_capture 不留半就绪（liveSlots == 0 且 captureValid == false）
//   · 失败计数 +1；重试只建 kCaptureSlots 个（不叠加成 2N）
// =============================================================================
namespace {

constexpr int kTestCaptureSlots = 6; // = 产品侧 kCaptureSlots

struct FakeCaptureBank {
    int created = 0;
    int released = 0;
    int failAtSlot = -1; // <0 = 全部成功；否则该槽位创建失败（模拟 E_OUTOFMEMORY）
    int lastSlotCount = 0;
    unsigned char body[kTestCaptureSlots][4] = {};
};

FakeCaptureBank g_fakeBank;

bool FakeBuild(void* user, int slotCount, unsigned width, unsigned height, void** outResources,
    uint64_t* outBytes, int* failedSlot)
{
    auto* bank = static_cast<FakeCaptureBank*>(user);
    bank->lastSlotCount = slotCount;
    *outBytes = 4096u;
    (void)width;
    (void)height;
    for (int i = 0; i < slotCount; ++i) {
        if (bank->failAtSlot == i) {
            *failedSlot = i;
            return false;
        }
        outResources[i] = bank->body[i];
        ++bank->created;
    }
    *failedSlot = -1;
    return true;
}

void FakeRelease(void* user, void* resource)
{
    auto* bank = static_cast<FakeCaptureBank*>(user);
    (void)resource;
    ++bank->released;
}

} // namespace

TEST_CASE("zfg capture bank: 中途失败释放已建槽位、重试不放大（B-6）")
{
    g_fakeBank = FakeCaptureBank {};
    zfg::ZfgCaptureSeam seam;
    seam.user = &g_fakeBank;
    seam.build = &FakeBuild;
    seam.release = &FakeRelease;
    zfg::WrapSetCaptureSeam(&seam);
    zfg::WrapNoteSwapchainSize(64u, 64u);

    // ① 第 4 个槽位失败（此前 4 个已建成）
    g_fakeBank.failAtSlot = 4;
    CHECK_FALSE(zfg::WrapEnsureCaptureBank());
    zfg::ZfgCaptureDiag diag {};
    REQUIRE(zfg::WrapGetCaptureDiag(&diag));
    CHECK(g_fakeBank.created == 4);
    CHECK(g_fakeBank.released == 4); // 0..3 全部释放
    CHECK(diag.liveSlots == 0);      // 不留半就绪
    CHECK_FALSE(diag.captureValid);
    CHECK(diag.allocFailures == 1u);
    CHECK(g_fakeBank.lastSlotCount == kTestCaptureSlots);

    // ② 重试（更早的槽位失败）：本次只建 2 个，不是"上一轮 4 个 + 本轮 2 个"
    const int createdBeforeRetry = g_fakeBank.created;
    g_fakeBank.failAtSlot = 2;
    CHECK_FALSE(zfg::WrapEnsureCaptureBank());
    CHECK(g_fakeBank.created - createdBeforeRetry == 2);
    CHECK(g_fakeBank.released == g_fakeBank.created); // 建多少释放多少，无累积
    REQUIRE(zfg::WrapGetCaptureDiag(&diag));
    CHECK(diag.liveSlots == 0);
    CHECK(diag.allocFailures == 2u);

    // ③ 放行：恰建 kCaptureSlots 个，并整组置就绪
    const int createdBeforeSuccess = g_fakeBank.created;
    g_fakeBank.failAtSlot = -1;
    CHECK(zfg::WrapEnsureCaptureBank());
    CHECK(g_fakeBank.created - createdBeforeSuccess == kTestCaptureSlots);
    REQUIRE(zfg::WrapGetCaptureDiag(&diag));
    CHECK(diag.liveSlots == kTestCaptureSlots);
    CHECK(diag.captureValid);
    // 幂等：已就绪时不再重建
    CHECK(zfg::WrapEnsureCaptureBank());
    CHECK(g_fakeBank.created - createdBeforeSuccess == kTestCaptureSlots);

    // 清场（同时验证"退休槽在下一次构建入口被释放"仍走同一接缝）
    zfg::WrapOnSwapchainResized(32u, 32u);
    g_fakeBank.failAtSlot = 0;
    CHECK_FALSE(zfg::WrapEnsureCaptureBank());
    CHECK(g_fakeBank.released == g_fakeBank.created);
    REQUIRE(zfg::WrapGetCaptureDiag(&diag));
    CHECK(diag.liveSlots == 0);
    CHECK_FALSE(diag.captureValid);
    zfg::WrapSetCaptureSeam(nullptr);
}
