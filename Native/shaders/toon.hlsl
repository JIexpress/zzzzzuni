// =============================================================================
// toon.hlsl —— NPR 卡通渲染变体（TASK-B-R5-03）
//
// 量化 ramp + 高光阈值二值（简报指定简化版）：
//   - N·L 3 阶 hard band：ndl < band0 → 暗部（darkShade）、< band1 → 中间调
//     （midShade）、否则亮部（1.0）
//   - 高光：blinn halfway 与法线夹角阈值二值（> hiThresh 加 hiShade，无平滑）
//   - 风格化光照方向 toonLightDir 独立于物理方向光（NPR 解耦；Gi/簇光源不进
//     toon——量化带须稳定，见施工报告简化边界）
//   - 输出 alpha = 1（粗糙度哨兵：SSR 粗糙度阈值把 toon 物体排除在反射采样外）
//
// 描边（反向壳）在 toon_outline.hlsl——第二 DrawPass（FrontCull 纯色），走
// draw list 描边视图掩码位（bit5，见 draw_list.h kOutlineViewId）。
//
// 根签名（rootCbvCount=1）：b0 CBufToon（208B，窗口程序 ToonConstants 镜像；
// TASK-B-R5-04 加法：toonBase 基色进 CB——--parity 契约要求基色 (0.9,0.3,0.25)
// 可配，--r5 探针基准不变（填回原硬编码值 (0.85,0.35,0.28)））。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
// =============================================================================

cbuffer CBufToon : register(b0)
{
    row_major float4x4 mvp;        // row_major（shader row_major 声明 ⇒ CPU 行主序写入）
    row_major float4x4 model;      // row_major（世界变换）
    float4 toonLightDir;           // xyz=指向风格化光源的方向（已归一化）
    float4 toonParams;             // x=band0 阈值 y=band1 阈值 z=暗部灰度 w=中间调灰度
    float4 toonSpecular;           // x=高光阈值 y=高光强度 zw 预留
    float4 camPos;                 // xyz=相机世界位置
    float4 toonBase;               // rgb=基色（B-R5-04 加法；量化 ramp 的乘数）
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
    output.normal = mul((float3x3) model, input.normal);
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 v = normalize(camPos.xyz - input.worldPos);
    float3 l = normalize(toonLightDir.xyz);

    // 基色（CB 可配；--r5 简化版原为硬编码 (0.85,0.35,0.28)，B-R5-04 起进 CB）
    const float3 base = toonBase.rgb;

    // N·L 3 阶 hard band 量化 ramp
    const float ndl = saturate(dot(n, l));
    float band;
    if (ndl < toonParams.x) {
        band = toonParams.z; // 暗部
    } else if (ndl < toonParams.y) {
        band = toonParams.w; // 中间调
    } else {
        band = 1.0f; // 亮部
    }

    // 高光阈值二值（无平滑项）
    float3 h = normalize(v + l);
    const float spec = (dot(n, h) > toonSpecular.x) ? toonSpecular.y : 0.0f;

    float3 color = base * band + spec.xxx;
    return float4(saturate(color), 1.0f); // alpha=1：粗糙度哨兵（SSR 排除）
}
