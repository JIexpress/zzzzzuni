// =============================================================================
// planet.hlsl —— TASK-B-TECH-03 行星尺度最小切片着色（--planet 模式）
//
// 三种绘制模式由 b0.params.x 选择（单 PSO 多用途，与 --planet 的 CPU 侧
// PlanetDrawConstants 一一对应）：
//   mode 0 地形网格（近场地形片/远盘）：高程着色 + 等高线（params.y = 间距 m）
//         ——高程来自 CPU 双精度球面建模（切平面偏移 → 球面点的曲率下沉），
//         经 uv.x 以 float 传入（渲染侧只看局部量——浮点原点纪律）。
//   mode 1 行星球体：伪大陆纬向带 + 极冠 + 大气边缘光（视角项：
//         渲染空间相机恒在原点——浮点原点的直接体现）。
//   mode 2 纯色（球面重力轨迹带 ribbon / 浮点原点演示立方体）：b0.color。
//
// 光照：固定方向光 lambert + 环境；距离雾向 kSkyColor（= --planet 清屏色）
// 融合——地平线处地形与天空无缝衔接。
//
// 根签名（rootCbvCount = 1）：b0 CBufPerDraw（160B）：
//   row_major float4x4 mvp    // clip = mvp·pos（列向量约定，行主序存储）
//   row_major float4x4 model  // 渲染空间（相机相对）物体变换——地形片/轨迹带
//                             // 顶点已按帧重定位为相机相对坐标（identity），
//                             // 球体/立方体经平移矩阵承载 double→float 相对偏移
//   float4 color              // mode 2 纯色 rgb
//   float4 params             // x=模式 y=等高线间距(m) z=雾距离(m,≤0 关) w=保留
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2（地形模式 uv.x=高程 m）
//
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
// =============================================================================

cbuffer CBufPerDraw : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 model;
    float4 color;
    float4 params;
};

// 指向太阳的方向（世界固定——行星晨昏线随相机绕行自然呈现）
static const float3 kSunDir = normalize(float3(0.42, 0.62, 0.66));
// 天空/太空色（与 --planet 清屏色一致：雾融合无缝）
static const float3 kSkyColor = float3(0.051, 0.077, 0.153);
static const float kAmbient = 0.22;

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PsIn
{
    float4 clip : SV_POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float3 viewPos : TEXCOORD1; // 渲染空间位置（相机在原点）
};

PsIn VsMain(VsIn input)
{
    PsIn o;
    o.clip = mul(mvp, float4(input.pos, 1.0));
    o.normal = input.normal;
    o.uv = input.uv;
    o.viewPos = mul(model, float4(input.pos, 1.0)).xyz;
    return o;
}

float4 PsMain(PsIn input) : SV_Target
{
    const float3 n = normalize(input.normal);
    const float lambert = saturate(dot(n, kSunDir));
    const float light = kAmbient + (1.0 - kAmbient) * lambert;
    const float dist = length(input.viewPos);
    float3 col;

    if (params.x < 0.5) {
        // ---- 地形网格：高程着色 + 等高线 + 世界对齐 1km 网格 ----
        // 高程带颜色随下沉加深（曲率低洼更暗）；uv.x = 相对切平面高程（m，≤0），
        // params.y = 等高线间距。1km 网格取自渲染空间 xz（相机相对——重定位平移
        // 网格随之移动，语义正确）；fwidth 归一化抗锯齿，远处收敛进雾。
        // "贴地 2m 看球面地平线曲率"的视觉载体：网格线在 distant 处弯沉。
        const float h = input.uv.x;
        const float t = saturate(-h / 120.0);
        col = lerp(float3(0.24, 0.40, 0.19), float3(0.12, 0.19, 0.11), t);
        const float contourPhase = frac(h / max(params.y, 1.0) + 0.5) - 0.5;
        const float contourLine = smoothstep(0.10, 0.0, abs(contourPhase) * 2.0);
        col = lerp(col, float3(0.80, 0.86, 0.72), contourLine * 0.35);
        const float2 q = input.viewPos.xz / 1000.0;
        const float2 gridPx = abs(frac(q - 0.5) - 0.5)
            / max(fwidth(q), float2(1e-5, 1e-5));
        const float gridFactor = 1.0 - saturate(min(gridPx.x, gridPx.y));
        col = lerp(col, float3(0.70, 0.80, 0.62), gridFactor * 0.5);
    } else if (params.x < 1.5) {
        // ---- 行星球体：伪大陆 + 极冠 + 大气边缘 ----
        const float band = sin(n.x * 6.0 + 1.3) * sin(n.y * 5.0 - 0.7)
            + sin(n.z * 7.0 + 0.4) * 0.5;
        const float land = smoothstep(0.15, 0.55, band);
        col = lerp(float3(0.06, 0.16, 0.42), float3(0.15, 0.31, 0.16), land);
        col = lerp(col, float3(0.88, 0.92, 0.95), smoothstep(0.80, 0.92, abs(n.y)));
        // 大气边缘：视角掠射增亮（相机在渲染空间原点 → 视线 = -viewPos）
        const float3 viewDir = -normalize(input.viewPos);
        const float rim = pow(1.0 - saturate(dot(n, viewDir)), 3.0);
        col += float3(0.30, 0.55, 1.0) * rim * 0.85;
    } else {
        // ---- 轨迹带 / 立方体：纯色 ----
        col = color.rgb;
    }

    col *= light;
    // 距离雾（params.z ≤ 0 关闭——太空模式）
    const float fog = (params.z > 0.0) ? saturate(dist / params.z) : 0.0;
    col = lerp(col, kSkyColor, fog * fog);
    return float4(col, 1.0);
}
