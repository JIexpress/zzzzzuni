// =============================================================================
// motion_vectors.hlsl —— 运动矢量 Pass（TASK-B-R6-01，RG 特性）
//
// FSR 必需输入：MV = 当前帧 clip pos − 上帧 clip pos（同一顶点经两帧 mvp 各自
// 变换；NDC 增量输出 RG16Float 渲染分辨率全分辨率）。FSR dispatch 侧再乘
// motionVectorScale = (0.5*renderW, -0.5*renderH)（见 zupscaler.h
// ZupMotionVectorScale）换算为像素单位（y 向下为正）。
//
// 场景契约（--fsr 模式）：相机静止（view 恒等），动画来自 PBR cube 绕 Y 旋转
// ——地面 mvpCur == mvpPrev → MV = 0；cube MV 来自旋转，真实可信。
// 两帧 mvp 均含各自 jitter（FSR jitter 注入主视投影），MV 内含相机 jitter；
// 上下文侧置 FFX_UPSCALE_ENABLE_MOTION_VECTORS_JITTER_CANCELLATION 位
// （fsr_upscaler.cpp——运行时按 dispatch 的 jitterOffset 反解出无 jitter MV）。
//
// 常量块 CBufMotionVectors（b0，128B）与 zupscaler.h
// ZrUpscalerMotionVectorConstants 一一对应（CPU 镜像单一事实源，修改须两侧同步，
// 单测 test_upscaler.cpp 锁定尺寸）。
//
// PSO：rtv=RG16Float、无深度、CullMode::None、rootCbvCount=1（b0）。
// =============================================================================

cbuffer CBufMotionVectors : register(b0)
{
    row_major float4x4 mvpCur;  // 当前帧 mvp（含本帧 jitter）
    row_major float4x4 mvpPrev; // 上帧 mvp（含上帧 jitter）
};

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0; // v1 固定顶点布局（32B，与 ZrD3D12_GraphicsInputLayout 一致）
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float4 prevClip : TEXCOORD0; // 上帧 clip pos（w 保留供透视除法）
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    const float4 worldPos = float4(input.pos, 1.0f);
    output.pos = mul(mvpCur, worldPos);
    output.prevClip = mul(mvpPrev, worldPos);
    return output;
}

float2 PsMain(PsIn input) : SV_Target
{
    // SV_POSITION.w = 插值后的 clip w（透视正确的光栅化保证）
    const float2 curNdc = input.pos.xy / input.pos.w;
    const float2 prevNdc = input.prevClip.xy / input.prevClip.w;
    return curNdc - prevNdc; // NDC 增量；CPU 侧乘 ZupMotionVectorScale → 像素单位
}
