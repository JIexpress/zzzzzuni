// =============================================================================
// draw_list.cpp —— 绘制列表实现（TASK-B-R4-02）
//
// 排序：LSD 基数（8 轮 x 8bit）。每轮只看 key 的一个字节（byte r = [8r+7:8r]），
// 计数排序把（key,item) 对按该字节散射进预分配的乒乓缓冲。8 轮全排完即得
// 全序；轮数偶数 => 结果落回 m_keys/m_items 原数组，无需收尾拷贝。
// 零堆分配：全部缓冲在构造时一次性 resize 定容；Sort 只触碰 m_keys/
// m_keysTmp/m_items/m_itemsTmp 与栈上 256 项计数数组（每轮 memset 复用）。
// =============================================================================
#include "zrender/draw_list.h"

#include <cstring>

namespace {

// 浮点 -> 可按无符号比较的单调 32 位序（标准 flip 技巧，IEEE-754）：
// 负数取反、正数置高符号位。任意两 float 的无符号序 = 其数值序。
// 取高 24 位作为 key 深度字段（单调性保持；低 8 位丢弃由序号字节平局决断）。
uint32_t QuantizeDepth24(float depth)
{
    uint32_t bits = 0;
    std::memcpy(&bits, &depth, sizeof(bits));
    const uint32_t sortable = (bits & 0x80000000u) != 0u ? ~bits : (bits | 0x80000000u);
    return sortable >> 8;
}

constexpr uint32_t kSeqBits = 8;
constexpr uint32_t kMeshLodBits = 7;
constexpr uint32_t kMaterialBits = 16;
constexpr uint32_t kShaderBits = 8;
constexpr uint32_t kDepthBits = 24;

// 自低位向高位推：seq[7:0] -> mesh/lod[14:8] -> material[30:15] -> shader[38:31]
// -> depth[62:39] -> translucent[63]（与简报/头文件位布局一致）
constexpr uint64_t kMaterialShift = kSeqBits + kMeshLodBits;    // 15
constexpr uint64_t kShaderShift = kMaterialShift + kMaterialBits; // 31
constexpr uint64_t kDepthShift = kShaderShift + kShaderBits;      // 39
constexpr uint64_t kTranslucentShift = kDepthShift + kDepthBits;  // 63
static_assert(kTranslucentShift == 63, "key layout must be [63]t [62:39]depth [38:31]shader [30:15]material [14:8]mesh/lod [7:0]seq");

} // namespace

// ---------------------------------------------------------------------------
// key 编码（位布局见 draw_list.h 文件头注释）
// ---------------------------------------------------------------------------
uint64_t DrawList::EncodeSortKey(const RenderItem& item, uint32_t seqIndex)
{
    uint32_t depth24 = QuantizeDepth24(item.depth);
    if (item.translucent) {
        depth24 = 0xFFFFFFu - depth24; // 反序量化：远 -> 小，排序后后 -> 前
    }

    // [14:8] = mesh 低 5bit << 2 | lod 低 2bit（截断进 key，仅作聚簇提示）
    const uint32_t meshLod7 = ((item.meshHandle & 0x1Fu) << 2) | (item.lod & 0x3u);

    uint64_t key = 0;
    key |= static_cast<uint64_t>(depth24 & 0xFFFFFFu) << kDepthShift;
    key |= static_cast<uint64_t>(item.shaderId & 0xFFu) << kShaderShift;
    key |= static_cast<uint64_t>(item.materialId & 0xFFFFu) << kMaterialShift;
    key |= static_cast<uint64_t>(meshLod7 & 0x7Fu) << kSeqBits;
    key |= static_cast<uint64_t>(seqIndex & 0xFFu);
    if (item.translucent) {
        key |= 1ull << kTranslucentShift;
    }
    return key;
}

// ---------------------------------------------------------------------------
// 构造 / 生命周期
// ---------------------------------------------------------------------------
DrawList::DrawList(uint32_t capacity)
    : m_capacity(capacity)
{
    // 一次性定容：之后 Add/Sort/BuildBatches 路径不再有任何堆分配
    m_items.resize(capacity);
    m_keys.resize(capacity);
    m_keysTmp.resize(capacity);
    m_itemsTmp.resize(capacity);
    m_batches.reserve(capacity);
    m_drawArgs.reserve(capacity);
    m_drawIds.reserve(capacity);
}

bool DrawList::Add(const RenderItem& item, uint8_t viewMask)
{
    if (m_count >= m_capacity) {
        return false; // 超容量报错（容量策略：定容不增长，见头注释）
    }
    if (viewMask == 0u) {
        return false; // 至少对一个视图可见才有意义
    }
    const uint32_t id = m_count;
    // 插入序号唯一且递增 -> 走 O(1) 追加（AddItem 的合并查重是 O(k)，仅
    // 适合外部对任意 id 的零散录入）
    if (!m_visibility.AppendItem(id, viewMask)) {
        return false;
    }
    m_items[id] = item;
    m_keys[id] = EncodeSortKey(item, id);
    ++m_count;
    m_sorted = false;
    return true;
}

void DrawList::Reset()
{
    m_count = 0;
    m_sorted = false;
    m_visibility.Reset();
    m_batches.clear();
    m_drawArgs.clear();
    m_drawIds.clear();
}

// ---------------------------------------------------------------------------
// LSD 基数排序（8 轮 x 8bit，零堆分配）
// ---------------------------------------------------------------------------
void DrawList::Sort()
{
    if (m_sorted) {
        return;
    }

    // 乒乓：src 初为 m_keys/m_items 本体，8 轮（偶数）后结果落回本体
    uint64_t* keysSrc = m_keys.data();
    RenderItem* itemsSrc = m_items.data();
    uint64_t* keysDst = m_keysTmp.data();
    RenderItem* itemsDst = m_itemsTmp.data();

    uint32_t counts[256]; // 桶计数数组：每轮 memset 复用（栈上，零分配）

    for (uint32_t round = 0; round < 8; ++round) {
        const uint32_t shift = round * 8;
        std::memset(counts, 0, sizeof(counts));

        // 1) 计数
        for (uint32_t i = 0; i < m_count; ++i) {
            ++counts[(keysSrc[i] >> shift) & 0xFFu];
        }
        // 2) 前缀和（ exclusive；全零字节轮直接跳过散射）
        uint32_t total = 0;
        bool anyNonZero = false;
        for (uint32_t b = 0; b < 256; ++b) {
            const uint32_t c = counts[b];
            counts[b] = total;
            total += c;
            anyNonZero = anyNonZero || (c != 0u);
        }
        if (!anyNonZero) {
            continue; // 该字节全 0：序不变
        }
        // 3) 稳定散射（按扫描序写入同桶 => LSD 稳定性）
        for (uint32_t i = 0; i < m_count; ++i) {
            const uint32_t dst = counts[(keysSrc[i] >> shift) & 0xFFu]++;
            keysDst[dst] = keysSrc[i];
            itemsDst[dst] = itemsSrc[i];
        }
        // 4) 乒乓交换
        uint64_t* tk = keysSrc;
        keysSrc = keysDst;
        keysDst = tk;
        RenderItem* ti = itemsSrc;
        itemsSrc = itemsDst;
        itemsDst = ti;
    }

    // 8 轮偶数次交换 => 结果在 keysSrc/itemsSrc，即 m_keys/m_items 本体。
    if (keysSrc != m_keys.data()) {
        std::memcpy(m_keys.data(), keysSrc, m_count * sizeof(uint64_t));
        std::memcpy(m_items.data(), itemsSrc, m_count * sizeof(RenderItem));
    }
    m_sorted = true;
}

// ---------------------------------------------------------------------------
// 合批（状态聚簇 -> Batch + ZrIndirectDrawArgs + drawID 表）
// ---------------------------------------------------------------------------
void DrawList::RegisterMesh(uint32_t meshHandle, uint32_t vertexCount)
{
    for (size_t i = 0; i + 1 < m_meshVertexCounts.size(); i += 2) {
        if (m_meshVertexCounts[i] == meshHandle) {
            m_meshVertexCounts[i + 1] = vertexCount;
            return;
        }
    }
    m_meshVertexCounts.push_back(meshHandle);
    m_meshVertexCounts.push_back(vertexCount);
}

uint32_t DrawList::BuildBatches()
{
    m_batches.clear();
    m_drawArgs.clear();
    m_drawIds.clear();
    if (!m_sorted) {
        return 0; // 须先 Sort
    }

    uint32_t drawIdNext = 0;
    uint32_t runStart = 0;
    while (runStart < m_count) {
        // 同状态连续段：shader + material + mesh 全同
        const RenderItem& head = m_items[runStart];
        uint32_t runEnd = runStart + 1;
        while (runEnd < m_count) {
            const RenderItem& it = m_items[runEnd];
            if (it.shaderId != head.shaderId || it.materialId != head.materialId
                || it.meshHandle != head.meshHandle) {
                break;
            }
            ++runEnd;
        }

        const uint32_t batchCount = runEnd - runStart;
        const uint32_t drawIdBase = drawIdNext;

        ZrDrawBatch batch;
        batch.startIndex = runStart;
        batch.count = batchCount;
        batch.shaderId = head.shaderId;
        batch.materialId = head.materialId;
        batch.meshHandle = head.meshHandle;
        batch.drawIdBase = drawIdBase;
        m_batches.push_back(batch);

        ZrIndirectDrawArgs args;
        args.vertexIndex = 0;
        for (size_t i = 0; i + 1 < m_meshVertexCounts.size(); i += 2) {
            if (m_meshVertexCounts[i] == head.meshHandle) {
                args.vertexIndex = m_meshVertexCounts[i + 1];
                break;
            }
        }
        args.instanceCount = batchCount;
        args.startVertex = 0;       // 顶点池基址归下一任务
        args.startInstance = drawIdBase;
        m_drawArgs.push_back(args);

        // drawID 表：批内每实例一个，跨批全局连续
        for (uint32_t i = 0; i < batchCount; ++i) {
            m_drawIds.push_back(drawIdBase + i);
        }

        drawIdNext += batchCount;
        runStart = runEnd;
    }
    return static_cast<uint32_t>(m_batches.size());
}
