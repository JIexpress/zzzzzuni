// =============================================================================
// zrhi_d3d12_slots.h —— 描述符堆槽位分配器 + 每资源状态表（TASK-B-R2-03）
//
// 两个纯逻辑类（不依赖 D3D12 设备/头，可单测）：
//   - ZrSlotAllocator       : 描述符堆槽位 free-list 分配器（分配/回收/复用/耗尽）
//                             语义与 zrhi_handles 的 ZrHandleTable 空闲链一致
//                             （LIFO 复用），但槽位即描述符索引——无代际需求
//                             （描述符槽复用由 Destroy→再写描述符覆盖，无 stale
//                             句柄风险），故独立实现
//   - ZrResourceStateTable  : per-resource 当前布局状态表（→ Vulkan 图像布局跟踪；
//                             D3D12 屏障须提供 StateBefore，表内记录上次状态供 R3
//                             RenderGraph 自动屏障推导）
//
// 线程模型：单线程独占（同 zrhi 约定，无锁）。
// =============================================================================
#pragma once

#include <cstdint>
#include <utility>
#include <vector>

#include "zrhi/zrhi_types.h"

/// 无效槽位哨兵（Allocate 耗尽时返回）
constexpr uint32_t ZR_SLOT_INVALID = 0xFFFFFFFFu;

// =============================================================================
// ZrSlotAllocator —— 描述符槽位 free-list 分配器（纯逻辑，可单测）
// =============================================================================
class ZrSlotAllocator {
public:
    /// capacity：槽位总数（0 = 不可用分配器）。容量固定——堆按上限预分配，
    /// 耗尽即断言（本期策略：预分配大堆 + 断言上限，按需增长重建留待后续任务）
    explicit ZrSlotAllocator(uint32_t capacity = 0);

    ZrSlotAllocator(const ZrSlotAllocator&) = delete;
    ZrSlotAllocator& operator=(const ZrSlotAllocator&) = delete;

    /// 分配一个槽位：优先复用 free list（LIFO，最近释放的最先复用），否则取
    /// 未用过的下一槽。耗尽（达容量上限）：断言 + 返回 ZR_SLOT_INVALID
    uint32_t Allocate();

    /// 回收槽位进 free list（越界/未分配/重复释放返回 false）
    bool Free(uint32_t slot);

    /// 槽位当前是否处于已分配态
    bool IsAllocated(uint32_t slot) const;

    uint32_t AllocatedCount() const { return m_allocatedCount; }
    uint32_t Capacity() const { return m_capacity; }
    uint32_t FreeCount() const { return m_capacity - m_allocatedCount; }

private:
    uint32_t m_capacity = 0;
    uint32_t m_allocatedCount = 0;
    uint32_t m_nextFresh = 0;          // 未动用过的下一槽（单调游标）
    std::vector<uint8_t> m_inUse;      // 槽位占用位图
    std::vector<uint32_t> m_freeList;  // 回收槽（LIFO：尾进尾出）
};

// =============================================================================
// ZrResourceStateTable —— 每资源当前布局状态表（纯逻辑，可单测）
// key = 句柄槽位索引（ZrHandle::Index()）；值 = 当前 ZrImageLayout。
// 缓冲无布局概念（D3D12 恒 COMMON 语义），不进表。
// =============================================================================
class ZrResourceStateTable {
public:
    /// 记录初始状态（→ vkCreateImage 的 initialLayout 落表；重复 SetInitial 覆盖）
    void SetInitial(uint32_t index, ZrImageLayout layout);

    /// 状态转移：记录 newLayout 并返回旧状态（未跟踪返回 ZrImageLayout::Undefined
    /// ——调用方可据此发现漏记；R3 自动屏障将以此表为 StateBefore 权威来源）
    ZrImageLayout Transition(uint32_t index, ZrImageLayout newLayout);

    /// 查询当前状态（未跟踪返回 false）
    bool TryGet(uint32_t index, ZrImageLayout& outLayout) const;

    /// 移除跟踪（资源销毁；未跟踪返回 false）
    bool Remove(uint32_t index);

    /// 跟踪中的资源数
    uint32_t TrackedCount() const { return static_cast<uint32_t>(m_entries.size()); }

private:
    // 稀疏索引 → 状态（线性扫描；单线程、资源量中等够用，R3 视性能换哈希表）
    std::vector<std::pair<uint32_t, ZrImageLayout>> m_entries;
};
