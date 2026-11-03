// =============================================================================
// test_zmesh.cpp —— .zmesh v1 解析 + zplugin 加载 C ABI 单测（TASK-B-R4-01）
//
// 覆盖：
//   - 头/统计结构体字节大小断言（与 C# 侧 Marshal.SizeOf 断言对齐）
//   - 合成小 buffer（3 顶点 3 索引，含法线/uv0）roundtrip 解析
//   - 错误路：坏 magic / 坏 version / 未知 flags / 截断 / 尺寸不一致 / 索引越界
//   - C ABI：句柄加载→统计→释放；坏数据拒绝；重复释放/野句柄拒绝
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <cstring>
#include <vector>

#include "zplugin/zplugin.h"
#include "zrender/zmesh.h"

namespace {

// 合成一个最小合法 .zmesh：3 顶点（含法线+uv0）3 索引。
std::vector<uint8_t> MakeSyntheticMesh()
{
    constexpr uint32_t kVerts = 3;
    constexpr uint32_t kIndices = 3;
    const float positions[kVerts * 3] = {
        0.f, 0.f, 0.f,
        1.f, 0.f, 0.f,
        0.f, 1.f, 0.f,
    };
    const float normals[kVerts * 3] = {
        0.f, 0.f, 1.f,
        0.f, 0.f, 1.f,
        0.f, 0.f, 1.f,
    };
    const float uvs[kVerts * 2] = {
        0.f, 0.f,
        1.f, 0.f,
        0.f, 1.f,
    };
    const uint32_t indices[kIndices] = { 0u, 1u, 2u };

    std::vector<uint8_t> buffer(sizeof(ZmeshHeader)
        + sizeof(positions) + sizeof(normals) + sizeof(uvs) + sizeof(indices));
    ZmeshHeader header{};
    std::memcpy(header.magic, "ZMSH", 4);
    header.version = 1u;
    header.vertexCount = kVerts;
    header.indexCount = kIndices;
    header.flags = 1u | 2u; // normals | uv0
    size_t offset = 0;
    std::memcpy(buffer.data() + offset, &header, sizeof(header));
    offset += sizeof(header);
    std::memcpy(buffer.data() + offset, positions, sizeof(positions));
    offset += sizeof(positions);
    std::memcpy(buffer.data() + offset, normals, sizeof(normals));
    offset += sizeof(normals);
    std::memcpy(buffer.data() + offset, uvs, sizeof(uvs));
    offset += sizeof(uvs);
    std::memcpy(buffer.data() + offset, indices, sizeof(indices));
    return buffer;
}

} // namespace

TEST_CASE("zmesh: 头与统计结构体字节大小（与 C# 侧断言对齐）")
{
    CHECK(sizeof(ZmeshHeader) == 20u);
    CHECK(sizeof(ZmeshStats) == 40u);
    CHECK(zmesh_CalcBodySize(nullptr) == 0u); // 空头按 0 体（防御路径）
}

TEST_CASE("zmesh: 合成 3 顶点 3 索引 roundtrip 解析")
{
    const std::vector<uint8_t> buffer = MakeSyntheticMesh();

    ZmeshData data{};
    REQUIRE(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_OK);

    CHECK(data.stats.vertexCount == 3u);
    CHECK(data.stats.indexCount == 3u);
    CHECK(data.stats.hasNormals == 1u);
    CHECK(data.stats.hasUv == 1u);

    CHECK(data.positions[0] == 0.f); // 首顶点
    CHECK(data.positions[3] == 1.f); // 第二顶点 x
    CHECK(data.positions[8] == 0.f); // 末顶点 z
    CHECK(data.normals[2] == 1.f);   // 首法线 z
    CHECK(data.uvs[2] == 1.f);       // 第二 uv.x
    CHECK(data.indices[0] == 0u);    // 首索引
    CHECK(data.indices[2] == 2u);    // 末索引

    // 包围盒
    CHECK(data.stats.boundsMin[0] == 0.f);
    CHECK(data.stats.boundsMax[0] == 1.f);
    CHECK(data.stats.boundsMin[1] == 0.f);
    CHECK(data.stats.boundsMax[1] == 1.f);
    CHECK(data.stats.boundsMin[2] == 0.f);
    CHECK(data.stats.boundsMax[2] == 0.f);
}

TEST_CASE("zmesh: 坏 magic 拒绝")
{
    std::vector<uint8_t> buffer = MakeSyntheticMesh();
    buffer[0] = 'X';
    ZmeshData data;
    CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_BAD_MAGIC);
}

TEST_CASE("zmesh: 坏 version 拒绝")
{
    std::vector<uint8_t> buffer = MakeSyntheticMesh();
    ZmeshHeader header{};
    std::memcpy(&header, buffer.data(), sizeof(header));
    header.version = 2u;
    std::memcpy(buffer.data(), &header, sizeof(header));
    ZmeshData data;
    CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_BAD_VERSION);
}

TEST_CASE("zmesh: 未知 flags 位拒绝")
{
    std::vector<uint8_t> buffer = MakeSyntheticMesh();
    ZmeshHeader header{};
    std::memcpy(&header, buffer.data(), sizeof(header));
    header.flags = 3u | 4u; // bit2 未定义
    std::memcpy(buffer.data(), &header, sizeof(header));
    ZmeshData data;
    CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_BAD_FLAGS);
}

TEST_CASE("zmesh: 截断（小于头）拒绝")
{
    const std::vector<uint8_t> buffer = MakeSyntheticMesh();
    ZmeshData data;
    CHECK(zmesh_Parse(buffer.data(), sizeof(ZmeshHeader) - 1u, &data)
        == ZMESH_ERR_BAD_SIZE);
    CHECK(zmesh_Parse(nullptr, buffer.size(), &data) == ZMESH_ERR_BAD_ARGS);
}

TEST_CASE("zmesh: 体尺寸与头声明不一致拒绝")
{
    // 多余 1 字节
    {
        std::vector<uint8_t> buffer = MakeSyntheticMesh();
        buffer.push_back(0);
        ZmeshData data;
        CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_BAD_SIZE);
    }
    // 少 1 字节
    {
        std::vector<uint8_t> buffer = MakeSyntheticMesh();
        buffer.pop_back();
        ZmeshData data;
        CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_BAD_SIZE);
    }
}

TEST_CASE("zmesh: 索引越界拒绝")
{
    std::vector<uint8_t> buffer = MakeSyntheticMesh();
    const size_t firstIndexOffset = buffer.size() - 3u * sizeof(uint32_t);
    uint32_t badIndex = 99u; // vertexCount=3
    std::memcpy(buffer.data() + firstIndexOffset, &badIndex, sizeof(badIndex));
    ZmeshData data;
    CHECK(zmesh_Parse(buffer.data(), buffer.size(), &data) == ZMESH_ERR_INDEX_RANGE);
}

TEST_CASE("C ABI: ZR_MeshLoadFromMemory -> StatsGet -> Free 全链")
{
    const std::vector<uint8_t> buffer = MakeSyntheticMesh();

    uint64_t handle = 0;
    REQUIRE(ZR_MeshLoadFromMemory(buffer.data(),
        static_cast<uint32_t>(buffer.size()), &handle) == ZMESH_OK);
    REQUIRE(handle != 0ull);

    ZmeshStats stats{};
    REQUIRE(ZR_MeshStatsGet(handle, &stats) == ZMESH_OK);
    CHECK(stats.vertexCount == 3u);
    CHECK(stats.indexCount == 3u);
    CHECK(stats.hasNormals == 1u);
    CHECK(stats.hasUv == 1u);
    CHECK(stats.boundsMax[0] == 1.f);

    REQUIRE(ZR_MeshFree(handle) == ZMESH_OK);
    // 重复释放与已释放句柄取统计均拒绝
    CHECK(ZR_MeshFree(handle) != 0);
    CHECK(ZR_MeshStatsGet(handle, &stats) != 0);
    // 野句柄拒绝
    CHECK(ZR_MeshStatsGet(0xDEADBEEFull, &stats) != 0);
}

TEST_CASE("C ABI: 坏数据经加载入口拒绝")
{
    std::vector<uint8_t> buffer = MakeSyntheticMesh();
    buffer[0] = 'X'; // 坏 magic
    uint64_t handle = 123u; // 非零哨兵：失败时不应被改写
    CHECK(ZR_MeshLoadFromMemory(buffer.data(),
        static_cast<uint32_t>(buffer.size()), &handle) == ZMESH_ERR_BAD_MAGIC);
    CHECK(handle == 123u);

    // 尺寸不一致
    std::vector<uint8_t> truncated = MakeSyntheticMesh();
    truncated.pop_back();
    CHECK(ZR_MeshLoadFromMemory(truncated.data(),
        static_cast<uint32_t>(truncated.size()), &handle) == ZMESH_ERR_BAD_SIZE);
}
