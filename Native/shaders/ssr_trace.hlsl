// =============================================================================
// ssr_trace.hlsl —— SSR 半分辨率射线步进（TASK-B-R5-03）
//
// 简化版 SSR trace（简报指定；简化边界见 ssr_pass.h / 施工报告）：
//   - 输入：Hi-Z mip 链（t0，mip0=场景深度）+ 场景颜色（t1，alpha=粗糙度近似）
//   - 起点：全屏 quad uv（trace RT 半分辨率，uv 与全分辨率同域）→ 场景深度
//     重建世界位置（invViewProj）
//   - 粗糙度阈值：sceneTex.a > 阈值 → 输出 0（回落环境项——环境项已在场景
//     颜色里，trace 只补反射增量）
//   - 反射方向：地平面常数法线 (0,1,0)（应用目标=地面；全量版应读 G-buffer
//     法线——简化边界）
//   - march：视空间方向、屏幕 UV 空间投影比较；固定 32 步（步长线性前进，
//     采样 mip 随步距升高——Hi-Z 加速）；投影深度 > Hi-Z 存储深度 → 穿入
//     表面 → 4 步二分细化（mip0 精度）；未命中/出屏 → 0
//   - 输出：rgb=反射颜色（已乘边缘 fade）、alpha=fade（预乘；blur/composite
//     直接相加）
//
// 根签名（rootCbvCount=1 + srvTableCount=2）：b0 CBufSsr（192B，与
// Native/zrender/ssr_pass.h SsrTraceConstants 逐字节一致）/ t0+t1 表 / s0+s1。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC）。
// =============================================================================

#define ZR_SSR_STEP_COUNT 32
#define ZR_SSR_BISECT_STEPS 4
#define ZR_SSR_MAX_MIPS 6

cbuffer CBufSsr : register(b0)
{
    row_major float4x4 invViewProj; // ndc(uv,depth) → world（起点重建）
    row_major float4x4 viewProj;    // world → ndc（march 点投影回屏幕）
    float4 projParams;              // x=A y=B z=nearZ w=farZ（保留——视深反解备用）
    float4 ssrParams;               // x=粗糙度阈值 y=步长(米) z=边缘 fade 宽度 w=SSR 开关
    float4 screenParams;            // x=全宽 y=全高 z=半宽 w=半高
    float4 camPos;                  // xyz=相机世界位置
};

Texture2D hizTex : register(t0);      // R32Float mip 链（mip0=场景深度）
Texture2D sceneTex : register(t1);    // RGBA8：rgb=颜色 a=粗糙度（近似）
SamplerState hizSampler : register(s0); // Point/Clamp（Hi-Z 比较：须点采样）
SamplerState sceneSampler : register(s1); // Point/Clamp（颜色采样——简化版无各向异性）

struct VsIn
{
    float3 pos : POSITION; // clip-space（全屏 quad）
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = float4(input.pos.xy, 0.0f, 1.0f);
    output.uv = input.uv;
    return output;
}

/// 屏幕 uv（v=0 顶端）+ 深度 → 世界位置（行主序 invViewProj + 列向量 mul）
float3 WorldFromUvDepth(float2 uv, float depth)
{
    // 视口约定：ndcX = 2u-1；ndcY = 1-2v（v=0 顶端 → ndcY=+1）
    const float2 ndc = float2(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f);
    const float4 w = mul(invViewProj, float4(ndc, depth, 1.0f));
    return w.xyz / w.w;
}

/// 世界位置 → 屏幕 uv（v=0 顶端）+ ndc 深度；w ≤ 0 时返回负 w 供调用方丢弃
float4 ProjectToUv(float3 world)
{
    const float4 c = mul(viewProj, float4(world, 1.0f));
    if (c.w <= 0.0f) {
        return float4(0.0f, 0.0f, 2.0f, c.w);
    }
    const float3 ndc = c.xyz / c.w;
    return float4(ndc.x * 0.5f + 0.5f, 0.5f - ndc.y * 0.5f, ndc.z, c.w);
}

float4 PsMain(PsIn input) : SV_Target
{
    if (ssrParams.w < 0.5f) {
        return float4(0.0f, 0.0f, 0.0f, 0.0f); // SSR 关：合成侧输出即场景
    }

    const float2 uv = input.uv;
    const float depth = hizTex.SampleLevel(hizSampler, uv, 0.0f).r;
    if (depth >= 0.9999f) {
        return float4(0.0f, 0.0f, 0.0f, 0.0f); // 天空：无反射
    }

    // 粗糙度阈值（sceneTex.a 近似——高于阈值回落环境项，不采样 SSR）
    const float roughness = sceneTex.SampleLevel(sceneSampler, uv, 0.0f).a;
    if (roughness > ssrParams.x) {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    const float3 world = WorldFromUvDepth(uv, depth);
    const float3 toCam = camPos.xyz - world;
    float3 incident = -normalize(toCam);           // 相机 → 表面点
    const float3 planeNormal = float3(0.0f, 1.0f, 0.0f); // 地平面（简化边界）
    const float3 refl = reflect(incident, planeNormal);
    if (refl.y <= 0.02f) {
        return float4(0.0f, 0.0f, 0.0f, 0.0f); // 反射向下半球：直接丢弃
    }

    // ---- 固定 32 步 UV 空间 march（mip 随步距升高——Hi-Z 加速）----------------
    float3 prev = world;
    bool found = false;
    float2 hitUv = float2(0.0f, 0.0f);
    [loop] for (uint i = 1u; i <= ZR_SSR_STEP_COUNT; ++i) {
        const float3 p = world + refl * (ssrParams.y * float(i));
        const float4 pd = ProjectToUv(p);
        if (pd.w <= 0.0f || pd.z < 0.0f || pd.z > 1.0f || pd.x < 0.0f || pd.x > 1.0f
            || pd.y < 0.0f || pd.y > 1.0f) {
            break; // 出屏/投影无效：终止
        }
        // 采样 mip：随步距线性升高（32 步 / 6 mip → 每 6 步 +1 级）
        const uint mip = min(ZR_SSR_MAX_MIPS - 1u, i / 6u);
        const float hz = hizTex.SampleLevel(hizSampler, pd.xy, float(mip)).r;
        if (pd.z > hz + 0.0004f) {
            // 穿入表面：prev（前一步，未穿）与 p（当前，已穿）之间二分细化
            float3 a = prev;
            float3 b = p;
            [loop] for (uint k = 0u; k < ZR_SSR_BISECT_STEPS; ++k) {
                const float3 mid = (a + b) * 0.5f;
                const float4 md = ProjectToUv(mid);
                const float midHz = hizTex.SampleLevel(hizSampler, md.xy, 0.0f).r;
                if (md.z > midHz + 0.0002f) {
                    b = mid; // 已穿入：收缩上界
                } else {
                    a = mid; // 仍在表面前：推进下界
                }
            }
            const float4 finalProj = ProjectToUv((a + b) * 0.5f);
            hitUv = finalProj.xy;
            found = true;
            break;
        }
        prev = p;
    }
    if (!found) {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    // 反射颜色（Point 采样——简化版静态采样器无双线性，报告注明）+ 屏幕边缘 fade
    const float3 reflColor = sceneTex.SampleLevel(sceneSampler, hitUv, 0.0f).rgb;
    const float2 edge = min(hitUv, 1.0f - hitUv);
    const float fade = saturate(min(edge.x, edge.y) / max(ssrParams.z, 1e-4f));
    return float4(reflColor * fade, fade); // 预乘（blur/composite 直接相加）
}
