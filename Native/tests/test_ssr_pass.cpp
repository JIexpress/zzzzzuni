// =============================================================================
// test_ssr_pass.cpp —— SSR 纯逻辑 + RG 特性声明单测（TASK-B-R5-03）
//
// 覆盖（简报设计要点 5 的单测清单）：
//   - Hi-Z mip 链计划：640×480 → 6 级（简报"最多 6 级"）、末级 20×15、
//     逐级减半单调、小图 1×1 截断、非法参数拒绝
//   - 4-tap 模糊核：4 个对角偏移、和为零（对称核）
//   - RG 特性声明（SSR Hi-Z 资源依赖）mock 断言：RecordSsrPasses 录制的 4 个
//     Pass 经 mock 设备编译/执行——声明序 = 执行序（HiZBuild → SsrTrace →
//     SsrBlur → SsrComposite）、hiz 生命周期覆盖 build..trace、深度 DSV 写→
//     Hi-Z 只读走 split barrier 计划、无未声明使用
//   - SsrTraceConstants 布局（与 ssr_trace.hlsl CBufSsr 逐字节契约，192B）
// =============================================================================
#include "doctest/doctest.h"

#include <cmath>
#include <cstdint>
#include <vector>

#include "rg_mock_device.h"
#include "zrender/ssr_pass.h"

// =============================================================================
// Hi-Z mip 链计划
// =============================================================================
TEST_CASE("SsrMipChainPlan: 640x480 builds 6 levels ending at 20x15")
{
    SsrMipLevel levels[ZR_SSR_MAX_MIPS] = {};
    const uint32_t count = SsrMipChainPlan(640u, 480u, ZR_SSR_MAX_MIPS, levels);
    CHECK(count == 6u); // 简报：Hi-Z mip 链最多 6 级（640×480 整链恰 6 级）
    uint32_t w = 640u;
    uint32_t h = 480u;
    for (uint32_t i = 0; i < count; ++i) {
        CHECK(levels[i].width == w);
        CHECK(levels[i].height == h);
        w /= 2u;
        h /= 2u;
    }
    CHECK(levels[5].width == 20u);
    CHECK(levels[5].height == 15u);
}

TEST_CASE("SsrMipChainPlan: maxMips truncates and small images clamp at 1x1")
{
    SsrMipLevel levels[8] = {};
    CHECK(SsrMipChainPlan(640u, 480u, 3u, levels) == 3u);
    CHECK(levels[2].width == 160u);
    CHECK(levels[2].height == 120u);

    // 4×2 整链只有 2 级（log2(4)+1）：后续级别 clamp 在 1×1 之外不存在
    const uint32_t tiny = SsrMipChainPlan(4u, 2u, ZR_SSR_MAX_MIPS, levels);
    CHECK(tiny == 3u); // 4→2→1（max 维 4：floor(log2 4)+1 = 3）
    CHECK(levels[0].width == 4u);
    CHECK(levels[0].height == 2u);
    CHECK(levels[1].width == 2u);
    CHECK(levels[1].height == 1u);
    CHECK(levels[2].width == 1u);
    CHECK(levels[2].height == 1u);
}

TEST_CASE("SsrMipChainPlan: invalid parameters rejected")
{
    SsrMipLevel levels[8] = {};
    CHECK(SsrMipChainPlan(0u, 480u, 6u, levels) == 0u);
    CHECK(SsrMipChainPlan(640u, 0u, 6u, levels) == 0u);
    CHECK(SsrMipChainPlan(640u, 480u, 0u, levels) == 0u);
    CHECK(SsrMipChainPlan(640u, 480u, 6u, nullptr) == 0u);
}

// =============================================================================
// 4-tap 模糊核
// =============================================================================
TEST_CASE("SsrBlurTapOffsets: four diagonal taps with zero vector sum")
{
    float taps[ZR_SSR_BLUR_TAPS][2];
    SsrBlurTapOffsets(taps);
    float sx = 0.0f;
    float sy = 0.0f;
    for (uint32_t i = 0; i < ZR_SSR_BLUR_TAPS; ++i) {
        CHECK(std::fabs(std::fabs(taps[i][0]) - 1.0f) < 1e-6f); // 对角：|x|=1
        CHECK(std::fabs(std::fabs(taps[i][1]) - 1.0f) < 1e-6f); // |y|=1
        sx += taps[i][0];
        sy += taps[i][1];
    }
    CHECK(std::fabs(sx) < 1e-6f); // 对称核：和为零（无亮度偏置）
    CHECK(std::fabs(sy) < 1e-6f);
}

// =============================================================================
// 常量布局契约
// =============================================================================
TEST_CASE("SSR constant buffer layouts match GPU contracts")
{
    CHECK(sizeof(SsrTraceConstants) == 192u);
    CHECK(offsetof(SsrTraceConstants, viewProj) == 64u);
    CHECK(offsetof(SsrTraceConstants, projParams) == 128u);
    CHECK(offsetof(SsrTraceConstants, ssrParams) == 144u);
    CHECK(offsetof(SsrTraceConstants, screenParams) == 160u);
    CHECK(offsetof(SsrTraceConstants, camPos) == 176u);
}

// =============================================================================
// RG 特性声明（SSR Hi-Z 资源依赖）—— mock 断言
// =============================================================================
TEST_CASE("RecordSsrPasses: declared order compiles; depth DSV write to Hi-Z read splits; hiz lifetime spans build..trace")
{
    RgGraph graph;
    // 资源：depth/scene/back 为 Imported（模拟跨帧持久图像）；hiz/trace/blur
    // 为 Transient（图内临时）
    ZrImageDesc colorDesc {};
    colorDesc.format = ZrFormat::RGBA8Unorm;
    colorDesc.width = 640u;
    colorDesc.height = 480u;
    colorDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    ZrImageDesc hizDesc = colorDesc;
    hizDesc.format = ZrFormat::R32Float;
    hizDesc.mipLevels = ZR_SSR_MAX_MIPS;
    (void)hizDesc;

    const RgHandle depth = graph.Import(RgResourceDesc::ImportImage("depth", ZrImage{}));
    const RgHandle scene = graph.Import(RgResourceDesc::ImportImage("sceneColor", ZrImage{}));
    const RgHandle hiz = graph.CreateTransient(RgResourceDesc::TransientImage("hiz", hizDesc));
    const RgHandle trace
        = graph.CreateTransient(RgResourceDesc::TransientImage("ssrTrace", colorDesc));
    const RgHandle blur
        = graph.CreateTransient(RgResourceDesc::TransientImage("ssrBlur", colorDesc));
    const RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", ZrImage{}));

    // 前置 DepthWrite pass（模拟主绘制写深度）——HiZBuild 只读 depth 走 split barrier
    graph.AddPass("DepthWrite",
        [&](RgPass& pass) {
            pass.Write(depth, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite,
                ZrPipelineStageBits::LateFragmentTests);
        },
        nullptr);

    SsrPassHandles handles;
    handles.depth = depth;
    handles.hiz = hiz;
    handles.sceneColor = scene;
    handles.ssrTrace = trace;
    handles.ssrBlur = blur;
    handles.back = back;
    RecordSsrPasses(graph, handles, nullptr, nullptr, nullptr, nullptr);

    REQUIRE(graph.Compile());
    CHECK(graph.Error() == RgError::None);
    // 声明序 = 执行序：DepthWrite → HiZBuild → SsrTrace → SsrBlur → SsrComposite
    REQUIRE(graph.CompiledOrder().size() == 5u);

    // hiz 生命周期：写于 HiZBuild（编译序 1）、读于 SsrTrace（编译序 2）
    const RgLifetime hizLifetime = graph.LifetimeOf(hiz);
    CHECK(hizLifetime.used);
    CHECK(hizLifetime.firstUse == 1u);
    CHECK(hizLifetime.lastUse == 2u);

    // 资源依赖契约：depth 的 DSV 写 → 只读转入 split barrier 计划（begin 在写后、
    // end 在 HiZBuild 前），Hi-Z 采样不与深度写入冲突
    REQUIRE(graph.BarrierPlan().SplitCount() == 1u);
    CHECK(graph.BarrierPlan().splits[0].resource == depth);
    CHECK(graph.BarrierPlan().splits[0].beginAfterPass == 0u);
    CHECK(graph.BarrierPlan().splits[0].endBeforePass == 1u);

    // scene 颜色只读贯穿 trace..composite（读-读零屏障）；back 写（Imported）为图根
    const RgLifetime sceneLifetime = graph.LifetimeOf(scene);
    CHECK_FALSE(sceneLifetime.used); // Imported 不参与 Transient 别名计划
}

TEST_CASE("RecordSsrPasses: mock execute runs in declared order with zero undeclared uses")
{
    MockDevice device;
    RgGraph graph;
    const RgHandle depth = graph.Import(RgResourceDesc::ImportImage("depth", ZrImage{}));
    ZrImageDesc colorDesc {};
    colorDesc.format = ZrFormat::RGBA8Unorm;
    colorDesc.width = 320u;
    colorDesc.height = 240u;
    colorDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    const RgHandle scene = graph.Import(RgResourceDesc::ImportImage("sceneColor", ZrImage{}));
    const RgHandle hiz = graph.CreateTransient(RgResourceDesc::TransientImage("hiz", colorDesc));
    const RgHandle trace
        = graph.CreateTransient(RgResourceDesc::TransientImage("ssrTrace", colorDesc));
    const RgHandle blur
        = graph.CreateTransient(RgResourceDesc::TransientImage("ssrBlur", colorDesc));
    const RgHandle back = graph.Import(RgResourceDesc::ImportImage("backbuffer", ZrImage{}));

    SsrPassHandles handles;
    handles.depth = depth;
    handles.hiz = hiz;
    handles.sceneColor = scene;
    handles.ssrTrace = trace;
    handles.ssrBlur = blur;
    handles.back = back;

    std::vector<std::string> execOrder;
    RecordSsrPasses(graph, handles,
        [&](RgContext&) { execOrder.push_back("HiZBuild"); },
        [&](RgContext&) { execOrder.push_back("SsrTrace"); },
        [&](RgContext&) { execOrder.push_back("SsrBlur"); },
        [&](RgContext&) { execOrder.push_back("SsrComposite"); });

    REQUIRE(graph.Compile());
    graph.ExecuteAliased(device, device.commandList);
    REQUIRE(execOrder.size() == 4u);
    CHECK(execOrder[0] == "HiZBuild");
    CHECK(execOrder[1] == "SsrTrace");
    CHECK(execOrder[2] == "SsrBlur");
    CHECK(execOrder[3] == "SsrComposite");
    // 资源依赖无未声明使用（全部访问均经 setup 声明——简报指定 mock 断言）
    CHECK(graph.LastExecuteUndeclaredUses() == 0u);
    graph.FlushRetired(device);
}
