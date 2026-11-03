// zmesh.cpp —— .zmesh v1 解析与校验实现（TASK-B-R4-01）
//
// 纯逻辑（无 GPU/系统依赖）：magic/version/flags/尺寸一致性校验 + 索引范围
// 校验 + 位置列包围盒统计。所有错误路一律拒绝（返回负错误码，不写 outData）。
#include "zrender/zmesh.h"

#include <cstring>

extern "C" {

size_t zmesh_CalcBodySize(const ZmeshHeader* header)
{
    if (header == nullptr) {
        return 0; // 防御路径
    }
    const uint64_t n = header->vertexCount;
    const uint64_t m = header->indexCount;
    uint64_t bytes = n * 3u * sizeof(float); // positions
    if (header->flags & 1u) { bytes += n * 3u * sizeof(float); } // normals
    if (header->flags & 2u) { bytes += n * 2u * sizeof(float); } // uv0
    bytes += m * sizeof(uint32_t); // indices
    return static_cast<size_t>(bytes);
}

int zmesh_Parse(const void* data, size_t sizeBytes, ZmeshData* outData)
{
    if (data == nullptr || outData == nullptr) {
        return ZMESH_ERR_BAD_ARGS;
    }
    if (sizeBytes < sizeof(ZmeshHeader)) {
        return ZMESH_ERR_BAD_SIZE;
    }

    ZmeshHeader header;
    std::memcpy(&header, data, sizeof(header));

    if (std::memcmp(header.magic, "ZMSH", 4) != 0) {
        return ZMESH_ERR_BAD_MAGIC;
    }
    if (header.version != 1u) {
        return ZMESH_ERR_BAD_VERSION;
    }
    if ((header.flags & ~3u) != 0u) {
        return ZMESH_ERR_BAD_FLAGS;
    }

    const size_t bodySize = zmesh_CalcBodySize(&header);
    if (bodySize != sizeBytes - sizeof(ZmeshHeader)) {
        return ZMESH_ERR_BAD_SIZE;
    }

    const uint8_t* base = static_cast<const uint8_t*>(data) + sizeof(ZmeshHeader);
    size_t offset = 0;

    const float* positions = reinterpret_cast<const float*>(base + offset);
    offset += header.vertexCount * 3u * sizeof(float);

    const float* normals = nullptr;
    if (header.flags & 1u) {
        normals = reinterpret_cast<const float*>(base + offset);
        offset += header.vertexCount * 3u * sizeof(float);
    }

    const float* uvs = nullptr;
    if (header.flags & 2u) {
        uvs = reinterpret_cast<const float*>(base + offset);
        offset += header.vertexCount * 2u * sizeof(float);
    }

    const uint32_t* indices = reinterpret_cast<const uint32_t*>(base + offset);

    for (uint32_t i = 0; i < header.indexCount; ++i) {
        if (indices[i] >= header.vertexCount) {
            return ZMESH_ERR_INDEX_RANGE;
        }
    }

    ZmeshStats stats{};
    stats.vertexCount = header.vertexCount;
    stats.indexCount = header.indexCount;
    stats.hasNormals = (header.flags & 1u) ? 1u : 0u;
    stats.hasUv = (header.flags & 2u) ? 1u : 0u;
    if (header.vertexCount > 0u) {
        stats.boundsMin[0] = stats.boundsMax[0] = positions[0];
        stats.boundsMin[1] = stats.boundsMax[1] = positions[1];
        stats.boundsMin[2] = stats.boundsMax[2] = positions[2];
        for (uint32_t v = 1u; v < header.vertexCount; ++v) {
            for (uint32_t c = 0u; c < 3u; ++c) {
                const float value = positions[v * 3u + c];
                if (value < stats.boundsMin[c]) { stats.boundsMin[c] = value; }
                if (value > stats.boundsMax[c]) { stats.boundsMax[c] = value; }
            }
        }
    }

    outData->stats = stats;
    outData->positions = positions;
    outData->normals = normals;
    outData->uvs = uvs;
    outData->indices = indices;
    return ZMESH_OK;
}

} // extern "C"
