// =============================================================================
// test_zfg_present_reentry.cpp —— Present 嵌套重入标志的保存/恢复（TASK-R10-05 B-1）
//
// 历史缺陷（R10-02 B-1）：hook 对嵌套标志只做"进时置位、出时硬清 false"。
// FFX 的 Present1 内部会再调同一个代理对象的 Present（1 层嵌套）；一旦出现
// Present1→Present→Present 的 2 层嵌套，内层退出把标志清成 false，外层余下的
// 嵌套 Present 被当成应用级 → WrapOnFrame 双跑 → frameID 双推进。
//
// 用例直接驱动产品同一份判定（zfgprobe/zfg_present_reentry.h，hook 内即用它），
// 模拟"应用 Present1 → 内部 Present → 更深 Present"的完整序列：
//   · 只有最外层一次被判应用级（frameID 单推进的判定面）；
//   · 每层退出恢复"进入前"的标志值（旧实现硬清 false 的位置）；
//   · 最外层退出后标志归 false，下一次应用 Present 仍是应用级。
// 变异验证：把 PresentReexit 改回 `nestedFlag = false`（旧值丢弃）→
// "三层序列"用例红——证明用例有牙。
// =============================================================================
#include "doctest/doctest.h"

#include "zfgprobe/zfg_present_reentry.h"

namespace {

/// 模拟一次被 hook 的 Present/Present1（与 hook 内的用法逐行同构）
bool SimulatePresent(bool isWrappedChain, bool& nestedFlag, int& appLevelCount)
{
    const zfg::PresentReentry re = zfg::PresentReenter(isWrappedChain, nestedFlag);
    if (re.appLevel) {
        ++appLevelCount; // = WrapOnFrame（frameID +1）的判定面
    }
    if (re.setNested) {
        nestedFlag = true; // 调真实 Present 前置位
    }
    // ……真实 Present 在这里执行；FFX 内部再进来的 Present 会看到置位后的标志……
    zfg::PresentReexit(re, nestedFlag);
    return re.appLevel;
}

} // namespace

TEST_CASE("zfg reentry: single application present (1 layer, B-R9-04 observed shape)")
{
    bool flag = false;
    int app = 0;
    CHECK(SimulatePresent(true, flag, app));
    CHECK(flag == false); // 退出后复位
    CHECK(app == 1);
}

TEST_CASE("zfg reentry: nested present inside Present1 (1 layer nesting)")
{
    bool flag = false;
    int app = 0;
    // 外层：应用级 Present1 —— reentry 记录在**进入时**捕获，外层尚未退出
    const zfg::PresentReentry outer = zfg::PresentReenter(true, flag);
    CHECK(outer.appLevel);
    ++app; // = WrapOnFrame（frameID +1）
    if (outer.setNested) {
        flag = true;
    }
    // 内层：FFX 内部转发的 Present（外层仍在执行 → 标志为 true）
    CHECK_FALSE(SimulatePresent(true, flag, app));
    CHECK(flag == true); // 内层退出**不得**清掉外层的标志（旧实现硬清 false）
    CHECK(app == 1);
    // 外层收尾：恢复"进入前"的值（false）
    zfg::PresentReexit(outer, flag);
    CHECK(flag == false);
    CHECK(app == 1); // frameID 单推进
}

TEST_CASE("zfg reentry: 3-layer nesting (Present1 -> Present -> Present) — B-1 regression")
{
    bool flag = false;
    int app = 0;

    // 第 1 层（应用级 Present1）：进入，持有外层记录
    const zfg::PresentReentry outer = zfg::PresentReenter(true, flag);
    CHECK(outer.appLevel);
    ++app;
    if (outer.setNested) {
        flag = true;
    }
    // 第 2 层（FFX 内部 Present）
    CHECK_FALSE(SimulatePresent(true, flag, app));
    // 第 3 层（更深的内部 Present；标志必须仍为 true —— 旧实现在这里已漏成 false）
    CHECK(flag == true);
    CHECK_FALSE(SimulatePresent(true, flag, app));
    CHECK(flag == true); // 旧实现：第 2 层退出已把标志硬清 false → 第 3 层被误判应用级
    CHECK(app == 1);     // 修复后：整个嵌套栈只推进一次 frameID
    // 逐层退出（后进先出）：每一层恢复"该层进入前"的值
    zfg::PresentReexit(outer, flag);
    CHECK(flag == false);
}

TEST_CASE("zfg reentry: flag unwinds correctly across a full app frame sequence")
{
    bool flag = false;
    int app = 0;
    // 帧 N：应用 Present1（持有外层记录）+ 2 层嵌套
    const zfg::PresentReentry outer = zfg::PresentReenter(true, flag);
    CHECK(outer.appLevel);
    ++app;
    if (outer.setNested) {
        flag = true;
    }
    CHECK_FALSE(SimulatePresent(true, flag, app));
    CHECK_FALSE(SimulatePresent(true, flag, app));
    // 外层退出（restore 到 false）
    zfg::PresentReexit(outer, flag);
    CHECK(flag == false);
    // 帧 N+1：下一次应用呈现必须重新被判应用级（标志残留 true 会永久丢帧）
    CHECK(SimulatePresent(true, flag, app));
    CHECK(app == 2);
}

TEST_CASE("zfg reentry: non-wrapped chain never sets the flag nor counts as app level")
{
    bool flag = false;
    int app = 0;
    CHECK_FALSE(SimulatePresent(false, flag, app)); // FFX 自建的底层链
    CHECK(flag == false); // 不置位
    CHECK(app == 0);
    // 非代理链的调用发生在嵌套中时：不置位 → 退出也不恢复（setNested=false 语义）
    flag = true;
    CHECK_FALSE(SimulatePresent(false, flag, app));
    CHECK(flag == true); // 外层状态不被底层链的调用破坏
}
