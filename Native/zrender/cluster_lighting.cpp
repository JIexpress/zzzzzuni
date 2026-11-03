// =============================================================================
// cluster_lighting.cpp —— CPU 侧簇光源分配实现（TASK-B-R5-01）
// 契约与 GPU 侧对照说明见 cluster_lighting.h 头注释。
// =============================================================================
#include "zrender/cluster_lighting.h"

#include <cmath>
#include <cstring>

void PackLights(const LightData* in, uint32_t count, PackedLight* out)
{
    if (in == nullptr || out == nullptr) {
        return;
    }
    const uint32_t n = (count > ZR_MAX_LIGHTS) ? ZR_MAX_LIGHTS : count;
    for (uint32_t i = 0; i < n; ++i) {
        const LightData& src = in[i];
        PackedLight& dst = out[i];
        dst.posRadius[0] = src.pos[0];
        dst.posRadius[1] = src.pos[1];
        dst.posRadius[2] = src.pos[2];
        // kind 编码：方向光 → w = -1（哨兵）；点光 → w = radius（≥0）
        dst.posRadius[3] = (src.kind == kZrLightDirectional) ? -1.0f : src.radius;
        dst.colorIntensity[0] = src.color[0];
        dst.colorIntensity[1] = src.color[1];
        dst.colorIntensity[2] = src.color[2];
        dst.colorIntensity[3] = src.intensity;
    }
}

void ClusterGrid::ClusterAabb(
    uint32_t cx, uint32_t cy, uint32_t cz, float outMin[3], float outMax[3]) const
{
    // 视深切片（线性划分）：与 ClusterIndexFromUv 的 cz 划分一致
    const float zSpan = farZ - nearZ;
    const float z0 = nearZ + zSpan * static_cast<float>(cz) / static_cast<float>(dimZ);
    const float z1 = nearZ + zSpan * static_cast<float>(cz + 1u) / static_cast<float>(dimZ);

    // NDC 子矩形：x 向右、y 向上（D3D LH）；cy 是像素空间行（自顶向下）→ y 翻转
    const float ndcX0 = -1.0f + 2.0f * static_cast<float>(cx) / static_cast<float>(dimX);
    const float ndcX1 = -1.0f + 2.0f * static_cast<float>(cx + 1u) / static_cast<float>(dimX);
    const float v0 = static_cast<float>(cy) / static_cast<float>(dimY);        // 行上沿（像素空间）
    const float v1 = static_cast<float>(cy + 1u) / static_cast<float>(dimY);   // 行下沿
    const float ndcY0 = 1.0f - 2.0f * v1; // 底（NDC 较小者）
    const float ndcY1 = 1.0f - 2.0f * v0; // 顶

    // 视空间射线束：x = ndcX * z * (aspect*tanHalfFovY)，y = ndcY * z * tanHalfFovY
    const float scaleX = aspect * tanHalfFovY;
    const float scaleY = tanHalfFovY;

    // 子视锥体段的 AABB：对 (ndcX0,ndcX1)×(z0,z1) 线性乘积取 min/max
    const float x00 = ndcX0 * z0 * scaleX;
    const float x01 = ndcX0 * z1 * scaleX;
    const float x10 = ndcX1 * z0 * scaleX;
    const float x11 = ndcX1 * z1 * scaleX;
    const float y00 = ndcY0 * z0 * scaleY;
    const float y01 = ndcY0 * z1 * scaleY;
    const float y10 = ndcY1 * z0 * scaleY;
    const float y11 = ndcY1 * z1 * scaleY;

    outMin[0] = x00;
    outMax[0] = x00;
    const float xCands[3] = { x01, x10, x11 };
    for (int i = 0; i < 3; ++i) {
        outMin[0] = (xCands[i] < outMin[0]) ? xCands[i] : outMin[0];
        outMax[0] = (xCands[i] > outMax[0]) ? xCands[i] : outMax[0];
    }
    outMin[1] = y00;
    outMax[1] = y00;
    const float yCands[3] = { y01, y10, y11 };
    for (int i = 0; i < 3; ++i) {
        outMin[1] = (yCands[i] < outMin[1]) ? yCands[i] : outMin[1];
        outMax[1] = (yCands[i] > outMax[1]) ? yCands[i] : outMax[1];
    }
    outMin[2] = z0;
    outMax[2] = z1;
}

bool ClusterGrid::SphereAabbIntersect(
    const float center[3], float radius, const float bmin[3], const float bmax[3])
{
    // 最近点距离平方：逐轴 clamp(center, bmin, bmax) 后的偏移；相切（d²==r²）算相交
    float d2 = 0.0f;
    for (int i = 0; i < 3; ++i) {
        float d = center[i];
        if (d < bmin[i]) {
            d = bmin[i] - d;
        } else if (d > bmax[i]) {
            d = d - bmax[i];
        } else {
            d = 0.0f;
        }
        d2 += d * d;
    }
    return d2 <= radius * radius;
}

uint32_t ClusterGrid::ClusterIndexFromUv(float u, float v, float viewZ) const
{
    // !!! 与 Native/shaders/pbr_forward.hlsl PsMain 的簇索引公式逐字一致
    // !!! （TASK-B-R5-01 设计要点 6；修改须两侧同步）
    const float invZRange = 1.0f / (farZ - nearZ);
    float cx = floor(u * static_cast<float>(dimX));
    float cy = floor(v * static_cast<float>(dimY));
    float cz = floor((viewZ - nearZ) * invZRange * static_cast<float>(dimZ));
    if (cx < 0.0f) {
        cx = 0.0f;
    }
    if (cx > static_cast<float>(dimX - 1u)) {
        cx = static_cast<float>(dimX - 1u);
    }
    if (cy < 0.0f) {
        cy = 0.0f;
    }
    if (cy > static_cast<float>(dimY - 1u)) {
        cy = static_cast<float>(dimY - 1u);
    }
    if (cz < 0.0f) {
        cz = 0.0f;
    }
    if (cz > static_cast<float>(dimZ - 1u)) {
        cz = static_cast<float>(dimZ - 1u);
    }
    return (static_cast<uint32_t>(cz) * dimY + static_cast<uint32_t>(cy)) * dimX
        + static_cast<uint32_t>(cx);
}

uint32_t ClusterGrid::ClusterIndexFromPixel(
    uint32_t pixelX, uint32_t pixelY, float viewZ, uint32_t screenW, uint32_t screenH) const
{
    // 像素中心采样（+0.5）：与 PS 的 SV_Position.xy（像素中心）对齐
    const float u = (static_cast<float>(pixelX) + 0.5f) / static_cast<float>(screenW);
    const float v = (static_cast<float>(pixelY) + 0.5f) / static_cast<float>(screenH);
    return ClusterIndexFromUv(u, v, viewZ);
}

int32_t AssignLightsToClusters(const ClusterGrid& grid, const LightData* lights, uint32_t count,
    uint32_t* outRanges, uint32_t* outFlat, uint32_t flatCapacity)
{
    if (lights == nullptr || outRanges == nullptr || outFlat == nullptr) {
        return -1;
    }
    if (count == 0u || count > ZR_MAX_LIGHTS) {
        return -1; // 超上限报错（CB1 光源数组 32）
    }

    // 容量校验（TASK-H-02 D1）：下方 counts/starts/cursors 是 ZR_CLUSTER_COUNT 项
    // static 数组，clusterTotal 超过它就会越界写静态区。dimX/dimY/dimZ 为 public
    // 可写成员，必须在写数组之前按返回码拒绝（Release 下 assert 不生效）。
    // 用 64 位乘积判定：32 位乘积在大维度下会回绕（如 65536³ → 0）而绕过检查。
    if (grid.dimX == 0u || grid.dimY == 0u || grid.dimZ == 0u) {
        return -3; // 维度为 0：除零 / 空簇表
    }
    const uint64_t requestedClusters = static_cast<uint64_t>(grid.dimX)
        * static_cast<uint64_t>(grid.dimY) * static_cast<uint64_t>(grid.dimZ);
    if (requestedClusters > ZR_CLUSTER_COUNT) {
        return -3; // 簇网格超容量（工作缓冲上限 ZR_CLUSTER_COUNT = 2304）
    }
    const uint32_t clusterTotal = static_cast<uint32_t>(requestedClusters);

    PackedLight packed[ZR_MAX_LIGHTS];
    PackLights(lights, count, packed);

    // ---- Pass 1：逐簇计数（方向光记全簇；点光按球-AABB 相交）-----------------------
    // 工作缓冲走函数级 static（3×9216B，避免大栈占用）；zrender 约定单线程独占，
    // 不可重入（与 zrhi 同策）
    static uint32_t counts[ZR_CLUSTER_COUNT];
    static uint32_t starts[ZR_CLUSTER_COUNT];
    static uint32_t cursors[ZR_CLUSTER_COUNT];
    for (uint32_t c = 0; c < clusterTotal; ++c) {
        counts[c] = 0u;
    }
    float bmin[3];
    float bmax[3];
    bool hasDirectional = false;
    for (uint32_t l = 0; l < count; ++l) {
        if (ZrIsDirectionalLight(packed[l])) {
            hasDirectional = true;
            continue;
        }
        const float radius = packed[l].posRadius[3];
        for (uint32_t cz = 0; cz < grid.dimZ; ++cz) {
            // XY 面粗筛与逐簇精筛合一：直接遍历该深度切片全部簇
            for (uint32_t cy = 0; cy < grid.dimY; ++cy) {
                for (uint32_t cx = 0; cx < grid.dimX; ++cx) {
                    grid.ClusterAabb(cx, cy, cz, bmin, bmax);
                    if (ClusterGrid::SphereAabbIntersect(packed[l].posRadius, radius, bmin, bmax)) {
                        ++counts[(cz * grid.dimY + cy) * grid.dimX + cx];
                    }
                }
            }
        }
    }

    // ---- 容量预算：平坦总数（含方向光的每簇 1 项）不得超 CB2 索引表容量 -------------
    uint32_t total = hasDirectional ? clusterTotal : 0u;
    for (uint32_t c = 0; c < clusterTotal; ++c) {
        total += counts[c];
    }
    if (total > flatCapacity) {
        return -2; // 超 CB2 平坦索引表预算（见 ZR_CLUSTER_FLAT_MAX）
    }

    // ---- 前缀和：每簇起始偏移（方向光的每簇 1 项计入段长，且排在段首）----------------
    uint32_t running = 0u;
    for (uint32_t c = 0; c < clusterTotal; ++c) {
        starts[c] = running;
        cursors[c] = running;
        running += counts[c] + (hasDirectional ? 1u : 0u);
    }

    // ---- Pass 2：填充平坦索引表（方向光在前、点光按光源序；输出确定）----------------
    if (hasDirectional) {
        for (uint32_t l = 0; l < count; ++l) {
            if (!ZrIsDirectionalLight(packed[l])) {
                continue;
            }
            for (uint32_t c = 0; c < clusterTotal; ++c) {
                outFlat[cursors[c]++] = l;
            }
        }
    }
    for (uint32_t l = 0; l < count; ++l) {
        if (ZrIsDirectionalLight(packed[l])) {
            continue;
        }
        const float radius = packed[l].posRadius[3];
        for (uint32_t cz = 0; cz < grid.dimZ; ++cz) {
            for (uint32_t cy = 0; cy < grid.dimY; ++cy) {
                for (uint32_t cx = 0; cx < grid.dimX; ++cx) {
                    grid.ClusterAabb(cx, cy, cz, bmin, bmax);
                    if (ClusterGrid::SphereAabbIntersect(packed[l].posRadius, radius, bmin, bmax)) {
                        const uint32_t c = (cz * grid.dimY + cy) * grid.dimX + cx;
                        outFlat[cursors[c]++] = l;
                    }
                }
            }
        }
    }

    // ---- 打包 start/count（段长含方向光的每簇 1 项）---------------------------------
    for (uint32_t c = 0; c < clusterTotal; ++c) {
        outRanges[c] = ClusterGrid::PackStartCount(
            starts[c], counts[c] + (hasDirectional ? 1u : 0u));
    }
    return static_cast<int32_t>(total);
}
