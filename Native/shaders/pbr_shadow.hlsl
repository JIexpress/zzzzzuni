// =============================================================================
// pbr_shadow.hlsl —— PBR 前向 + 簇光源 + CSM 级联阴影（TASK-B-R5-02）
//
// 在 pbr_forward.hlsl（TASK-B-R5-01，保持不动作回归基线）基础上扩展：
//   - b0 追加 cascadeSplits（级联远边界）/ shadowParams（x=阴影强度 0/1、
//     y=阴影图边长）——窗口程序 --shadows 模式的 ShadowDrawConstants 镜像
//   - b3 CBufShadows：4×row_major lightVP + 每级联 depthBias/slopeBias
//     （与 Native/zrender/shadows.h ShadowCbufferLayout 逐字节一致，336B）
//   - t0 Texture2DArray：级联阴影图（4 层，D32Float 经 R32Float SRV 解释；
//     层 i = 级联 i）；s0：静态 Point/Clamp 采样器（根签名 srvTable）
//   - 阴影：像素→级联选择（按视深，与 CPU CascadeSplits 边界语义一致）→
//     光视空间投影 → PCF 3x3（9 点采样手动深度比较）→ shadow factor 乘入
//     方向光项（点光项不接收 CSM 阴影——逐光阴影图归后续任务）
//
// 根签名（与 zrhi_d3d12_device.cpp CreateFirstDrawRootSignature 的
// rootCbvCount=4 + srvTable 布局一致）：
//   b0 CBufPerDraw（224B，窗口程序 ShadowDrawConstants 镜像）
//   b1 CBufLights（1024B，同 pbr_forward.hlsl）
//   b2 CBufClusters（65216B，同 pbr_forward.hlsl）
//   b3 CBufShadows（336B，ShadowCbufferLayout 镜像）
//   t0+表 / s0（srvTable 根参数 = 槽 4）
//
// 簇索引两侧一致性：与 pbr_forward.hlsl 相同（CLUSTER-INDEX 对照段逐字保留）。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
// =============================================================================

// ---- GPU 侧镜像常量（与 Native/zrender/cluster_lighting.h 一一对应）----------
#define ZR_MAX_LIGHTS 32
#define ZR_CLUSTER_DIM_X 16
#define ZR_CLUSTER_DIM_Y 9
#define ZR_CLUSTER_DIM_Z 16
#define ZR_CLUSTER_COUNT 2304
#define ZR_CLUSTER_FLAT_MAX 14000      // 平坦光索引表容量（uint 条目数）
#define ZR_CLUSTER_RANGE_VEC_MAX 576   // 簇范围表按 uint4 分装的向量数（2304/4）
#define ZR_CLUSTER_FLAT_VEC_MAX 3500   // 平坦光索引表按 uint4 分装的向量数（14000/4）

cbuffer CBufPerDraw : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 model;
    float4 projParams;   // x=projA=zf/(zf-zn) y=projB=zn*zf/(zf-zn) z=nearZ w=farZ
    float4 gridParams;   // x/y/z=dimX/Y/Z w=invZRange
    float4 screenParams; // x=width y=height z=lightCount w=ambient
    float4 material;     // rgb=albedo a=roughness
    float4 camPos;       // xyz=相机世界位置
    float4 cascadeSplits; // xyz=级联 0..2 远边界（视深），w=farZ=级联 3 远边界
    float4 shadowParams;  // x=阴影强度（0=关 factor 恒 1 / 1=开）y=阴影图边长
};

struct GpuLight
{
    float4 posRadius;      // xyz=点光位置/方向光方向；w=点光半径（方向光=-1 哨兵）
    float4 colorIntensity; // rgb=光色；a=强度
};

cbuffer CBufLights : register(b1)
{
    GpuLight lights[ZR_MAX_LIGHTS];
};

cbuffer CBufClusters : register(b2)
{
    uint4 clusterRanges[ZR_CLUSTER_RANGE_VEC_MAX];
    uint4 clusterLightIndices[ZR_CLUSTER_FLAT_VEC_MAX];
};

// 级联阴影常量（与 Native/zrender/shadows.h ShadowCbufferLayout 逐字节一致，336B）
cbuffer CBufShadows : register(b3)
{
    row_major float4x4 shadowVP[4]; // 每级联光视投影矩阵（row_major）
    float4 cascadeBias[4];          // x=depthBias y=slopeBias（深度域，每级联）
    float4 shadowSplits;            // 与 b0.cascadeSplits 同值（采样侧就近）
};

Texture2DArray shadowMap : register(t0); // 层 i = 级联 i（D32 → R32Float SRV）
SamplerState shadowSampler : register(s0); // Point/Clamp（根签名静态采样器）

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float3 normal : NORMAL;
    float3 worldPos : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = mul(mvp, float4(input.pos, 1.0f));
    float4 world = mul(model, float4(input.pos, 1.0f));
    output.worldPos = world.xyz;
    output.normal = mul((float3x3) model, input.normal);
    return output;
}

// =============================================================================
// PBR：GGX NDF + Smith 可见性（Schlick-GGX 近似）+ Lambert + Fresnel-Schlick
// （与 pbr_forward.hlsl 逐字一致）
// =============================================================================

static const float PI = 3.14159265f;
static const float3 kF0Dielectric = float3(0.04f, 0.04f, 0.04f);

float3 ShadePbr(float3 n, float3 v, float3 l, float3 radiance, float3 albedo, float roughness)
{
    float ndl = saturate(dot(n, l));
    if (ndl <= 0.0f) {
        return float3(0.0f, 0.0f, 0.0f);
    }
    float3 h = normalize(v + l);
    float ndv = saturate(dot(n, v));
    float ndh = saturate(dot(n, h));
    float vdh = saturate(dot(v, h));

    float a = max(roughness * roughness, 1e-3f);
    float a2 = a * a;
    float dd = ndh * ndh * (a2 - 1.0f) + 1.0f;
    float D = a2 / (PI * dd * dd);
    float k = max(roughness * 0.5f, 1e-3f);
    float gv = ndv / (ndv * (1.0f - k) + k);
    float gl = ndl / (ndl * (1.0f - k) + k);
    float Vis = (gv * gl) / max(4.0f * ndv * ndl, 1e-4f);
    float3 F = kF0Dielectric + (1.0f - kF0Dielectric) * pow(1.0f - vdh, 5.0f);

    float3 specular = D * Vis * F;
    float3 diffuse = albedo / PI;
    return (diffuse + specular) * radiance * ndl;
}

// =============================================================================
// CSM：级联选择（按视深）+ PCF 3x3
// =============================================================================

/// 级联选择：视深反解（D3D LH 透视 z = B/(A-ndcZ)，与 CLUSTER-INDEX 同式）后
/// 取第一个满足 viewZ ≤ 边界 的级联（边界 = cascadeSplits.xyzw，w=farZ）。
/// 与 CPU 侧 CascadeSplits/BuildCascades 的级联覆盖语义一致（修改须两侧同步）。
uint SelectCascade(float viewZ)
{
    uint cascade = 3u;
    [unroll] for (uint i = 0u; i < 4u; ++i) {
        if (viewZ <= shadowSplits[i]) {
            cascade = i;
            break;
        }
    }
    return cascade;
}

/// PCF 3x3：光视空间投影 → 3×3 texel 邻域采样，逐点与接收深度比较（含
/// depthBias + slopeBias·slope），受影比例 = 通过点数 / 9。
float ShadowFactorPcf(float3 worldPos, float viewZ, float ndl)
{
    if (shadowParams.x < 0.5f) {
        return 1.0f; // --shadows off：factor 恒 1（可证伪对比的关闭侧）
    }
    const uint cascade = SelectCascade(viewZ);

    float4 pl = mul(shadowVP[cascade], float4(worldPos, 1.0f));
    float3 proj = pl.xyz / pl.w;
    // LH 视口：NDC y=+1 为屏幕顶端 = 纹理 v=0 → v 翻转；u 直通
    float2 uv = float2(proj.x * 0.5f + 0.5f, 0.5f - proj.y * 0.5f);
    if (uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f || proj.z < 0.0f
        || proj.z > 1.0f) {
        return 1.0f; // 级联视锥外：不受影
    }

    // 深度域 bias：常量项 + 斜率项（入射越掠射 bias 越大；ndl 已归一化）
    const float slope = sqrt(saturate(1.0f - ndl * ndl));
    const float bias = cascadeBias[cascade].x + cascadeBias[cascade].y * slope;
    const float receiver = proj.z - bias;

    const float texel = 1.0f / shadowParams.y;
    float sum = 0.0f;
    [unroll] for (int dy = -1; dy <= 1; ++dy) {
        [unroll] for (int dx = -1; dx <= 1; ++dx) {
            // SampleLevel（显式 LOD 0）：点采样 PCF 无需隐式梯度，且隐式 LOD 的
            // Sample 在动态分支内于部分运行时被 PSO 创建拒绝（E_INVALIDARG 实测）
            const float d = shadowMap.SampleLevel(shadowSampler,
                float3(uv + float2(float(dx), float(dy)) * texel, float(cascade)), 0.0f).r;
            sum += (receiver > d) ? 0.0f : 1.0f;
        }
    }
    return sum / 9.0f;
}

// =============================================================================
// PsMain：方向光（槽 0，乘 CSM shadow factor）+ 簇点光 + Reinhard tonemap
// =============================================================================
float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 v = normalize(camPos.xyz - input.worldPos);
    float3 albedo = material.rgb;
    float roughness = material.a;

    float3 acc = albedo * screenParams.w; // 环境项兜底（背光面不完全黑）

    // 视深反解（D3D LH 透视）：ndcZ = A - B/z → z = B/(A-ndcZ)
    float ndcZ = input.pos.z;
    float viewZ = projParams.y / max(projParams.x - ndcZ, 1e-4f);

    // ---- 方向光（场景约定占用槽 0；乘 CSM shadow factor）------------------------
    if (screenParams.z > 0.5f) {
        GpuLight dirLight = lights[0];
        if (dirLight.posRadius.w < 0.0f) { // 方向光哨兵（posRadius.w=-1）
            float3 l = normalize(dirLight.posRadius.xyz);
            float ndl = saturate(dot(n, l));
            if (ndl > 0.0f) {
                const float shadow = ShadowFactorPcf(input.worldPos, viewZ, ndl);
                if (shadow > 0.0f) {
                    acc += ShadePbr(n, v, l, dirLight.colorIntensity.rgb
                                              * dirLight.colorIntensity.a,
                               albedo, roughness)
                        * shadow;
                }
            }
        }
    }

    // ---- CLUSTER-INDEX（两侧一致性对照段；与 cluster_lighting.h
    //      ClusterGrid::ClusterIndexFromUv 逐字一致，修改须两侧同步）----------------
    float u = input.pos.x / screenParams.x;
    float vPix = input.pos.y / screenParams.y;
    uint cx = min(uint(floor(u * ZR_CLUSTER_DIM_X)), uint(ZR_CLUSTER_DIM_X - 1));
    uint cy = min(uint(floor(vPix * ZR_CLUSTER_DIM_Y)), uint(ZR_CLUSTER_DIM_Y - 1));
    float czF = floor((viewZ - projParams.z) * gridParams.w * ZR_CLUSTER_DIM_Z);
    uint cz = min(uint(max(czF, 0.0f)), uint(ZR_CLUSTER_DIM_Z - 1));
    uint clusterIndex = (cz * ZR_CLUSTER_DIM_Y + cy) * ZR_CLUSTER_DIM_X + cx;
    // ---------------------------------------------------------------------------
    uint rangeVec = clusterIndex >> 2;
    uint rangeLane = clusterIndex & 3u;
    uint4 range4 = clusterRanges[rangeVec];
    uint packedRange = (rangeLane == 0u)
        ? range4.x
        : (rangeLane == 1u) ? range4.y : (rangeLane == 2u) ? range4.z : range4.w;
    uint count = packedRange >> 16;
    uint start = packedRange & 0xFFFFu;

    [loop] for (uint j = 0; j < count; ++j) {
        uint flatIndex = start + j;
        uint4 packedIndices = clusterLightIndices[flatIndex >> 2];
        uint lane = flatIndex & 3u;
        uint lightIndex = (lane == 0u)
            ? packedIndices.x
            : (lane == 1u) ? packedIndices.y : (lane == 2u) ? packedIndices.z : packedIndices.w;
        GpuLight light = lights[lightIndex];
        float radius = light.posRadius.w;
        if (radius < 0.0f) {
            continue; // 方向光不经簇表（防御；方向光项已在上方处理）
        }
        float3 toLight = light.posRadius.xyz - input.worldPos;
        float dist2 = dot(toLight, toLight);
        float dist = sqrt(dist2);
        float3 l = toLight / max(dist, 1e-4f);
        float window = saturate(1.0f - dist2 / (radius * radius));
        float attenuation = window * window / (dist2 + 1.0f);
        acc += ShadePbr(n, v, l, light.colorIntensity.rgb * (light.colorIntensity.a * attenuation),
            albedo, roughness);
    }

    // Reinhard 简易 tonemap（与 pbr_forward.hlsl 同式）
    float3 mapped = acc / (1.0f + acc);
    return float4(saturate(mapped), 1.0f);
}
