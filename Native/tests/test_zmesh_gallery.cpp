// =============================================================================
// test_zmesh_gallery.cpp —— 4 人物 .zmesh 真数据解析（TASK-B-R9-13）
//
// cube.zmesh（B-R4-01）是合成数据单测 + 窗口程序通道；本文件第一次把真建模
// 资产的烘焙产物拉进 doctest：Unity ZMeshBaker.BakeGalleryToRepoData 烘焙的
// CHR_*.zmesh（画廊模式输入）逐个解析断言：
//   - counts > 0、索引数 3 的倍数、法线列存在（pbr 管线要求）
//   - 包围盒合理：三轴 min<max；y 跨度（Y-up 站立高度）落在人形档 0.5..3.0m；
//     x/z 跨度 ≤ 高度×1.5（站立人形语义）
// 烘焙侧导入设置（bakeAxisConversion / globalScale）若回退（Z-up 未转、厘米级
// 缩放丢失），这里的包围盒断言会红——用例对"轴朝向/缩放"风险有牙。
// 路径相对仓库根（ctest WORKING_DIRECTORY 由 CMake 钉仓库根，G-63 先例）。
// =============================================================================
#include "doctest/doctest.h"

#include "zrender/zmesh.h"

#include <cstdint>
#include <fstream>
#include <vector>

namespace {

bool ReadAllBytes(const char* path, std::vector<uint8_t>& out)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        return false;
    }
    const std::streampos length = file.tellg();
    file.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(length));
    return static_cast<bool>(file.read(reinterpret_cast<char*>(out.data()), length));
}

/// 真数据画廊网格公共断言（counts/法线/包围盒站立语义）。
void CheckGalleryMesh(const char* path)
{
    std::vector<uint8_t> bytes;
    REQUIRE_MESSAGE(ReadAllBytes(path, bytes), path);

    ZmeshData data {};
    REQUIRE(zmesh_Parse(bytes.data(), bytes.size(), &data) == ZMESH_OK);

    CHECK(data.stats.vertexCount > 0u);
    CHECK(data.stats.indexCount > 0u);
    CHECK(data.stats.indexCount % 3u == 0u); // 全三角面（烘焙取 mesh.triangles）
    CHECK(data.stats.hasNormals == 1u);      // PBR 管线要求法线列

    // 包围盒：三轴非退化
    for (int axis = 0; axis < 3; ++axis) {
        CHECK(data.stats.boundsMax[axis] > data.stats.boundsMin[axis]);
    }
    // Y-up 站立语义：高度（y 跨度）人形档 0.5..3.0m；x/z 跨度 ≤ 高度×1.5
    //（Z-up 轴转换回退时 y 跨度塌缩到厘米级；scale 丢失时全轴塌缩——两者都会红）
    const float height = data.stats.boundsMax[1] - data.stats.boundsMin[1];
    const float width = data.stats.boundsMax[0] - data.stats.boundsMin[0];
    const float depth = data.stats.boundsMax[2] - data.stats.boundsMin[2];
    CHECK(height > 0.5f);
    CHECK(height < 3.0f);
    CHECK(width <= height * 1.5f);
    CHECK(depth <= height * 1.5f);
}

} // namespace

TEST_CASE("zmesh gallery: CHR_Kenney_CharacterMedium.zmesh 真数据解析")
{
    CheckGalleryMesh("Native/tests/data/CHR_Kenney_CharacterMedium.zmesh");
}

TEST_CASE("zmesh gallery: CHR_Robot.zmesh 真数据解析")
{
    CheckGalleryMesh("Native/tests/data/CHR_Robot.zmesh");
}

TEST_CASE("zmesh gallery: CHR_Human.zmesh 真数据解析")
{
    CheckGalleryMesh("Native/tests/data/CHR_Human.zmesh");
}

TEST_CASE("zmesh gallery: CHR_DarkElf.zmesh 真数据解析")
{
    CheckGalleryMesh("Native/tests/data/CHR_DarkElf.zmesh");
}

TEST_CASE("zmesh gallery: 4 真数据 counts 汇总（画廊输入非空清单）")
{
    const char* paths[] = {
        "Native/tests/data/CHR_Kenney_CharacterMedium.zmesh",
        "Native/tests/data/CHR_Robot.zmesh",
        "Native/tests/data/CHR_Human.zmesh",
        "Native/tests/data/CHR_DarkElf.zmesh",
    };
    uint64_t totalVerts = 0u;
    uint64_t totalIndices = 0u;
    for (const char* path : paths) {
        std::vector<uint8_t> bytes;
        REQUIRE_MESSAGE(ReadAllBytes(path, bytes), path);
        ZmeshData data {};
        REQUIRE(zmesh_Parse(bytes.data(), bytes.size(), &data) == ZMESH_OK);
        totalVerts += data.stats.vertexCount;
        totalIndices += data.stats.indexCount;
    }
    // 画廊 4 模型合计非平凡（低于 1k 顶点说明烘焙塌缩/取错子资产）
    CHECK(totalVerts >= 1000u);
    CHECK(totalIndices >= 1000u);
    std::printf("zmesh gallery: total verts=%llu indices=%llu across 4 CHR meshes\n",
        static_cast<unsigned long long>(totalVerts),
        static_cast<unsigned long long>(totalIndices));
}
