// =============================================================================
// test_rg_execute.cpp —— ExecuteAliased 执行器单测（TASK-B-R3-02）
//
// 执行器为设备无关（只依赖 IZrDevice/IZrCommandList），本组用例在 Mock 后端上
// 验证三件事：
//   1. 别名真复用：生命周期不重叠的 Transient 共用别名堆（heaps < transients、
//      placed image 同堆 offset=0、退役冲刷后资源计数归零）
//   2. split 屏障真实下发：写 Pass 结束 BEGIN_ONLY、读 Pass 之前 END_ONLY，
//      半边内容与编译期 split 计划一致、句柄已替换为当帧真资源
//   3. 屏障占位句柄解析 + 生命周期区间物化/退役（峰值存活 < 物化总数）
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>

#include "rg_mock_device.h"
#include "zrender/rg_render_graph.h"

namespace {

ZrImageDesc MakeColorRtDesc()
{
    ZrImageDesc desc {};
    desc.format = ZrFormat::RGBA8Unorm;
    desc.width = 64u;
    desc.height = 64u;
    desc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    return desc;
}

ZrImageDesc MakeDepthDesc()
{
    ZrImageDesc desc {};
    desc.format = ZrFormat::D32Float;
    desc.width = 64u;
    desc.height = 64u;
    desc.usage = ZrImageUsage::DepthStencilAttachment;
    return desc;
}

} // namespace

// -----------------------------------------------------------------------------
// 1. 别名真复用：rtA [P0..P1] 与 rtB [P2..P3] 生命周期不重叠 → 同槽同堆
// -----------------------------------------------------------------------------
TEST_CASE("rg execute: alias heap reuse places both images in one heap")
{
    RgGraph graph;
    const ZrImage importedBack = ZrImage::FromRaw(9001u);
    RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", importedBack));
    RgHandle rtA = graph.CreateTransient(RgResourceDesc::TransientImage("styleRt", MakeColorRtDesc()));
    RgHandle rtB = graph.CreateTransient(RgResourceDesc::TransientImage("postRt", MakeColorRtDesc()));

    // 图连通性：P1 写 Imported（根）读 rtA 维持 A 链存活；P3 写 Imported 读 rtB
    // 维持 B 链存活；后台缓冲 WAW（P1→P3）保证 rtA [0..1] 与 rtB [2..3] 不重叠
    graph.AddPass("P0 write A", [&](RgPass& pass) { pass.Write(rtA); }, nullptr);
    graph.AddPass("P1 read A", [&](RgPass& pass) { pass.Read(rtA).Write(back); }, nullptr);
    graph.AddPass("P2 write B", [&](RgPass& pass) { pass.Write(rtB); }, nullptr);
    graph.AddPass("P3 present", [&](RgPass& pass) { pass.Read(rtB).Write(back); }, nullptr);
    REQUIRE(graph.Compile());

    // 编译产物前置：两资源同槽（别名计划就绪）
    REQUIRE(graph.AliasPlan().slotCount == 1u);
    REQUIRE(graph.AliasPlan().SlotOf(rtA) == 0);
    REQUIRE(graph.AliasPlan().SlotOf(rtB) == 0);

    MockDevice device;
    MockCommandList& cmd = device.commandList;
    RgExecuteStats stats = graph.ExecuteAliased(device, cmd);

    CHECK(stats.aliasHeapsCreated == 1u); // 1 堆 < 2 个 Transient —— 真内存复用
    CHECK(stats.transientImages == 2u);
    CHECK(device.placedImages.size() == 2u);
    REQUIRE(device.placedImages.size() == 2u);
    CHECK(device.placedImages[0].heap == device.placedImages[1].heap); // 同堆
    CHECK(device.placedImages[0].offset == 0u); // 每槽一堆：偏移恒 0
    CHECK(device.placedImages[1].offset == 0u);

    // 生命周期区间外不存在（峰值存活 1 < 物化总数 2：A 退役后 B 才落位）
    CHECK(stats.peakAliveTransients == 1u);
    // 堆尺寸 = 槽内最大落位需求（两 desc 同尺寸 → w*h*4）
    CHECK(device.lastHeapSize == 64u * 64u * 4u);

    // 冲刷退役：图像与堆全部物理销毁（计数归零）
    graph.FlushRetired(device);
    ZrDeviceStats after = device.GetDeviceStats();
    CHECK(after.imageCount == 0u);
    CHECK(after.aliasHeapCount == 0u);
}

// -----------------------------------------------------------------------------
// 2. split 屏障半边真实下发：写 Pass 结束 BEGIN_ONLY、读 Pass 之前 END_ONLY
// -----------------------------------------------------------------------------
TEST_CASE("rg execute: split barrier begin/end emission order and content")
{
    RgGraph graph;
    const ZrImage importedBack = ZrImage::FromRaw(9002u);
    RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", importedBack));
    RgHandle depth
        = graph.CreateTransient(RgResourceDesc::TransientImage("depth", MakeDepthDesc()));

    // P1 写 Imported（根）且读 depth——保深度链存活，使 DSV 写->读 真实进入计划
    graph.AddPass("P0 depth write", [&](RgPass& pass) { pass.Write(depth); }, nullptr);
    graph.AddPass("P1 depth read", [&](RgPass& pass) { pass.Read(depth).Write(back); }, nullptr);
    REQUIRE(graph.Compile());

    // 编译产物前置：DSV 写->读 输出 split 计划
    REQUIRE(graph.BarrierPlan().SplitCount() == 1u);
    REQUIRE(graph.BarrierPlan().splits[0].beginAfterPass == 0u);
    REQUIRE(graph.BarrierPlan().splits[0].endBeforePass == 1u);

    MockDevice device;
    MockCommandList& cmd = device.commandList;
    RgExecuteStats stats = graph.ExecuteAliased(device, cmd);

    CHECK(stats.splitBegins == 1u);
    CHECK(stats.splitEnds == 1u);
    REQUIRE(cmd.splitCalls.size() == 2u);
    // 顺序：先 BEGIN_ONLY（写 Pass 结束）后 END_ONLY（读 Pass 之前）
    CHECK(cmd.splitCalls[0].beginOnly == true);
    CHECK(cmd.splitCalls[1].beginOnly == false);
    // 半边内容一致（同一屏障拆两半）且句柄已替换为当帧真资源（非编译期占位）
    REQUIRE(cmd.splitCalls[0].images.size() == 1u);
    REQUIRE(cmd.splitCalls[1].images.size() == 1u);
    CHECK(cmd.splitCalls[0].images[0].image.IsValid());
    CHECK(cmd.splitCalls[0].images[0].image == cmd.splitCalls[1].images[0].image);
    CHECK(cmd.splitCalls[0].images[0].newLayout == ZrImageLayout::DepthStencilReadOnlyOptimal);
    // 调用序列中 begin 半边先于 end 半边
    const auto beginIt = std::find(cmd.calls.begin(), cmd.calls.end(), "ResourceBarrierSplitBegin");
    const auto endIt = std::find(cmd.calls.begin(), cmd.calls.end(), "ResourceBarrierSplitEnd");
    REQUIRE(beginIt != cmd.calls.end());
    REQUIRE(endIt != cmd.calls.end());
    CHECK(beginIt < endIt);
}

// -----------------------------------------------------------------------------
// 3. 屏障占位句柄解析 + 区间物化/退役：重叠生命周期各自成槽，内联批次句柄有效
// -----------------------------------------------------------------------------
TEST_CASE("rg execute: inline barrier handles resolved and lifecycle respected")
{
    RgGraph graph;
    const ZrImage importedBack = ZrImage::FromRaw(9003u);
    RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", importedBack));
    RgHandle rtA = graph.CreateTransient(RgResourceDesc::TransientImage("rtA", MakeColorRtDesc()));
    RgHandle rtB = graph.CreateTransient(RgResourceDesc::TransientImage("rtB", MakeColorRtDesc()));

    // A/B 同时存活（P0 双写）→ 生命周期重叠 → 必然异槽异堆
    graph.AddPass("P0 write A+B", [&](RgPass& pass) { pass.Write(rtA).Write(rtB); }, nullptr);
    graph.AddPass("P1 present", [&](RgPass& pass) { pass.Read(rtA).Read(rtB).Write(back); },
        nullptr);
    REQUIRE(graph.Compile());
    REQUIRE(graph.AliasPlan().slotCount == 2u); // 重叠 → 不复用

    MockDevice device;
    MockCommandList& cmd = device.commandList;
    RgExecuteStats stats = graph.ExecuteAliased(device, cmd);

    CHECK(stats.aliasHeapsCreated == 2u); // 无重叠可复用 → 堆数 == Transient 数
    CHECK(stats.transientImages == 2u);
    CHECK(stats.peakAliveTransients == 2u); // 同帧并存

    // 内联屏障批次的图像句柄全部解析为当帧真资源（placed image 句柄，非占位）
    REQUIRE(device.placedImages.size() == 2u);
    uint32_t resolvedBarrierImages = 0u;
    for (const MockBarrierCall& call : cmd.barrierCalls) {
        for (const ZrImageBarrier& b : call.images) {
            CHECK(b.image.IsValid());
            ++resolvedBarrierImages;
        }
    }
    CHECK(resolvedBarrierImages >= 2u); // P0 双转 RENDER_TARGET + P1 双转 SRV

    // 末次使用（P1）后退役，冲刷归零
    graph.FlushRetired(device);
    CHECK(device.GetDeviceStats().imageCount == 0u);
    CHECK(device.GetDeviceStats().aliasHeapCount == 0u);
}
