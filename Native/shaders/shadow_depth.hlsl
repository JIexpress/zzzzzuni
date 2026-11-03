// =============================================================================
// shadow_depth.hlsl —— CSM 级联阴影 depth-only 顶点着色器（TASK-B-R5-02）
//
// 深度-only 阴影 Pass（每级联一次）：把几何变换到该级联光视空间，仅写深度
// （PSO 以 ZrGraphicsPsoDesc::depthOnly=true 创建——无像素着色器、无颜色目标，
// NumRenderTargets=0；深度测试 LESS + 深度写入）。
//
// 根签名（b0 级联 lightVP + b1 逐绘 model，PSO rootCbvCount=2 / depthOnly=true）：
//   b0 CBufShadowPass（64B，与窗口程序 ShadowPassConstants 一一对应）：
//     row_major float4x4 lightVP    // 64B：该级联光视投影（BuildCascades 产物）
//   b1 CBufShadowModel（64B，与窗口程序 ShadowModelConstants 一一对应）：
//     row_major float4x4 shadowModel // 64B：该绘制项世界变换（与主 pass model 一致）
//                                   //（row_major 存储 ⇒ CPU 侧行主序写入；
//                                   //  该级联的 CascadeMatrix，见
//                                   //  Native/zrender/shadows.h BuildCascades）
//
// 剔除绕序约定（R4-03 CCW 发现的关系说明）：
//   主 pass 因资产烘焙绕序在 LH 投影下屏幕呈 CCW，PSO 取 CullMode::None（R4-03
//   实证决定，绕序统一归资产管线任务）。本阴影 pass 取 CullMode::Front——
//   D3D12 光栅化 FrontCounterClockwise=FALSE 下"正面=CW 绕序"，Front 剔除即
//   剔除屏幕 CW 面、保留 CCW 面：与主相机同样为 LH 投影时"面向光源的表面呈
//   CCW"一致，故 Front 剔除恰好保留面向光源的表面（shadow map 只需光向正面
//   的深度；相对"常规引擎 主 pass Back / 阴影 pass Front"的配对，因主 pass 被
//   迫 None（R4-03），本 pass 的 Front 即是"与主 pass 相反"的绕序约定）。
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2（后两者深度-only 不消费）
//
// 编译：运行时 D3DCompile（FXC，vs_5_0 → DXBC），入口 VsMain（无 PsMain）。
// =============================================================================

cbuffer CBufShadowPass : register(b0)
{
    row_major float4x4 lightVP; // 该级联光视投影矩阵（世界空间 → 光空间 clip）
};

cbuffer CBufShadowModel : register(b1)
{
    row_major float4x4 shadowModel; // 该绘制项的世界变换（与主 pass model 一致）
};

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL; // 深度-only 不消费（固定输入布局的语义超集）
    float2 uv : TEXCOORD0;  // 深度-only 不消费
};

struct VsOut
{
    float4 pos : SV_POSITION;
};

VsOut VsMain(VsIn input)
{
    VsOut output;
    // 世界变换 + 光视投影（clip = lightVP · model · pos；列向量约定）
    float4 world = mul(shadowModel, float4(input.pos, 1.0f));
    // 消费全部 3 个属性（v1 固定输入布局须与 VS 输入签名完全一致——
    // 未用属性会被 FXC 裁掉，布局超集在该运行时引致未定义顶点获取）
    float4 p = world + float4(input.normal, 0.0f) * 0.0f + float4(input.uv, 0.0f, 0.0f) * 0.0f;
    output.pos = mul(lightVP, p);
    return output;
}
