// =============================================================================
// upsample_bilinear.hlsl —— Bilinear 基线上采样（TASK-B-R6-01）
//
// 自写基线回退后端（zupscaler BilinearUpscaler）的着色器：全屏三角
// （SV_VertexID，零顶点缓冲）+ 线性采样——等价于 viewport 缩放 blit 的
// GPU 路径，作 FSR 可证伪对比的关闭侧基线。
//
// 根签名（BilinearUpscaler 内部创建）：b0 根 CBV（CBufBilinear 16B）+
// t0 描述符表 + s0 静态线性采样器。编译：D3DCompile（FXC，vs_5_0 / ps_5_0）。
// =============================================================================

cbuffer CBufBilinear : register(b0)
{
    float4 srcTexel; // x=1/srcW y=1/srcH（uv 直采无需目标尺寸；w 留对齐）
};

Texture2D srcTex : register(t0);
SamplerState srcSampler : register(s0); // Linear/Clamp（根签名静态采样器）

struct PsIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
};

PsIn VsMain(uint vid : SV_VertexID)
{
    // 全屏大三角：uv=(0,0)/(2,0)/(0,2)，pos=(uv*2-1, 1-uv.y*2)——可见域 uv [0,1]²
    // （v=0 对齐纹理顶行；NDC y 向上），屏幕外两顶点保证完全覆盖
    PsIn output;
    const float2 uv = float2((vid << 1u) & 2u, vid & 2u);
    output.pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
    output.uv = uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    return srcTex.SampleLevel(srcSampler, input.uv, 0.0f);
}
