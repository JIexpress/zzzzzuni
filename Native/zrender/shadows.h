// =============================================================================
// shadows.h —— 方向光 CSM 级联阴影 CPU 侧纯逻辑（TASK-B-R5-02）
//
// 三件套（全部纯 CPU、可单测；真 GPU 绘制在窗口程序 --shadows 模式）：
//   - CascadeSplits      : 对数+均匀插值（lambda 混合）的级联分割纯函数
//   - BuildCascades      : 每级联光视图（texel snap 防闪烁）+ 正交投影（D3D LH，
//                          clip z ∈ [0,1]）→ row_major lightVP
//   - 阴影 draw 项过滤   : 复用 draw_list.h 的共享可见性（RenderItem::castShadow
//                          标志 + 视图掩码位）——按视图掩码过滤同一份
//                          VisibilitySet，不做第二次剔除（简报要求）
//
// 矩阵约定（与 Native/zrender/draw_list.h / 窗口程序 first_draw 一致）：
//   row_major 存储 + 列向量数学：clip = P·V·world（P/V 均 4x4 row_major）。
//   深度映射 D3D LH：clip z ∈ [0,1]（近 0 远 1），与 ZrD3D12 深度缓冲一致。
//
// 与 GPU 侧的契约（两侧一致性由单测 + 验收行号对照保证）：
//   Native/shaders/pbr_shadow.hlsl 的 CBufShadows 布局与 ShadowCbufferLayout
//   （static_assert 锁定）一一对应；PS 侧级联选择按视深与 CascadeSplits 边界
//   语义一致（viewZ ≤ 边界 i 即级联 i，边界末值为 farZ）。
//
// 仅纯 CPU 逻辑：不依赖 zrhi/d3d12，不动 zkernel。命名遵循 file-conventions.md §3。
// =============================================================================
#pragma once

#include <cstdint>

// =============================================================================
// GPU 侧镜像常量（与 Native/shaders/pbr_shadow.hlsl 顶部 #define 一一对应）
// =============================================================================
constexpr uint32_t ZR_SHADOW_CASCADES = 4;   // 级联数（CB3 数组上限）
constexpr uint32_t ZR_SHADOW_TEX_SIZE = 2048; // 每级联阴影图边长（2048²）

/// 阴影常量 CB3 布局（与 pbr_shadow.hlsl 的 CBufShadows 一一对应；static_assert
/// 锁定字节数）：
///   lightVP[4]     4×row_major float4x4 = 256B（每级联光视投影矩阵）
///   cascadeBias[4] 每级联 float4 = 64B（x=depthBias y=slopeBias z/w 预留）
///   cascadeSplits  float4 = 16B（xyz=级联 0..2 远边界，w=farZ=级联 3 远边界；
///                  PS 侧级联选择按视深与该边界语义一致：viewZ ≤ 边界 i 即级联 i）
/// 合计 336B
struct ShadowCbufferLayout {
    float lightVP[ZR_SHADOW_CASCADES][16];    // row_major；clip = lightVP * world
    float cascadeBias[ZR_SHADOW_CASCADES][4]; // x=depthBias y=slopeBias（深度域）
    float cascadeSplits[4];
};
static_assert(sizeof(ShadowCbufferLayout) == 336, "ShadowCbufferLayout must be 336 bytes");

// =============================================================================
// CascadeSplits —— 对数+均匀混合分割（纯函数）
// =============================================================================
/// 计算 count 级联的 (count-1) 个内部分割距离（沿相机前向的视深）。
///   uniform_i = near + (far-near) * i/count   （均匀：近处分辨率浪费）
///   log_i     = near * (far/near)^(i/count)   （对数：近处过密远处过疏）
///   split_i   = lerp(uniform_i, log_i, lambda)  （lambda=0 均匀 / 1 纯对数）
/// 输出 outSplits[0..count-2] 单调递增；outSplits[0] ≥ near；
/// outSplits[count-2] ≤ far。级联 i 的视深区间：[prev, outSplits[i]]
/// （prev = 上一分割或 near；末级联上界 = farZ）。
/// count < 2 或 lambda ∉ [0,1] 时拒绝（返回 false，不写 outSplits）。
bool CascadeSplits(float nearZ, float farZ, uint32_t count, float lambda, float* outSplits);

// =============================================================================
// BuildCascades —— 每级联 lightVP（texel snap + 正交包裹）
// =============================================================================
/// 单级联产物
struct ShadowCascade {
    float lightVP[16]; // row_major：clip = lightVP * worldPos（D3D LH，z ∈ [0,1]）
    float splitNear;   // 本级联相机视深下界（沿相机前向）
    float splitFar;    // 本级联相机视深上界（= CascadeSplits 边界或 farZ）
    float zRange;      // 光空间正交深度范围（maxZ-minZ；供 bias 归一化）
};

/// 场景/相机参数（构建输入；矩阵 row_major 列向量约定）
struct ShadowSceneParams {
    float camView[16];    // row_major 相机视图矩阵（world→view；刚体变换假设）
    float tanHalfFovY;    // tan(fovY/2)（与窗口程序投影参数一致）
    float aspect;         // 宽高比（x = ndcX * z * aspect * tanHalfFovY）
    float nearZ;          // 相机近远平面（级联覆盖范围）
    float farZ;
    float lightDir[3];    // 指向光源的方向（已归一化；与 LightData 方向光语义一致）
    float sceneCenter[3]; // 场景包围球心（世界）
    float sceneRadius;    // 场景包围球半径（光空间深度方向的 caster 余量）
    float lambda;         // 分割混合系数（0=均匀 1=纯对数；简报指定 0.75）
    uint32_t texSize;     // 每级联阴影图边长（texel snap 网格 = 视锥宽/texSize）
};

/// 构建 count 级联（≤ ZR_SHADOW_CASCADES）的 lightVP。
/// 流程（每级联）：
///   1. 相机视锥切片 [split_i, split_i+1] 的 8 角（世界，经逆 camView 反投影）
///   2. 变换到光视图空间取 AABB；min/max 按 texel 网格向下/上取整（texel snap——
///      光视图内平移量小于一个 texel 时矩阵逐位不变，防边缘闪烁）
///   3. 深度范围按场景包围球外扩（slice 前后的 caster 均入图）
///   4. 正交投影（D3D LH：x/y ∈ [-1,1]、z ∈ [0,1]）· 光视图 = lightVP
/// 参数非法（count 超限/非归一化方向/半径 ≤ 0）返回 false。
bool BuildCascades(const ShadowSceneParams& params, ShadowCascade* out, uint32_t count);

// =============================================================================
// 阴影 draw 项过滤 —— 复用 draw_list.h 共享可见性（不重复剔除）
// =============================================================================
/// 视图 id 约定：0 = 主视图；1..4 = 级联 0..3 的阴影视图（kMaxViews=8 内分配）。
/// RenderItem::castShadow=false 的项录入可见性时只给主视图位——共享同一
/// VisibilitySet 按位过滤即得阴影 draw list，主视图与阴影视图零二次剔除。
constexpr uint32_t kShadowViewBase = 1u;

/// 级联 cascadeIndex 的阴影视图 id（kMaxShadowCascades 内合法）
constexpr uint32_t ShadowViewId(uint32_t cascadeIndex)
{
    return kShadowViewBase + cascadeIndex;
}

/// 级联数上限（视图掩码 uint8 留 4 位给阴影视图：bit1..bit4 = 0x1E）
constexpr uint32_t kMaxShadowCascades = ZR_SHADOW_CASCADES;

/// 一次 DrawList::Add 用的视图掩码：主视图恒可见；castShadow 追加级联位
inline uint8_t ShadowViewMask(bool castShadow)
{
    // bit0=主视图；bit1..4=级联 0..3（ShadowViewId(0..3)）
    constexpr uint8_t kShadowBits = 0x1Eu;
    return castShadow ? static_cast<uint8_t>(0x01u | kShadowBits) : 0x01u;
}
