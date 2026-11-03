// zrhi_d3d12_slots.cpp —— 槽位分配器 + 每资源状态表实现（TASK-B-R2-03）
// 纯逻辑（无 D3D12 依赖），单测见 tests/test_zrhi_d3d12_slots.cpp。
#include "zrhi/d3d12/zrhi_d3d12_slots.h"

#include <cassert>

// =============================================================================
// ZrSlotAllocator
// =============================================================================
ZrSlotAllocator::ZrSlotAllocator(uint32_t capacity)
    : m_capacity(capacity)
    , m_inUse(capacity, 0u)
{
}

uint32_t ZrSlotAllocator::Allocate()
{
    // free list 优先（LIFO：最近释放的槽最先复用——时间局部性最好）
    if (!m_freeList.empty()) {
        const uint32_t slot = m_freeList.back();
        m_freeList.pop_back();
        assert(slot < m_capacity && m_inUse[slot] == 0u);
        m_inUse[slot] = 1u;
        m_allocatedCount += 1u;
        return slot;
    }
    // 无回收槽：取未动用游标的下一槽
    if (m_nextFresh < m_capacity) {
        const uint32_t slot = m_nextFresh;
        m_nextFresh += 1u;
        m_inUse[slot] = 1u;
        m_allocatedCount += 1u;
        return slot;
    }
    // 耗尽：本期策略为"预分配大堆 + 断言上限"（按需增长重建留待后续任务）
    assert(false && "ZrSlotAllocator: 描述符槽位耗尽（预分配容量上限）");
    return ZR_SLOT_INVALID;
}

bool ZrSlotAllocator::Free(uint32_t slot)
{
    if (slot >= m_capacity || m_inUse[slot] == 0u) {
        return false; // 越界 / 未分配 / 重复释放
    }
    m_inUse[slot] = 0u;
    m_freeList.push_back(slot);
    m_allocatedCount -= 1u;
    return true;
}

bool ZrSlotAllocator::IsAllocated(uint32_t slot) const
{
    return slot < m_capacity && m_inUse[slot] != 0u;
}

// =============================================================================
// ZrResourceStateTable
// =============================================================================
void ZrResourceStateTable::SetInitial(uint32_t index, ZrImageLayout layout)
{
    for (std::pair<uint32_t, ZrImageLayout>& entry : m_entries) {
        if (entry.first == index) {
            entry.second = layout; // 覆盖（重复初始化语义）
            return;
        }
    }
    m_entries.emplace_back(index, layout);
}

ZrImageLayout ZrResourceStateTable::Transition(uint32_t index, ZrImageLayout newLayout)
{
    for (std::pair<uint32_t, ZrImageLayout>& entry : m_entries) {
        if (entry.first == index) {
            const ZrImageLayout old = entry.second;
            entry.second = newLayout;
            return old;
        }
    }
    // 未跟踪：返回 Undefined（调用方可据此发现漏记），并顺带以新布局开始跟踪
    m_entries.emplace_back(index, newLayout);
    return ZrImageLayout::Undefined;
}

bool ZrResourceStateTable::TryGet(uint32_t index, ZrImageLayout& outLayout) const
{
    for (const std::pair<uint32_t, ZrImageLayout>& entry : m_entries) {
        if (entry.first == index) {
            outLayout = entry.second;
            return true;
        }
    }
    return false;
}

bool ZrResourceStateTable::Remove(uint32_t index)
{
    for (size_t i = 0; i < m_entries.size(); ++i) {
        if (m_entries[i].first == index) {
            m_entries[i] = m_entries.back(); // 尾元素补位删除（无序语义）
            m_entries.pop_back();
            return true;
        }
    }
    return false;
}
