// zrhi_handles.cpp —— 泛代句柄表槽位引擎实现（TASK-B-R2-01）
//
// 槽位模型：每槽 {generation, payload, alive, retired} + 全表空闲链（LIFO 栈）。
//   - 新槽 generation = 1（首代；0 保留使零值句柄永远无效）
//   - Acquire 交出当前代际；Release 后 generation +1 交由下次复用
//   - 255 代用尽即退役该槽（不再入空闲链）——generation 回绕保护：
//     宁可废弃槽位也不让代际绕回旧值，杜绝"陈年句柄复活"
//   - 容量上限 0xFFFFFF 槽：index 0xFFFFFF 永不分配，INVALID 哨兵永不撞值
#include "zrhi/zrhi_handles.h"

#include <vector>

// =============================================================================
// ZrHandleTableBase::Impl —— 内部存储
// =============================================================================
struct ZrHandleTableBase::Impl {
    struct Slot {
        uint32_t generation = ZR_HANDLE_GEN_FIRST; // 下一次分配将交出的代际
        void* payload = nullptr;
        bool alive = false;
        bool retired = false; // 代际耗尽，永久退出复用
    };

    std::vector<Slot> slots;
    std::vector<uint32_t> freeList; // 可复用空闲槽（LIFO；退役槽不入链）
    uint32_t aliveCount = 0;
    uint32_t retiredCount = 0;

    /// 句柄定位校验：true 时写 outSlot（stale/越界/零值/哨兵一律拒绝）
    bool Locate(uint32_t raw, const Slot*& outSlot) const
    {
        if (raw == ZR_HANDLE_INVALID) {
            return false;
        }
        const uint32_t index = raw & ZR_HANDLE_INDEX_MASK;
        const uint32_t gen = (raw >> ZR_HANDLE_GEN_SHIFT) & ZR_HANDLE_GEN_MASK;
        if (gen == 0u) {
            return false; // 代际 0 保留：memset 零句柄永远无效
        }
        if (index >= slots.size()) {
            return false;
        }
        const Slot& s = slots[index];
        if (!s.alive || s.generation != gen) {
            return false; // stale（已释放/代际失配）
        }
        outSlot = &s;
        return true;
    }
};

// =============================================================================
// ZrHandleTableBase —— 公共 API
// =============================================================================
ZrHandleTableBase::ZrHandleTableBase() : m_impl(new Impl) {}

ZrHandleTableBase::~ZrHandleTableBase() { delete m_impl; }

uint32_t ZrHandleTableBase::AcquireRaw(void* payload)
{
    Impl& t = *m_impl;
    uint32_t index;
    if (!t.freeList.empty()) {
        index = t.freeList.back(); // LIFO 复用（局部性最好）
        t.freeList.pop_back();
    } else {
        if (t.slots.size() >= static_cast<size_t>(ZR_HANDLE_MAX_SLOTS)) {
            return ZR_HANDLE_INVALID; // 容量耗尽（哨兵 index 永不分配）
        }
        index = static_cast<uint32_t>(t.slots.size());
        t.slots.push_back(Impl::Slot{}); // 满表扩容：追加新槽
    }
    Impl::Slot& s = t.slots[index];
    s.alive = true;
    s.payload = payload;
    t.aliveCount += 1;
    return (s.generation << ZR_HANDLE_GEN_SHIFT) | index;
}

bool ZrHandleTableBase::ReleaseRaw(uint32_t raw)
{
    Impl& t = *m_impl;
    const Impl::Slot* slot = nullptr;
    if (!t.Locate(raw, slot)) {
        return false; // stale/无效句柄拒绝释放
    }
    const uint32_t index = raw & ZR_HANDLE_INDEX_MASK;
    Impl::Slot& s = t.slots[index];
    s.alive = false;
    s.payload = nullptr;
    t.aliveCount -= 1;
    if (s.generation >= ZR_HANDLE_GEN_LAST) {
        // 末代再释放：+1 会回绕 → 槽位退役（回绕保护，永不复用）
        s.retired = true;
        t.retiredCount += 1;
    } else {
        s.generation += 1; // 旧句柄自此失配
        t.freeList.push_back(index);
    }
    return true;
}

bool ZrHandleTableBase::TryGetRaw(uint32_t raw, void*& outPayload) const
{
    const Impl& t = *m_impl;
    const Impl::Slot* slot = nullptr;
    if (!t.Locate(raw, slot)) {
        return false;
    }
    outPayload = slot->payload;
    return true;
}

uint32_t ZrHandleTableBase::Capacity() const
{
    return static_cast<uint32_t>(m_impl->slots.size());
}

uint32_t ZrHandleTableBase::AliveCount() const { return m_impl->aliveCount; }

uint32_t ZrHandleTableBase::RetiredCount() const { return m_impl->retiredCount; }
