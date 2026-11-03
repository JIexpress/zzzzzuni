// =============================================================================
// test_zrhi_d3d12_slots.cpp —— 槽位分配器 + 资源状态表单测（TASK-B-R2-03）
//
// 覆盖验收（纯逻辑，无需 GPU/设备）：
//   1. ZrSlotAllocator：顺序分配 / 回收 / LIFO 复用 / 重复释放拒绝 / 越界拒绝 /
//      耗尽返回 ZR_SLOT_INVALID / 批量分配回收（计数一致性与槽互异）
//   2. ZrResourceStateTable：初始状态记录 / 状态转移返回旧值 / 未跟踪查询 /
//      移除后再查询 / 覆盖式 SetInitial
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include "zrhi/d3d12/zrhi_d3d12_slots.h"

#include <cstdint>
#include <vector>

// =============================================================================
// ZrSlotAllocator
// =============================================================================
TEST_CASE("ZrSlotAllocator：顺序分配——新槽单调递增且互异")
{
    ZrSlotAllocator allocator(8u);
    REQUIRE(allocator.Capacity() == 8u);
    REQUIRE(allocator.AllocatedCount() == 0u);

    std::vector<uint32_t> slots;
    for (uint32_t i = 0; i < 8u; ++i) {
        const uint32_t slot = allocator.Allocate();
        REQUIRE(slot != ZR_SLOT_INVALID);
        slots.push_back(slot);
        CHECK(allocator.IsAllocated(slot));
    }
    CHECK(allocator.AllocatedCount() == 8u);
    CHECK(allocator.FreeCount() == 0u);

    // 互异（无重复分配）
    for (uint32_t i = 0; i < slots.size(); ++i) {
        for (uint32_t j = i + 1; j < slots.size(); ++j) {
            CHECK(slots[i] != slots[j]);
        }
    }
}

TEST_CASE("ZrSlotAllocator：回收后 LIFO 复用（最近释放的最先复用）")
{
    ZrSlotAllocator allocator(8u);
    const uint32_t a = allocator.Allocate(); // 0
    const uint32_t b = allocator.Allocate(); // 1
    const uint32_t c = allocator.Allocate(); // 2
    REQUIRE(a != b);
    REQUIRE(b != c);

    CHECK(allocator.Free(b)); // 释放 1
    CHECK(allocator.Free(a)); // 释放 0（free list: [1, 0]，LIFO）
    CHECK(allocator.AllocatedCount() == 1u);

    CHECK_FALSE(allocator.IsAllocated(a));
    CHECK_FALSE(allocator.IsAllocated(b));
    CHECK(allocator.IsAllocated(c));

    // LIFO：最近释放的 a(0) 最先复用，然后 b(1)
    CHECK(allocator.Allocate() == a);
    CHECK(allocator.Allocate() == b);
    CHECK(allocator.AllocatedCount() == 3u);
    CHECK(allocator.IsAllocated(a));
    CHECK(allocator.IsAllocated(b));
}

TEST_CASE("ZrSlotAllocator：重复释放/越界释放/释放未分配槽均拒绝")
{
    ZrSlotAllocator allocator(4u);
    const uint32_t a = allocator.Allocate();
    REQUIRE(a != ZR_SLOT_INVALID);

    CHECK(allocator.Free(a));
    CHECK_FALSE(allocator.Free(a)); // 重复释放
    CHECK_FALSE(allocator.Free(100u)); // 越界（超容量）
    CHECK_FALSE(allocator.Free(3u)); // 未分配槽（容量内）
    CHECK(allocator.AllocatedCount() == 0u);
}

TEST_CASE("ZrSlotAllocator：耗尽——返回 ZR_SLOT_INVALID（断言上限策略）")
{
    // 预分配容量 3，全部分配后耗尽：Allocate 返回 ZR_SLOT_INVALID
    // （debug 构建伴 assert；此处验证耗尽时的返回值契约）
    ZrSlotAllocator allocator(3u);
    for (uint32_t i = 0; i < 3u; ++i) {
        REQUIRE(allocator.Allocate() != ZR_SLOT_INVALID);
    }
    CHECK(allocator.AllocatedCount() == 3u);
    CHECK(allocator.FreeCount() == 0u);
    // 注：debug 下该调用触发断言（耗尽即程序错误）；Release 下验证优雅返回。
    // 单测构建（RelWithDebInfo/Release）不定义 NDEBUG 时此行会 abort——为使测试
    // 可移植，仅在断言被编译掉时检查返回值：
#ifdef NDEBUG
    CHECK(allocator.Allocate() == ZR_SLOT_INVALID);
#endif
    // 回收一个槽后可继续分配（耗尽非永久态）
    CHECK(allocator.Free(1u));
    CHECK(allocator.Allocate() == 1u);
}

TEST_CASE("ZrSlotAllocator：批量分配-回收-再分配（槽互异 + 计数一致性）")
{
    constexpr uint32_t kCap = 64u;
    ZrSlotAllocator allocator(kCap);
    std::vector<uint32_t> slots;
    for (uint32_t i = 0; i < kCap; ++i) {
        const uint32_t slot = allocator.Allocate();
        REQUIRE(slot != ZR_SLOT_INVALID);
        slots.push_back(slot);
    }
    CHECK(allocator.AllocatedCount() == kCap);

    // 回收全部（乱序回收）再全量重分配：槽集合一致、计数归位
    for (uint32_t i = 0; i < kCap; i += 2u) { // 先偶数槽
        CHECK(allocator.Free(slots[i]));
    }
    for (uint32_t i = 1; i < kCap; i += 2u) { // 再奇数槽
        CHECK(allocator.Free(slots[i]));
    }
    CHECK(allocator.AllocatedCount() == 0u);

    std::vector<uint8_t> seen(kCap, 0u);
    for (uint32_t i = 0; i < kCap; ++i) {
        const uint32_t slot = allocator.Allocate();
        REQUIRE(slot != ZR_SLOT_INVALID);
        REQUIRE(slot < kCap);
        CHECK(seen[slot] == 0u); // 重分配槽互异
        seen[slot] = 1u;
    }
    CHECK(allocator.AllocatedCount() == kCap);
}

// =============================================================================
// ZrResourceStateTable
// =============================================================================
TEST_CASE("ZrResourceStateTable：初始状态记录与查询")
{
    ZrResourceStateTable table;
    table.SetInitial(7u, ZrImageLayout::Undefined);
    table.SetInitial(42u, ZrImageLayout::TransferDstOptimal); // Image initialLayout
    CHECK(table.TrackedCount() == 2u);

    ZrImageLayout layout = ZrImageLayout::Present;
    CHECK(table.TryGet(7u, layout));
    CHECK(layout == ZrImageLayout::Undefined);
    CHECK(table.TryGet(42u, layout));
    CHECK(layout == ZrImageLayout::TransferDstOptimal);

    CHECK_FALSE(table.TryGet(999u, layout)); // 未跟踪
    CHECK_FALSE(table.Remove(999u)); // 移除未跟踪返回 false
}

TEST_CASE("ZrResourceStateTable：状态转移记录并返回旧值")
{
    ZrResourceStateTable table;
    table.SetInitial(5u, ZrImageLayout::Undefined);
    // 首次屏障：Undefined → ColorAttachmentOptimal
    CHECK(table.Transition(5u, ZrImageLayout::ColorAttachmentOptimal)
        == ZrImageLayout::Undefined);
    // 第二次屏障：ColorAttachmentOptimal → Present
    CHECK(table.Transition(5u, ZrImageLayout::Present) == ZrImageLayout::ColorAttachmentOptimal);

    ZrImageLayout layout = ZrImageLayout::Undefined;
    CHECK(table.TryGet(5u, layout));
    CHECK(layout == ZrImageLayout::Present); // 表内为最新布局

    // 未跟踪资源转移：返回 Undefined（调用方可据此发现漏记）
    CHECK(table.Transition(1234u, ZrImageLayout::General) == ZrImageLayout::Undefined);
    // （转移会顺带跟踪该资源——语义：从此以新布局为准）
    CHECK(table.TryGet(1234u, layout));
    CHECK(layout == ZrImageLayout::General);
}

TEST_CASE("ZrResourceStateTable：移除跟踪与覆盖式 SetInitial")
{
    ZrResourceStateTable table;
    table.SetInitial(3u, ZrImageLayout::Undefined);
    table.SetInitial(3u, ZrImageLayout::ShaderReadOnlyOptimal); // 覆盖（不新增条目）
    CHECK(table.TrackedCount() == 1u);
    ZrImageLayout layout = ZrImageLayout::Undefined;
    CHECK(table.TryGet(3u, layout));
    CHECK(layout == ZrImageLayout::ShaderReadOnlyOptimal);

    CHECK(table.Remove(3u));
    CHECK(table.TrackedCount() == 0u);
    CHECK_FALSE(table.TryGet(3u, layout)); // 移除后不可查询

    CHECK(table.Remove(3u) == false); // 重复移除拒绝
}
