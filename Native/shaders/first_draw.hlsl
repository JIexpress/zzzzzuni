// =============================================================================
// first_draw.hlsl —— 首绘着色器（TASK-B-R4-03）
//
// VS：mvp 变换（模型旋转随帧更新，经 CBufPerDraw 每帧写入）
// PS：Lambert N·L + 环境项 + uv checker 调试项（8x8 棋盘，肉眼/像素校验可辨）
//
// 根签名 v1（与 zrhi_d3d12_device.cpp CreateFirstDrawRootSignature 一致）：
//   单一根 CBV b0 —— CBufPerDraw，常量布局（96B）：
//     row_major float4x4 mvp   // 64B：clip = mvp * float4(pos,1)（列向量约定，
//                              //      row_major 存储 ⇒ CPU 侧行主序写入）
//     float4 lightDir          // 16B：xyz = 指向光源的方向（已归一化）
//     float4 lightColor        // 16B：rgb = 光色
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 编译：本期运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口
//   VsMain / PsMain（zrhi_d3d12_device.cpp CompileHlsl）。
// DXC/SM6 升级路径：改用 dxc.exe /T vs_6_0|ps_6_0（-spirv 出 Vulkan 轨 SPIR-V）；
// 本文件语法为 SM5/SM6 通用子集，无需修改源码即可切换编译器。
// =============================================================================

cbuffer CBufPerDraw : register(b0)
{
    row_major float4x4 mvp;
    float4 lightDir;
    float4 lightColor;
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
    float2 uv : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = mul(mvp, float4(input.pos, 1.0f));
    output.normal = input.normal;
    output.uv = input.uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    // Lambert：N·L（含零面截断）+ 环境项兜底（背光面不完全黑）
    float3 n = normalize(input.normal);
    float ndl = saturate(dot(n, normalize(lightDir.xyz)));
    float3 lit = lightColor.rgb * ndl;
    float3 ambient = float3(0.25f, 0.25f, 0.25f);

    // uv checker（8x8 棋盘调试项：黑白蓝三色可辨，用于像素级校验证据）
    float2 cell = floor(input.uv * 8.0f);
    float checker = fmod(cell.x + cell.y, 2.0f);
    float3 albedo = lerp(float3(0.85f, 0.85f, 0.85f), float3(0.15f, 0.35f, 0.85f), checker);

    return float4(albedo * (ambient + lit), 1.0f);
}
