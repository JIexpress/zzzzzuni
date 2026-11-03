// =============================================================================
// pbr_forward.hlsl —— PBR 前向光照 + 簇光源（TASK-B-R5-01）
//
// PS：像素级簇索引 → 读 range → 循环点光；GGX NDF + Smith 可见性 + Lambert
//     漫反射；方向光（槽 0）+ 点光数组；Reinhard 简易 tonemap（TODO：后续任务
//     由 Volume 后处理管线替代——曝光/自适应/ACES）。
//
// 根签名（与 zrhi_d3d12_device.cpp CreateFirstDrawRootSignature 的 3 槽布局一致，
// ZrGraphicsPsoDesc.rootCbvCount = 3）：
//   b0 CBufPerDraw（208B，与窗口程序 LightsConstants 一一对应）：
//     row_major float4x4 mvp        //  64B：clip = mvp * float4(pos,1)（列向量约定，
//                                   //      row_major 存储 ⇒ CPU 侧行主序写入）
//     row_major float4x4 model      //  64B：世界 = model * float4(pos,1)（法线用
//                                   //      其 3x3；--lights 场景仅平移/等比，可直接用）
//     float4 projParams             //  16B：x=projA=zf/(zf-zn) y=projB=zn*zf/(zf-zn)
//                                   //      z=nearZ w=farZ（视深反解：z = B/(A-ndcZ)）
//     float4 gridParams             //  16B：x/y/z=dimX/Y/Z（与 CPU 侧 ZR_CLUSTER_DIM_* 一致）
//                                   //      w=invZRange=1/(farZ-nearZ)
//     float4 screenParams           //  16B：x=width y=height z=lightCount w=ambient
//     float4 material               //  16B：rgb=albedo（PBR 参数常量简化）a=roughness
//     float4 camPos                 //  16B：xyz=相机世界位置（v = normalize(camPos-P)）
//   b1 CBufLights（1024B）：
//     GpuLight lights[32]           //  float4×2/盏=32B：posRadius(xyz=位置或方向,
//                                   //  w=半径,方向光=-1 哨兵) + colorIntensity(rgb,a=强度)
//                                   //  布局与 Native/zrender/cluster_lighting.h
//                                   //  PackedLight（static_assert 32B）一致
//   b2 CBufClusters（9216B + 56000B = 65216B ≤ 64KB cbuffer 预算；两表均 uint4
//   分装——字节布局=连续 uint32，规避 FXC 动态索引 4096 槽上限，见 CBufClusters 注释）：
//     uint4 clusterRanges[576]        //  每簇 start/count（count<<16|start 打包）
//     uint4 clusterLightIndices[3500] //  平坦光索引表（start 指向此处）
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
//
// 簇索引两侧一致性（TASK-B-R5-01 设计要点 6）：本文件 PsMain 内 "CLUSTER-INDEX"
// 标注段与 Native/zrender/cluster_lighting.h ClusterGrid::ClusterIndexFromUv
// 逐字对应（floor(u*dimX)/floor(v*dimY)/floor((viewZ-nearZ)*invZRange*dimZ)，
// clamp 到 [0,dim-1]，索引 (cz*dimY+cy)*dimX+cx；dim 常量与 ZR_CLUSTER_DIM_*
// 相同）。修改须两侧同步。
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
    float4 projParams;
    float4 gridParams;
    float4 screenParams;
    float4 material;
    float4 camPos;
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
    // 两表均按 uint4 分装（576+3500=4076 槽 ≤ FXC 动态索引 4096 槽上限；字节布局
    // 均等于"连续 uint32 串联"，CPU 侧写入不变）。标量 uint 数组动态索引每元素
    // 占一整槽（2304+3500=5804>4096 会被 FXC 拒绝出码），故必须 uint4 分装。
    uint4 clusterRanges[ZR_CLUSTER_RANGE_VEC_MAX];      // 簇 c → 向量 c>>2 的 c&3 通道
    uint4 clusterLightIndices[ZR_CLUSTER_FLAT_VEC_MAX]; // entry k → 向量 k>>2 的 k&3 通道
};

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
    // --lights 场景 model 仅平移（无旋转/非等比缩放）→ 法线直接变换即可
    output.normal = mul((float3x3) model, input.normal);
    return output;
}

// =============================================================================
// PBR：GGX NDF + Smith 可见性（Schlick-GGX 近似）+ Lambert + Fresnel-Schlick
// PBR 参数常量简化：albedo/roughness 每物体常量（CB0 material），F0 固定 0.04
// =============================================================================

static const float PI = 3.14159265f;
static const float3 kF0Dielectric = float3(0.04f, 0.04f, 0.04f); // 非金属 F0

/// 单光源直接光照贡献（n=法线 v=视线(指向相机) l=指向光源；radiance=光源颜色×强度×衰减）
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
    // GGX NDF（Walter et al. 2007, "Microfacet Models for Refraction through
    // Rough Surfaces", Eq.33）：D = a² / (π ((n·h)² (a²-1) + 1)²)
    float dd = ndh * ndh * (a2 - 1.0f) + 1.0f;
    float D = a2 / (PI * dd * dd);
    // Smith 遮蔽-阴影，Schlick-GGX 近似 k = roughness/2（Karis 2013, UE4 "Real
    // Shading in Unreal Engine 4"）——分离式 V = G(v)/G(l)，除以 4(n·v)(n·l)
    float k = max(roughness * 0.5f, 1e-3f);
    float gv = ndv / (ndv * (1.0f - k) + k);
    float gl = ndl / (ndl * (1.0f - k) + k);
    float Vis = (gv * gl) / max(4.0f * ndv * ndl, 1e-4f);
    // Fresnel（Schlick 1994）：F = F0 + (1-F0)(1-(v·h))^5
    float3 F = kF0Dielectric + (1.0f - kF0Dielectric) * pow(1.0f - vdh, 5.0f);

    float3 specular = D * Vis * F;
    float3 diffuse = albedo / PI; // Lambert（BRDF 漫反射项 = albedo/π）
    return (diffuse + specular) * radiance * ndl;
}

// =============================================================================
// PsMain：簇索引 → 读 range → 循环点光；方向光槽 0；Reinhard tonemap
// =============================================================================
float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 v = normalize(camPos.xyz - input.worldPos);
    float3 albedo = material.rgb;
    float roughness = material.a;

    float3 acc = albedo * screenParams.w; // 环境项兜底（背光面不完全黑）

    // ---- 方向光（场景约定占用槽 0；不参与簇裁剪——CPU Assign 里"写全簇"承载语义）----
    if (screenParams.z > 0.5f) {
        GpuLight dirLight = lights[0];
        if (dirLight.posRadius.w < 0.0f) { // 方向光哨兵（posRadius.w=-1）
            float3 l = normalize(dirLight.posRadius.xyz);
            acc += ShadePbr(n, v, l, dirLight.colorIntensity.rgb * dirLight.colorIntensity.a,
                albedo, roughness);
        }
    }

    // ---- CLUSTER-INDEX（两侧一致性对照段；与 cluster_lighting.h
    //      ClusterGrid::ClusterIndexFromUv 逐字一致，修改须两侧同步）----------------
    // SV_Position.xy = 像素中心（px+0.5）→ u/v 与 CPU 像素中心采样对齐
    float u = input.pos.x / screenParams.x;
    float vPix = input.pos.y / screenParams.y;
    uint cx = min(uint(floor(u * ZR_CLUSTER_DIM_X)), uint(ZR_CLUSTER_DIM_X - 1));
    uint cy = min(uint(floor(vPix * ZR_CLUSTER_DIM_Y)), uint(ZR_CLUSTER_DIM_Y - 1));
    // 视深反解（D3D LH 透视）：ndcZ = A - B/z → z = B/(A-ndcZ)
    float ndcZ = input.pos.z;
    float viewZ = projParams.y / max(projParams.x - ndcZ, 1e-4f);
    float czF = floor((viewZ - projParams.z) * gridParams.w * ZR_CLUSTER_DIM_Z);
    uint cz = min(uint(max(czF, 0.0f)), uint(ZR_CLUSTER_DIM_Z - 1));
    uint clusterIndex = (cz * ZR_CLUSTER_DIM_Y + cy) * ZR_CLUSTER_DIM_X + cx;
    // ---------------------------------------------------------------------------
    // 簇范围表 uint4 分装：簇 c 在第 c>>2 向量的 c&3 通道（字节布局同连续 uint32）
    uint rangeVec = clusterIndex >> 2;
    uint rangeLane = clusterIndex & 3u;
    uint4 range4 = clusterRanges[rangeVec];
    uint packedRange = (rangeLane == 0u)
        ? range4.x
        : (rangeLane == 1u) ? range4.y : (rangeLane == 2u) ? range4.z : range4.w;
    uint count = packedRange >> 16;
    uint start = packedRange & 0xFFFFu;

    [loop] for (uint j = 0; j < count; ++j) {
        // 平坦索引表 uint4 分装：entry k 在第 k/4 向量的 k%4 通道（字节布局同连续 uint32）
        uint flatIndex = start + j;
        uint4 packedIndices = clusterLightIndices[flatIndex >> 2];
        uint lane = flatIndex & 3u;
        uint lightIndex = (lane == 0u)
            ? packedIndices.x
            : (lane == 1u) ? packedIndices.y : (lane == 2u) ? packedIndices.z : packedIndices.w;
        GpuLight light = lights[lightIndex];
        float radius = light.posRadius.w;
        if (radius < 0.0f) {
            continue; // 方向光不经簇表（防御；CPU 侧方向光只作"写全簇"语义）
        }
        float3 toLight = light.posRadius.xyz - input.worldPos;
        float dist2 = dot(toLight, toLight);
        float dist = sqrt(dist2);
        float3 l = toLight / max(dist, 1e-4f);
        // 衰减：逆平方（分母 dist²+1 压奇异）× 平滑窗口（半径外归零）
        float window = saturate(1.0f - dist2 / (radius * radius));
        float attenuation = window * window / (dist2 + 1.0f);
        acc += ShadePbr(n, v, l, light.colorIntensity.rgb * (light.colorIntensity.a * attenuation),
            albedo, roughness);
    }

    // Reinhard 简易 tonemap（c/(1+c)，HDR→LDR 全局压缩）
    // TODO(后续任务)：由 Volume 后处理管线替代——曝光/自适应/ACES（简报指定注明）
    float3 mapped = acc / (1.0f + acc);
    return float4(saturate(mapped), 1.0f);
}
