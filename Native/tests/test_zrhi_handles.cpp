// =============================================================================
// test_zrhi_handles.cpp —— 强类型句柄与泛代句柄表单测（TASK-B-R2-01）
//
// 覆盖验收清单：
//   1. 位域打包/解包（index:24 + generation:8）
//   2. 分配/释放：槽位复用 generation+1、stale 句柄 TryGet 拒绝
//   3. 满表扩容（空闲链耗尽追加新槽，索引互异）
//   4. generation 回绕保护（255 代耗尽 → 槽位退役不复用）
//   5. 零值/哨兵/越界/代际超前句柄一律拒绝
//   6. 强类型防误用（编译期不可互转）
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <type_traits>
#include <vector>

#include "zrhi/zrhi.h"

namespace {

/// 置位计数（校验 index 位域容量用）
uint32_t PopCount32(uint32_t v)
{
    uint32_t n = 0;
    while (v != 0u) {
        v &= v - 1u;
        ++n;
    }
    return n;
}

} // namespace

// ---- 编译期防误用：句柄类型之间不可隐式互转（错类别赋值/传参即编译错误） ----
static_assert(!std::is_convertible_v<ZrBuffer, ZrImage>, "ZrBuffer 不可转 ZrImage");
static_assert(!std::is_convertible_v<ZrImage, ZrImageView>, "ZrImage 不可转 ZrImageView");
static_assert(!std::is_convertible_v<ZrSampler, ZrShader>, "ZrSampler 不可转 ZrShader");
static_assert(!std::is_convertible_v<ZrCommandList, ZrSwapchain>, "句柄类型不可互转");
static_assert(!std::is_convertible_v<ZrBuffer, ZrHandle<ZrImage>>, "不可混入异类句柄基座");

TEST_CASE("句柄位域打包/解包：index 24 bit + generation 8 bit")
{
    ZrImage const h = ZrImage::FromRaw((123u << 24) | 456789u);
    CHECK(h.Generation() == 123u);
    CHECK(h.Index() == 456789u);
    CHECK(h.IsValid());
    CHECK(h == ZrImage::FromRaw((123u << 24) | 456789u));
    CHECK(h != ZrImage::FromRaw((124u << 24) | 456789u)); // 代际不同
    CHECK(h != ZrImage::FromRaw((123u << 24) | 456790u)); // 索引不同

    // 默认构造即无效句柄（非零内存陷阱防护）
    ZrImage const def{};
    CHECK(!def.IsValid());
    CHECK(def.raw == ZR_HANDLE_INVALID);
}

TEST_CASE("句柄表：分配/释放/复用槽位 generation+1、stale 拒绝")
{
    ZrHandleTable<ZrImage> table;
    int tagA = 1;
    int tagB = 2;
    int tagC = 3;
    int tagD = 4;

    ZrImage a = table.Acquire(&tagA);
    ZrImage b = table.Acquire(&tagB);
    ZrImage c = table.Acquire(&tagC);
    REQUIRE(a.IsValid());
    REQUIRE(b.IsValid());
    REQUIRE(c.IsValid());
    CHECK(a.Index() == 0u); // 顺序分配
    CHECK(b.Index() == 1u);
    CHECK(c.Index() == 2u);
    CHECK(a.Generation() == 1u); // 首代为 1（0 保留）
    CHECK(table.AliveCount() == 3u);
    CHECK(table.Capacity() == 3u);
    CHECK(table.RetiredCount() == 0u);

    // 载荷关联
    void* out = nullptr;
    REQUIRE(table.TryGet(a, out));
    CHECK(out == &tagA);
    REQUIRE(table.TryGet(b, out));
    CHECK(out == &tagB);

    // 释放中间 b → stale
    CHECK(table.Release(b));
    CHECK(!table.TryGet(b, out)); // stale 句柄拒绝
    CHECK(!table.Release(b));      // 重复释放拒绝
    CHECK(table.AliveCount() == 2u);

    // 复用槽位：index 相同、generation+1
    ZrImage d = table.Acquire(&tagD);
    CHECK(d.Index() == b.Index());
    CHECK(d.Generation() == b.Generation() + 1u);
    CHECK(!table.TryGet(b, out)); // 旧句柄仍被拒
    REQUIRE(table.TryGet(d, out));
    CHECK(out == &tagD);          // 载荷已更新为新占用者
    CHECK(table.AliveCount() == 3u);
}

TEST_CASE("句柄表：满表扩容（空闲链耗尽追加新槽）")
{
    ZrHandleTable<ZrBuffer> table;
    constexpr uint32_t kCount = 1000;
    std::vector<ZrBuffer> handles;
    handles.reserve(kCount);
    for (uint32_t i = 0; i < kCount; ++i) {
        ZrBuffer h = table.Acquire(nullptr);
        REQUIRE(h.IsValid());
        handles.push_back(h);
    }
    CHECK(table.AliveCount() == kCount);
    CHECK(table.Capacity() >= kCount); // 无释放的连续分配 → 持续扩容

    // 索引互异（复用检查）
    std::vector<bool> seen(kCount + 1, false);
    for (ZrBuffer h : handles) {
        REQUIRE(h.Index() < kCount);
        CHECK(!seen[h.Index()]);
        seen[h.Index()] = true;
    }

    // 释放一半再分配：索引全部来自被释放的槽（LIFO 复用，不新增容量）
    const uint32_t capacityBefore = table.Capacity();
    for (uint32_t i = 0; i < kCount / 2u; ++i) {
        CHECK(table.Release(handles[i]));
    }
    CHECK(table.AliveCount() == kCount - kCount / 2u);
    for (uint32_t i = 0; i < kCount / 2u; ++i) {
        ZrBuffer h = table.Acquire(nullptr);
        REQUIRE(h.IsValid());
        CHECK(h.Generation() == 2u);            // 各槽第二次分配 → 代际 2
        bool reused = false;
        for (uint32_t j = 0; j < kCount / 2u; ++j) {
            if (handles[j].Index() == h.Index()) {
                reused = true;
                break;
            }
        }
        CHECK(reused); // 新分配必来自被释放槽位
    }
    CHECK(table.Capacity() == capacityBefore); // 复用阶段不再扩容
}

TEST_CASE("句柄表：generation 回绕保护（255 代耗尽槽位退役）")
{
    ZrHandleTable<ZrSampler> table;
    ZrSampler first = table.Acquire(nullptr);
    const uint32_t idx = first.Index();
    REQUIRE(first.IsValid());
    CHECK(first.Generation() == 1u);
    CHECK(table.Release(first)); // gen → 2

    // 依次消费 gen 2..254
    for (uint32_t gen = 2u; gen <= 254u; ++gen) {
        ZrSampler h = table.Acquire(nullptr);
        REQUIRE(h.Index() == idx);
        CHECK(h.Generation() == gen);
        CHECK(table.Release(h));
    }

    // gen 255：该槽最后一代
    ZrSampler last = table.Acquire(nullptr);
    REQUIRE(last.Index() == idx);
    CHECK(last.Generation() == 255u);
    CHECK(table.Release(last)); // 255+1 将回绕 → 退役而非绕回
    CHECK(table.RetiredCount() == 1u);

    // 该槽全部历史句柄（含 last、first）一律拒绝
    void* out = nullptr;
    CHECK(!table.TryGet(last, out));
    CHECK(!table.TryGet(first, out));

    // 下一次分配必须开辟新槽（退役槽永不复用），新槽从 gen 1 起算
    ZrSampler fresh = table.Acquire(nullptr);
    CHECK(fresh.IsValid());
    CHECK(fresh.Index() != idx);
    CHECK(fresh.Generation() == 1u);
    CHECK(table.RetiredCount() == 1u);
    CHECK(!table.TryGet(ZrSampler::FromRaw((255u << 24) | idx), out)); // 伪造末代句柄也无效
}

TEST_CASE("句柄表：零值/哨兵/越界/代际超前句柄一律拒绝")
{
    ZrHandleTable<ZrImage> table;
    ZrImage a = table.Acquire(nullptr);
    REQUIRE(a.IsValid());

    void* out = nullptr;
    ZrImage const zero = ZrImage::FromRaw(0u); // memset 零句柄：gen 0 保留
    ZrImage const invalid{};                   // 哨兵
    ZrImage const oob = ZrImage::FromRaw((1u << 24) | 0xFFFFFFu); // 越界 index
    ZrImage const future
        = ZrImage::FromRaw(((a.Generation() + 1u) << 24) | a.Index()); // 代际超前
    CHECK(!table.TryGet(zero, out));
    CHECK(!table.TryGet(invalid, out));
    CHECK(!table.TryGet(oob, out));
    CHECK(!table.TryGet(future, out));
    CHECK(!table.Release(zero));
    CHECK(!table.Release(invalid));

    // 释放后失效
    CHECK(table.TryGet(a, out));
    CHECK(table.Release(a));
    CHECK(!table.TryGet(a, out));
    CHECK(!table.Release(a));
}

TEST_CASE("不同句柄类型的表互相独立")
{
    ZrHandleTable<ZrImage> images;
    ZrHandleTable<ZrBuffer> buffers;
    ZrImage img = images.Acquire(nullptr);
    ZrBuffer buf = buffers.Acquire(nullptr);
    CHECK(img.Index() == 0u); // 各表独立计数
    CHECK(buf.Index() == 0u);

    // 同 index 不同类型互不干扰；释放一方不影响他方
    void* out = nullptr;
    CHECK(images.TryGet(img, out));
    CHECK(buffers.TryGet(buf, out));
    CHECK(images.Release(img));
    CHECK(buffers.TryGet(buf, out)); // buffers 表不受影响
    CHECK(!images.TryGet(img, out));

    // 24 bit 索引容量（16777215 槽上限保证哨兵唯一）
    CHECK(PopCount32(ZR_HANDLE_INDEX_MASK) == 24u);
    CHECK(PopCount32(ZR_HANDLE_GEN_MASK) == 8u);
}
