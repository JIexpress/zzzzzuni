// =============================================================================
// test_gpu_hud.cpp —— HUD 统计聚合纯函数单测（TASK-B-R9-16）
//
// 覆盖：
//   1. 资源字节估算：单 mip 全图 / mip 链逐级减半 / arrayLayers 倍乘 /
//      缓冲按 desc.size（公式逐一手工核算）；
//   2. 聚合器：最新值取末样本、峰值取窗口、fps=1000/平均帧耗、环形容量
//      （超窗口覆盖最旧）、Reset 归零、空窗口快照全零；
//   3. 格式化：JSON 键值齐全、数值定点（确定性）、文本行可读、两次生成逐位一致。
// 变异验证基线：断言 drawCallsPeak/fps 取值口径（改错任一口径必红）。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <string>

#include "zrender/gpu_hud.h"

// ---- 1. 资源字节估算 ---------------------------------------------------------------
TEST_CASE("gpu-hud: image byte estimate follows mip chain and array layers")
{
    using zrender::ZrEstimateImageBytes;

    // RGBA8Unorm（4B/px，块 1x1）640x480 单 mip 单层：640*480*4 = 1,228,800
    ZrImageDesc rt {};
    rt.format = ZrFormat::RGBA8Unorm;
    rt.width = 640u;
    rt.height = 480u;
    CHECK(ZrEstimateImageBytes(rt) == 1228800ull);

    // D32Float（4B/px）1024x1024 单 mip：4 MiB
    ZrImageDesc depth {};
    depth.format = ZrFormat::D32Float;
    depth.width = 1024u;
    depth.height = 1024u;
    CHECK(ZrEstimateImageBytes(depth) == 4194304ull);

    // mip 链：4x4 RGBA8 两级 mip = 4*4*4 + 2*2*4 = 80（逐级减半、下限 1）
    ZrImageDesc mips {};
    mips.format = ZrFormat::RGBA8Unorm;
    mips.width = 4u;
    mips.height = 4u;
    mips.mipLevels = 2u;
    CHECK(ZrEstimateImageBytes(mips) == 80ull);

    // 奇数尺寸 mip 链：3x3 两级 = 9*4 + ceil(1)*4 = 40（3/2 下取整 = 1）
    ZrImageDesc odd {};
    odd.format = ZrFormat::RGBA8Unorm;
    odd.width = 3u;
    odd.height = 3u;
    odd.mipLevels = 2u;
    CHECK(ZrEstimateImageBytes(odd) == 40ull);

    // arrayLayers 倍乘（CSM 阴影数组：2048x2048x4 层 D32）
    // 单层 = 2048*2048*4B = 16,777,216；×4 层 = 67,108,864
    ZrImageDesc shadowArray {};
    shadowArray.format = ZrFormat::D32Float;
    shadowArray.width = 2048u;
    shadowArray.height = 2048u;
    shadowArray.arrayLayers = 4u;
    CHECK(ZrEstimateImageBytes(shadowArray) == 4ull * 16777216ull);
}

TEST_CASE("gpu-hud: buffer byte estimate is desc.size")
{
    ZrBufferDesc vb {};
    vb.size = 1536u; // 48 顶点 x 32B
    CHECK(zrender::ZrEstimateBufferBytes(vb) == 1536ull);
    ZrBufferDesc empty {};
    CHECK(zrender::ZrEstimateBufferBytes(empty) == 0ull);
}

// ---- 2. 聚合器 ---------------------------------------------------------------------
TEST_CASE("gpu-hud: aggregator snapshots latest sample, peak and fps over window")
{
    zrender::ZrHudAggregator agg(8u);

    // 空窗口：全零快照（frames=0 明确标记"尚无数据"，fps 不臆造）
    zrender::ZrHudFrameStats empty = agg.Snapshot();
    CHECK(empty.frames == 0u);
    CHECK(empty.fps == 0.0);
    CHECK(empty.drawCalls == 0u);

    zrender::ZrHudSample a {};
    a.drawCalls = 2u;
    a.rtWidth = 640u;
    a.rtHeight = 480u;
    a.frameMs = 10.0;
    a.memoryBytes = 1000u;
    zrender::ZrHudSample b = a;
    b.drawCalls = 6u; // 峰值样本（后面两个样本都低于它——峰值 ≠ 末样本）
    b.frameMs = 20.0;
    b.memoryBytes = 2000u;
    zrender::ZrHudSample c = a;
    c.drawCalls = 4u;
    c.frameMs = 10.0;
    c.memoryBytes = 3000u;

    agg.Push(a);
    agg.Push(b);
    agg.Push(c);
    const zrender::ZrHudFrameStats snap = agg.Snapshot();
    // 最新值 = 末样本（drawCalls/显存/RT）
    CHECK(snap.drawCalls == 4u);
    CHECK(snap.memoryBytes == 3000ull);
    CHECK(snap.rtWidth == 640u);
    CHECK(snap.rtHeight == 480u);
    // 峰值 = 窗口内最大（变异验证基线：改成"取末样本"即红——末样本 4 ≠ 峰值 6）
    CHECK(snap.drawCallsPeak == 6u);
    // 平均帧耗 = (10+20+10)/3 = 13.333...；fps = 1000/平均
    CHECK(snap.frames == 3u);
    CHECK(snap.frameMsAvg == doctest::Approx(40.0 / 3.0).epsilon(1e-9));
    CHECK(snap.fps == doctest::Approx(1000.0 / (40.0 / 3.0)).epsilon(1e-9));

    // 环形覆盖：容量 8，压 7 个爬升样本 + 1 个回落后样本 → 最新=90、峰值=107
    for (uint32_t i = 0; i < 7u; ++i) {
        zrender::ZrHudSample s {};
        s.drawCalls = 100u + i; // 100..106，峰值 106
        s.frameMs = 1.0;
        agg.Push(s);
    }
    zrender::ZrHudSample drop {};
    drop.drawCalls = 90u; // 回落：最新 ≠ 峰值（第二条变异敏感性断言）
    drop.frameMs = 1.0;
    agg.Push(drop);
    CHECK(agg.Count() == 8u);
    CHECK(agg.Window() == 8u);
    const zrender::ZrHudFrameStats ring = agg.Snapshot();
    CHECK(ring.drawCalls == 90u);            // 最新 = 最后压入的 90
    CHECK(ring.drawCallsPeak == 106u);       // 峰值 106（旧样本已滚出窗口）
    CHECK(ring.frames == 8u);
    CHECK(ring.frameMsAvg == doctest::Approx(1.0).epsilon(1e-9));

    agg.Reset();
    CHECK(agg.Count() == 0u);
    CHECK(agg.Snapshot().frames == 0u);
}

TEST_CASE("gpu-hud: device stats pass through the latest sample")
{
    zrender::ZrHudAggregator agg(4u);
    zrender::ZrHudSample s {};
    s.rtWidth = 640u;
    s.rtHeight = 480u;
    s.device.imageCount = 5u;
    s.device.bufferCount = 7u;
    s.device.aliasHeapCount = 2u;
    s.device.presentFailureCount = 3u;
    s.device.deviceRemoved = true;
    agg.Push(s);
    const zrender::ZrHudFrameStats snap = agg.Snapshot();
    CHECK(snap.imageCount == 5u);
    CHECK(snap.bufferCount == 7u);
    CHECK(snap.aliasHeapCount == 2u);
    CHECK(snap.presentFailureCount == 3u);
    CHECK(snap.deviceRemoved);
}

// ---- 3. 格式化 ---------------------------------------------------------------------
TEST_CASE("gpu-hud: JSON/text formats are single-line and deterministic")
{
    zrender::ZrHudFrameStats stats;
    stats.drawCalls = 3u;
    stats.drawCallsPeak = 5u;
    stats.rtWidth = 640u;
    stats.rtHeight = 480u;
    stats.memoryBytes = 2904064ull;
    stats.imageCount = 6u;
    stats.bufferCount = 5u;
    stats.aliasHeapCount = 2u;
    stats.presentFailureCount = 1u;
    stats.deviceRemoved = false;
    stats.frameMsAvg = 2.5;
    stats.fps = 400.0;
    stats.frames = 60u;

    const std::string json = zrender::ZrHudFormatJson(stats);
    CHECK(json.find('{') == 0u);
    CHECK(json.find('\n') == std::string::npos); // 单行（DevConsole 协议）
    CHECK(json.find("\"drawCalls\":3") != std::string::npos);
    CHECK(json.find("\"drawCallsPeak\":5") != std::string::npos);
    CHECK(json.find("\"rtWidth\":640") != std::string::npos);
    CHECK(json.find("\"rtHeight\":480") != std::string::npos);
    CHECK(json.find("\"memoryBytes\":2904064") != std::string::npos);
    CHECK(json.find("\"imageCount\":6") != std::string::npos);
    CHECK(json.find("\"bufferCount\":5") != std::string::npos);
    CHECK(json.find("\"aliasHeapCount\":2") != std::string::npos);
    CHECK(json.find("\"presentFailureCount\":1") != std::string::npos);
    CHECK(json.find("\"deviceRemoved\":false") != std::string::npos);
    CHECK(json.find("\"frameMs\":2.5000") != std::string::npos);
    CHECK(json.find("\"fps\":400.0000") != std::string::npos);
    CHECK(json.find("\"frames\":60") != std::string::npos);
    CHECK(zrender::ZrHudFormatJson(stats) == json); // 确定性

    const std::string text = zrender::ZrHudFormatText(stats);
    CHECK(text.find('\n') == std::string::npos);
    CHECK(text.find("drawCalls=3 (peak 5)") != std::string::npos);
    CHECK(text.find("rt=640x480") != std::string::npos);
    // 2904064 B = 2.77 MiB
    CHECK(text.find("mem=2.77MiB") != std::string::npos);
    CHECK(text.find("images=6") != std::string::npos);
    CHECK(text.find("presentFail=1") != std::string::npos);
    CHECK(text.find("removed=0") != std::string::npos);
    CHECK(text.find("frameMs=2.5000ms") != std::string::npos);
    CHECK(text.find("fps=400.0000") != std::string::npos);
    CHECK(zrender::ZrHudFormatText(stats) == text);
}
