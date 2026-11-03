// =============================================================================
// shadows.cpp —— CSM 级联阴影 CPU 侧实现（TASK-B-R5-02）
//
// 分割：对数+均匀 lambda 混合（见 shadows.h CascadeSplits 注）。
// 级联矩阵：光视图（D3D LH LookAt 语义）→ 视锥切片角 AABB → texel snap →
// 正交投影（clip z ∈ [0,1]）。texel snap 把 AABB min/max 吸附到光空间
// texel 网格——相机/场景在光视图内平移不足一个 texel 时，矩阵逐位不变
// （防级联边缘阴影闪烁；单测以"微小平移矩阵不变"证伪）。
//
// 可见性共享：本文件不维护独立阴影剔除——过滤仅按 draw_list.h 的视图掩码
// 位（ShadowViewMask）进行，主视图与阴影视图共享同一份 VisibilitySet。
// =============================================================================
#include "zrender/shadows.h"

#include <cmath>
#include <cstring>

namespace {

// ---- row_major 4x4 助手（列向量数学：out = A · v / out = A · B）---------------

void MatMultiply(const float* a, const float* b, float* out)
{
    float r[16];
    for (int row = 0; row < 4; ++row) {
        for (int col = 0; col < 4; ++col) {
            float sum = 0.0f;
            for (int k = 0; k < 4; ++k) {
                sum += a[row * 4 + k] * b[k * 4 + col];
            }
            r[row * 4 + col] = sum;
        }
    }
    std::memcpy(out, r, sizeof(r));
}

/// 变换齐次点（列向量）：out.xyz = M * (x,y,z,1)（除 w 由调用方处理）
void MatTransformPoint(const float* m, float x, float y, float z, float out[3])
{
    out[0] = m[0] * x + m[1] * y + m[2] * z + m[3];
    out[1] = m[4] * x + m[5] * y + m[6] * z + m[7];
    out[2] = m[8] * x + m[9] * y + m[10] * z + m[11];
}

/// 通用 4x4 求逆（伴随矩阵法；调用方保证可逆——相机视图矩阵为刚体变换）
bool MatrixInverse4x4(const float* m, float* out)
{
    float inv[16];
    inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15]
        + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
    inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15]
        - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
    inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15]
        + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
    inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14]
        - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
    inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15]
        - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
    inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15]
        + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
    inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15]
        - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
    inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14]
        + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
    inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15]
        + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
    inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15]
        - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
    inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15]
        + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
    inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14]
        - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
    inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11]
        - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
    inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11]
        + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
    inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11]
        - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
    inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10]
        + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];

    float det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
    if (det == 0.0f) {
        return false;
    }
    const float invDet = 1.0f / det;
    for (int i = 0; i < 16; ++i) {
        out[i] = inv[i] * invDet;
    }
    return true;
}

} // namespace

// ---------------------------------------------------------------------------
// CascadeSplits —— 对数+均匀 lambda 混合（纯函数，见 shadows.h 注）
// ---------------------------------------------------------------------------
bool CascadeSplits(float nearZ, float farZ, uint32_t count, float lambda, float* outSplits)
{
    if (count < 2u || outSplits == nullptr) {
        return false;
    }
    if (!(lambda >= 0.0f && lambda <= 1.0f)) {
        return false; // NaN/越界 lambda 拒绝（NaN 比较恒 false 即命中）
    }
    if (!(nearZ > 0.0f) || !(farZ > nearZ)) {
        return false; // 近远平面非法（含 NaN/负值）
    }

    const float ratio = farZ / nearZ;
    for (uint32_t i = 1; i < count; ++i) {
        const float t = static_cast<float>(i) / static_cast<float>(count);
        const float uniform = nearZ + (farZ - nearZ) * t;
        const float logarithmic = nearZ * std::pow(ratio, t);
        outSplits[i - 1] = uniform + (logarithmic - uniform) * lambda;
    }
    return true;
}

// ---------------------------------------------------------------------------
// BuildCascades —— 光视图 + texel snap + 正交投影（见 shadows.h 注）
// ---------------------------------------------------------------------------
bool BuildCascades(const ShadowSceneParams& params, ShadowCascade* out, uint32_t count)
{
    if (out == nullptr || count == 0u || count > kMaxShadowCascades) {
        return false;
    }
    // 方向光须已归一化（与 LightData 方向光契约一致）；半径须为正
    const float dirLen = std::sqrt(params.lightDir[0] * params.lightDir[0]
        + params.lightDir[1] * params.lightDir[1] + params.lightDir[2] * params.lightDir[2]);
    if (std::fabs(dirLen - 1.0f) > 1e-3f) {
        return false;
    }
    if (!(params.sceneRadius > 0.0f) || !(params.texSize > 0u) || !(params.tanHalfFovY > 0.0f)
        || !(params.aspect > 0.0f) || !(params.nearZ > 0.0f) || !(params.farZ > params.nearZ)) {
        return false;
    }

    // 内部分割（count-1 个）+ 级联边界表 [near, s1, s2, ..., far]
    float splits[ZR_SHADOW_CASCADES - 1];
    if (!CascadeSplits(params.nearZ, params.farZ, count, params.lambda, splits)) {
        return false;
    }

    // 逆相机视图（世界反投影用；刚体变换必然可逆）
    float invView[16];
    if (!MatrixInverse4x4(params.camView, invView)) {
        return false;
    }

    // ---- 光视图（D3D LH LookAt 语义；方向光：视线 d = -lightDir）----------------
    const float d[3] = { -params.lightDir[0], -params.lightDir[1], -params.lightDir[2] };
    // 光空间原点：从场景球心沿"指向光源"方向后撤（视线 d 正向覆盖场景；
    // 正交投影下眼点位置不影响 XY 覆盖，仅定深度基准）
    const float backoff = params.sceneRadius * 2.0f + 10.0f;
    const float eye[3] = { params.sceneCenter[0] + params.lightDir[0] * backoff,
        params.sceneCenter[1] + params.lightDir[1] * backoff,
        params.sceneCenter[2] + params.lightDir[2] * backoff };

    float zaxis[3] = { d[0], d[1], d[2] };
    // 世界 up 叉乘退化（光向 ≈ ±Y）时退备 (0,0,1)
    float up[3] = { 0.0f, 1.0f, 0.0f };
    float xaxis[3] = { up[1] * zaxis[2] - up[2] * zaxis[1],
        up[2] * zaxis[0] - up[0] * zaxis[2], up[0] * zaxis[1] - up[1] * zaxis[0] };
    float xLen = std::sqrt(xaxis[0] * xaxis[0] + xaxis[1] * xaxis[1] + xaxis[2] * xaxis[2]);
    if (xLen < 1e-4f) {
        up[0] = 0.0f;
        up[1] = 0.0f;
        up[2] = 1.0f;
        xaxis[0] = up[1] * zaxis[2] - up[2] * zaxis[1];
        xaxis[1] = up[2] * zaxis[0] - up[0] * zaxis[2];
        xaxis[2] = up[0] * zaxis[1] - up[1] * zaxis[0];
        xLen = std::sqrt(xaxis[0] * xaxis[0] + xaxis[1] * xaxis[1] + xaxis[2] * xaxis[2]);
    }
    const float invXLen = 1.0f / xLen;
    xaxis[0] *= invXLen;
    xaxis[1] *= invXLen;
    xaxis[2] *= invXLen;
    const float yaxis[3] = { zaxis[1] * xaxis[2] - zaxis[2] * xaxis[1],
        zaxis[2] * xaxis[0] - zaxis[0] * xaxis[2],
        zaxis[0] * xaxis[1] - zaxis[1] * xaxis[0] };

    float lightView[16] = {};
    lightView[0] = xaxis[0];
    lightView[1] = xaxis[1];
    lightView[2] = xaxis[2];
    lightView[3] = -(xaxis[0] * eye[0] + xaxis[1] * eye[1] + xaxis[2] * eye[2]);
    lightView[4] = yaxis[0];
    lightView[5] = yaxis[1];
    lightView[6] = yaxis[2];
    lightView[7] = -(yaxis[0] * eye[0] + yaxis[1] * eye[1] + yaxis[2] * eye[2]);
    lightView[8] = zaxis[0];
    lightView[9] = zaxis[1];
    lightView[10] = zaxis[2];
    lightView[11] = -(zaxis[0] * eye[0] + zaxis[1] * eye[1] + zaxis[2] * eye[2]);
    lightView[12] = 0.0f;
    lightView[13] = 0.0f;
    lightView[14] = 0.0f;
    lightView[15] = 1.0f;

    for (uint32_t c = 0; c < count; ++c) {
        const float sliceNear = (c == 0u) ? params.nearZ : splits[c - 1];
        const float sliceFar = (c + 1u == count) ? params.farZ : splits[c];

        // ---- 1) 相机视锥切片 8 角（世界）-------------------------------------
        const float ndc[4][2] = { { -1.0f, -1.0f }, { 1.0f, -1.0f }, { 1.0f, 1.0f },
            { -1.0f, 1.0f } };
        float minLight[3] = { 0.0f, 0.0f, 0.0f };
        float maxLight[3] = { 0.0f, 0.0f, 0.0f };
        bool first = true;
        for (uint32_t corner = 0; corner < 4u; ++corner) {
            for (uint32_t depthEnd = 0; depthEnd < 2u; ++depthEnd) {
                const float z = (depthEnd == 0u) ? sliceNear : sliceFar;
                const float vx = ndc[corner][0] * z * params.aspect * params.tanHalfFovY;
                const float vy = ndc[corner][1] * z * params.tanHalfFovY;
                float world[3];
                MatTransformPoint(invView, vx, vy, z, world);
                float lv[3];
                MatTransformPoint(lightView, world[0], world[1], world[2], lv);
                if (first) {
                    minLight[0] = maxLight[0] = lv[0];
                    minLight[1] = maxLight[1] = lv[1];
                    minLight[2] = maxLight[2] = lv[2];
                    first = false;
                } else {
                    for (int k = 0; k < 3; ++k) {
                        minLight[k] = (lv[k] < minLight[k]) ? lv[k] : minLight[k];
                        maxLight[k] = (lv[k] > maxLight[k]) ? lv[k] : maxLight[k];
                    }
                }
            }
        }

        // ---- 2) texel snap：AABB min 向下 / max 向上吸附 texel 网格-------------
        //（光视图内不足一个 texel 的平移不再改变矩阵边界 → 阴影边缘稳定）
        const float texelX = (maxLight[0] - minLight[0]) / static_cast<float>(params.texSize);
        const float texelY = (maxLight[1] - minLight[1]) / static_cast<float>(params.texSize);
        if (texelX <= 0.0f || texelY <= 0.0f) {
            return false; // 退化切片（理论上不可达，防御）
        }
        minLight[0] = std::floor(minLight[0] / texelX) * texelX;
        minLight[1] = std::floor(minLight[1] / texelY) * texelY;
        maxLight[0] = std::ceil(maxLight[0] / texelX) * texelX;
        maxLight[1] = std::ceil(maxLight[1] / texelY) * texelY;

        // ---- 3) 深度范围按场景包围球外扩（slice 前后的 caster 均入图）----------
        minLight[2] -= params.sceneRadius;
        maxLight[2] += params.sceneRadius;
        const float zRange = maxLight[2] - minLight[2];
        if (zRange <= 0.0f) {
            return false;
        }

        // ---- 4) 正交投影（D3D LH：x/y ∈ [-1,1]、z ∈ [0,1]）· 光视图 -------------
        const float w = maxLight[0] - minLight[0];
        const float h = maxLight[1] - minLight[1];
        float ortho[16] = {};
        ortho[0] = 2.0f / w;
        ortho[3] = -(maxLight[0] + minLight[0]) / w;
        ortho[5] = 2.0f / h;
        ortho[7] = -(maxLight[1] + minLight[1]) / h;
        ortho[10] = 1.0f / zRange;
        ortho[11] = -minLight[2] / zRange;
        ortho[15] = 1.0f;

        ShadowCascade& cascade = out[c];
        MatMultiply(ortho, lightView, cascade.lightVP);
        cascade.splitNear = sliceNear;
        cascade.splitFar = sliceFar;
        cascade.zRange = zRange;
    }
    return true;
}
