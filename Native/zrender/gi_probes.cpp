// =============================================================================
// gi_probes.cpp —— 动态 GI 探针辐射缓存 CPU 侧实现（TASK-B-R5-03）
//
// 全部纯逻辑：三线性权重/辐照度累加/轮转切片。公式与 pbr_r5.hlsl 的契约见
// gi_probes.h 头注释（修改须两侧同步）。
// =============================================================================
#include "zrender/gi_probes.h"

#include <cmath>

#include "zrender/cluster_lighting.h" // LightData / kZrLightPoint

namespace {

/// 线性插值
inline float Lerp(float a, float b, float t) { return a + (b - a) * t; }

} // namespace

// =============================================================================
// GiProbeGrid
// =============================================================================
void GiProbeGrid::GridUv(const float worldPos[3], float out[3]) const
{
    for (int i = 0; i < 3; ++i) {
        const float span = spacing[i] * static_cast<float>((i == 0) ? dimX : (i == 1) ? dimY : dimZ);
        out[i] = (worldPos[i] - origin[i]) / span;
    }
}

void GiProbeGrid::TrilinearWeights(const float worldPos[3], uint32_t outIndices[8],
    float outWeights[8]) const
{
    float uv[3];
    GridUv(worldPos, uv);

    constexpr uint32_t dims[3] = { dimX, dimY, dimZ };
    int base[3];
    float frac[3];
    for (int axis = 0; axis < 3; ++axis) {
        const float c = uv[axis] * static_cast<float>(dims[axis]) - 0.5f;
        base[axis] = static_cast<int>(std::floor(c));
        frac[axis] = c - static_cast<float>(base[axis]);
        if (frac[axis] < 0.0f) {
            frac[axis] = 0.0f; // floor 数值噪声防御（c 恰为整数时 frac=0）
        }
    }

    // 8 邻域：off ∈ {0,1}³，索引逐轴 clamp（边缘外重复索引——权重和仍为 1）
    for (uint32_t corner = 0; corner < 8u; ++corner) {
        const int offX = (corner & 1u) != 0u ? 1 : 0;
        const int offY = (corner & 2u) != 0u ? 1 : 0;
        const int offZ = (corner & 4u) != 0u ? 1 : 0;
        const int cx = base[0] + offX;
        const int cy = base[1] + offY;
        const int cz = base[2] + offZ;
        const int clampedX = (cx < 0) ? 0 : (cx >= static_cast<int>(dimX)) ? static_cast<int>(dimX) - 1 : cx;
        const int clampedY = (cy < 0) ? 0 : (cy >= static_cast<int>(dimY)) ? static_cast<int>(dimY) - 1 : cy;
        const int clampedZ = (cz < 0) ? 0 : (cz >= static_cast<int>(dimZ)) ? static_cast<int>(dimZ) - 1 : cz;
        outIndices[corner] = static_cast<uint32_t>(
            (clampedZ * static_cast<int>(dimY) + clampedY) * static_cast<int>(dimX) + clampedX);
        const float wx = (offX != 0) ? frac[0] : 1.0f - frac[0];
        const float wy = (offY != 0) ? frac[1] : 1.0f - frac[1];
        const float wz = (offZ != 0) ? frac[2] : 1.0f - frac[2];
        outWeights[corner] = wx * wy * wz;
    }
}

void GiProbeGrid::SampleIrradiance(const float (*probes)[3], const float worldPos[3],
    float outRgb[3]) const
{
    uint32_t indices[8];
    float weights[8];
    TrilinearWeights(worldPos, indices, weights);
    outRgb[0] = 0.0f;
    outRgb[1] = 0.0f;
    outRgb[2] = 0.0f;
    for (uint32_t corner = 0; corner < 8u; ++corner) {
        for (int ch = 0; ch < 3; ++ch) {
            outRgb[ch] += weights[corner] * probes[indices[corner]][ch];
        }
    }
}

// =============================================================================
// 辐照度累加（直接光球面平均；点光衰减式与 pbr_r5.hlsl 簇光源项一致）
// =============================================================================
void AccumulateProbeIrradiance(const float probePos[3], const LightData* lights,
    uint32_t count, float outRgb[3])
{
    for (uint32_t i = 0; i < count; ++i) {
        const LightData& light = lights[i];
        if (light.kind == kZrLightDirectional) {
            // N·L 球面平均 = 常数 1：方向光对探针的辐照度 = color * intensity
            for (int ch = 0; ch < 3; ++ch) {
                outRgb[ch] += light.color[ch] * light.intensity;
            }
        } else {
            float diff[3] = { light.pos[0] - probePos[0], light.pos[1] - probePos[1],
                light.pos[2] - probePos[2] };
            const float dist2 = diff[0] * diff[0] + diff[1] * diff[1] + diff[2] * diff[2];
            // window = saturate(1 - d²/r²)；atten = window² / (d² + 1)（与 GPU 一致）
            float window = 1.0f - dist2 / (light.radius * light.radius);
            if (window < 0.0f) {
                window = 0.0f;
            }
            const float attenuation = window * window / (dist2 + 1.0f);
            for (int ch = 0; ch < 3; ++ch) {
                outRgb[ch] += light.color[ch] * light.intensity * attenuation;
            }
        }
    }
}

uint32_t UpdateProbeSlice(uint32_t frameIndex, uint32_t sliceCount, uint32_t* outSlice,
    uint32_t outSliceCapacity)
{
    if (sliceCount == 0u || ZR_GI_PROBE_COUNT == 0u || outSlice == nullptr) {
        return 0u;
    }
    const uint32_t slice = frameIndex % sliceCount;
    uint32_t written = 0u;
    for (uint32_t probe = 0; probe < ZR_GI_PROBE_COUNT; ++probe) {
        if (probe % sliceCount == slice) {
            if (written < outSliceCapacity) {
                outSlice[written] = probe;
            }
            ++written;
        }
    }
    return written; // 注意：written 为逻辑条数；超出 capacity 的未写入（防御）
}
