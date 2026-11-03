// =============================================================================
// test_draw_list.cpp —— 共享可见性 + 64 位 sort key + 绘制列表编译单测
// （TASK-B-R4-02）
//
// 覆盖：
//   - 可见性：三视图交叠掩码过滤，各视图遍历集合精确断言；掩码合并；
//             零掩码拒绝；越界视图防御
//   - key：手工构造已知项断言编码值（逐字段移位核对）；透明/不透明双向
//          深度序；shader 聚簇；量化平局保持插入序
//   - 排序：LSD radix 与 std::sort 对拍（10 万随机项逐项一致）；
//           零分配（容量/数据指针不变，排序路径代码审查无 new/malloc）
//   - 合批：同状态合 1 批 / 状态切换断批 / drawArgs 数值 / drawID 全局连续
//   - 容量：超容量 Add 拒绝
// =============================================================================
#include "doctest/doctest.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <random>
#include <vector>

#include "zrender/draw_list.h"
#include "zrender/shadows.h" // ShadowViewMask（描边掩码与级联掩码正交性断言用）

// =============================================================================
// 可见性
// =============================================================================

TEST_CASE("visibility: 三视图交叠掩码过滤——各视图遍历集合精确断言")
{
    VisibilitySet vs;
    REQUIRE(vs.AddItem(0u, 0x07u)); // 三视图全见
    REQUIRE(vs.AddItem(1u, 0x01u)); // 仅视图 0
    REQUIRE(vs.AddItem(2u, 0x06u)); // 视图 1+2
    REQUIRE(vs.AddItem(3u, 0x04u)); // 仅视图 2

    const auto collect = [&](uint32_t view) {
        std::vector<uint32_t> ids;
        vs.Iterate(view, [&](uint32_t id) { ids.push_back(id); });
        return ids;
    };

    CHECK(collect(0) == std::vector<uint32_t> { 0u, 1u });
    CHECK(collect(1) == std::vector<uint32_t> { 0u, 2u });
    CHECK(collect(2) == std::vector<uint32_t> { 0u, 2u, 3u });

    CHECK(vs.Count() == 4u);
    CHECK(vs.ViewMaskOf(0u) == 0x07u);
    CHECK(vs.ViewMaskOf(2u) == 0x06u);
    CHECK(vs.ViewMaskOf(99u) == 0u); // 不存在
}

TEST_CASE("visibility: 同 id 掩码合并 / 零掩码拒绝 / 越界视图防御")
{
    VisibilitySet vs;
    REQUIRE(vs.AddItem(5u, 0x01u));
    REQUIRE(vs.AddItem(5u, 0x04u)); // 重复添加 -> OR 合并
    CHECK(vs.Count() == 1u);
    CHECK(vs.ViewMaskOf(5u) == 0x05u);

    CHECK_FALSE(vs.AddItem(6u, 0x00u)); // 零掩码拒绝
    CHECK(vs.Count() == 1u);

    // 越界视图：不迭代任何项（无 UB）
    vs.Iterate(VisibilitySet::kMaxViews, [](uint32_t) {
        FAIL("iterate must not visit items for an out-of-range view");
    });
    vs.Iterate(255u, [](uint32_t) { FAIL("out-of-range view 255 must not iterate"); });

    vs.Reset();
    CHECK(vs.Count() == 0u);
}

TEST_CASE("drawlist: Add 转发可见性（renderItemId=插入序号）")
{
    DrawList dl;
    RenderItem a {};
    a.meshHandle = 1u;
    RenderItem b {};
    b.meshHandle = 2u;
    REQUIRE(dl.Add(a, 0x03u));
    REQUIRE(dl.Add(b, 0x04u));
    CHECK(dl.Visibility().ViewMaskOf(0u) == 0x03u);
    CHECK(dl.Visibility().ViewMaskOf(1u) == 0x04u);
    CHECK(dl.Visibility().Count() == 2u);
}

// =============================================================================
// key 编码（位布局核对）
// =============================================================================

TEST_CASE("key: 已知项编码值逐字段核对（不透明与半透明）")
{
    // depth=0.5f -> bits 0x3F000000 -> sortable 0xBF000000 -> depth24=0xBF0000
    RenderItem item;
    item.meshHandle = 0x13u;
    item.materialId = 0xBEEFu;
    item.shaderId = 0xABu;
    item.lod = 2u;
    item.depth = 0.5f;
    item.translucent = false;

    // meshLod7 = ((0x13 & 0x1F) << 2) | (2 & 3) = 0x4C | 2 = 0x4E
    const uint64_t opaqueExpected = (0xBF0000ull << 39) // [62:39] 深度（近->小）
        | (0xABull << 31)                               // [38:31] shader
        | (0xBEEFull << 15)                             // [30:15] material
        | (0x4Eull << 8)                                // [14:8] mesh/lod
        | 7ull;                                         // [7:0] 序号
    CHECK(DrawList::EncodeSortKey(item, 7u) == opaqueExpected);
    CHECK((DrawList::EncodeSortKey(item, 7u) >> 63) == 0ull); // bit63=0

    item.translucent = true;
    // 反序量化：0xFFFFFF - 0xBF0000 = 0x40FFFF（远 -> 小）
    const uint64_t translucentExpected = (1ull << 63) | (0x40FFFFull << 39)
        | (0xABull << 31) | (0xBEEFull << 15) | (0x4Eull << 8) | 7ull;
    CHECK(DrawList::EncodeSortKey(item, 7u) == translucentExpected);
}

TEST_CASE("key: 不透明深度前->后 / 半透明深度后->前 / 不透明整体在前")
{
    RenderItem near_ {};
    near_.depth = 1.0f;
    near_.shaderId = 1u;
    RenderItem far_ {};
    far_.depth = 100.0f;
    far_.shaderId = 1u;

    // 不透明：近者 key 小（排序后先画 => 前->后）
    CHECK(DrawList::EncodeSortKey(near_, 0u) < DrawList::EncodeSortKey(far_, 1u));

    // 半透明：远者 key 小（排序后先画 => 后->前）
    near_.translucent = true;
    far_.translucent = true;
    CHECK(DrawList::EncodeSortKey(far_, 0u) < DrawList::EncodeSortKey(near_, 1u));

    // 不透明（bit63=0）整体排在半透明（bit63=1）之前，无论深度
    RenderItem opaqueFar {};
    opaqueFar.depth = 1e9f;
    RenderItem translucentNear {};
    translucentNear.depth = 0.001f;
    translucentNear.translucent = true;
    CHECK(DrawList::EncodeSortKey(opaqueFar, 0u) < DrawList::EncodeSortKey(translucentNear, 1u));
}

TEST_CASE("key: 同深度按 shader 聚簇（状态聚簇为排序天然产物）")
{
    RenderItem base {};
    base.depth = 7.0f;
    base.materialId = 3u;
    RenderItem s9 = base;
    s9.shaderId = 9u;
    RenderItem s2 = base;
    s2.shaderId = 2u;
    RenderItem s5 = base;
    s5.shaderId = 5u;
    CHECK(DrawList::EncodeSortKey(s2, 0u) < DrawList::EncodeSortKey(s5, 1u));
    CHECK(DrawList::EncodeSortKey(s5, 0u) < DrawList::EncodeSortKey(s9, 1u));
}

TEST_CASE("key: 深度量化平局（24bit 内不可分辨）保持插入序")
{
    RenderItem a {};
    a.depth = 1.0f;
    a.shaderId = 4u;
    RenderItem b {};
    // b.depth = 1.0 的下一个可表示 float（位模式 0x3F800001）；高 24bit 可排序值相同
    const uint32_t depthBitsOnePlusEps = 0x3F800001u;
    std::memcpy(&b.depth, &depthBitsOnePlusEps, sizeof(float));
    b.shaderId = 4u;

    // 高位完全相同，仅序号字节区分 -> 先插入者 key 小
    CHECK((DrawList::EncodeSortKey(a, 0u) ^ DrawList::EncodeSortKey(b, 1u)) == 1ull);
    CHECK(DrawList::EncodeSortKey(a, 0u) < DrawList::EncodeSortKey(b, 1u));
}

// =============================================================================
// 排序
// =============================================================================

TEST_CASE("sort: 不透明按深度前->后；同深度按 shader 聚簇；半透明反序")
{
    DrawList dl;
    auto add = [&](float depth, uint32_t shader, bool translucent) {
        RenderItem it;
        it.depth = depth;
        it.shaderId = shader;
        it.materialId = 1u;
        it.translucent = translucent;
        REQUIRE(dl.Add(it, 0x01u));
    };

    add(3.0f, 1u, false);
    add(1.0f, 1u, false);
    add(2.0f, 1u, false);
    add(2.0f, 5u, false); // 与 2.0 同深度：shader 1 先于 5（聚簇）
    add(1.0f, 9u, false); // 同深度 1.0：shader 1 先于 9
    add(1.0f, 1u, true);  // 半透明排全部不透明之后
    add(50.0f, 1u, true);
    dl.Sort();

    REQUIRE(dl.Count() == 7u);
    // 不透明前->后，同深度 shader 升序
    CHECK(dl.ItemAt(0u).depth == 1.0f);
    CHECK(dl.ItemAt(0u).shaderId == 1u);
    CHECK(dl.ItemAt(1u).depth == 1.0f);
    CHECK(dl.ItemAt(1u).shaderId == 9u);
    CHECK(dl.ItemAt(2u).depth == 2.0f);
    CHECK(dl.ItemAt(2u).shaderId == 1u);
    CHECK(dl.ItemAt(3u).depth == 2.0f);
    CHECK(dl.ItemAt(3u).shaderId == 5u);
    CHECK(dl.ItemAt(4u).depth == 3.0f);
    // 半透明后->前（远者先）
    CHECK(dl.ItemAt(5u).translucent);
    CHECK(dl.ItemAt(5u).depth == 50.0f);
    CHECK(dl.ItemAt(6u).translucent);
    CHECK(dl.ItemAt(6u).depth == 1.0f);
    // key 升序贯穿
    for (uint32_t i = 1; i < dl.Count(); ++i) {
        CHECK(dl.KeyOf(i - 1u) < dl.KeyOf(i));
    }
}

TEST_CASE("sort: 与 std::sort 对拍——10 万随机项逐项一致（不透明+半透明混合）")
{
    constexpr uint32_t kN = 100000u;
    DrawList dl(kN);
    std::mt19937 rng(0xD5A0C0DEu);
    std::uniform_real_distribution<float> depthDist(0.0f, 1000.0f);
    std::uniform_int_distribution<uint32_t> u32(0u, 0xFFFFFFFFu);

    for (uint32_t i = 0; i < kN; ++i) {
        RenderItem it;
        it.meshHandle = u32(rng);
        it.materialId = u32(rng); // 全域随机：顺带验证 16bit 截断不破坏一致性
        it.shaderId = u32(rng);   // 全域随机：8bit 截断制造大量同 key 段
        it.lod = u32(rng) & 0x3u;
        // 量化到 0.5 网格：制造大量深度平局
        it.depth = static_cast<float>(static_cast<int>(depthDist(rng) * 2.0f)) * 0.5f;
        it.translucent = (u32(rng) & 1u) != 0u;
        REQUIRE(dl.Add(it, 0xFFu));
    }
    REQUIRE(dl.Count() == kN);

    // 参照：key 副本经 std::sort
    std::vector<uint64_t> reference(dl.Keys(), dl.Keys() + kN);
    std::sort(reference.begin(), reference.end());

    dl.Sort();
    for (uint32_t i = 0; i < kN; ++i) {
        REQUIRE(dl.KeyOf(i) == reference[i]);
    }

    // 项与 key 一致：排序后每项重编码（序号字节置 0）与 key（抹序号字节）相等
    for (uint32_t i = 0; i < kN; ++i) {
        const uint64_t reencoded = DrawList::EncodeSortKey(dl.ItemAt(i), 0u);
        REQUIRE((dl.KeyOf(i) & ~0xFFull) == (reencoded & ~0xFFull));
    }
}

TEST_CASE("sort: 零分配——排序前后容量与数据指针不变")
{
    DrawList dl(256u);
    std::mt19937 rng(42u);
    for (uint32_t i = 0; i < 256u; ++i) {
        RenderItem it;
        it.depth = static_cast<float>(i % 17);
        it.shaderId = i % 5u;
        REQUIRE(dl.Add(it, 0x01u));
    }

    const uint32_t capacityBefore = dl.Capacity();
    const RenderItem* itemsBefore = dl.Items();
    const uint64_t* keysBefore = dl.Keys();

    dl.Sort();
    dl.Sort(); // 重复排序幂等（早退）

    CHECK(dl.Capacity() == capacityBefore);
    CHECK(dl.Items() == itemsBefore); // 无重分配 => 排序路径未触碰堆
    CHECK(dl.Keys() == keysBefore);
}

// =============================================================================
// 合批
// =============================================================================

TEST_CASE("batch: 同状态连续项合 1 批；状态切换处断批；drawArgs 数值正确")
{
    DrawList dl;
    dl.RegisterMesh(0x11u, 8u);   // meshA: 8 顶点
    dl.RegisterMesh(0x12u, 16u);  // meshB: 16 顶点

    auto add = [&](uint32_t shader, uint32_t mesh) {
        RenderItem it;
        it.depth = 5.0f; // 同深度：序由 shader/mesh 字段决定
        it.shaderId = shader;
        it.materialId = 100u;
        it.meshHandle = mesh;
        REQUIRE(dl.Add(it, 0x01u));
    };
    add(1u, 0x11u); // 排序后 [0..2]：S1/M100/MeshA
    add(1u, 0x11u);
    add(1u, 0x11u);
    add(1u, 0x12u); // [3]：S1/M100/MeshB（mesh 变 -> 断批）
    add(2u, 0x11u); // [4..5]：S2/M100/MeshA（shader 变 -> 断批）
    add(2u, 0x11u);

    dl.Sort();
    REQUIRE(dl.BuildBatches() == 3u);

    const auto& batches = dl.Batches();
    REQUIRE(batches.size() == 3u);
    CHECK(batches[0].startIndex == 0u);
    CHECK(batches[0].count == 3u);
    CHECK(batches[0].shaderId == 1u);
    CHECK(batches[0].materialId == 100u);
    CHECK(batches[0].meshHandle == 0x11u);
    CHECK(batches[0].drawIdBase == 0u);
    CHECK(batches[1].startIndex == 3u);
    CHECK(batches[1].count == 1u);
    CHECK(batches[1].meshHandle == 0x12u);
    CHECK(batches[1].drawIdBase == 3u);
    CHECK(batches[2].startIndex == 4u);
    CHECK(batches[2].count == 2u);
    CHECK(batches[2].shaderId == 2u);
    CHECK(batches[2].drawIdBase == 4u);

    const auto& args = dl.DrawArgs();
    REQUIRE(args.size() == 3u);
    CHECK(args[0].vertexIndex == 8u);
    CHECK(args[0].instanceCount == 3u);
    CHECK(args[0].startVertex == 0u);
    CHECK(args[0].startInstance == 0u);
    CHECK(args[1].vertexIndex == 16u);
    CHECK(args[1].instanceCount == 1u);
    CHECK(args[1].startInstance == 3u);
    CHECK(args[2].vertexIndex == 8u);
    CHECK(args[2].instanceCount == 2u);
    CHECK(args[2].startInstance == 4u);
}

TEST_CASE("batch: 深度交错使同状态项分段——状态切换处断批为 3 批")
{
    DrawList dl;
    auto add = [&](float depth, uint32_t shader) {
        RenderItem it;
        it.depth = depth;
        it.shaderId = shader;
        it.materialId = 7u;
        it.meshHandle = 1u;
        REQUIRE(dl.Add(it, 0x01u));
    };
    add(1.0f, 1u); // 排序后：S1(depth1) S2(depth2) S1(depth3) -> 3 批
    add(2.0f, 2u);
    add(3.0f, 1u);

    dl.Sort();
    REQUIRE(dl.BuildBatches() == 3u);
    CHECK(dl.Batches()[0].count == 1u);
    CHECK(dl.Batches()[0].shaderId == 1u);
    CHECK(dl.Batches()[1].shaderId == 2u);
    CHECK(dl.Batches()[2].shaderId == 1u);
}

TEST_CASE("batch: drawID 表跨批全局连续（0..总数-1）且批内每实例一个")
{
    DrawList dl;
    auto add = [&](uint32_t shader, float depth) {
        RenderItem it;
        it.depth = depth;
        it.shaderId = shader;
        REQUIRE(dl.Add(it, 0x01u));
    };
    add(1u, 1.0f); // batch0: 2 实例（drawID 0,1）
    add(1u, 1.5f);
    add(2u, 2.0f); // batch1: 3 实例（drawID 2,3,4）
    add(2u, 2.5f);
    add(2u, 3.0f);

    dl.Sort();
    REQUIRE(dl.BuildBatches() == 2u);
    CHECK(dl.DrawIds().size() == 5u);
    for (uint32_t i = 0; i < 5u; ++i) {
        CHECK(dl.DrawIds()[i] == i); // 全局连续
    }
    CHECK(dl.Batches()[0].drawIdBase == 0u);
    CHECK(dl.Batches()[0].count == 2u);
    CHECK(dl.Batches()[1].drawIdBase == 2u);
    CHECK(dl.Batches()[1].count == 3u);
    // startInstance == drawIdBase 闭环
    CHECK(dl.DrawArgs()[0].startInstance == 0u);
    CHECK(dl.DrawArgs()[1].startInstance == 2u);
}

// =============================================================================
// 容量与生命周期
// =============================================================================

TEST_CASE("capacity: 超容量 Add 拒绝且状态不变；Reset 后可复用且零分配")
{
    DrawList dl(3u);
    RenderItem it;
    it.depth = 1.0f;
    REQUIRE(dl.Add(it, 0x01u));
    REQUIRE(dl.Add(it, 0x01u));
    REQUIRE(dl.Add(it, 0x01u));
    CHECK_FALSE(dl.Add(it, 0x01u)); // 超容量
    CHECK(dl.Count() == 3u);
    CHECK(dl.Capacity() == 3u);

    dl.Sort();
    dl.BuildBatches();
    const RenderItem* itemsBefore = dl.Items();
    dl.Reset();
    CHECK(dl.Count() == 0u);
    CHECK(dl.Items() == itemsBefore); // 容量保留，复用零分配
    REQUIRE(dl.Add(it, 0x01u));       // 清空后可再添加
    CHECK(dl.Count() == 1u);

    CHECK_FALSE(dl.Add(it, 0x00u)); // 零掩码拒绝
}

// =============================================================================
// 描边视图掩码位（TASK-B-R5-03）—— NPR 反向壳描边的第二次遍历
// =============================================================================
TEST_CASE("outline view: bit5 mask is orthogonal to main/cascade bits and iterates independently")
{
    DrawList dl(8u);
    dl.RegisterMesh(1u, 24u);
    RenderItem ground;
    ground.meshHandle = 1u;
    ground.depth = 2.0f;
    RenderItem toon;
    toon.meshHandle = 1u;
    toon.depth = 3.0f;
    toon.shaderId = 1u;

    // 地面仅主视图；toon cube 主视图 + 描边视图（简报：outline 视图掩码位）
    REQUIRE(dl.Add(ground, OutlineViewMask(false)));
    REQUIRE(dl.Add(toon, static_cast<uint8_t>(ShadowViewMask(false) | kOutlineViewBit)));

    // 契约：主视图 bit0 恒可见；描边位 = bit5（0x20）；两者正交
    CHECK(dl.Visibility().ViewMaskOf(0u) == 0x01u);
    CHECK(dl.Visibility().ViewMaskOf(1u) == 0x21u);
    static_assert(kOutlineViewId == 5u, "outline view is bit5 (bits1..4 = cascade views)");

    // 第二次遍历（描边 pass）只收集描边位项：地面（id 0）不出现
    std::vector<uint32_t> outlineItems;
    dl.Visibility().Iterate(kOutlineViewId, [&](uint32_t id) { outlineItems.push_back(id); });
    REQUIRE(outlineItems.size() == 1u);
    CHECK(outlineItems[0] == 1u);

    // 主视图两项齐全
    std::vector<uint32_t> mainItems;
    dl.Visibility().Iterate(0u, [&](uint32_t id) { mainItems.push_back(id); });
    REQUIRE(mainItems.size() == 2u);
}
