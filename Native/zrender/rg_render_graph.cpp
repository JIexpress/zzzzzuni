// =============================================================================
// rg_render_graph.cpp —— 手写 RenderGraph 纯逻辑层实现（TASK-B-R3-01）
//
// 核心不变式：
//   1. 屏障只在真实状态变化处产出：layout 变化 或 访问不兼容（含写足迹）。
//      读-读兼容（ZrAccessCompatible）→ 连续只读 Pass 之间零屏障（只读共享）。
//   2. 同一 Pass 前的全部屏障合并进该 Pass 的一次批量 ResourceBarrier 提交。
//   3. DSV 写->后续读 的转换输出 split barrier 计划（不进内联批次，执行归下任务）。
//   4. 别名槽：生命周期区间 [firstUse, lastUse]（编译序）不重叠才可共槽。
// =============================================================================
#include <cstdio>

#include "zrender/rg_render_graph.h"

#include <algorithm>

// =============================================================================
// 访问推导：按资源 desc 用途把 Read/Write/Present 映射到 (layout, access, stage)
// =============================================================================
namespace {

struct SyncTriple {
    ZrImageLayout layout;
    ZrAccessBits access;
    ZrPipelineStageBits stage;
};

SyncTriple DeriveImageSync(RgPassAccessKind kind, const ZrImageUsage& usage)
{
    const bool color = ZrHasAny(usage, ZrImageUsage::ColorAttachment);
    const bool depth = ZrHasAny(usage, ZrImageUsage::DepthStencilAttachment);
    const bool sampled = ZrHasAny(usage, ZrImageUsage::Sampled);
    const bool storage = ZrHasAny(usage, ZrImageUsage::Storage);
    const bool transferSrc = ZrHasAny(usage, ZrImageUsage::TransferSrc);
    const bool transferDst = ZrHasAny(usage, ZrImageUsage::TransferDst);

    switch (kind) {
    case RgPassAccessKind::Present:
        return { ZrImageLayout::Present, ZrAccessBits::MemoryRead,
            ZrPipelineStageBits::BottomOfPipe };
    case RgPassAccessKind::Write:
        if (color) {
            return { ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite,
                ZrPipelineStageBits::ColorAttachmentOutput };
        }
        if (depth) {
            return { ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite,
                ZrPipelineStageBits::LateFragmentTests };
        }
        if (storage) {
            return { ZrImageLayout::General, ZrAccessBits::ShaderWrite,
                ZrPipelineStageBits::ComputeShader };
        }
        if (transferDst) {
            return { ZrImageLayout::TransferDstOptimal, ZrAccessBits::TransferWrite,
                ZrPipelineStageBits::Transfer };
        }
        return { ZrImageLayout::General, ZrAccessBits::MemoryWrite,
            ZrPipelineStageBits::BottomOfPipe };
    case RgPassAccessKind::Read:
    default:
        if (sampled || storage) {
            return { ZrImageLayout::ShaderReadOnlyOptimal, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::FragmentShader };
        }
        if (depth) {
            return { ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrAccessBits::DepthStencilAttachmentRead,
                ZrPipelineStageBits::EarlyFragmentTests };
        }
        if (transferSrc) {
            return { ZrImageLayout::TransferSrcOptimal, ZrAccessBits::TransferRead,
                ZrPipelineStageBits::Transfer };
        }
        return { ZrImageLayout::General, ZrAccessBits::MemoryRead,
            ZrPipelineStageBits::BottomOfPipe };
    }
}

SyncTriple DeriveBufferSync(RgPassAccessKind kind, const ZrBufferUsage& usage)
{
    const bool uniform = ZrHasAny(usage, ZrBufferUsage::Uniform);
    const bool storage = ZrHasAny(usage, ZrBufferUsage::Storage);
    const bool transferSrc = ZrHasAny(usage, ZrBufferUsage::TransferSrc);
    const bool transferDst = ZrHasAny(usage, ZrBufferUsage::TransferDst);

    switch (kind) {
    case RgPassAccessKind::Write:
        if (transferDst) {
            return { ZrImageLayout::Undefined, ZrAccessBits::TransferWrite,
                ZrPipelineStageBits::Transfer };
        }
        return { ZrImageLayout::Undefined, ZrAccessBits::ShaderWrite,
            ZrPipelineStageBits::ComputeShader };
    case RgPassAccessKind::Present:
    case RgPassAccessKind::Read:
    default:
        if (uniform) {
            return { ZrImageLayout::Undefined, ZrAccessBits::UniformRead,
                ZrPipelineStageBits::FragmentShader };
        }
        if (storage) {
            return { ZrImageLayout::Undefined, ZrAccessBits::ShaderRead,
                ZrPipelineStageBits::ComputeShader };
        }
        if (transferSrc) {
            return { ZrImageLayout::Undefined, ZrAccessBits::TransferRead,
                ZrPipelineStageBits::Transfer };
        }
        return { ZrImageLayout::Undefined, ZrAccessBits::MemoryRead,
            ZrPipelineStageBits::BottomOfPipe };
    }
}

/// 去重追加有向边
void AddEdge(std::vector<std::vector<uint32_t>>& edges, uint32_t from, uint32_t to)
{
    auto& list = edges[from];
    if (std::find(list.begin(), list.end(), to) == list.end()) {
        list.push_back(to);
    }
}

} // namespace

// =============================================================================
// RgAliasPlan
// =============================================================================
int32_t RgAliasPlan::SlotOf(RgHandle resource) const
{
    for (const Entry& e : entries) {
        if (e.resource == resource) {
            return static_cast<int32_t>(e.slot);
        }
    }
    return -1;
}

// =============================================================================
// RgBarrierPlan
// =============================================================================
uint32_t RgBarrierPlan::InlineImageBarrierCount() const
{
    // A-10：split 计划同时产一条等价内联镜像（供 Execute 下发），它已计入
    // SplitCount()——此处跳过镜像，避免同一道转换被数两次。
    uint32_t total = 0;
    for (size_t p = 0; p < perPassImageBatches.size(); ++p) {
        const std::vector<bool>* mirror = p < perPassImageBatchSplitMirror.size()
            ? &perPassImageBatchSplitMirror[p]
            : nullptr;
        for (size_t i = 0; i < perPassImageBatches[p].size(); ++i) {
            if (mirror != nullptr && i < mirror->size() && (*mirror)[i]) {
                continue;
            }
            total += 1u;
        }
    }
    return total;
}

uint32_t RgBarrierPlan::InlineBufferBarrierCount() const
{
    uint32_t total = 0;
    for (const auto& batch : perPassBufferBatches) {
        total += static_cast<uint32_t>(batch.size());
    }
    return total;
}

// =============================================================================
// RgPass
// =============================================================================
RgPass& RgPass::Read(RgHandle handle)
{
    RgPassAccess a;
    a.resource = handle;
    a.kind = RgPassAccessKind::Read;
    m_accesses.push_back(a);
    return *this;
}

RgPass& RgPass::Read(RgHandle handle, ZrImageLayout layout, ZrAccessBits access,
    ZrPipelineStageBits stage)
{
    RgPassAccess a;
    a.resource = handle;
    a.kind = RgPassAccessKind::Read;
    a.layout = layout;
    a.access = access;
    a.stage = stage;
    a.explicitSync = true;
    m_accesses.push_back(a);
    return *this;
}

RgPass& RgPass::Write(RgHandle handle)
{
    RgPassAccess a;
    a.resource = handle;
    a.kind = RgPassAccessKind::Write;
    m_accesses.push_back(a);
    return *this;
}

RgPass& RgPass::Write(RgHandle handle, ZrImageLayout layout, ZrAccessBits access,
    ZrPipelineStageBits stage)
{
    RgPassAccess a;
    a.resource = handle;
    a.kind = RgPassAccessKind::Write;
    a.layout = layout;
    a.access = access;
    a.stage = stage;
    a.explicitSync = true;
    m_accesses.push_back(a);
    return *this;
}

RgPass& RgPass::Present(RgHandle handle)
{
    RgPassAccess a;
    a.resource = handle;
    a.kind = RgPassAccessKind::Present;
    m_accesses.push_back(a);
    return *this;
}

// =============================================================================
// RgContext
// =============================================================================
ZrImage RgContext::GetImage(RgHandle handle)
{
    const RgGraph::ResourceEntry* entry = m_graph->FindResource(handle);
    bool declared = false;
    if (entry != nullptr && entry->desc.type == RgResourceType::Image) {
        // 仅当当前 Pass 在 setup 中声明了该句柄才放行
        for (const RgPassAccess& a : m_graph->PassAt(m_passDeclIndex).Accesses()) {
            if (a.resource == handle) {
                declared = true;
                break;
            }
        }
    }
    if (!declared) {
        ++m_undeclaredUses;
        return ZrImage{};
    }
    return entry->desc.residency == RgResidency::Imported ? entry->desc.importedImage
                                                          : entry->transientImage;
}

ZrBuffer RgContext::GetBuffer(RgHandle handle)
{
    const RgGraph::ResourceEntry* entry = m_graph->FindResource(handle);
    bool declared = false;
    if (entry != nullptr && entry->desc.type == RgResourceType::Buffer) {
        for (const RgPassAccess& a : m_graph->PassAt(m_passDeclIndex).Accesses()) {
            if (a.resource == handle) {
                declared = true;
                break;
            }
        }
    }
    if (!declared) {
        ++m_undeclaredUses;
        return ZrBuffer{};
    }
    return entry->desc.residency == RgResidency::Imported ? entry->desc.importedBuffer
                                                          : entry->transientBuffer;
}

// =============================================================================
// RgGraph —— Record
// =============================================================================
RgHandle RgGraph::CreateTransient(const RgResourceDesc& desc)
{
    RgResourceDesc d = desc;
    d.residency = RgResidency::Transient;
    ResourceEntry e;
    e.desc = d;
    e.gen = 1;
    const uint32_t index = static_cast<uint32_t>(m_resources.size());
    m_resources.push_back(e);
    RgHandle h;
    h.index = index;
    h.gen = 1;
    return h;
}

RgHandle RgGraph::Import(const RgResourceDesc& desc)
{
    RgResourceDesc d = desc;
    d.residency = RgResidency::Imported;
    // Imported 图像没带尺寸/格式时，用宿主注册的反查器补全（诊断/导出需要真实 desc）
    if (d.type == RgResourceType::Image && !d.hasImageDesc &&
        m_importDescResolver != nullptr) {
        ZrImageDesc resolved {};
        if (m_importDescResolver(d.importedImage, &resolved)) {
            d.imageDesc = resolved;
            d.hasImageDesc = true;
        }
    }
    ResourceEntry e;
    e.desc = d;
    e.gen = 1;
    const uint32_t index = static_cast<uint32_t>(m_resources.size());
    m_resources.push_back(e);
    RgHandle h;
    h.index = index;
    h.gen = 1;
    return h;
}

RgPass& RgGraph::AddPass(const char* name, RgSetupFn setup, RgExecuteFn execute)
{
    PassEntry e;
    e.pass.m_name = name;
    e.execute = std::move(execute);
    m_passes.push_back(std::move(e));
    RgPass& pass = m_passes.back().pass;
    if (setup) {
        setup(pass);
    }
    return pass;
}

const RgGraph::ResourceEntry* RgGraph::FindResource(RgHandle handle) const
{
    if (!handle.IsValid() || handle.index >= m_resources.size()) {
        return nullptr;
    }
    const ResourceEntry& e = m_resources[handle.index];
    if (e.gen != handle.gen) {
        return nullptr;
    }
    return &e;
}

// PassAt 访问器（RgContext 用；声明序号）
const RgPass& RgGraph::PassAt(uint32_t declIndex) const
{
    return m_passes[declIndex].pass;
}

// ---- TASK-B-R9-16：rg.export 导出查询（纯只读，见 rg_export.h）--------------------
const char* RgGraph::PassNameAt(uint32_t declIndex) const
{
    if (declIndex >= m_passes.size()) {
        return "";
    }
    return m_passes[declIndex].pass.Name();
}

const std::vector<RgPassAccess>* RgGraph::PassAccessesAt(uint32_t declIndex) const
{
    if (declIndex >= m_passes.size()) {
        return nullptr;
    }
    return &m_passes[declIndex].pass.Accesses();
}

const RgResourceDesc* RgGraph::ResourceDescAt(uint32_t index) const
{
    if (index >= m_resources.size()) {
        return nullptr;
    }
    return &m_resources[index].desc;
}

RgHandle RgGraph::ResourceHandleAt(uint32_t index) const
{
    if (index >= m_resources.size()) {
        return RgHandle{};
    }
    return RgHandle{ index, m_resources[index].gen };
}

// =============================================================================
// RgGraph —— Compile
// =============================================================================
bool RgGraph::Compile()
{
    m_lastError = RgError::None;
    m_compiledOrder.clear();
    m_culledPasses.clear();
    m_aliasPlan = RgAliasPlan{};
    m_barrierPlan = RgBarrierPlan{};

    if (!BuildEdgesAndDetectCycle()) {
        return false;
    }
    CullDeadPasses();
    if (m_compiledOrder.empty()) {
        m_lastError = RgError::NoRoots;
        return false;
    }
    if (!TopologicalSort()) {
        m_lastError = RgError::CycleDetected;
        return false;
    }
    ComputeLifetimesAndAlias();
    PlanBarriers();
    return true;
}

// 依赖建边：
//   - 写->读：资源的每个写 Pass 先于每个读 Pass（含 Present）
//   - WAW：同资源多写按声明序连成链（后声明的写依赖先声明的写）
// 全图 Kahn 环检测（对全部 Pass，先于死剔除）。
bool RgGraph::BuildEdgesAndDetectCycle()
{
    const uint32_t passCount = static_cast<uint32_t>(m_passes.size());
    const uint32_t resCount = static_cast<uint32_t>(m_resources.size());
    m_edges.assign(passCount, {});

    // 句柄合法性校验 + 按资源收集读/写 Pass
    std::vector<std::vector<uint32_t>> readers(resCount);
    std::vector<std::vector<uint32_t>> writers(resCount);
    for (uint32_t p = 0; p < passCount; ++p) {
        for (const RgPassAccess& a : m_passes[p].pass.Accesses()) {
            if (FindResource(a.resource) == nullptr) {
                m_lastError = RgError::InvalidHandle;
                return false;
            }
            if (a.kind == RgPassAccessKind::Write) {
                writers[a.resource.index].push_back(p);
            } else {
                readers[a.resource.index].push_back(p);
            }
        }
    }
    for (uint32_t r = 0; r < resCount; ++r) {
        for (uint32_t w : writers[r]) {
            for (uint32_t rd : readers[r]) {
                if (w != rd) {
                    AddEdge(m_edges, w, rd);
                }
            }
        }
        for (size_t i = 1; i < writers[r].size(); ++i) {
            AddEdge(m_edges, writers[r][i - 1], writers[r][i]); // WAW 按声明序
        }
    }

    // 全图 Kahn（含将被剔除的死 Pass——死 Pass 也可能参与环）
    std::vector<uint32_t> indegree(passCount, 0);
    for (uint32_t p = 0; p < passCount; ++p) {
        for (uint32_t succ : m_edges[p]) {
            ++indegree[succ];
        }
    }
    std::vector<uint32_t> ready;
    for (uint32_t p = 0; p < passCount; ++p) {
        if (indegree[p] == 0u) {
            ready.push_back(p);
        }
    }
    uint32_t sorted = 0;
    while (!ready.empty()) {
        // 稳定：每次取声明序最小者
        auto minIt = std::min_element(ready.begin(), ready.end());
        const uint32_t p = *minIt;
        ready.erase(minIt);
        ++sorted;
        for (uint32_t succ : m_edges[p]) {
            if (--indegree[succ] == 0u) {
                ready.push_back(succ);
            }
        }
    }
    if (sorted != passCount) {
        m_lastError = RgError::CycleDetected; // 含"多写无序"经读旁路成环的场景
        return false;
    }
    return true;
}

// 死 Pass 剔除：根 = 写 Imported 资源的 Pass ∪ 显式 MarkRoot 的 Pass。
// 沿依赖边反向（生产者方向）从根回溯，不可达者剔除。
void RgGraph::CullDeadPasses()
{
    const uint32_t passCount = static_cast<uint32_t>(m_passes.size());
    std::vector<uint32_t> predEdges(passCount, 0u);
    std::vector<std::vector<uint32_t>> preds(passCount);
    for (uint32_t p = 0; p < passCount; ++p) {
        for (uint32_t succ : m_edges[p]) {
            preds[succ].push_back(p);
        }
    }

    std::vector<uint8_t> alive(passCount, 0u);
    std::vector<uint32_t> stack;
    for (uint32_t p = 0; p < passCount; ++p) {
        const RgPass& pass = m_passes[p].pass;
        bool root = pass.IsRoot();
        if (!root) {
            for (const RgPassAccess& a : pass.Accesses()) {
                if (a.kind == RgPassAccessKind::Write
                    && m_resources[a.resource.index].desc.residency
                        == RgResidency::Imported) {
                    root = true;
                    break;
                }
            }
        }
        if (root) {
            alive[p] = 1u;
            stack.push_back(p);
        }
    }
    while (!stack.empty()) {
        const uint32_t p = stack.back();
        stack.pop_back();
        for (uint32_t pred : preds[p]) {
            if (alive[pred] == 0u) {
                alive[pred] = 1u;
                stack.push_back(pred);
            }
        }
    }
    for (uint32_t p = 0; p < passCount; ++p) {
        if (alive[p] != 0u) {
            m_compiledOrder.push_back(p);
        } else {
            m_culledPasses.push_back(p);
        }
    }
}

// 存留子图上的稳定拓扑排序（Kahn，取声明序最小者），重写 m_compiledOrder
bool RgGraph::TopologicalSort()
{
    const uint32_t kept = static_cast<uint32_t>(m_compiledOrder.size());
    std::vector<uint8_t> keptFlag(m_passes.size(), 0u);
    for (uint32_t p : m_compiledOrder) {
        keptFlag[p] = 1u;
    }
    std::vector<uint32_t> indegree(m_passes.size(), 0u);
    for (uint32_t p : m_compiledOrder) {
        for (uint32_t succ : m_edges[p]) {
            if (keptFlag[succ] != 0u) {
                ++indegree[succ];
            }
        }
    }
    std::vector<uint32_t> ready;
    for (uint32_t p : m_compiledOrder) {
        if (indegree[p] == 0u) {
            ready.push_back(p);
        }
    }
    std::vector<uint32_t> order;
    order.reserve(kept);
    while (!ready.empty()) {
        auto minIt = std::min_element(ready.begin(), ready.end());
        const uint32_t p = *minIt;
        ready.erase(minIt);
        order.push_back(p);
        for (uint32_t succ : m_edges[p]) {
            if (keptFlag[succ] != 0u && --indegree[succ] == 0u) {
                ready.push_back(succ);
            }
        }
    }
    if (order.size() != m_compiledOrder.size()) {
        return false; // 理论不可达（全图已过环检测），防御性返回
    }
    m_compiledOrder = std::move(order);
    return true;
}

// 生命周期区间（编译序序号）+ 别名槽分配（first-fit：找末尾 < 本区间头的槽）
void RgGraph::ComputeLifetimesAndAlias()
{
    const uint32_t resCount = static_cast<uint32_t>(m_resources.size());
    std::vector<uint32_t> firstUse(resCount, 0xFFFFFFFFu);
    std::vector<uint32_t> lastUse(resCount, 0);

    for (uint32_t order = 0; order < m_compiledOrder.size(); ++order) {
        const uint32_t p = m_compiledOrder[order];
        for (const RgPassAccess& a : m_passes[p].pass.Accesses()) {
            const uint32_t r = a.resource.index;
            firstUse[r] = std::min(firstUse[r], order);
            lastUse[r] = std::max(lastUse[r], order);
        }
    }

    // 别名槽：每槽记录当前占用者的 lastUse；区间不重叠（槽末尾 < 新区间头）才复用
    struct SlotState {
        uint32_t lastUse;
    };
    std::vector<SlotState> slots;
    m_aliasPlan.entries.clear();
    for (uint32_t r = 0; r < resCount; ++r) {
        if (m_resources[r].desc.residency != RgResidency::Transient
            || firstUse[r] == 0xFFFFFFFFu) {
            continue; // Imported / 未被存留 Pass 使用者不参与别名
        }
        uint32_t slot = static_cast<uint32_t>(slots.size());
        for (uint32_t s = 0; s < slots.size(); ++s) {
            if (slots[s].lastUse < firstUse[r]) { // 严格小于：区间端点相接视为重叠
                slot = s;
                break;
            }
        }
        if (slot == slots.size()) {
            slots.push_back(SlotState{ 0u });
        }
        slots[slot].lastUse = lastUse[r];
        RgAliasPlan::Entry e;
        e.resource = RgHandle{ r, m_resources[r].gen };
        e.firstUse = firstUse[r];
        e.lastUse = lastUse[r];
        e.slot = slot;
        m_aliasPlan.entries.push_back(e);
    }
    m_aliasPlan.slotCount = static_cast<uint32_t>(slots.size());
}

RgLifetime RgGraph::LifetimeOf(RgHandle resource) const
{
    RgLifetime out;
    const ResourceEntry* entry = FindResource(resource);
    if (entry == nullptr) {
        return out;
    }
    for (const RgAliasPlan::Entry& e : m_aliasPlan.entries) {
        if (e.resource == resource) {
            out.firstUse = e.firstUse;
            out.lastUse = e.lastUse;
            out.used = true;
            return out;
        }
    }
    return out;
}

// =============================================================================
// RgBarrierPlanner —— PlanBarriers 核心
//
// 每资源维护当前状态 {layout, access, stage}（初值 Undefined/None/None），
// 逐 pass 按访问声明推进；仅当 layout 变化 或 访问不兼容时产出屏障：
//   - 只读共享：连续只读访问（同布局、读-读兼容）→ 零屏障
//   - 同性质合并：同一 pass 的全部屏障进同一批次（一次 ResourceBarrier）
//   - split：写侧访问含 DepthStencilAttachmentWrite 且转入只读 → 记 split 计划，
//     并同时产一条等价内联镜像（A-10：split 是优化，不是唯一表达）
// =============================================================================
void RgGraph::PlanBarriers()
{
    const uint32_t passCountCompiled = static_cast<uint32_t>(m_compiledOrder.size());
    const uint32_t resCount = static_cast<uint32_t>(m_resources.size());
    m_barrierPlan.perPassImageBatches.assign(passCountCompiled, {});
    m_barrierPlan.perPassBufferBatches.assign(passCountCompiled, {});
    m_barrierPlan.perPassImageBatchResources.assign(passCountCompiled, {});
    m_barrierPlan.perPassBufferBatchResources.assign(passCountCompiled, {});
    m_barrierPlan.perPassImageBatchSplitMirror.assign(passCountCompiled, {});
    m_barrierPlan.splits.clear();

    struct ResState {
        ZrImageLayout layout = ZrImageLayout::Undefined;
        ZrAccessBits access = ZrAccessBits::None;
        ZrPipelineStageBits stage = ZrPipelineStageBits::None;
        // 最近一次写访问所在 pass（编译序）；split 计划的 begin 锚点
        uint32_t lastWritePass = 0xFFFFFFFFu;
    };
    std::vector<ResState> state(resCount);

    for (uint32_t order = 0; order < passCountCompiled; ++order) {
        const uint32_t p = m_compiledOrder[order];
        for (const RgPassAccess& a : m_passes[p].pass.Accesses()) {
            const uint32_t r = a.resource.index;
            const RgResourceDesc& desc = m_resources[r].desc;

            SyncTriple need;
            if (desc.type == RgResourceType::Image) {
                need = a.explicitSync
                    ? SyncTriple{ a.layout, a.access, a.stage }
                    : DeriveImageSync(a.kind, desc.imageDesc.usage);
            } else {
                need = a.explicitSync
                    ? SyncTriple{ ZrImageLayout::Undefined, a.access, a.stage }
                    : DeriveBufferSync(a.kind, desc.bufferDesc.usage);
            }

            ResState& cur = state[r];
            const bool layoutChanged = desc.type == RgResourceType::Image
                && cur.layout != need.layout;
            const bool accessConflict = !ZrAccessCompatible(cur.access, need.access);

            if (layoutChanged || accessConflict) {
                // split 候选：写侧为 DSV 写、读侧转入只读 → 记 split 计划（并产等价
                // 内联镜像，见下方 A-10 注释）
                const bool dsvWriteBefore = ZrHasAll(
                    cur.access, ZrAccessBits::DepthStencilAttachmentWrite);
                const bool toReadOnly = ZrIsReadOnlyAccess(need.access);
                if (desc.type == RgResourceType::Image && dsvWriteBefore && toReadOnly
                    && cur.lastWritePass != 0xFFFFFFFFu) {
                    RgSplitBarrier split;
                    split.resource = a.resource;
                    split.beginAfterPass = cur.lastWritePass;
                    split.endBeforePass = order;
                    split.barrier.image = desc.importedImage; // Transient 句柄由执行层替换（下任务）
                    split.barrier.prevLayout = cur.layout;
                    split.barrier.newLayout = need.layout;
                    split.barrier.srcAccess = cur.access;
                    split.barrier.dstAccess = need.access;
                    split.barrier.srcStage = cur.stage;
                    split.barrier.dstStage = need.stage;
                    m_barrierPlan.splits.push_back(split);
                    // A-10（TASK-R10-07）：split 是优化，不是唯一表达——同一次转换
                    // 另产一条等价内联屏障，公共 API Execute()（从不读 splits）因此
                    // 天然正确；ExecuteAliased 跳过镜像、仍以 BEGIN_ONLY/END_ONLY 下发。
                    m_barrierPlan.perPassImageBatches[order].push_back(split.barrier);
                    m_barrierPlan.perPassImageBatchResources[order].push_back(a.resource);
                    m_barrierPlan.perPassImageBatchSplitMirror[order].push_back(true);
                } else if (desc.type == RgResourceType::Image) {
                    ZrImageBarrier b{};
                    b.image = desc.importedImage; // Imported=外部句柄；Transient 由执行层替换
                    b.prevLayout = cur.layout;
                    b.newLayout = need.layout;
                    b.srcAccess = cur.access;
                    b.dstAccess = need.access;
                    b.srcStage = cur.stage;
                    b.dstStage = need.stage;
                    m_barrierPlan.perPassImageBatches[order].push_back(b);
                    // 平行记录句柄（执行器据此把编译期占位替换为当帧真资源）
                    m_barrierPlan.perPassImageBatchResources[order].push_back(a.resource);
                    m_barrierPlan.perPassImageBatchSplitMirror[order].push_back(false);
                } else {
                    ZrBufferBarrier b{};
                    b.buffer = desc.importedBuffer;
                    b.srcAccess = cur.access;
                    b.dstAccess = need.access;
                    b.srcStage = cur.stage;
                    b.dstStage = need.stage;
                    m_barrierPlan.perPassBufferBatches[order].push_back(b);
                    m_barrierPlan.perPassBufferBatchResources[order].push_back(a.resource);
                }
            }

            // 状态推进：同 pass 多读共享状态用并集；发生屏障后以本访问为准
            if (layoutChanged || accessConflict) {
                cur.access = need.access;
                cur.stage = need.stage;
            } else {
                cur.access |= need.access;
                cur.stage |= need.stage;
            }
            if (desc.type == RgResourceType::Image) {
                cur.layout = need.layout;
            }
            if (!ZrIsReadOnlyAccess(need.access)) {
                cur.lastWritePass = order;
            }
        }
    }
}

// =============================================================================
// RgGraph —— ExecuteAliased（TASK-B-R3-02：真后端执行器）
//
// 与 Execute 的差异（编译产物驱动的物化与下发）：
//   1. 别名槽 → 别名堆：每槽一堆（尺寸=槽内最大 GetImagePlacementSize，64KB 由
//      后端对齐），槽内 Transient 图像经 CreateImageInHeap 落位 placed resource
//      ——同槽前后资源真共享物理内存（串行复用，区间不重叠天然无别名冒险）
//   2. 生命周期区间：首次使用前一刻物化、末次使用后立即退役（逻辑死亡；物理
//      销毁延迟到 FlushRetired——D3D12 要求命令表引用的资源活到提交完成）
//   3. 屏障批次/split 半边的占位句柄替换为当帧真资源句柄后再提交
//   4. split 计划真实下发：写 Pass 结束 BEGIN_ONLY、读 Pass 之前 END_ONLY
//      （IZrCommandList::ResourceBarrierSplit；D3D12 原生 split barrier 语义）
// 设备无关：只依赖 IZrDevice/IZrCommandList 接口，D3D12 与 Mock 均可驱动。
// =============================================================================
RgExecuteStats RgGraph::ExecuteAliased(IZrDevice& device, IZrCommandList& commandList)
{
    m_lastExecuteUndeclaredUses = 0;
    FlushRetired(device); // 冲刷上一帧退役资源（调用方亦可显式冲刷）

    RgExecuteStats stats;
    const uint32_t passCountCompiled = static_cast<uint32_t>(m_compiledOrder.size());

    // ---- 每别名槽建堆：槽尺寸 = 槽内图像落位需求最大值（每槽一堆模型）------------
    std::vector<ZrAliasHeap> slotHeaps(m_aliasPlan.slotCount);
    {
        std::vector<uint64_t> slotSizes(m_aliasPlan.slotCount, 0u);
        for (const RgAliasPlan::Entry& e : m_aliasPlan.entries) {
            if (m_resources[e.resource.index].desc.type != RgResourceType::Image) {
                continue;
            }
            const uint64_t size
                = device.GetImagePlacementSize(m_resources[e.resource.index].desc.imageDesc);
            if (size > slotSizes[e.slot]) {
                slotSizes[e.slot] = size;
            }
        }
        for (uint32_t s = 0; s < m_aliasPlan.slotCount; ++s) {
            if (slotSizes[s] > 0u) {
                slotHeaps[s] = device.CreateAliasHeap(slotSizes[s]);
                if (slotHeaps[s].IsValid()) {
                    m_activeAliasHeaps.push_back(slotHeaps[s]);
                    stats.aliasHeapsCreated += 1u;
                }
            }
        }
    }

    // Transient 句柄清零（当帧重新物化）
    for (ResourceEntry& e : m_resources) {
        e.transientImage = ZrImage{};
        e.transientBuffer = ZrBuffer{};
    }

    // 句柄 → 当帧真资源解析（屏障占位替换与 RgContext 共用同一来源）
    auto resolveImage = [this](RgHandle handle) -> ZrImage {
        const ResourceEntry& e = m_resources[handle.index];
        return e.desc.residency == RgResidency::Imported ? e.desc.importedImage
                                                         : e.transientImage;
    };
    auto resolveBuffer = [this](RgHandle handle) -> ZrBuffer {
        const ResourceEntry& e = m_resources[handle.index];
        return e.desc.residency == RgResidency::Imported ? e.desc.importedBuffer
                                                         : e.transientBuffer;
    };

    // 首次使用物化 + 末次使用退役（编译序驱动）
    uint32_t alive = 0u;
    RgContext ctx;
    ctx.m_graph = this;
    for (uint32_t order = 0; order < passCountCompiled; ++order) {
        // ---- 物化：firstUse == order 的 Transient（区间起点）--------------------
        for (const RgAliasPlan::Entry& e : m_aliasPlan.entries) {
            if (e.firstUse != order) {
                continue;
            }
            ResourceEntry& entry = m_resources[e.resource.index];
            if (entry.desc.type == RgResourceType::Image) {
                const ZrAliasHeap& heap = slotHeaps[e.slot];
                entry.transientImage = heap.IsValid()
                    ? device.CreateImageInHeap(entry.desc.imageDesc, heap, 0u)
                    : device.CreateImage(entry.desc.imageDesc);
                if (entry.transientImage.IsValid()) {
                    stats.transientImages += 1u;
                    alive += 1u;
                }
            } else {
                entry.transientBuffer = device.CreateBuffer(entry.desc.bufferDesc);
                if (entry.transientBuffer.IsValid()) {
                    stats.transientBuffers += 1u;
                    alive += 1u;
                }
            }
        }
        if (alive > stats.peakAliveTransients) {
            stats.peakAliveTransients = alive;
        }

        // ---- split END_ONLY：endBeforePass == order 的半边（读 Pass 之前）--------
        {
            std::vector<ZrImageBarrier> ends;
            for (const RgSplitBarrier& s : m_barrierPlan.splits) {
                if (s.endBeforePass == order) {
                    ZrImageBarrier b = s.barrier;
                    b.image = resolveImage(s.resource);
                    ends.push_back(b);
                }
            }
            if (!ends.empty()) {
                commandList.ResourceBarrierSplit(
                    ends.data(), static_cast<uint32_t>(ends.size()), /*beginOnly=*/false);
                stats.splitEnds += static_cast<uint32_t>(ends.size());
            }
        }

        // ---- 内联屏障批次：占位句柄替换为当帧真资源后单次批量提交 -----------------
        {
            const std::vector<ZrImageBarrier>& imgBatch = m_barrierPlan.perPassImageBatches[order];
            const std::vector<RgHandle>& imgRes = m_barrierPlan.perPassImageBatchResources[order];
            const std::vector<bool>& imgMirror
                = m_barrierPlan.perPassImageBatchSplitMirror[order];
            const std::vector<ZrBufferBarrier>& bufBatch
                = m_barrierPlan.perPassBufferBatches[order];
            const std::vector<RgHandle>& bufRes
                = m_barrierPlan.perPassBufferBatchResources[order];
            std::vector<ZrImageBarrier> imgResolved;
            imgResolved.reserve(imgBatch.size());
            for (size_t i = 0; i < imgBatch.size(); ++i) {
                // A-10：split 的等价内联镜像不走内联批次——本执行器用真正的
                // BEGIN_ONLY/END_ONLY 下发同一次转换（否则会发两遍）。
                if (i < imgMirror.size() && imgMirror[i]) {
                    continue;
                }
                ZrImageBarrier b = imgBatch[i];
                if (i < imgRes.size()) {
                    b.image = resolveImage(imgRes[i]);
                }
                imgResolved.push_back(b);
            }
            std::vector<ZrBufferBarrier> bufResolved;
            bufResolved.reserve(bufBatch.size());
            for (size_t i = 0; i < bufBatch.size(); ++i) {
                ZrBufferBarrier b = bufBatch[i];
                if (i < bufRes.size()) {
                    b.buffer = resolveBuffer(bufRes[i]);
                }
                bufResolved.push_back(b);
            }
            if (!imgResolved.empty() || !bufResolved.empty()) {
                commandList.ResourceBarrier(imgResolved.data(),
                    static_cast<uint32_t>(imgResolved.size()), bufResolved.data(),
                    static_cast<uint32_t>(bufResolved.size()));
                stats.inlineBarrierCalls += 1u;
            }
        }

        // ---- Pass 执行回调 --------------------------------------------------------
        const uint32_t p = m_compiledOrder[order];
        ctx.m_passDeclIndex = p;
        ctx.m_undeclaredUses = 0;
        if (m_passes[p].execute) {
            m_passes[p].execute(ctx);
        }
        m_lastExecuteUndeclaredUses += ctx.m_undeclaredUses;

        // ---- split BEGIN_ONLY：beginAfterPass == order 的半边（写 Pass 结束）-----
        {
            std::vector<ZrImageBarrier> begins;
            for (const RgSplitBarrier& s : m_barrierPlan.splits) {
                if (s.beginAfterPass == order) {
                    ZrImageBarrier b = s.barrier;
                    b.image = resolveImage(s.resource);
                    begins.push_back(b);
                }
            }
            if (!begins.empty()) {
                commandList.ResourceBarrierSplit(
                    begins.data(), static_cast<uint32_t>(begins.size()), /*beginOnly=*/true);
                stats.splitBegins += static_cast<uint32_t>(begins.size());
            }
        }

        // ---- 退役：lastUse == order 的 Transient（逻辑死亡，物理销毁延迟）--------
        for (const RgAliasPlan::Entry& e : m_aliasPlan.entries) {
            if (e.lastUse != order) {
                continue;
            }
            ResourceEntry& entry = m_resources[e.resource.index];
            if (entry.desc.type == RgResourceType::Image && entry.transientImage.IsValid()) {
                m_retiredImages.push_back(entry.transientImage);
                entry.transientImage = ZrImage{};
                alive -= 1u;
            } else if (entry.desc.type == RgResourceType::Buffer
                && entry.transientBuffer.IsValid()) {
                m_retiredBuffers.push_back(entry.transientBuffer);
                entry.transientBuffer = ZrBuffer{};
                alive -= 1u;
            }
        }
    }
    return stats;
}

// 退役资源/别名堆的物理销毁（GPU 同步点后；下次 ExecuteAliased 入口自动冲刷）
void RgGraph::FlushRetired(IZrDevice& device)
{
    for (ZrImage image : m_retiredImages) {
        device.Destroy(image);
    }
    for (ZrBuffer buffer : m_retiredBuffers) {
        device.Destroy(buffer);
    }
    for (ZrAliasHeap heap : m_activeAliasHeaps) {
        device.Destroy(heap);
    }
    m_retiredImages.clear();
    m_retiredBuffers.clear();
    m_activeAliasHeaps.clear();
}

// =============================================================================
// RgGraph —— Execute（R3-01 的直通执行路径：全量物化/末尾统一回收，无别名）
// =============================================================================
void RgGraph::Execute(IZrDevice& device, IZrCommandList& commandList)
{
    m_lastExecuteUndeclaredUses = 0;

    // Transient 资源物化（Mock 设备返回占位句柄；真后端含别名池分配，下任务）
    for (ResourceEntry& e : m_resources) {
        if (e.desc.residency == RgResidency::Transient) {
            if (e.desc.type == RgResourceType::Image) {
                e.transientImage = device.CreateImage(e.desc.imageDesc);
            } else {
                e.transientBuffer = device.CreateBuffer(e.desc.bufferDesc);
            }
        }
    }

    RgContext ctx;
    ctx.m_graph = this;
    for (uint32_t order = 0; order < m_compiledOrder.size(); ++order) {
        const uint32_t p = m_compiledOrder[order];
        // 该 pass 的屏障批次（同性质合并后的单次批量提交）
        const std::vector<ZrImageBarrier>& imgBatch = m_barrierPlan.perPassImageBatches[order];
        const std::vector<ZrBufferBarrier>& bufBatch
            = m_barrierPlan.perPassBufferBatches[order];
        if (!imgBatch.empty() || !bufBatch.empty()) {
            commandList.ResourceBarrier(imgBatch.data(),
                static_cast<uint32_t>(imgBatch.size()), bufBatch.data(),
                static_cast<uint32_t>(bufBatch.size()));
        }
        ctx.m_passDeclIndex = p;
        ctx.m_undeclaredUses = 0;
        if (m_passes[p].execute) {
            m_passes[p].execute(ctx);
        }
        m_lastExecuteUndeclaredUses += ctx.m_undeclaredUses;
    }

    // Transient 资源回收
    for (ResourceEntry& e : m_resources) {
        if (e.desc.residency == RgResidency::Transient) {
            if (e.desc.type == RgResourceType::Image && e.transientImage.IsValid()) {
                device.Destroy(e.transientImage);
            } else if (e.desc.type == RgResourceType::Buffer
                && e.transientBuffer.IsValid()) {
                device.Destroy(e.transientBuffer);
            }
        }
    }
}
