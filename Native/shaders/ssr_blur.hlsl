// =============================================================================
// ssr_blur.hlsl —— SSR 4-tap 空间模糊（半分辨率，TASK-B-R5-03）
//
// 简化版（简报指定）：对角 ±半 texel 4-tap box 平均（偏移与
// Native/zrender/ssr_pass.h SsrBlurTapOffsets 一致——修改须两侧同步）。
// 无时间降噪/无边缘感知权重（简化边界，施工报告注明）。
// 输入已预乘 fade（ssr_trace.hlsl 输出 alpha=fade）——平均保预乘语义。
//
// 根签名（rootCbvCount=1 + srvTable）：b0 CBufBlur（16B）/ t0 trace RT / s0。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC）。
// =============================================================================

cbuffer CBufBlur : register(b0)
{
    float4 blurTexel; // x=1/半分辨率宽 y=1/半分辨率高 zw 预留
};

Texture2D blurSource : register(t0); // trace RT（rgb=反射颜色、alpha=fade，预乘）
SamplerState blurSampler : register(s0); // Point/Clamp（根签名静态采样器）

struct VsIn
{
    float3 pos : POSITION;
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

float4 PsMain(PsIn input) : SV_Target
{
    const float2 t = blurTexel.xy * 0.5f;
    const float4 s00 = blurSource.SampleLevel(blurSampler, input.uv + float2(-t.x, -t.y), 0.0f);
    const float4 s10 = blurSource.SampleLevel(blurSampler, input.uv + float2(t.x, -t.y), 0.0f);
    const float4 s01 = blurSource.SampleLevel(blurSampler, input.uv + float2(-t.x, t.y), 0.0f);
    const float4 s11 = blurSource.SampleLevel(blurSampler, input.uv + float2(t.x, t.y), 0.0f);
    return (s00 + s10 + s01 + s11) * 0.25f;
}
