// =============================================================================
// test_rg_export.cpp —— RG 编译产物导出纯函数单测（TASK-B-R9-16）
//
// 覆盖：
//   1. JSON 导出形状：版本头/pass 列表（执行序+访问集）/culled/edges/
//      aliasPlan/barriers（逐 pass 批次 + split）/lifetimes 逐项断言；
//   2. 与 RgGraph 查询接口同源：barriers.total == TotalBarrierCount() 等交叉验证；
//   3. 文本导出：pass 名/边/别名槽/生命周期区间行齐全；
//   4. 确定性：同一编译产物两次导出逐字节一致（纯函数契约）；
//   5. 编译失败图：error 字段带错误码、集合为空（失败状态也是排查数据）。
// 图结构镜像窗口程序 RunRenderGraphFrame（styleRt/depth/postRt + DSV split），
// 仅 Compile 不 Execute——导出是纯编译产物视图，无需设备。
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#include "zrender/rg_export.h"
#include "zrender/rg_render_graph.h"

namespace {

ZrImageDesc ColorTarget(const char*, uint32_t w = 64, uint32_t h = 64)
{
    ZrImageDesc d;
    d.format = ZrFormat::RGBA8Unorm;
    d.width = w;
    d.height = h;
    d.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::Sampled;
    return d;
}

ZrImageDesc DepthTarget(const char*, uint32_t w = 64, uint32_t h = 64)
{
    ZrImageDesc d;
    d.format = ZrFormat::D32Float;
    d.width = w;
    d.height = h;
    d.usage = ZrImageUsage::DepthStencilAttachment;
    return d;
}

/// 镜像窗口程序 RG 帧结构：styleRt(0..1) depth(1..2, split) postRt(2..3) +
/// backbuffer(Imported, 3..) —— 声明序：back/style/depth/post。
/// 返回句柄表供断言（下标 = 声明序）。
struct FrameGraph {
    RgGraph graph;
    RgHandle back;
    RgHandle style;
    RgHandle depth;
    RgHandle post;

    FrameGraph()
    {
        // Imported 资源只出现在导出的元数据里（本测试不 Execute，无需真设备句柄）
        back = graph.Import(RgResourceDesc::ImportImage("backbuffer", ZrImage {}));
        style = graph.CreateTransient(RgResourceDesc::TransientImage("styleRt",
            ColorTarget("styleRt")));
        depth = graph.CreateTransient(RgResourceDesc::TransientImage("depth",
            DepthTarget("depth")));
        post = graph.CreateTransient(RgResourceDesc::TransientImage("postRt",
            ColorTarget("postRt")));

        graph.AddPass("StylePass", [&](RgPass& pass) { pass.Write(style); }, nullptr);
        graph.AddPass("DepthPrepass", [&](RgPass& pass) { pass.Read(style).Write(depth); },
            nullptr);
        graph.AddPass("CompositePass", [&](RgPass& pass) { pass.Read(depth).Write(post); },
            nullptr);
        graph.AddPass("FinalPass",
            [&](RgPass& pass) {
                pass.Read(post).Write(back, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
            },
            nullptr);
        graph.AddPass("PresentPass", [&](RgPass& pass) { pass.Present(back).MarkRoot(); },
            nullptr);
        REQUIRE(graph.Compile());
    }
};

} // namespace

// ---- 1. JSON 形状：头/资源/Pass/边/别名/屏障/生命周期 ------------------------------
TEST_CASE("rg-export: JSON carries passes, edges, alias plan, barriers, lifetimes")
{
    FrameGraph f;
    const std::string json = zrender::RgExportJson(f.graph);

    // 头：版本 + 5 pass 全存活（无剔除）；单文档对象：首尾括号配对（花括号计数相等）
    CHECK(json.find("\"rgExportVersion\":1") != std::string::npos);
    CHECK(json.find("\"error\":0") != std::string::npos);
    CHECK(json.find("\"passCount\":5") != std::string::npos);
    CHECK(json.find("\"alivePasses\":5") != std::string::npos);
    CHECK(json.find("\"culledPasses\":0") != std::string::npos);

    // 资源表：4 资源（1 Imported + 3 Transient），Transient 带 slot/生命周期
    CHECK(json.find("\"name\":\"backbuffer\",\"residency\":\"Imported\"") != std::string::npos);
    CHECK(json.find("\"name\":\"styleRt\",\"residency\":\"Transient\"") != std::string::npos);
    CHECK(json.find("\"format\":\"D32Float\"") != std::string::npos);

    // Pass 列表：执行序 = 声明序（本图依赖链无翻转）；访问集带资源名
    CHECK(json.find("\"decl\":0,\"name\":\"StylePass\"") != std::string::npos);
    CHECK(json.find("\"decl\":1,\"name\":\"DepthPrepass\"") != std::string::npos);
    CHECK(json.find("\"decl\":2,\"name\":\"CompositePass\"") != std::string::npos);
    CHECK(json.find("\"decl\":3,\"name\":\"FinalPass\"") != std::string::npos);
    CHECK(json.find("\"decl\":4,\"name\":\"PresentPass\"") != std::string::npos);
    // DepthPrepass 的读写集
    CHECK(json.find("{\"resource\":\"styleRt\",\"kind\":\"Read\"}") != std::string::npos);
    CHECK(json.find("{\"resource\":\"depth\",\"kind\":\"Write\"}") != std::string::npos);
    // PresentPass 的 Present 访问
    CHECK(json.find("{\"resource\":\"backbuffer\",\"kind\":\"Present\"}") != std::string::npos);

    // 依赖边：写->读 链（0->1, 1->2, 2->3, 3->4）
    CHECK(json.find("{\"from\":0,\"to\":1}") != std::string::npos);
    CHECK(json.find("{\"from\":1,\"to\":2}") != std::string::npos);
    CHECK(json.find("{\"from\":2,\"to\":3}") != std::string::npos);
    CHECK(json.find("{\"from\":3,\"to\":4}") != std::string::npos);

    // 别名计划：3 Transient（styleRt [0..1] / depth [1..2] / postRt [2..3]）→
    // styleRt 与 postRt 区间不重叠共享 slot0，depth 独占 slot1 → 共 2 槽
    // （与窗口程序 "2 heaps < 3 Transient" 的别名复用口径一致）
    CHECK(json.find("\"aliasPlan\":{\"slotCount\":2") != std::string::npos);
    CHECK(json.find("{\"resource\":\"styleRt\",\"firstUse\":0,\"lastUse\":1,\"slot\":0}")
        != std::string::npos);
    CHECK(json.find("{\"resource\":\"depth\",\"firstUse\":1,\"lastUse\":2,\"slot\":1}")
        != std::string::npos);
    CHECK(json.find("{\"resource\":\"postRt\",\"firstUse\":2,\"lastUse\":3,\"slot\":0}")
        != std::string::npos);

    // 屏障计划：总数与查询接口同源；DSV 写->读产出 split（DepthPrepass 写 depth
    // → CompositePass 读）——split 计划与镜像同在
    const RgBarrierPlan& plan = f.graph.BarrierPlan();
    CHECK(json.find("\"total\":" + std::to_string(plan.TotalBarrierCount()))
        != std::string::npos);
    CHECK(json.find("\"splitList\":[{\"resource\":\"depth\",\"beginAfterPass\":1,"
                    "\"endBeforePass\":2}]")
        != std::string::npos);
    CHECK(plan.SplitCount() == 1u); // 镜像窗口程序的 DSV split 语义
    // 逐 pass 批次带布局名（StylePass 的 styleRt undefined -> color_attachment）
    CHECK(json.find("\"from\":\"undefined\",\"to\":\"color_attachment\"")
        != std::string::npos);

    // 生命周期：三个 Transient 全部被使用
    CHECK(json.find("\"lifetimes\":[") != std::string::npos);
    CHECK(json.find("{\"resource\":\"styleRt\",\"firstUse\":0,\"lastUse\":1}")
        != std::string::npos);
    CHECK(json.find("{\"resource\":\"postRt\",\"firstUse\":2,\"lastUse\":3}")
        != std::string::npos);

    // 单文档形状护栏：首字符 '{'、尾字符 '}'、花括号计数相等（防"头/值漏引号、
    // 外壳多闭括号"类拼接错——曾实测：头多一个 '}'、Image 分支漏一个 '"'）
    REQUIRE(json.size() >= 2u);
    CHECK(json.front() == '{');
    CHECK(json.back() == '}');
    const int64_t openBraces = static_cast<int64_t>(
        std::count(json.begin(), json.end(), '{'));
    const int64_t closeBraces = static_cast<int64_t>(
        std::count(json.begin(), json.end(), '}'));
    CHECK(openBraces == closeBraces);
    // 值引号配对：不存在 `"Image,` / `"Buffer,` 形态（漏闭引号的特征）
    CHECK(json.find("\"Image,") == std::string::npos);
    CHECK(json.find("\"Buffer,") == std::string::npos);
}

// ---- 2. 死 Pass 剔除进 culled 列表 ------------------------------------------------
TEST_CASE("rg-export: culled pass shows in culled list with name")
{
    RgGraph graph;
    const RgHandle back = graph.Import(RgResourceDesc::ImportImage("back", ZrImage {}));
    const RgHandle orphan = graph.CreateTransient(
        RgResourceDesc::TransientImage("orphan", ColorTarget("orphan")));
    graph.AddPass("Orphan", [&](RgPass& pass) { pass.Write(orphan); }, nullptr);
    graph.AddPass("Present", [&](RgPass& pass) { pass.Present(back).MarkRoot(); }, nullptr);
    REQUIRE(graph.Compile());
    REQUIRE(graph.CulledPasses().size() == 1u);

    const std::string json = zrender::RgExportJson(graph);
    CHECK(json.find("\"culledPasses\":1") != std::string::npos);
    CHECK(json.find("{\"decl\":0,\"name\":\"Orphan\"}") != std::string::npos);
    // 存活 pass 只有 Present
    CHECK(json.find("\"alivePasses\":1") != std::string::npos);
    CHECK(json.find("\"name\":\"Present\"") != std::string::npos);

    const std::string text = zrender::RgExportText(graph);
    CHECK(text.find("CULLED PASSES") != std::string::npos);
    CHECK(text.find("Orphan") != std::string::npos);
}

// ---- 3. 文本导出：人读视图齐全 -----------------------------------------------------
TEST_CASE("rg-export: text view lists passes, reads/writes, edges, slots")
{
    FrameGraph f;
    const std::string text = zrender::RgExportText(f.graph);

    CHECK(text.find("RG EXPORT v1") != std::string::npos);
    CHECK(text.find("passes=5") != std::string::npos);
    CHECK(text.find("aliasSlots=2") != std::string::npos);
    CHECK(text.find("RESOURCES:") != std::string::npos);
    CHECK(text.find("styleRt") != std::string::npos);
    CHECK(text.find("D32Float") != std::string::npos);
    // Transient 资源带 slot 与生命周期区间（甘特视图；postRt 复用 styleRt 的槽）
    CHECK(text.find("styleRt") != std::string::npos);
    CHECK(text.find("slot=0 lifetime=[0..1]") != std::string::npos);
    CHECK(text.find("slot=1 lifetime=[1..2]") != std::string::npos);
    CHECK(text.find("slot=0 lifetime=[2..3]") != std::string::npos);
    // Pass 读写集
    CHECK(text.find("1. DepthPrepass (decl 1)") != std::string::npos);
    CHECK(text.find("reads : styleRt") != std::string::npos);
    CHECK(text.find("writes: depth") != std::string::npos);
    // 依赖边
    CHECK(text.find("0 -> 1") != std::string::npos);
    CHECK(text.find("3 -> 4") != std::string::npos);
    // split 计划
    CHECK(text.find("SPLIT BARRIERS:") != std::string::npos);
    CHECK(text.find("depth: begin after pass 1, end before pass 2") != std::string::npos);
}

// ---- 4. 确定性（纯函数契约）与名表助手 ----------------------------------------------
TEST_CASE("rg-export: deterministic output and enum name helpers")
{
    FrameGraph f;
    CHECK(zrender::RgExportJson(f.graph) == zrender::RgExportJson(f.graph));
    CHECK(zrender::RgExportText(f.graph) == zrender::RgExportText(f.graph));

    using zrender::RgAccessBitsName;
    using zrender::RgFormatName;
    using zrender::RgImageLayoutName;
    using zrender::RgPipelineStageName;
    CHECK(std::string(RgImageLayoutName(ZrImageLayout::Undefined)) == "undefined");
    CHECK(std::string(RgImageLayoutName(ZrImageLayout::Present)) == "present");
    CHECK(std::string(RgAccessBitsName(ZrAccessBits::None)) == "none");
    CHECK(std::string(RgAccessBitsName(ZrAccessBits::ColorAttachmentWrite))
        == "color_attachment_write");
    CHECK(std::string(RgAccessBitsName(
              ZrAccessBits::ShaderRead | ZrAccessBits::ColorAttachmentWrite))
        == "shader_read|color_attachment_write");
    CHECK(std::string(RgPipelineStageName(ZrPipelineStageBits::Transfer)) == "transfer");
    CHECK(std::string(RgFormatName(ZrFormat::RGBA8Unorm)) == "RGBA8Unorm");
    CHECK(std::string(RgFormatName(ZrFormat::Count)) == "unknown"); // 哨兵不进名表
}

// ---- 5. 编译失败图：error 字段带码、集合为空 ----------------------------------------
TEST_CASE("rg-export: uncompiled/failed graph exports skeleton with error code")
{
    // 无根图（无 Imported 写、无显式 Root）→ Compile 失败 RgError::NoRoots
    RgGraph graph;
    const RgHandle a = graph.CreateTransient(RgResourceDesc::TransientImage("a",
        ColorTarget("a")));
    graph.AddPass("A", [&](RgPass& pass) { pass.Write(a); }, nullptr);
    CHECK_FALSE(graph.Compile());
    CHECK(graph.Error() == RgError::NoRoots);

    const std::string json = zrender::RgExportJson(graph);
    CHECK(json.find("\"error\":3") != std::string::npos); // RgError::NoRoots = 3
    CHECK(json.find("\"alivePasses\":0") != std::string::npos);
    CHECK(json.find("\"passes\":[]") != std::string::npos);
    CHECK(json.find("\"edges\":[]") != std::string::npos);
    CHECK(json.find("\"aliasPlan\":{\"slotCount\":0") != std::string::npos);

    const std::string text = zrender::RgExportText(graph);
    CHECK(text.find("error=3") != std::string::npos);
}

// =============================================================================
// Imported 资源必须导出真实 desc（缺口：曾把 depth 报成 RGBA8Unorm 1x1）
//
// 背景：RgResourceDesc 的 imageDesc 默认就是 RGBA8Unorm 1x1（ZrImageDesc 自身的
// 默认值），所以"没填"和"填了个 1x1"无法区分。加 hasImageDesc 显式标记后：
//   - ImportImage(name, image)             → hasImageDesc=false（靠 resolver 补全）
//   - ImportImage(name, image, desc)       → hasImageDesc=true（导出真实值）
// 本用例钉死第二条：导出的 format/width/height 必须等于传入的 desc，而不是默认值。
// =============================================================================
TEST_CASE("rg-export: imported image with explicit desc exports real format and size")
{
    ZrImageDesc depthDesc;
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = 640;
    depthDesc.height = 480;

    RgGraph graph;
    // 三参重载：显式给真实 desc（Unity target 走的就是这条）
    const RgHandle depthH = graph.Import(
        RgResourceDesc::ImportImage("zr_depth", ZrImage {}, depthDesc));

    graph.AddPass("WriteDepth",
        [&](RgPass& pass) {
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        nullptr);
    graph.AddPass("Present", [&](RgPass& pass) { pass.Present(depthH).MarkRoot(); }, nullptr);
    REQUIRE(graph.Compile());

    const std::string json = zrender::RgExportJson(graph);
    // 真实格式与尺寸必须出现在导出里
    CHECK(json.find("\"format\":\"D32Float\"") != std::string::npos);
    CHECK(json.find("\"width\":640") != std::string::npos);
    CHECK(json.find("\"height\":480") != std::string::npos);
    // 且不能再出现占位值（1x1 的默认 desc）
    CHECK(json.find("\"width\":1,") == std::string::npos);
}

TEST_CASE("rg-export: imported image without desc reports the default placeholder")
{
    // 两参重载不携带 desc，也没有注册 resolver 时，应如实报默认值（说明这是"未提供"
    // 而非"真实就是 1x1"）。这条用例固定现状，避免有人误以为两参重载能导出真实值。
    RgGraph graph;
    const RgHandle backH = graph.Import(RgResourceDesc::ImportImage("backbuffer", ZrImage {}));
    graph.AddPass("Present", [&](RgPass& pass) { pass.Present(backH).MarkRoot(); }, nullptr);
    REQUIRE(graph.Compile());

    const std::string json = zrender::RgExportJson(graph);
    CHECK(json.find("\"format\":\"RGBA8Unorm\"") != std::string::npos);
    CHECK(json.find("\"width\":1,") != std::string::npos);
}
