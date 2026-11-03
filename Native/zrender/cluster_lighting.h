// =============================================================================
// cluster_lighting.h —— CPU 侧簇光源分配（TASK-B-R5-01）
//
// 视空间 3D 簇网格（16×9×16 = 2304 簇）+ 光源按簇裁剪（前向+）：
//   - LightData / PackedLight : 光源创作结构与 GPU 常量布局（float4×2 = 32B，
//     static_assert 锁定；kind 编码进 posRadius.w 符号——<0 即方向光）
//   - ClusterGrid             : 簇网格参数（默认 dim 16×9×16 / nearZ 0.3 /
//     farZ 60）+ 视空间（LH 相机 +Z 前）簇 AABB + 球-AABB 相交 + 像素→簇索引
//   - AssignLightsToClusters  : 每簇 start/count（uint32 打包 count<<16|start）
//     → 范围平坦数组（2304 项）+ 光索引平坦数组（start 指向此处）
//
// 与 GPU 侧的契约（两侧一致性由单测 + 验收行号对照保证）：
//   Native/shaders/pbr_forward.hlsl 的 PsMain 簇索引公式与
//   ClusterGrid::ClusterIndexFromUv 逐字一致（floor(u*dimX) / floor(v*dimY) /
//   floor((viewZ-nearZ)*invZRange*dimZ)，均 clamp 到 [0,dim-1]，索引序
//   (cz*dimY+cy)*dimX+cx）。
//
// 簇-光源相交近似（简报指定）：点光=球与簇"包围盒"（子视锥体段的 AABB）相交，
// 比精确视锥体裁剪保守（可能多纳入贴着 AABB 角落的光）——错误方向是"多算"，
// 渲染结果仍正确。方向光写全簇。
//
// 仅纯 CPU 逻辑：不依赖 zrhi/d3d12，可单测。命名遵循 file-conventions.md §3。
// =============================================================================
#pragma once

#include <cstdint>

// =============================================================================
// GPU 侧镜像常量（与 pbr_forward.hlsl 顶部 #define 一一对应；修改须两侧同步）
// =============================================================================
constexpr uint32_t ZR_MAX_LIGHTS = 32;     // CB1 光源数组上限（超限报错）
constexpr uint32_t ZR_CLUSTER_DIM_X = 16;  // 簇网格 X（屏幕列）
constexpr uint32_t ZR_CLUSTER_DIM_Y = 9;   // 簇网格 Y（屏幕行）
constexpr uint32_t ZR_CLUSTER_DIM_Z = 16;  // 簇网格 Z（视深切片，线性划分）
constexpr uint32_t ZR_CLUSTER_COUNT = ZR_CLUSTER_DIM_X * ZR_CLUSTER_DIM_Y * ZR_CLUSTER_DIM_Z; // 2304
// CB2 平坦光索引表容量：2304×4B（范围）+ 14000×4B（索引）= 65216B ≤ cbuffer
// 64KB 上限；Assign 超容量按返回码报错（简报"超限报错"约定）。GPU 侧该表按
// uint4 分装（3500×16B，字节布局=范围后紧跟连续 uint32），规避 FXC 动态索引
// 4096 向量上限——见 Native/shaders/pbr_forward.hlsl CBufClusters 注释。
constexpr uint32_t ZR_CLUSTER_FLAT_MAX = 14000;

/// 光源种类（LightData.kind；PackedLight 侧由 posRadius.w 符号承载）
enum ZrLightKind : uint32_t {
    kZrLightDirectional = 0, // 方向光：pos = 指向光源的方向（已归一化）
    kZrLightPoint = 1,       // 点光：pos = 世界/视空间位置，radius 生效
};

/// 光源创作结构（CPU 侧；打包见 PackLights）
struct LightData {
    float pos[3];     // 点光=位置；方向光=指向光源的方向（已归一化）
    float radius;     // 点光半径（米）；方向光忽略
    float color[3];   // 线性光色
    float intensity;  // 强度（线性倍率）
    uint32_t kind;    // ZrLightKind
};

/// GPU 常量布局（CB1 光源数组元素；float4×2 = 32B，static_assert 锁定）：
///   posRadius      xyz = 点光位置 / 方向光方向；w = 点光半径，方向光 = -1.0
///   colorIntensity rgb = 光色；a = 强度
struct PackedLight {
    float posRadius[4];
    float colorIntensity[4];
};
static_assert(sizeof(PackedLight) == 32, "PackedLight must be 32 bytes (float4 x2 GPU layout)");
static_assert(ZR_MAX_LIGHTS * sizeof(PackedLight) == 1024, "CB1 light array must be 1024 bytes");
static_assert(ZR_CLUSTER_COUNT == 2304, "cluster grid must be 2304 clusters");
static_assert(ZR_CLUSTER_COUNT * 4 + ZR_CLUSTER_FLAT_MAX * 4 <= 65536,
    "CB2 (ranges + flat light indices) must fit the 64KB cbuffer budget");

/// 打包 LightData → PackedLight（kind 编码：Point → posRadius.w=radius（≥0），
/// Directional → posRadius.w=-1.0）。in/out 均 ≥ count 项；count ≤ ZR_MAX_LIGHTS。
void PackLights(const LightData* in, uint32_t count, PackedLight* out);

/// PackedLight 是否方向光（posRadius.w < 0）
inline bool ZrIsDirectionalLight(const PackedLight& light)
{
    return light.posRadius[3] < 0.0f;
}

// =============================================================================
// ClusterGrid —— 簇网格参数 + 几何/索引助手（视空间：LH 相机，+Z 向前）
// =============================================================================
struct ClusterGrid {
    uint32_t dimX = ZR_CLUSTER_DIM_X;
    uint32_t dimY = ZR_CLUSTER_DIM_Y;
    uint32_t dimZ = ZR_CLUSTER_DIM_Z;
    float nearZ = 0.3f;  // 视深切片近端（线性划分下限）
    float farZ = 60.0f;  // 视深切片远端
    float tanHalfFovY = 0.57735026f; // tan(fovY/2)（视空间参数；默认 60° 视场）
    float aspect = 1.0f;             // 宽高比（x = ndcX * z * aspect * tanHalfFovY）

    /// 簇总数 = dimX×dimY×dimZ（32 位乘积，**不含**上限校验）。工作缓冲容量上限为
    /// ZR_CLUSTER_COUNT——AssignLightsToClusters 内部按 64 位乘积校验并返回 -3，
    /// 调用方若自行依此值分配缓冲须先判断是否 ≤ ZR_CLUSTER_COUNT。
    constexpr uint32_t ClusterCount() const { return dimX * dimY * dimZ; }

    /// 簇 (cx,cy,cz) 的视空间 AABB（子视锥体段 [z0,z1] 的包围盒）。
    /// cy 为像素空间行（自顶向下），NDC y 随之翻转。outMin/outMax 各 3 分量。
    void ClusterAabb(uint32_t cx, uint32_t cy, uint32_t cz, float outMin[3], float outMax[3]) const;

    /// 球-AABB 相交（最近点距离平方 ≤ r²；相切=相交）
    static bool SphereAabbIntersect(
        const float center[3], float radius, const float bmin[3], const float bmax[3]);

    /// 屏幕像素（整数坐标）+ 视深 → 簇索引。像素取中心采样（+0.5），
    /// 与 PS 的 SV_Position（已是 px+0.5）对齐。公式与 pbr_forward.hlsl 一致。
    uint32_t ClusterIndexFromPixel(
        uint32_t pixelX, uint32_t pixelY, float viewZ, uint32_t screenW, uint32_t screenH) const;

    /// 屏幕归一化坐标（u=(px+0.5)/W, v=(py+0.5)/H）+ 视深 → 簇索引。
    /// 核心公式（pbr_forward.hlsl PsMain 与此逐字一致，TASK-B-R5-01 要点 6）：
    ///   cx = clamp(floor(u*dimX), 0, dimX-1)
    ///   cy = clamp(floor(v*dimY), 0, dimY-1)
    ///   cz = clamp(floor((viewZ-nearZ)*invZRange*dimZ), 0, dimZ-1)
    ///   index = (cz*dimY + cy)*dimX + cx
    uint32_t ClusterIndexFromUv(float u, float v, float viewZ) const;

    /// start/count → uint32（count<<16 | start；count/start 各占 16bit）
    static constexpr uint32_t PackStartCount(uint32_t start, uint32_t count)
    {
        return (count << 16) | (start & 0xFFFFu);
    }
    static constexpr uint32_t UnpackStart(uint32_t packed) { return packed & 0xFFFFu; }
    static constexpr uint32_t UnpackCount(uint32_t packed) { return packed >> 16; }
};

/// 簇光源分配（前向+核心；两遍前缀和，输出确定序：方向光在前、点光按光源序）。
///
/// 参数：
///   grid        簇网格参数（dimX/dimY/dimZ 必须 ≥1 且乘积 ≤ ZR_CLUSTER_COUNT）
///   lights      光源数组（count ≤ ZR_MAX_LIGHTS，超限报错）
///   count       光源数
///   outRanges   [out] ClusterCount() 项，每簇 PackStartCount(start,count)
///   outFlat     [out] 平坦光索引表（start 指向此处）
///   flatCapacity outFlat 容量（应 ≥ ZR_CLUSTER_FLAT_MAX 以对齐 CB2 预算）
/// 返回：
///   ≥0 = 写入 outFlat 的条目数（平坦总数）；<0 = 参数/容量错误，且**不写**任何输出：
///        -1 空指针 / 光源数非法
///        -2 平坦索引表容量不足（flatCapacity）
///        -3 簇网格维度为 0 或 ClusterCount() > ZR_CLUSTER_COUNT（超出内部工作缓冲容量）
/// 复杂度：O(lightCount × clusterCount)（2304×32 上界，毫秒级）
int32_t AssignLightsToClusters(const ClusterGrid& grid, const LightData* lights, uint32_t count,
    uint32_t* outRanges, uint32_t* outFlat, uint32_t flatCapacity);
