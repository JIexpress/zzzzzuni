// =============================================================================
// rg_export.cpp —— RG 编译产物导出实现（TASK-B-R9-16）
//
// 纯函数：只读 RgGraph 的编译产物查询接口（CompiledOrder/CulledPasses/Edges/
// AliasPlan/BarrierPlan/LifetimeOf/PassNameAt/PassAccessesAt/ResourceDescAt），
// 不触碰 RgGraph 可变状态，不做任何 IO（写盘由调用方负责）。
// 名字表全部 switch/查表实现——无静态注册、无 map（保证输出确定性）。
// =============================================================================
#include "zrender/rg_export.h"

#include <cstdint>
#include <cstdio>
#include <vector>

#include "zrhi/zrhi_barriers.h"

namespace zrender {

// =============================================================================
// 枚举名表（switch 全覆盖；ZrFormat 按 traits 表同源的顺序枚举查表）
// =============================================================================

const char* RgImageLayoutName(ZrImageLayout layout)
{
    switch (layout) {
        case ZrImageLayout::Undefined: return "undefined";
        case ZrImageLayout::General: return "general";
        case ZrImageLayout::ColorAttachmentOptimal: return "color_attachment";
        case ZrImageLayout::DepthStencilAttachmentOptimal: return "depth_stencil_attachment";
        case ZrImageLayout::DepthStencilReadOnlyOptimal: return "depth_stencil_read_only";
        case ZrImageLayout::ShaderReadOnlyOptimal: return "shader_read_only";
        case ZrImageLayout::TransferSrcOptimal: return "transfer_src";
        case ZrImageLayout::TransferDstOptimal: return "transfer_dst";
        case ZrImageLayout::Present: return "present";
        default: return "unknown";
    }
}

std::string RgAccessBitsName(ZrAccessBits access)
{
    if (access == ZrAccessBits::None) {
        return "none";
    }
    static const struct {
        ZrAccessBits bit;
        const char* name;
    } kNames[] = {
        { ZrAccessBits::IndirectCommandRead, "indirect_command_read" },
        { ZrAccessBits::IndexRead, "index_read" },
        { ZrAccessBits::VertexAttributeRead, "vertex_attribute_read" },
        { ZrAccessBits::UniformRead, "uniform_read" },
        { ZrAccessBits::InputAttachmentRead, "input_attachment_read" },
        { ZrAccessBits::ShaderRead, "shader_read" },
        { ZrAccessBits::ShaderWrite, "shader_write" },
        { ZrAccessBits::ColorAttachmentRead, "color_attachment_read" },
        { ZrAccessBits::ColorAttachmentWrite, "color_attachment_write" },
        { ZrAccessBits::DepthStencilAttachmentRead, "ds_attachment_read" },
        { ZrAccessBits::DepthStencilAttachmentWrite, "ds_attachment_write" },
        { ZrAccessBits::TransferRead, "transfer_read" },
        { ZrAccessBits::TransferWrite, "transfer_write" },
        { ZrAccessBits::MemoryRead, "memory_read" },
        { ZrAccessBits::MemoryWrite, "memory_write" },
    };
    std::string out;
    const uint32_t mask = static_cast<uint32_t>(access);
    for (const auto& entry : kNames) {
        if ((mask & static_cast<uint32_t>(entry.bit)) != 0u) {
            if (!out.empty()) {
                out += '|';
            }
            out += entry.name;
        }
    }
    if (out.empty()) {
        // 全部位都落在未命名区间（后续任务扩展枚举后此路径应不可达）
        char tmp[16];
        std::snprintf(tmp, sizeof(tmp), "bits_0x%X", mask);
        return tmp;
    }
    return out;
}

std::string RgPipelineStageName(ZrPipelineStageBits stage)
{
    if (stage == ZrPipelineStageBits::None) {
        return "none";
    }
    static const struct {
        ZrPipelineStageBits bit;
        const char* name;
    } kNames[] = {
        { ZrPipelineStageBits::TopOfPipe, "top_of_pipe" },
        { ZrPipelineStageBits::DrawIndirect, "draw_indirect" },
        { ZrPipelineStageBits::VertexInput, "vertex_input" },
        { ZrPipelineStageBits::VertexShader, "vertex_shader" },
        { ZrPipelineStageBits::FragmentShader, "fragment_shader" },
        { ZrPipelineStageBits::EarlyFragmentTests, "early_fragment_tests" },
        { ZrPipelineStageBits::LateFragmentTests, "late_fragment_tests" },
        { ZrPipelineStageBits::ColorAttachmentOutput, "color_attachment_output" },
        { ZrPipelineStageBits::ComputeShader, "compute_shader" },
        { ZrPipelineStageBits::Transfer, "transfer" },
        { ZrPipelineStageBits::BottomOfPipe, "bottom_of_pipe" },
    };
    std::string out;
    const uint32_t mask = static_cast<uint32_t>(stage);
    for (const auto& entry : kNames) {
        if ((mask & static_cast<uint32_t>(entry.bit)) != 0u) {
            if (!out.empty()) {
                out += '|';
            }
            out += entry.name;
        }
    }
    if (out.empty()) {
        char tmp[16];
        std::snprintf(tmp, sizeof(tmp), "bits_0x%X", mask);
        return tmp;
    }
    return out;
}

const char* RgFormatName(ZrFormat format)
{
    // 顺序与 zrhi_types.h 的 ZrFormat 枚举字面量一一对应（头内 static_assert
    // 已保证 traits 全枚举覆盖；此处表序 = 枚举序，Count 哨兵不在此表内）
    static const char* kNames[] = {
        "RGBA8Unorm", "BGRA8Unorm", "RGBA8sRGB", "R8Unorm", "R16Float", "RG16Float",
        "R11G11B10UFloat", "RGBA16Float", "R32Float", "RGBA32Float", "R32Uint",
        "D32Float", "D24UnormS8", "D16Unorm",
    };
    static_assert(sizeof(kNames) / sizeof(kNames[0]) == static_cast<size_t>(ZrFormat::Count),
        "RgFormatName 名表与 ZrFormat 枚举失同步");
    const uint32_t v = static_cast<uint32_t>(format);
    if (v >= sizeof(kNames) / sizeof(kNames[0])) {
        return "unknown";
    }
    return kNames[v];
}

// =============================================================================
// 内部：JSON 字符串转义与资源名解析
// =============================================================================
namespace {

/// JSON 字符串转义（pass/资源名来自调用方传入的 const char*，防御性转义）
std::string JsonEscape(const char* s)
{
    std::string out;
    if (s == nullptr) {
        return out;
    }
    for (const char* p = s; *p != '\0'; ++p) {
        const unsigned char c = *p;
        if (c == '"' || c == '\\') {
            out += '\\';
            out += static_cast<char>(c);
        } else if (c < 0x20u) {
            char tmp[8];
            std::snprintf(tmp, sizeof(tmp), "\\u%04X", c);
            out += tmp;
        } else {
            out += static_cast<char>(c);
        }
    }
    return out;
}

std::string JsonEscape(const std::string& s)
{
    return JsonEscape(s.c_str());
}

/// 资源句柄 -> 展示名（查 desc.name；句柄越界/未声明时回退 "res#<index>"）
std::string ResourceDisplayName(const RgGraph& graph, RgHandle handle)
{
    const RgResourceDesc* desc = graph.ResourceDescAt(handle.index);
    if (desc != nullptr && desc->name != nullptr && desc->name[0] != '\0') {
        return desc->name;
    }
    char tmp[32];
    std::snprintf(tmp, sizeof(tmp), "res#%u", handle.index);
    return tmp;
}

} // namespace

// =============================================================================
// JSON 导出
// =============================================================================
std::string RgExportJson(const RgGraph& graph)
{
    std::string out;
    out.reserve(4096u);
    char tmp[128];

    // ---- 头：版本 + 错误码 + 概要计数（单文档对象，闭括号在函数末尾）-------------
    std::snprintf(tmp, sizeof(tmp),
        "{\"rgExportVersion\":%u,\"error\":%u,\"passCount\":%u,\"alivePasses\":%u,"
        "\"culledPasses\":%u",
        kRgExportVersion, static_cast<unsigned>(graph.Error()), graph.PassCount(),
        static_cast<unsigned>(graph.CompiledOrder().size()),
        static_cast<unsigned>(graph.CulledPasses().size()));
    out += tmp;

    // ---- 资源表（声明序；图像带尺寸/格式，缓冲带字节数）-------------------------
    out += ",\"resources\":[";
    for (uint32_t i = 0; i < graph.ResourceCount(); ++i) {
        const RgResourceDesc* desc = graph.ResourceDescAt(i);
        if (desc == nullptr) {
            continue;
        }
        if (i != 0u) {
            out += ',';
        }
        out += "{\"index\":";
        std::snprintf(tmp, sizeof(tmp), "%u", i);
        out += tmp;
        out += ",\"name\":\"";
        out += JsonEscape(desc->name);
        out += "\",\"residency\":\"";
        out += desc->residency == RgResidency::Transient ? "Transient" : "Imported";
        out += "\",\"type\":\"";
        out += desc->type == RgResourceType::Image ? "Image" : "Buffer";
        if (desc->type == RgResourceType::Image) {
            std::snprintf(tmp, sizeof(tmp),
                "\",\"width\":%u,\"height\":%u,\"arrayLayers\":%u,\"mipLevels\":%u,"
                "\"format\":\"%s\"",
                desc->imageDesc.width, desc->imageDesc.height, desc->imageDesc.arrayLayers,
                desc->imageDesc.mipLevels, RgFormatName(desc->imageDesc.format));
            out += tmp;
        } else {
            std::snprintf(tmp, sizeof(tmp), "\",\"sizeBytes\":%llu",
                static_cast<unsigned long long>(desc->bufferDesc.size));
            out += tmp;
        }
        out += '}';
    }
    out += ']';

    // ---- Pass 列表（执行序；decl=声明序号，可对照 edges/culled）-----------------
    out += ",\"passes\":[";
    const std::vector<uint32_t>& order = graph.CompiledOrder();
    for (size_t oi = 0; oi < order.size(); ++oi) {
        const uint32_t decl = order[oi];
        if (oi != 0u) {
            out += ',';
        }
        std::snprintf(tmp, sizeof(tmp), "{\"order\":%zu,\"decl\":%u,\"name\":\"", oi, decl);
        out += tmp;
        out += JsonEscape(graph.PassNameAt(decl));
        out += "\",\"accesses\":[";
        const std::vector<RgPassAccess>* accesses = graph.PassAccessesAt(decl);
        if (accesses != nullptr) {
            for (size_t ai = 0; ai < accesses->size(); ++ai) {
                const RgPassAccess& access = (*accesses)[ai];
                if (ai != 0u) {
                    out += ',';
                }
                out += "{\"resource\":\"";
                out += JsonEscape(ResourceDisplayName(graph, access.resource));
                out += "\",\"kind\":\"";
                out += access.kind == RgPassAccessKind::Read
                        ? "Read"
                        : (access.kind == RgPassAccessKind::Write ? "Write" : "Present");
                out += "\"}";
            }
        }
        out += "]}";
    }
    out += ']';

    // ---- 被剔除 Pass（死代码剔除证据：decl + 名字）------------------------------
    out += ",\"culled\":[";
    const std::vector<uint32_t>& culled = graph.CulledPasses();
    for (size_t ci = 0; ci < culled.size(); ++ci) {
        if (ci != 0u) {
            out += ',';
        }
        std::snprintf(tmp, sizeof(tmp), "{\"decl\":%u,\"name\":\"", culled[ci]);
        out += tmp;
        out += JsonEscape(graph.PassNameAt(culled[ci]));
        out += "\"}";
    }
    out += ']';

    // ---- 依赖边（decl -> decl；拓扑序的输入数据）--------------------------------
    out += ",\"edges\":[";
    bool firstEdge = true;
    const std::vector<std::vector<uint32_t>>& edges = graph.Edges();
    for (uint32_t from = 0; from < edges.size(); ++from) {
        for (const uint32_t to : edges[from]) {
            if (!firstEdge) {
                out += ',';
            }
            firstEdge = false;
            std::snprintf(tmp, sizeof(tmp), "{\"from\":%u,\"to\":%u}", from, to);
            out += tmp;
        }
    }
    out += ']';

    // ---- 别名池槽计划（内存复用的核心证据）--------------------------------------
    const RgAliasPlan& alias = graph.AliasPlan();
    std::snprintf(tmp, sizeof(tmp), ",\"aliasPlan\":{\"slotCount\":%u,\"entries\":[",
        alias.slotCount);
    out += tmp;
    for (size_t ei = 0; ei < alias.entries.size(); ++ei) {
        const RgAliasPlan::Entry& entry = alias.entries[ei];
        if (ei != 0u) {
            out += ',';
        }
        out += "{\"resource\":\"";
        out += JsonEscape(ResourceDisplayName(graph, entry.resource));
        std::snprintf(tmp, sizeof(tmp), "\",\"firstUse\":%u,\"lastUse\":%u,\"slot\":%u}",
            entry.firstUse, entry.lastUse, entry.slot);
        out += tmp;
    }
    out += "]}";

    // ---- 屏障计划（逐 pass 批次 + split 计划 + 汇总计数）-------------------------
    const RgBarrierPlan& barriers = graph.BarrierPlan();
    std::snprintf(tmp, sizeof(tmp),
        ",\"barriers\":{\"total\":%u,\"inlineImage\":%u,\"inlineBuffer\":%u,"
        "\"splitMirrors\":%u,\"splits\":%u,\"perPass\":[",
        barriers.TotalBarrierCount(), barriers.InlineImageBarrierCount(),
        barriers.InlineBufferBarrierCount(), barriers.SplitMirrorCount(), barriers.SplitCount());
    out += tmp;
    for (size_t pi = 0; pi < barriers.perPassImageBatches.size(); ++pi) {
        if (pi != 0u) {
            out += ',';
        }
        std::snprintf(tmp, sizeof(tmp), "{\"pass\":%zu,\"image\":[", pi);
        out += tmp;
        const std::vector<ZrImageBarrier>& imageBatch = barriers.perPassImageBatches[pi];
        const std::vector<RgHandle>& imageRes
            = pi < barriers.perPassImageBatchResources.size()
                ? barriers.perPassImageBatchResources[pi]
                : std::vector<RgHandle>();
        const std::vector<bool>& mirrors
            = pi < barriers.perPassImageBatchSplitMirror.size()
                ? barriers.perPassImageBatchSplitMirror[pi]
                : std::vector<bool>();
        for (size_t bi = 0; bi < imageBatch.size(); ++bi) {
            if (bi != 0u) {
                out += ',';
            }
            const ZrImageBarrier& b = imageBatch[bi];
            const std::string resName
                = bi < imageRes.size() ? ResourceDisplayName(graph, imageRes[bi]) : "";
            out += "{\"resource\":\"";
            out += JsonEscape(resName);
            out += "\",\"from\":\"";
            out += RgImageLayoutName(b.prevLayout);
            out += "\",\"to\":\"";
            out += RgImageLayoutName(b.newLayout);
            out += "\",\"srcAccess\":\"";
            out += RgAccessBitsName(b.srcAccess);
            out += "\",\"dstAccess\":\"";
            out += RgAccessBitsName(b.dstAccess);
            out += "\",\"srcStage\":\"";
            out += RgPipelineStageName(b.srcStage);
            out += "\",\"dstStage\":\"";
            out += RgPipelineStageName(b.dstStage);
            out += "\",\"splitMirror\":";
            out += (bi < mirrors.size() && mirrors[bi]) ? "true" : "false";
            out += '}';
        }
        out += "],\"buffer\":[";
        if (pi < barriers.perPassBufferBatches.size()) {
            const std::vector<ZrBufferBarrier>& bufferBatch = barriers.perPassBufferBatches[pi];
            const std::vector<RgHandle>& bufferRes
                = pi < barriers.perPassBufferBatchResources.size()
                    ? barriers.perPassBufferBatchResources[pi]
                    : std::vector<RgHandle>();
            for (size_t bi = 0; bi < bufferBatch.size(); ++bi) {
                if (bi != 0u) {
                    out += ',';
                }
                const ZrBufferBarrier& b = bufferBatch[bi];
                const std::string resName
                    = bi < bufferRes.size() ? ResourceDisplayName(graph, bufferRes[bi]) : "";
                out += "{\"resource\":\"";
                out += JsonEscape(resName);
                std::snprintf(tmp, sizeof(tmp),
                    "\",\"offset\":%llu,\"sizeBytes\":%llu,\"srcAccess\":\"",
                    static_cast<unsigned long long>(b.offset),
                    static_cast<unsigned long long>(b.size));
                out += tmp;
                out += RgAccessBitsName(b.srcAccess);
                out += "\",\"dstAccess\":\"";
                out += RgAccessBitsName(b.dstAccess);
                out += "\"}";
            }
        }
        out += "]}";
    }
    out += "],\"splitList\":[";
    for (size_t si = 0; si < barriers.splits.size(); ++si) {
        const RgSplitBarrier& split = barriers.splits[si];
        if (si != 0u) {
            out += ',';
        }
        out += "{\"resource\":\"";
        out += JsonEscape(ResourceDisplayName(graph, split.resource));
        std::snprintf(tmp, sizeof(tmp), "\",\"beginAfterPass\":%u,\"endBeforePass\":%u}",
            split.beginAfterPass, split.endBeforePass);
        out += tmp;
    }
    out += "]}";

    // ---- Transient 生命周期（甘特视图数据；Imported 不参与）----------------------
    out += ",\"lifetimes\":[";
    bool firstLife = true;
    for (uint32_t i = 0; i < graph.ResourceCount(); ++i) {
        const RgResourceDesc* desc = graph.ResourceDescAt(i);
        if (desc == nullptr || desc->residency != RgResidency::Transient) {
            continue;
        }
        const RgHandle handle = graph.ResourceHandleAt(i);
        const RgLifetime life = graph.LifetimeOf(handle);
        if (!life.used) {
            continue;
        }
        if (!firstLife) {
            out += ',';
        }
        firstLife = false;
        out += "{\"resource\":\"";
        out += JsonEscape(ResourceDisplayName(graph, handle));
        std::snprintf(tmp, sizeof(tmp),
            "\",\"firstUse\":%u,\"lastUse\":%u}", life.firstUse, life.lastUse);
        out += tmp;
    }
    out += "]}";

    return out;
}

// =============================================================================
// 文本导出（人类可读；与 JSON 同源数据）
// =============================================================================
std::string RgExportText(const RgGraph& graph)
{
    std::string out;
    out.reserve(2048u);
    char tmp[160];

    const RgBarrierPlan& barriers = graph.BarrierPlan();
    std::snprintf(tmp, sizeof(tmp),
        "RG EXPORT v%u | error=%u | passes=%u (alive %u culled %u) | barriers=%u "
        "(img %u buf %u split %u) | aliasSlots=%u\n",
        kRgExportVersion, static_cast<unsigned>(graph.Error()), graph.PassCount(),
        static_cast<unsigned>(graph.CompiledOrder().size()),
        static_cast<unsigned>(graph.CulledPasses().size()), barriers.TotalBarrierCount(),
        barriers.InlineImageBarrierCount(), barriers.InlineBufferBarrierCount(),
        barriers.SplitCount(), graph.AliasPlan().slotCount);
    out += tmp;

    // 资源表（带 Transient 的 slot/lifetime = 甘特区间）
    out += "RESOURCES:\n";
    for (uint32_t i = 0; i < graph.ResourceCount(); ++i) {
        const RgResourceDesc* desc = graph.ResourceDescAt(i);
        if (desc == nullptr) {
            continue;
        }
        std::snprintf(tmp, sizeof(tmp), "  [%u] %-20s %-9s %-6s ", i, desc->name,
            desc->residency == RgResidency::Transient ? "Transient" : "Imported",
            desc->type == RgResourceType::Image ? "Image" : "Buffer");
        out += tmp;
        if (desc->type == RgResourceType::Image) {
            std::snprintf(tmp, sizeof(tmp), "%ux%ux%u mips=%u fmt=%s",
                desc->imageDesc.width, desc->imageDesc.height, desc->imageDesc.arrayLayers,
                desc->imageDesc.mipLevels, RgFormatName(desc->imageDesc.format));
            out += tmp;
        } else {
            std::snprintf(tmp, sizeof(tmp), "%llu B",
                static_cast<unsigned long long>(desc->bufferDesc.size));
            out += tmp;
        }
        if (desc->residency == RgResidency::Transient) {
            const RgHandle handle = graph.ResourceHandleAt(i);
            const RgLifetime life = graph.LifetimeOf(handle);
            const int32_t slot = graph.AliasPlan().SlotOf(handle);
            if (life.used) {
                std::snprintf(tmp, sizeof(tmp), " slot=%d lifetime=[%u..%u]", slot, life.firstUse,
                    life.lastUse);
                out += tmp;
            } else {
                out += " (unused)";
            }
        }
        out += '\n';
    }

    // Pass 列表（执行序 + 访问集 + 逐 pass 屏障批次）
    out += "PASSES (execution order):\n";
    const std::vector<uint32_t>& order = graph.CompiledOrder();
    for (size_t oi = 0; oi < order.size(); ++oi) {
        const uint32_t decl = order[oi];
        std::snprintf(tmp, sizeof(tmp), "  %zu. %s (decl %u)\n", oi, graph.PassNameAt(decl),
            decl);
        out += tmp;
        const std::vector<RgPassAccess>* accesses = graph.PassAccessesAt(decl);
        if (accesses != nullptr) {
            std::string reads, writes;
            for (const RgPassAccess& access : *accesses) {
                std::string& sink = access.kind == RgPassAccessKind::Read ? reads : writes;
                if (!sink.empty()) {
                    sink += ' ';
                }
                sink += ResourceDisplayName(graph, access.resource);
            }
            out += "     reads : ";
            out += reads.empty() ? "-" : reads;
            out += "\n     writes: ";
            out += writes.empty() ? "-" : writes;
            out += '\n';
        }
        // 逐 pass 屏障批次（perPass* 下标 = 执行序，与 oi 对齐）
        if (oi < barriers.perPassImageBatches.size()
            && !barriers.perPassImageBatches[oi].empty()) {
            const std::vector<RgHandle>& imageRes
                = oi < barriers.perPassImageBatchResources.size()
                    ? barriers.perPassImageBatchResources[oi]
                    : std::vector<RgHandle>();
            out += "     barriers:\n";
            for (size_t bi = 0; bi < barriers.perPassImageBatches[oi].size(); ++bi) {
                const ZrImageBarrier& b = barriers.perPassImageBatches[oi][bi];
                const std::string resName
                    = bi < imageRes.size() ? ResourceDisplayName(graph, imageRes[bi]) : "?";
                const bool mirror = oi < barriers.perPassImageBatchSplitMirror.size()
                    && bi < barriers.perPassImageBatchSplitMirror[oi].size()
                    && barriers.perPassImageBatchSplitMirror[oi][bi];
                std::snprintf(tmp, sizeof(tmp), "       img %-16s %s -> %s%s\n", resName.c_str(),
                    RgImageLayoutName(b.prevLayout), RgImageLayoutName(b.newLayout),
                    mirror ? " (split mirror)" : "");
                out += tmp;
            }
        }
        if (oi < barriers.perPassBufferBatches.size()
            && !barriers.perPassBufferBatches[oi].empty()) {
            const std::vector<RgHandle>& bufferRes
                = oi < barriers.perPassBufferBatchResources.size()
                    ? barriers.perPassBufferBatchResources[oi]
                    : std::vector<RgHandle>();
            for (size_t bi = 0; bi < barriers.perPassBufferBatches[oi].size(); ++bi) {
                const ZrBufferBarrier& b = barriers.perPassBufferBatches[oi][bi];
                const std::string resName
                    = bi < bufferRes.size() ? ResourceDisplayName(graph, bufferRes[bi]) : "?";
                std::snprintf(tmp, sizeof(tmp), "       buf %-16s %s -> %s\n", resName.c_str(),
                    RgAccessBitsName(b.srcAccess).c_str(), RgAccessBitsName(b.dstAccess).c_str());
                out += tmp;
            }
        }
    }

    // 被剔除 Pass
    const std::vector<uint32_t>& culled = graph.CulledPasses();
    if (!culled.empty()) {
        out += "CULLED PASSES (dead-pass elimination):\n";
        for (const uint32_t decl : culled) {
            std::snprintf(tmp, sizeof(tmp), "  decl %u: %s\n", decl, graph.PassNameAt(decl));
            out += tmp;
        }
    }

    // 依赖边
    out += "EDGES (decl -> decl):\n";
    const std::vector<std::vector<uint32_t>>& edges = graph.Edges();
    for (uint32_t from = 0; from < edges.size(); ++from) {
        for (const uint32_t to : edges[from]) {
            std::snprintf(tmp, sizeof(tmp), "  %u -> %u\n", from, to);
            out += tmp;
        }
    }

    // split barrier 计划
    if (!barriers.splits.empty()) {
        out += "SPLIT BARRIERS:\n";
        for (const RgSplitBarrier& split : barriers.splits) {
            out += "  ";
            out += ResourceDisplayName(graph, split.resource);
            std::snprintf(tmp, sizeof(tmp), ": begin after pass %u, end before pass %u\n",
                split.beginAfterPass, split.endBeforePass);
            out += tmp;
        }
    }

    return out;
}

} // namespace zrender
