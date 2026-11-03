// =============================================================================
// gi_probes.h —— 动态 GI 探针辐射缓存 CPU 侧纯逻辑（TASK-B-R5-03）
//
// 简化版辐射缓存（简报指定）：8×2×8 世界空间探针网格，CPU 逐帧（隔 4 帧轮转）
// 为每探针累加直接光贡献 → 探针辐照度 RGB 三元组（无遮蔽、无 SH/球谐）；
// PS 侧按世界位置三线性插值 8 邻域探针 → 替代 pbr 材质的常数环境项。
//
// 简化边界（与全量版的差距，施工报告诚实清单）：
//   - 探针辐照度 = 直接光球面平均（N·L 取常数 1），无遮蔽/无间接光弹射
//   - RGB 三元组而非 SH9/SH7——方向信息丢失，插值结果为"环境填充"而非方向 GI
//   - 每 4 帧轮转刷新（探针值滞后至多 4 帧），无时间平滑
//
// 与 GPU 侧的契约（两侧一致性由单测 + 验收行号对照保证）：
//   Native/shaders/pbr_r5.hlsl 的 CBufGi 布局与 GiCbufferLayout（static_assert
//   锁定）逐字节一致；SampleGi 的三线性公式与 GiProbeGrid::TrilinearWeights
//   一致（uv = (world-origin)/(spacing*dims)，cell = uv*dims-0.5，floor/floor+1
//   邻域、索引越界 clamp 到边缘、索引序 (cz*dimY+cy)*dimX+cx）。
//
// 仅纯 CPU 逻辑：不依赖 zrhi/d3d12。命名遵循 file-conventions.md §3。
// =============================================================================
#pragma once

#include <cstdint>

// =============================================================================
// GPU 侧镜像常量（与 pbr_r5.hlsl 顶部 #define 一一对应；修改须两侧同步）
// =============================================================================
constexpr uint32_t ZR_GI_PROBE_DIM_X = 8;
constexpr uint32_t ZR_GI_PROBE_DIM_Y = 2;
constexpr uint32_t ZR_GI_PROBE_DIM_Z = 8;
constexpr uint32_t ZR_GI_PROBE_COUNT = ZR_GI_PROBE_DIM_X * ZR_GI_PROBE_DIM_Y * ZR_GI_PROBE_DIM_Z; // 128

/// GI 探针 CB4 布局（与 pbr_r5.hlsl 的 CBufGi 一一对应；static_assert 锁定）：
///   gridOrigin  float4 = xyz=探针 (0,0,0) 世界位置；w=GI 开关（1=探针 / 0=常数环境）
///   gridSpacing float4 = xyz=探针单元间距（米）；w=探针数（128）
///   probes      float4[128] = rgb=辐照度（线性）；a=1（保留）
/// 合计 32 + 2048 = 2080B
struct GiCbufferLayout {
    float gridOrigin[4];
    float gridSpacing[4];
    float probes[ZR_GI_PROBE_COUNT][4];
};
static_assert(sizeof(GiCbufferLayout) == 2080, "GiCbufferLayout must be 2080 bytes");

// =============================================================================
// GiProbeGrid —— 探针网格参数 + 三线性权重（CPU 侧，PS 侧同式）
// =============================================================================
struct GiProbeGrid {
    float origin[3] = { -10.0f, -1.0f, 0.0f }; // 探针 (0,0,0) 世界位置
    float spacing[3] = { 2.5f, 2.0f, 2.5f };   // 单元间距（探针位于单元中心）

    static constexpr uint32_t dimX = ZR_GI_PROBE_DIM_X;
    static constexpr uint32_t dimY = ZR_GI_PROBE_DIM_Y;
    static constexpr uint32_t dimZ = ZR_GI_PROBE_DIM_Z;

    /// 世界位置 → 网格归一化坐标 uv ∈ [0,1]^3（越界不 clamp——三线性权重内部
    /// 对索引 clamp，uv 越界即边缘探针外推）。out[3]。
    void GridUv(const float worldPos[3], float out[3]) const;

    /// 三线性 8 邻域权重（PS 侧 SampleGi 同式）：
    ///   c = uv * dims - 0.5；base = floor(c)；frac = c - base（各轴独立）
    ///   邻域 = base 与 base+1 的 8 组合，索引逐轴 clamp 到 [0, dim-1]（边缘
    ///   clamp 后同索引重复出现——权重和恒为 1）
    /// outIndices/outWeights 各 8 项（outWeights 和 = 1，单测锁定）。
    void TrilinearWeights(const float worldPos[3], uint32_t outIndices[8],
        float outWeights[8]) const;

    /// 三线性采样探针辐照度数组（probes 为 [ZR_GI_PROBE_COUNT][3]）→ outRgb
    void SampleIrradiance(const float (*probes)[3], const float worldPos[3],
        float outRgb[3]) const;
};

// =============================================================================
// 探针辐照度累加（直接光球面平均，无遮蔽——简报指定简化）
// =============================================================================
/// 单探针位置 probePos 处的直接光辐照度累加（N·L 取球面平均 = 常数 1）：
///   方向光：E += color * intensity
///   点光：  d² = |probePos - pos|²；window = saturate(1 - d²/radius²)；
///           E += color * intensity * window² / (d² + 1)
///   （点光衰减式与 pbr_r5.hlsl 簇光源项逐字一致——修改须两侧同步）
/// probePos[3] = 探针世界位置；lights 光源数组（LightData 语义见
/// cluster_lighting.h）；outRgb 累加目标（调用方先清零可做"累加"语义）。
void AccumulateProbeIrradiance(const float probePos[3], const struct LightData* lights,
    uint32_t count, float outRgb[3]);

/// 每帧探针轮转刷新（简报"每 4 帧轮转"）：frameIndex % sliceCount 决定本帧
/// 刷新的探针子集（probeIndex % sliceCount == frameIndex % sliceCount）。
/// 4 帧为一个完整轮转周期（sliceCount=4 时），每探针至多滞后 4 帧。
/// outSlice 接收本帧探针索引，返回写入条数（sliceCount ≤ 0 或探针数为 0 → 0）。
uint32_t UpdateProbeSlice(uint32_t frameIndex, uint32_t sliceCount, uint32_t* outSlice,
    uint32_t outSliceCapacity);
