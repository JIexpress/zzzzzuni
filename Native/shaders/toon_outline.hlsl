// =============================================================================
// toon_outline.hlsl —— NPR 反向壳描边（TASK-B-R5-03）
//
// 反向壳（inverted hull）描边（简报指定简化版）：
//   - VS：顶点沿法线外扩固定宽度（世界域，width 由 CB 给出），经 mvp 投影
//   - PS：纯色输出（深灰蓝描边色；全量版应可配置/沿屏幕宽度自适应）
//   - 绘制约定（窗口程序 --r5 OutlinePass）：CullMode::Front（剔除正面保留
//     背面壳）+ 深度测试开/深度写入关——轮廓带处壳背面在物体深度之外（测试
//     通过），物体正面覆盖区域内被深度剔除——只剩轮廓线
//   - 视图掩码：bit5 描边视图（draw_list.h kOutlineViewId）——主 pass 后的
//     第二次遍历只画该位可见的项
//
// 简化边界（施工报告诚实清单）：法线外扩在硬边模型（cube）的顶点法线为面法线
// 的复制——角点法线不连续处壳会裂开（缝隙）；全量版应焊接平滑法线或走屏幕
// 空间边缘检测。cube.zmesh 顶点法线为烘焙面法线，描边缝在轮廓像素级不可辨。
//
// 根签名（rootCbvCount=1）：b0 CBufOutline（80B，窗口程序 OutlineConstants 镜像）。
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
// =============================================================================

cbuffer CBufOutline : register(b0)
{
    row_major float4x4 mvp; // row_major：clip = P·V·M·(pos + normal*width)
    float4 outlineParams;   // x=壳外扩宽度（世界域，米）yzw 预留
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
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    const float3 inflated = input.pos + input.normal * outlineParams.x;
    output.pos = mul(mvp, float4(inflated, 1.0f));
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    // 纯色描边（深灰蓝，近黑）
    return float4(0.05f, 0.05f, 0.08f, 1.0f);
}
