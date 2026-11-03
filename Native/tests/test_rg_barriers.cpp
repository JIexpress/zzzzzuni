// =============================================================================
// test_rg_barriers.cpp —— RenderGraph 屏障规划/合并单测（TASK-B-R3-01）
//
// 覆盖：屏障最小化（具体数字断言）/ 只读共享零屏障 / 同性质同批合并 /
//       split barrier 计划 / 写-读-读-写-读全链路屏障计数 / 执行层批量提交
// =============================================================================
#include "doctest/doctest.h"

#include <string>
#include <utility>
#include <vector>

#include "rg_mock_device.h"
#include "zrender/rg_render_graph.h"

namespace {

ZrImageDesc ColorTarget()
{
    ZrImageDesc d;
    d.format = ZrFormat::RGBA16Float;
    d.width = 64;
    d.height = 64;
    d.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    return d;
}

/// 纯 DSV（无 Sampled）：读侧推导 DepthStencilReadOnlyOptimal —— 正对审计 A-10
/// 要求的 `DEPTH_WRITE -> DEPTH_READ` 断言（带 Sampled 的深度推导 ShaderReadOnly，
/// 由紧随其后的等价性用例覆盖）。
ZrImageDesc DepthTarget()
{
    ZrImageDesc d;
    d.format = ZrFormat::D32Float;
    d.width = 64;
    d.height = 64;
    d.usage = ZrImageUsage::DepthStencilAttachment;
    return d;
}

} // namespace

// ---- 场景 7：屏障最小化 + 只读共享——3 连读恰 2 屏障 ---------------------------
// 资源连续 3 个 Pass 只读（ShaderReadOnly），末尾 Present：
//   屏障 1：Undefined -> ShaderReadOnly（首个读 Pass 前）
//   屏障 2：ShaderReadOnly -> Present（末 Pass 前）
// 中间两个只读 Pass 之间零屏障（只读共享）。
TEST_CASE("rg-barriers: three consecutive read passes produce exactly 2 barriers")
{
    RgGraph graph;
    const RgHandle tex = graph.CreateTransient(
        RgResourceDesc::TransientImage("tex", ColorTarget()));

    // 纯读链无写者、无边通向呈现根 -> 各读 Pass 显式标记根保活
    auto addReadPass = [&graph, tex](const char* name) {
        graph.AddPass(name,
            [&](RgPass& pass) {
                pass.Read(tex);
                pass.MarkRoot();
            },
            [](RgContext&) {});
    };
    addReadPass("Read0");
    addReadPass("Read1");
    addReadPass("Read2");
    graph.AddPass("Present",
        [&](RgPass& pass) {
            pass.Present(tex);
            pass.MarkRoot();
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgBarrierPlan& plan = graph.BarrierPlan();
    CHECK(plan.SplitCount() == 0u);
    CHECK(plan.TotalBarrierCount() == 2u); // 恰 2 屏障

    // 第一个读 Pass 前：Undefined -> ShaderReadOnly
    REQUIRE(plan.perPassImageBatches[0].size() == 1u);
    CHECK(plan.perPassImageBatches[0][0].prevLayout == ZrImageLayout::Undefined);
    CHECK(plan.perPassImageBatches[0][0].newLayout == ZrImageLayout::ShaderReadOnlyOptimal);
    CHECK(plan.perPassImageBatches[0][0].dstAccess == ZrAccessBits::ShaderRead);

    // 只读共享：中间两个读 Pass 零屏障
    CHECK(plan.perPassImageBatches[1].empty());
    CHECK(plan.perPassImageBatches[2].empty());

    // 末 Pass 前：ShaderReadOnly -> Present
    REQUIRE(plan.perPassImageBatches[3].size() == 1u);
    CHECK(plan.perPassImageBatches[3][0].prevLayout == ZrImageLayout::ShaderReadOnlyOptimal);
    CHECK(plan.perPassImageBatches[3][0].newLayout == ZrImageLayout::Present);
}

// ---- 场景 8：同性质合并——一 Pass 前多资源屏障进同一次批量提交 ------------------
TEST_CASE("rg-barriers: same-nature barriers of multiple resources merge into one batch")
{
    RgGraph graph;
    const RgHandle t1 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t1", ColorTarget()));
    const RgHandle t2 = graph.CreateTransient(
        RgResourceDesc::TransientImage("t2", ColorTarget()));

    graph.AddPass("SampleBoth",
        [&](RgPass& pass) {
            pass.Read(t1);
            pass.Read(t2);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("DrawToBoth",
        [&](RgPass& pass) {
            pass.Write(t1);
            pass.Write(t2);
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    // 编译序：写者 DrawToBoth 在读者 SampleBoth 之前（依赖边强制）
    REQUIRE(graph.CompiledOrder().size() == 2u);
    CHECK(graph.CompiledOrder()[0] == 1u); // DrawToBoth（声明序 1）
    CHECK(graph.CompiledOrder()[1] == 0u); // SampleBoth（声明序 0）

    const RgBarrierPlan& plan = graph.BarrierPlan();
    // 写 Pass（序 0）：t1/t2 各需 Undefined->ColorAttachment 一道，合并为一批
    REQUIRE(plan.perPassImageBatches[0].size() == 2u);
    for (const ZrImageBarrier& b : plan.perPassImageBatches[0]) {
        CHECK(b.prevLayout == ZrImageLayout::Undefined);
        CHECK(b.newLayout == ZrImageLayout::ColorAttachmentOptimal);
        CHECK(b.dstStage == ZrPipelineStageBits::ColorAttachmentOutput);
    }
    // 读 Pass（序 1）：两道同性质 Color->ShaderReadOnly 合并为一批
    REQUIRE(plan.perPassImageBatches[1].size() == 2u);
    for (const ZrImageBarrier& b : plan.perPassImageBatches[1]) {
        CHECK(b.prevLayout == ZrImageLayout::ColorAttachmentOptimal);
        CHECK(b.newLayout == ZrImageLayout::ShaderReadOnlyOptimal);
        CHECK(b.srcStage == ZrPipelineStageBits::ColorAttachmentOutput);
        CHECK(b.dstStage == ZrPipelineStageBits::FragmentShader);
    }
    CHECK(plan.TotalBarrierCount() == 4u); // 每资源 2 道（进入写 + 进入读）

    // 执行层：两道同性质屏障出现在同一次 ResourceBarrier 调用
    MockDevice device;
    graph.Execute(device, device.commandList);
    REQUIRE(device.commandList.barrierCalls.size() == 2u);
    REQUIRE(device.commandList.barrierCalls[0].images.size() == 2u);
    REQUIRE(device.commandList.barrierCalls[1].images.size() == 2u);
}

// ---- 场景 9：split barrier 计划 + **真 Execute** 下发等价转换（A-10）-----------
// A-10（TASK-R10-07）：本场景原先只 `Compile()` 后断言 `plan.SplitCount()`，从不
// Execute —— 于是 `RgGraph::Execute()` 从不下发 split 屏障的缺陷一直是绿的。
// 现在改成真的走公共 API `Execute`，断言下发序列里恰有一条
// `DepthStencilAttachmentOptimal -> DepthStencilReadOnlyOptimal`（审计要求的形态，
// 不是"至少一条任意屏障"）。
TEST_CASE("rg-barriers: dsv write followed by read yields split plan and Execute emits it")
{
    RgGraph graph;
    const RgHandle depth = graph.CreateTransient(
        RgResourceDesc::TransientImage("depth", DepthTarget()));

    graph.AddPass("DepthPrepass",
        [&](RgPass& pass) {
            pass.Write(depth);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("SSAO",
        [&](RgPass& pass) {
            pass.Read(depth);
            pass.MarkRoot(); // 读 Pass 通向无下游 -> 显式根保活
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgBarrierPlan& plan = graph.BarrierPlan();

    // 写 Pass 自身：Undefined -> DepthStencilAttachmentOptimal（内联）
    REQUIRE(plan.perPassImageBatches[0].size() == 1u);
    CHECK(plan.perPassImageBatches[0][0].newLayout
        == ZrImageLayout::DepthStencilAttachmentOptimal);

    // 读 Pass：split 计划（begin=写后立刻，end=读前）
    REQUIRE(plan.SplitCount() == 1u);
    const RgSplitBarrier& split = plan.splits[0];
    CHECK(split.resource == depth);
    CHECK(split.beginAfterPass == 0u); // 写 Pass（编译序 0）完成后立刻 begin
    CHECK(split.endBeforePass == 1u);  // 读 Pass（编译序 1）之前 end
    CHECK(split.barrier.prevLayout == ZrImageLayout::DepthStencilAttachmentOptimal);
    CHECK(split.barrier.newLayout == ZrImageLayout::DepthStencilReadOnlyOptimal);
    CHECK(split.barrier.srcStage == ZrPipelineStageBits::LateFragmentTests);
    CHECK(split.barrier.dstStage == ZrPipelineStageBits::EarlyFragmentTests);
    CHECK(plan.TotalBarrierCount() == 2u); // 1 内联 + 1 split（镜像不重复计数）

    // 真执行（A-10 的判据）：公共 API Execute 必须把这道转换发下去
    MockDevice device;
    graph.Execute(device, device.commandList);
    uint32_t depthReadConversions = 0u;
    for (const MockBarrierCall& call : device.commandList.barrierCalls) {
        for (const ZrImageBarrier& b : call.images) {
            if (b.prevLayout == ZrImageLayout::DepthStencilAttachmentOptimal
                && b.newLayout == ZrImageLayout::DepthStencilReadOnlyOptimal) {
                ++depthReadConversions;
            }
        }
    }
    CHECK(depthReadConversions == 1u); // 修前为 0（split 只记计划，Execute 不读）
}

// ---- 场景 12：A-10 双路径等价——Execute 与 ExecuteAliased 的转换序列一致 --------
// 同一张图（DSV 写 -> 采样读，带 Sampled → 读侧推导 ShaderReadOnlyOptimal）：
//   Execute          : 内联批次 = [Undefined→DSV, DSV→SRO]
//   ExecuteAliased   : 内联批次（跳过 split 镜像）= [Undefined→DSV]
//                      + split BEGIN/END 半边 = DSV→SRO
// 把"承载转换的下发"按顺序摊平（split 只取 END 半边，BEGIN 半边是同一道转换的
// 拆半），两条路径的 (prev,new) 序列必须逐位相同，且 split 侧不得多发。
TEST_CASE("rg-barriers: Execute and ExecuteAliased emit the same dsv-to-read conversion")
{
    ZrImageDesc sampledDepth = DepthTarget();
    sampledDepth.usage = ZrImageUsage::DepthStencilAttachment | ZrImageUsage::Sampled;

    auto build = [&sampledDepth](RgGraph& graph) {
        const RgHandle depth = graph.CreateTransient(
            RgResourceDesc::TransientImage("depth", sampledDepth));
        graph.AddPass("Depth",
            [&](RgPass& pass) {
                pass.Write(depth);
                pass.MarkRoot();
            },
            nullptr);
        graph.AddPass("Read",
            [&](RgPass& pass) {
                pass.Read(depth);
                pass.MarkRoot(); // 读链不通向呈现根 -> 显式根保活
            },
            nullptr);
    };

    using Conversion = std::pair<ZrImageLayout, ZrImageLayout>;
    auto flatten = [](const std::vector<MockBarrierCall>& calls) {
        std::vector<Conversion> out;
        for (const MockBarrierCall& call : calls) {
            for (const ZrImageBarrier& b : call.images) {
                out.emplace_back(b.prevLayout, b.newLayout);
            }
        }
        return out;
    };

    RgGraph plain;
    build(plain);
    REQUIRE(plain.Compile());
    REQUIRE(plain.BarrierPlan().SplitCount() == 1u);
    CHECK(plain.BarrierPlan().SplitMirrorCount() == 1u); // split 的等价内联表达在位
    MockDevice plainDevice;
    plain.Execute(plainDevice, plainDevice.commandList);
    const std::vector<Conversion> seqExecute = flatten(plainDevice.commandList.barrierCalls);
    REQUIRE(seqExecute.size() == 2u);
    CHECK(seqExecute[0] == Conversion(ZrImageLayout::Undefined,
        ZrImageLayout::DepthStencilAttachmentOptimal));
    CHECK(seqExecute[1] == Conversion(ZrImageLayout::DepthStencilAttachmentOptimal,
        ZrImageLayout::ShaderReadOnlyOptimal));

    RgGraph aliased;
    build(aliased);
    REQUIRE(aliased.Compile());
    MockDevice aliasedDevice;
    const RgExecuteStats stats = aliased.ExecuteAliased(aliasedDevice, aliasedDevice.commandList);
    // 镜像不参与内联下发：内联批次仍只有写 Pass 那一条，split 半边各一次
    CHECK(stats.inlineBarrierCalls == 1u);
    CHECK(stats.splitBegins == 1u);
    CHECK(stats.splitEnds == 1u);
    std::vector<Conversion> seqAliased = flatten(aliasedDevice.commandList.barrierCalls);
    for (const MockSplitCall& split : aliasedDevice.commandList.splitCalls) {
        if (split.beginOnly) {
            continue; // BEGIN 半边与 END 半边是同一道转换的拆半，只计一次
        }
        for (const ZrImageBarrier& b : split.images) {
            seqAliased.emplace_back(b.prevLayout, b.newLayout);
        }
    }
    REQUIRE(seqAliased.size() == seqExecute.size());
    for (size_t i = 0; i < seqAliased.size(); ++i) {
        CHECK(seqAliased[i] == seqExecute[i]);
    }
}

// ---- 场景 10：全链路屏障计数——写-读-读-写-呈现 恰 4 道 ------------------------
// 朴素逐访问计数 = 5（每个访问声明各发一道）；合并/共享后 = 4（中间读-读共享 0）。
TEST_CASE("rg-barriers: write-read-read-write-present chain produces exactly 4 barriers")
{
    RgGraph graph;
    const RgHandle rt = graph.CreateTransient(
        RgResourceDesc::TransientImage("rt", ColorTarget()));

    graph.AddPass("Clear",
        [&](RgPass& pass) {
            pass.Write(rt);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("Sample0",
        [&](RgPass& pass) {
            pass.Read(rt);
            pass.MarkRoot(); // 读链不通向呈现根 -> 显式根保活
        },
        [](RgContext&) {});
    graph.AddPass("Sample1",
        [&](RgPass& pass) {
            pass.Read(rt);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("PostWrite",
        [&](RgPass& pass) { pass.Write(rt); }, [](RgContext&) {});
    graph.AddPass("Present",
        [&](RgPass& pass) {
            pass.Present(rt);
            pass.MarkRoot();
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgBarrierPlan& plan = graph.BarrierPlan();
    CHECK(plan.SplitCount() == 0u);
    CHECK(plan.TotalBarrierCount() == 4u); // 朴素 5 -> 合并后 4
    // 编译序：Clear, PostWrite, Sample0, Sample1, Present（读见最新写）
    CHECK(graph.CompiledOrder()[0] == 0u);
    CHECK(graph.CompiledOrder()[1] == 3u);
    CHECK(graph.CompiledOrder()[2] == 1u);
    CHECK(graph.CompiledOrder()[3] == 2u);
    CHECK(graph.CompiledOrder()[4] == 4u);
    // 逐 Pass 分布：[0]=1 [1]=1(WAW) [2]=1 [3]=0(读-读共享) [4]=1
    CHECK(plan.perPassImageBatches[0].size() == 1u);
    CHECK(plan.perPassImageBatches[1].size() == 1u);
    CHECK(plan.perPassImageBatches[2].size() == 1u);
    CHECK(plan.perPassImageBatches[3].size() == 0u); // 读-读共享：零屏障
    CHECK(plan.perPassImageBatches[4].size() == 1u);

    MockDevice device;
    graph.Execute(device, device.commandList);
    REQUIRE(device.commandList.barrierCalls.size() == 4u); // 4 次批量调用
}

// ---- 场景 11：WAW 也发屏障——连续两次写同资源（不同布局性质相同则零）----------
// 两次同为 ColorAttachment 写：layout 不变、access 相同（写-写兼容判定走
// access 冲突路径：写足迹与写足迹不兼容 -> 每次写前一道屏障保序）。
TEST_CASE("rg-barriers: write-after-write still yields sync barrier")
{
    RgGraph graph;
    const RgHandle rt = graph.CreateTransient(
        RgResourceDesc::TransientImage("rt", ColorTarget()));

    graph.AddPass("W1",
        [&](RgPass& pass) {
            pass.Write(rt);
            pass.MarkRoot();
        },
        [](RgContext&) {});
    graph.AddPass("W2",
        [&](RgPass& pass) { pass.Write(rt); }, [](RgContext&) {});
    graph.AddPass("R",
        [&](RgPass& pass) {
            pass.Read(rt);
            pass.MarkRoot(); // 尾读显式根，保 W1/W2 存活
        },
        [](RgContext&) {});

    REQUIRE(graph.Compile());
    const RgBarrierPlan& plan = graph.BarrierPlan();
    // [0] Undefined->Color 1 道；[1] WAW 保序 1 道；[2] Color->ReadOnly 1 道
    CHECK(plan.TotalBarrierCount() == 3u);
    CHECK(plan.perPassImageBatches[1].size() == 1u);
    CHECK(plan.perPassImageBatches[1][0].prevLayout == ZrImageLayout::ColorAttachmentOptimal);
    CHECK(plan.perPassImageBatches[1][0].newLayout == ZrImageLayout::ColorAttachmentOptimal);
}
