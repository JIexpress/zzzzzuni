// =============================================================================
// test_rg_compile.cpp —— RenderGraph 编译层单测（TASK-B-R3-01）
//
// 覆盖：拓扑稳定排序 / 死 Pass 剔除 / 别名双向（不重叠同槽、重叠异槽）/
//       未声明使用拒绝 / 环错误码 / 独立 Pass 声明序保持
// =============================================================================
#include "doctest/doctest.h"

#include <string>
#include <vector>

#include "rg_mock_device.h"
#include "zrender/rg_render_graph.h"

namespace {

ZrImageDesc ColorTarget(const char*, uint32_t w = 64, uint32_t h = 64)
{
    ZrImageDesc d;
    d.format = ZrFormat::RGBA16Float;
    d.width = w;
    d.height = h;
    d.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    return d;
}

} // namespace

// ---- 场景 1：拓扑序——依赖强制写先于读（即使声明序相反）------------------------
TEST_CASE("rg-compile: topological order forces writer before reader")
{
    RgGraph graph;
    const RgHandle target = graph.CreateTransient(
        RgResourceDesc::TransientImage("target", ColorTarget("target")));

    std::vector<std::string> executed;
    // 声明序：先声明读者 Post，再声明写者 GBuffer —— 依赖必须翻转执行序
    graph.AddPass("Post",
        [&](RgPass& pass) {
            pass.Read(target);
            pass.MarkRoot(); // 尾读作根：写者经依赖边成为其祖先而保活
        },
        [&](RgContext&) { executed.push_back("Post"); });
    graph.AddPass("GBuffer",
        [&](RgPass& pass) { pass.Write(target); },
        [&](RgContext&) { executed.push_back("GBuffer"); });

    REQUIRE(graph.Compile());
    REQUIRE(graph.CompiledOrder().size() == 2u);
    // 声明序 0=Post 1=GBuffer；执行序必须 GBuffer 在前
    CHECK(graph.CompiledOrder()[0] == 1u);
    CHECK(graph.CompiledOrder()[1] == 0u);

    MockDevice device;
    graph.Execute(device, device.commandList);
    REQUIRE(executed.size() == 2u);
    CHECK(executed[0] == "GBuffer");
    CHECK(executed[1] == "Post");
}

// ---- 场景 1b：无依赖的独立 Pass 保持声明序（稳定排序）--------------------------
TEST_CASE("rg-compile: independent passes keep declaration order")
{
    RgGraph graph;
    const RgHandle a = graph.CreateTransient(
        RgResourceDesc::TransientImage("a", ColorTarget("a")));
    const RgHandle b = graph.CreateTransient(
        RgResourceDesc::TransientImage("b", ColorTarget("b")));

    graph.AddPass("A",
        [&](RgPass& pass) {
            pass.Write(a);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("B",
        [&](RgPass& pass) {
            pass.Write(b);
            pass.MarkRoot();
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    REQUIRE(graph.CompiledOrder().size() == 2u);
    CHECK(graph.CompiledOrder()[0] == 0u); // A 先声明先执行
    CHECK(graph.CompiledOrder()[1] == 1u);
}

// ---- 场景 2：死 Pass 剔除——写无人读且非根 --------------------------------------
TEST_CASE("rg-compile: dead pass writing unread transient is culled")
{
    RgGraph graph;
    const RgHandle dead = graph.CreateTransient(
        RgResourceDesc::TransientImage("dead", ColorTarget("dead")));
    const RgHandle backbuffer = graph.Import(
        RgResourceDesc::ImportImage("backbuffer", ZrImage::FromRaw(7u)));

    bool deadExecuted = false;
    bool liveExecuted = false;
    graph.AddPass("DeadPass",
        [&](RgPass& pass) { pass.Write(dead); }, // 无人读、非根 -> 剔除
        [&](RgContext&) { deadExecuted = true; });
    graph.AddPass("LivePass",
        [&](RgPass& pass) { pass.Write(backbuffer); }, // 写 Imported -> 天然根
        [&](RgContext&) { liveExecuted = true; });

    REQUIRE(graph.Compile());
    REQUIRE(graph.CompiledOrder().size() == 1u);
    REQUIRE(graph.CulledPasses().size() == 1u);
    CHECK(graph.CulledPasses()[0] == 0u); // DeadPass 声明序 0
    CHECK(graph.CompiledOrder()[0] == 1u); // LivePass 存留

    MockDevice device;
    graph.Execute(device, device.commandList);
    CHECK_FALSE(deadExecuted); // 剔除 Pass 的 execute 不触发
    CHECK(liveExecuted);
}

// ---- 场景 3a：别名——生命周期不重叠 -> 同槽 ------------------------------------
TEST_CASE("rg-compile: disjoint lifetimes share alias slot")
{
    RgGraph graph;
    const RgHandle t1 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t1", ColorTarget("t1")));
    const RgHandle t2 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t2", ColorTarget("t2")));

    graph.AddPass("W1",
        [&](RgPass& pass) { pass.Write(t1); }, [](RgContext&) {});
    graph.AddPass("R1",
        [&](RgPass& pass) {
            pass.Read(t1);
            pass.MarkRoot(); // 保 W1/R1 段存活（后半图由 R2 根保活）
        },
        [](RgContext&) {});
    graph.AddPass("W2",
        [&](RgPass& pass) { pass.Write(t2); }, [](RgContext&) {});
    graph.AddPass("R2",
        [&](RgPass& pass) {
            pass.Read(t2);
            pass.MarkRoot();
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgAliasPlan& alias = graph.AliasPlan();
    const int32_t slot1 = alias.SlotOf(t1);
    const int32_t slot2 = alias.SlotOf(t2);
    REQUIRE(slot1 >= 0);
    REQUIRE(slot2 >= 0);
    CHECK(slot1 == slot2);      // t1[0,1] 与 t2[2,3] 不重叠 -> 同槽
    CHECK(alias.slotCount == 1u); // 全图只需 1 个别名槽

    const RgLifetime life1 = graph.LifetimeOf(t1);
    const RgLifetime life2 = graph.LifetimeOf(t2);
    CHECK(life1.used);
    CHECK(life1.firstUse == 0u);
    CHECK(life1.lastUse == 1u);
    CHECK(life2.firstUse == 2u);
    CHECK(life2.lastUse == 3u);
}

// ---- 场景 3b：别名——生命周期重叠 -> 异槽 --------------------------------------
TEST_CASE("rg-compile: overlapping lifetimes take distinct alias slots")
{
    RgGraph graph;
    const RgHandle t1 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t1", ColorTarget("t1")));
    const RgHandle t2 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t2", ColorTarget("t2")));

    graph.AddPass("W1",
        [&](RgPass& pass) { pass.Write(t1); }, [](RgContext&) {});
    graph.AddPass("Blit",
        [&](RgPass& pass) {
            pass.Read(t1);  // t1 存活到 pass1
            pass.Write(t2); // t2 自 pass1 起活 -> 与 t1 重叠
        },
        [](RgContext&) {});
    graph.AddPass("R2",
        [&](RgPass& pass) {
            pass.Read(t2);
            pass.MarkRoot();
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgAliasPlan& alias = graph.AliasPlan();
    const int32_t slot1 = alias.SlotOf(t1);
    const int32_t slot2 = alias.SlotOf(t2);
    REQUIRE(slot1 >= 0);
    REQUIRE(slot2 >= 0);
    CHECK(slot1 != slot2);     // t1[0,1] 与 t2[1,2] 重叠 -> 异槽
    CHECK(alias.slotCount == 2u);
}

// ---- 场景 4：未声明使用拒绝——execute 解析未声明句柄 -> 无效句柄 + 记错 -------
TEST_CASE("rg-compile: undeclared handle resolution is rejected in execute")
{
    RgGraph graph;
    const RgHandle declared = graph.CreateTransient(
        RgResourceDesc::TransientImage("declared", ColorTarget("declared")));
    const RgHandle undeclared = graph.CreateTransient(
        RgResourceDesc::TransientImage("undeclared", ColorTarget("undeclared")));

    uint32_t ctxErrors = 0;
    bool gotDeclared = false;
    bool gotUndeclaredValid = false;
    graph.AddPass("Only",
        [&](RgPass& pass) {
            pass.Write(declared);
            pass.MarkRoot();
        },
        [&](RgContext& ctx) {
            gotDeclared = ctx.GetImage(declared).IsValid();
            gotUndeclaredValid = ctx.GetImage(undeclared).IsValid(); // 未声明
            ctxErrors = ctx.UndeclaredUseCount();
        });

    REQUIRE(graph.Compile());
    MockDevice device;
    graph.Execute(device, device.commandList);
    CHECK(gotDeclared);            // 声明过的句柄解析成功
    CHECK_FALSE(gotUndeclaredValid); // 未声明 -> 无效句柄
    CHECK(ctxErrors == 1u);
    CHECK(graph.LastExecuteUndeclaredUses() == 1u); // 图级累计
}

// ---- 场景 5：环检测——互相写读的依赖环 -> 明确错误码 ----------------------------
TEST_CASE("rg-compile: dependency cycle is detected with explicit error")
{
    RgGraph graph;
    const RgHandle r1 = graph.CreateTransient(
        RgResourceDesc::TransientImage("r1", ColorTarget("r1")));
    const RgHandle r2 = graph.CreateTransient(
        RgResourceDesc::TransientImage("r2", ColorTarget("r2")));

    graph.AddPass("P1",
        [&](RgPass& pass) {
            pass.Write(r2);
            pass.Read(r1); // 依赖 P2 写 r1
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("P2",
        [&](RgPass& pass) {
            pass.Write(r1);
            pass.Read(r2); // 依赖 P1 写 r2 -> 成环
            pass.MarkRoot();
        },
        [](RgContext&) {});

    CHECK_FALSE(graph.Compile());
    CHECK(graph.Error() == RgError::CycleDetected);
    CHECK(graph.CompiledOrder().empty());
}

// ---- 场景 6：无根图（全死）-> NoRoots 错误 -------------------------------------
TEST_CASE("rg-compile: graph without any root reports NoRoots")
{
    RgGraph graph;
    const RgHandle t = graph.CreateTransient(
        RgResourceDesc::TransientImage("t", ColorTarget("t")));
    graph.AddPass("LonelyWriter",
        [&](RgPass& pass) { pass.Write(t); }, // 无 Imported 写、无 MarkRoot
        [](RgContext&) {});
    CHECK_FALSE(graph.Compile());
    CHECK(graph.Error() == RgError::NoRoots);
}
