// =============================================================================
// hiz.hlsl —— SSR Hi-Z mip 链生成（TASK-B-R5-03）
//
// 两个像素入口（同一 VS）：
//   - HiZCopyPsMain  ：mip0——场景深度 1-tap 直拷（源 = 场景深度图的 R32Float
//                      孪生 SRV）
//   - HiZMinPsMain   ：mip 1..5——对源 mip 的 4 邻域（±半 texel）取 min（Hi-Z
//                      存"最近可见深度"，射线比较方向为"投影深度 > 存储 →
//                      穿入表面"）
//
// 视口/纹理约定：NDC y=+1 为屏幕顶端 = 纹理 v=0（与 pbr_shadow.hlsl 一致）；
// 全屏 quad VB 直接给 clip-space 坐标 + uv（uv.y=0 顶端，与 RTV 行序一致）。
//
// 执行约定（窗口程序 --r5 HiZBuild pass，与 ssr_pass.h RecordSsrPasses 对应）：
//   每级一个 draw（RTV = 目标 mip、viewport = mip 尺寸、t0 = 源 SRV），
//   pass 内 per-mip 手动子资源屏障（RENDER_TARGET ↔ PIXEL_SHADER_RESOURCE），
//   pass 末全部 mip 回 RENDER_TARGET（与 RG 整资源声明一致）。
//
// 根签名（rootCbvCount=1 + srvTable）：b0 CBufHiZ（16B）/ t0 源 mip + s0 Point。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC）。
// =============================================================================

cbuffer CBufHiZ : register(b0)
{
    float4 hizTexel; // x=源 mip 1/宽 y=源 mip 1/高 zw 预留
};

Texture2D hizSource : register(t0); // mip0=场景深度（R32Float）/ 上一级 Hi-Z
SamplerState hizSampler : register(s0); // Point/Clamp（根签名静态采样器）

struct VsIn
{
    float3 pos : POSITION; // clip-space（全屏 quad：xy ∈ [-1,1]，z=0）
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0; // uv.y=0 顶端（与 RTV 行序/深度图行序一致）
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = float4(input.pos.xy, 0.0f, 1.0f); // 无深度附件直通 clip
    output.uv = input.uv;
    return output;
}

/// mip0：场景深度 1-tap 直拷
float4 HiZCopyPsMain(PsIn input) : SV_Target
{
    return float4(hizSource.SampleLevel(hizSampler, input.uv, 0.0f).r, 0.0f, 0.0f, 1.0f);
}

/// mip 1..5：4 邻域 min（±半 texel 对角）
float4 HiZMinPsMain(PsIn input) : SV_Target
{
    const float2 halfTexel = hizTexel.xy * 0.5f;
    const float d00 = hizSource.SampleLevel(hizSampler, input.uv + float2(-halfTexel.x, -halfTexel.y), 0.0f).r;
    const float d10 = hizSource.SampleLevel(hizSampler, input.uv + float2(halfTexel.x, -halfTexel.y), 0.0f).r;
    const float d01 = hizSource.SampleLevel(hizSampler, input.uv + float2(-halfTexel.x, halfTexel.y), 0.0f).r;
    const float d11 = hizSource.SampleLevel(hizSampler, input.uv + float2(halfTexel.x, halfTexel.y), 0.0f).r;
    const float m0 = min(d00, d10);
    const float m1 = min(d01, d11);
    return float4(min(m0, m1), 0.0f, 0.0f, 1.0f);
}
