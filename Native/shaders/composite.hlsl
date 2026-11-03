// =============================================================================
// composite.hlsl —— 场景 + SSR 合成（TASK-B-R5-03）
//
// 输出 = 场景颜色（t1）+ 模糊反射（t0，已预乘 fade）× SSR 开关。
// SSR 关（params.x < 0.5）时输出即场景颜色（可证伪对比的关闭侧——同一 PSO
// 分支，关闭侧不绑定无效资源）。
//
// 根签名（rootCbvCount=1 + srvTableCount=2）：b0 CBufComposite（16B）/ t0+t1 / s0+s1。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC）。
// =============================================================================

cbuffer CBufComposite : register(b0)
{
    float4 compositeParams; // x=SSR 开关（1=加反射 / 0=直通场景）
};

Texture2D ssrTex : register(t0);      // 模糊反射（rgb 预乘 fade）
Texture2D sceneTex : register(t1);    // 场景颜色（rgb=颜色 a=粗糙度）
SamplerState ssrSampler : register(s0);   // Point/Clamp（根签名静态采样器）
SamplerState sceneSampler : register(s1); // Point/Clamp（根签名静态采样器）

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
    const float4 scene = sceneTex.SampleLevel(sceneSampler, input.uv, 0.0f);
    const float4 ssr = ssrTex.SampleLevel(ssrSampler, input.uv, 0.0f);
    const float ssrOn = (compositeParams.x > 0.5f) ? 1.0f : 0.0f;
    return float4(saturate(scene.rgb + ssr.rgb * ssrOn), 1.0f);
}
