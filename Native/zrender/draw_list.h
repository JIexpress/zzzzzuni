// =============================================================================
// draw_list.h —— 共享可见性 + 64 位 sort key + 绘制列表编译（TASK-B-R4-02）
//
// "剔除、排序、批处理一体化"的 CPU 侧三层：
//   - VisibilitySet : 多视图共享一份可见性数据（kMaxViews=8 视图掩码过滤，
//                     杜绝逐视图重复剔除）
//   - RenderItem    : 绘制项（mesh/材质/shader/lod/视深/透明标志）
//   - DrawList      : Add 记录可见性 + 计算 64 位 sort key
//                     -> Sort：LSD 基数排序（8 轮 x 8bit，零堆分配，O(n)）
//                     -> BuildBatches：按状态聚簇合并成批
//                        （Batch + ZrIndirectDrawArgs + drawID 表）
//
// 64 位 sort key 位布局（bit 63 为最高位；不透明整体排在半透明之前）：
//   [63]      透明标志：0=不透明 / 1=半透明
//   [62:39]   深度 24bit（FloatToSortableBits 的量化值右移 8 位，单调保序）
//             - 不透明：原值（近 -> 小） => 排序后深度前 -> 后（early-z 友好）
//             - 半透明：0xFFFFFF - 原值（远 -> 小） => 排序后深度后 -> 前
//   [38:31]   shaderId 低 8bit（状态聚簇第一位）
//   [30:15]   materialId 低 16bit（状态聚簇第二位）
//   [14:8]    mesh 5bit（meshHandle 低 5 位截断）<<2 | lod 低 2bit
//   [7:0]     序号（Add 顺序低 8bit；同 key 平局时保持插入序）
//   状态聚簇（shader -> material -> mesh/lod）因此成为排序的天然产物，
//   相邻同状态项即可合并为批。
//
// 容量策略（二选一之"超容量报错"）：构造时定容并一次性预分配全部排序/
// 合批缓冲，Add 超容量返回 false——换取 Sort/BuildBatches 路径可证明的
// 零堆分配（无 reserve 增长、无临时容器）。
//
// 仅纯 CPU 逻辑：不依赖 zrhi/d3d12，不动 zkernel。真 GPU 绘制提交是下一任务。
// 命名遵循 file-conventions.md §3；前缀 dl_/DrawList、ZrIndirectDrawArgs。
// =============================================================================
#pragma once

#include <cstdint>
#include <vector>

// =============================================================================
// VisibilitySet —— 共享可见性（视图掩码，kMaxViews=8）
// =============================================================================
class VisibilitySet {
public:
    /// 支持的最大视图数（viewMask 为 uint8，bit i = 视图 i 可见）
    static constexpr uint32_t kMaxViews = 8;

    /// 清空（保留容量）
    void Reset() { m_entries.clear(); }

    /// 记录 renderItemId 在 viewMask 置位的各视图可见。
    /// 同一 id 重复添加按掩码合并（OR，O(k) 线性查重）。viewMask==0 拒绝。
    /// 批量逐项录入请用 AppendItem（O(1)，id 唯一时）避免 O(n^2) 查重。
    bool AddItem(uint32_t renderItemId, uint8_t viewMask)
    {
        if (viewMask == 0u) {
            return false;
        }
        for (Entry& e : m_entries) {
            if (e.renderItemId == renderItemId) {
                e.viewMask |= viewMask;
                return true;
            }
        }
        m_entries.push_back(Entry { renderItemId, viewMask });
        return true;
    }

    /// 追加条目（O(1)，无查重/合并）：id 必须此前不存在——DrawList 以
    /// 插入序号（唯一）录入可见性即走此路径。viewMask==0 拒绝。
    bool AppendItem(uint32_t renderItemId, uint8_t viewMask)
    {
        if (viewMask == 0u) {
            return false;
        }
        m_entries.push_back(Entry { renderItemId, viewMask });
        return true;
    }

    /// 项数（去重合并后的条目数）
    uint32_t Count() const { return static_cast<uint32_t>(m_entries.size()); }

    /// 查询条目的视图掩码；不存在返回 0
    uint8_t ViewMaskOf(uint32_t renderItemId) const
    {
        for (const Entry& e : m_entries) {
            if (e.renderItemId == renderItemId) {
                return e.viewMask;
            }
        }
        return 0u;
    }

    /// 遍历视图 viewIndex 可见的全部条目，回调 fn(renderItemId)。
    /// 选型：位扫描（线性扫全表 + 掩码位测试），而非按视图桶索引——
    /// 每帧每视图一次 O(n) 顺序扫描对缓存友好，无需维护/重建桶结构；
    /// 桶索引仅在"视图多且反复迭代"场景占优，当前帧型用法收益不抵复杂度。
    /// viewIndex >= kMaxViews 时不迭代任何项（防御，无 UB）。
    template <typename F>
    void Iterate(uint32_t viewIndex, F&& fn) const
    {
        if (viewIndex >= kMaxViews) {
            return;
        }
        const uint8_t bit = static_cast<uint8_t>(1u << viewIndex);
        for (const Entry& e : m_entries) {
            if ((e.viewMask & bit) != 0u) {
                fn(e.renderItemId);
            }
        }
    }

private:
    struct Entry {
        uint32_t renderItemId;
        uint8_t viewMask;
    };
    std::vector<Entry> m_entries;
};

// =============================================================================
// RenderItem —— 绘制项（depth 为视深：距相机越近值越小，供排序）
// =============================================================================
struct RenderItem {
    uint32_t meshHandle = 0;   // 网格句柄（zkernel/zmesh 侧句柄的低 32 位即可）
    uint32_t materialId = 0;
    uint32_t shaderId = 0;
    uint32_t lod = 0;
    float depth = 0.0f;        // 视深（量化进 key 的 [62:39]）
    bool translucent = false;  // true=半透明（key bit63=1，深度反序）
    // TASK-B-R5-02：是否投射阴影。true 时录入可见性带级联阴影视图掩码位
    // （bit1..4，见 zrender/shadows.h ShadowViewMask）——主视图与阴影视图共享
    // 同一份 VisibilitySet，按位过滤即得阴影 draw list，无第二次剔除。
    bool castShadow = true;
};

// =============================================================================
// 描边视图（TASK-B-R5-03）—— NPR 反向壳描边的第二次遍历走独立视图掩码位
// =============================================================================
/// 描边视图 id（bit0=主视图、bit1..4=级联阴影视图——见 shadows.h；bit5=描边）
constexpr uint32_t kOutlineViewId = 5u;
constexpr uint8_t kOutlineViewBit = 0x20u;

/// 一次 DrawList::Add 用的视图掩码：主视图恒可见；outline=true 追加描边位。
/// （与级联阴影视图掩码正交：需要同时投影阴影的描边项用
///  ShadowViewMask(true) | kOutlineViewBit。）
inline uint8_t OutlineViewMask(bool outline)
{
    return outline ? static_cast<uint8_t>(0x01u | kOutlineViewBit) : 0x01u;
}

// =============================================================================
// ZrIndirectDrawArgs —— 单批的（未来 GPU indirect）绘制参数（POD，纯数值）
// 本任务仅填充数值；真正经 zrhi 提交是下一任务。
// =============================================================================
struct ZrIndirectDrawArgs {
    uint32_t vertexIndex;   // 每实例顶点数（SV_VertexID 范围；来自 RegisterMesh）
    uint32_t instanceCount; // 实例数 = 批内渲染项数
    uint32_t startVertex;   // 顶点池基址（本任务恒 0；顶点池编排归下一任务）
    uint32_t startInstance; // 起始实例 = 该批的 drawIdBase（取 drawID 表基址）
};

/// 单批（状态聚簇：批内 shader/material/mesh 全同）
struct ZrDrawBatch {
    uint32_t startIndex;  // 排序后项数组中的起始下标
    uint32_t count;       // 批内项数（= 实例数）
    uint32_t shaderId;
    uint32_t materialId;
    uint32_t meshHandle;
    uint32_t drawIdBase;  // 该批在 drawID 表中的基址（批内实例依次 +1）
};

// =============================================================================
// DrawList —— 绘制列表：Add（可见性+key） -> Sort（radix） -> BuildBatches
// =============================================================================
class DrawList {
public:
    /// 默认容量（超容量需显式给构造参数；见头注释"容量策略"）
    static constexpr uint32_t kDefaultCapacity = 4096;

    /// 定容构造：一次性预分配项/key/排序临时/批/drawID 全部缓冲。
    explicit DrawList(uint32_t capacity = kDefaultCapacity);

    /// 记录绘制项：可见性入 m_visibility（renderItemId=插入序号），key 即刻编码。
    /// 超容量或 viewMask==0 返回 false（列表状态不变）。
    bool Add(const RenderItem& item, uint8_t viewMask);

    /// LSD 基数排序：8 轮 x 8bit，桶计数数组复用（栈上 256 项/轮），
    /// 项与 key 经预分配缓冲乒乓搬运——排序路径零堆分配。稳定排序。
    void Sort();

    /// 合批（须先 Sort 成功，否则返回 0）：扫描排序后相邻项，
    /// shader+material+mesh 全同的连续段合并为一批；同时产出
    /// ZrIndirectDrawArgs（每批一条）与 drawID 表（批内每实例一个，全局连续）。
    /// 返回批数。
    uint32_t BuildBatches();

    /// 清空（保留全部预分配容量；再次使用无需任何分配）
    void Reset();

    // ---- 查询 ------------------------------------------------------------------
    uint32_t Count() const { return m_count; }
    uint32_t Capacity() const { return m_capacity; }
    const VisibilitySet& Visibility() const { return m_visibility; }

    /// 项与 key 数组（排序后即按 key 升序）。指针用于零分配断言（Sort 前后不变）。
    const RenderItem* Items() const { return m_items.data(); }
    const uint64_t* Keys() const { return m_keys.data(); }
    const RenderItem& ItemAt(uint32_t i) const { return m_items[i]; }
    uint64_t KeyOf(uint32_t i) const { return m_keys[i]; }

    const std::vector<ZrDrawBatch>& Batches() const { return m_batches; }
    const std::vector<ZrIndirectDrawArgs>& DrawArgs() const { return m_drawArgs; }
    /// drawID 表：批内每实例一个 drawID，跨批全局连续（0..总数-1）
    const std::vector<uint32_t>& DrawIds() const { return m_drawIds; }

    /// 注册网格顶点数（供 DrawArgs.vertexIndex 填充；未注册的 mesh 按 0）。
    /// 重复注册以最后一次为准。
    void RegisterMesh(uint32_t meshHandle, uint32_t vertexCount);

    /// 64 位 sort key 编码（位布局见文件头注释）。seqIndex 为插入序号。
    static uint64_t EncodeSortKey(const RenderItem& item, uint32_t seqIndex);

private:
    uint32_t m_capacity = 0;
    uint32_t m_count = 0;
    bool m_sorted = false;

    VisibilitySet m_visibility;

    // 全部定容预分配（构造时 resize 一次；Add/Sort/BuildBatches 不再分配）
    std::vector<RenderItem> m_items;     // [capacity]
    std::vector<uint64_t> m_keys;        // [capacity]，与 m_items 平行
    std::vector<uint64_t> m_keysTmp;     // [capacity]，radix 乒乓缓冲
    std::vector<RenderItem> m_itemsTmp;  // [capacity]，radix 乒乓缓冲
    std::vector<ZrDrawBatch> m_batches;        // BuildBatches 产出（容量已预留）
    std::vector<ZrIndirectDrawArgs> m_drawArgs; // 每批一条（容量已预留）
    std::vector<uint32_t> m_drawIds;           // 每实例一个（容量已预留）
    std::vector<uint32_t> m_meshVertexCounts;  // RegisterMesh：偶数对 [mesh, vertexCount]
};
