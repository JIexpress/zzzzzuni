// =============================================================================
// zmesh.h —— .zmesh v1 资产格式解析（TASK-B-R4-01）
//
// 格式（小端，版本 1）：
//   头 20B: magic 'Z''M''S''H'(4B) | version u32=1 | vertexCount u32
//           | indexCount u32 | flags u32（bit0=含法线列 bit1=含 uv0 列）
//   体（列式/SoA，各列连续）：
//           positions float3×N | normals float3×N(bit0) | uv0 float2×N(bit1)
//           | indices u32×M
//
// 头结构体在 C# 侧（UnityProject/Assets/ZRender/Runtime/ZMesh.cs 的
// ZmeshHeader）各定义一份并注释对齐——两侧均为 POD、显式固定字段、无 padding，
// 字节大小断言两侧都有（C++ static_assert 20B / C# Marshal.SizeOf==20）。
// C# 侧 magic 以小端 u32 0x48534D5A 表示，与本侧 char[4]="ZMSH" 等值。
//
// 命名遵循 file-conventions.md §3；前缀 zmesh_/Zmesh。
// =============================================================================
#pragma once

#include <cstddef>
#include <cstdint>

// .zmesh 头（POD，显式固定字段，无 padding；两侧字节布局一致）。
struct ZmeshHeader {
    char magic[4];        // 'Z''M''S''H'
    uint32_t version;     // 当前 1
    uint32_t vertexCount; // N
    uint32_t indexCount;  // M
    uint32_t flags;       // bit0=normals bit1=uv0（其余位必须为 0）
};
static_assert(sizeof(ZmeshHeader) == 20, "ZmeshHeader must be 20 bytes (no padding)");

// 网格统计（ZR_MeshStatsGet 的跨边界载荷；全定宽字段，无 padding）。
struct ZmeshStats {
    uint32_t vertexCount;
    uint32_t indexCount;
    uint32_t hasNormals; // 0/1
    uint32_t hasUv;      // 0/1
    float boundsMin[3];  // 位置列包围盒（无顶点时为 0）
    float boundsMax[3];
};
static_assert(sizeof(ZmeshStats) == 40, "ZmeshStats must be 40 bytes (no padding)");

// 解析结果：各列指针指向入参 buffer 内部（零拷贝），生命周期随 buffer。
struct ZmeshData {
    ZmeshStats stats;
    const float* positions;   // 3*N（必有）
    const float* normals;     // 3*N（flags bit0），否则 nullptr
    const float* uvs;         // 2*N（flags bit1），否则 nullptr
    const uint32_t* indices;  // M
};

// 错误码（负值；0 成功）。
enum ZmeshError {
    ZMESH_OK = 0,
    ZMESH_ERR_BAD_ARGS = -1,     // 空指针参数
    ZMESH_ERR_BAD_MAGIC = -2,    // magic != "ZMSH"
    ZMESH_ERR_BAD_VERSION = -3,  // version != 1
    ZMESH_ERR_BAD_FLAGS = -4,    // 未知 flag 位
    ZMESH_ERR_BAD_SIZE = -5,     // buffer 小于头 / 体大小与头声明不一致
    ZMESH_ERR_INDEX_RANGE = -6   // 索引值 >= vertexCount
};

extern "C" {

/// 按头声明计算体字节数（不含 20B 头）。
size_t zmesh_CalcBodySize(const ZmeshHeader* header);

/// 从内存 buffer 解析 .zmesh（零拷贝：outData 各列指针指向 buffer 内部）。
/// 成功返回 ZMESH_OK；任何校验失败返回负错误码且不写 outData。
int zmesh_Parse(const void* data, size_t sizeBytes, ZmeshData* outData);

} // extern "C"
