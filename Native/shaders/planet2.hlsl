// =============================================================================
// planet2.hlsl —— TASK-B-TECH-04 行星尺度第二阶段着色（--planet2 模式）
//
// 与 planet.hlsl（B-TECH-03 --planet）独立：本文件服务 cube-sphere chunk 地形
// （两档 LOD 网格 + 裙边）与大气天穹渐变。两种绘制模式由 b0.params.x 选择
// （单 PSO 多用途，与窗口程序 CPU 侧 Planet2DrawConstants 一一对应）：
//   mode 0 地形 chunk：顶点 uv.x = 噪声高程（m，±2400）——高度带色（谷绿→
//         岩棕→雪白）+ 等高线（params.w = 间距 m）+ 距离雾（目标色 = 当前
//         海拔的地平线色——贴地蓝天/高空黑的一致衔接）+ 掠射大气边缘光
//         （limb 增亮，随海拔因子增强——远观行星的蓝色镶边）。
//   mode 1 大气天穹：相机中心单位球（缩放经 model 承载）；视线方向相对
//         渲染空间 up（b0.upVec.xyz）做天顶/地平线两色插值，两色由 CPU 按
//         相机海拔（对数插值，1km 全强 → 2 万 km 全黑）预混后传入——着色器
//         只做方向渐变（天穹渐变的"方向"半边）。
//
// 光照：固定方向光（与 planet.hlsl 同一 kSunDir）lambert + 环境；地形法线
// 来自 CPU 端噪声高度场中心差分（建筑在 chunk 构建期，非着色器）。
//
// 根签名（rootCbvCount = 1）：b0 CBufPlanet2（208B）：
//   row_major float4x4 mvp        // clip = mvp·pos
//   row_major float4x4 model      // 渲染空间变换（chunk: T(锚点−相机)；
//                                 // 天穹: T·S(穹半径)——顶点为单位球）
//   float4 params                 // x=模式 y=雾距离(m,≤0关) z=海拔因子
//                                 //   t(0=贴地→1=2万km) w=等高线间距(m)
//   float4 fogColor               // 当前海拔的地平线雾色
//   float4 zenithColor            // 当前海拔的天顶色
//   float4 horizonColor           // 当前海拔的地平线色
//   float4 upVec                  // xyz=渲染空间相机 up（天穹渐变轴）
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3（chunk: 相对锚点含地形位移；天穹: 单位球）| NORMAL float3
//   （地形: 噪声差分法线；天穹: 球方向）| TEXCOORD0 float2（地形 uv.x=高程 m）
//
// 编译：运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口 VsMain / PsMain。
// =============================================================================

cbuffer CBufPlanet2 : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 model;
    float4 params;
    float4 fogColor;
    float4 zenithColor;
    float4 horizonColor;
    float4 upVec;
};

// 指向太阳的方向（与 planet.hlsl 一致——两模式光照语义连续）
static const float3 kSunDir = normalize(float3(0.42, 0.62, 0.66));
// 高程色带常量（与 planet_math.h kTerrainMaxAmpM = 2400 对齐）
static const float kTerrainAmp = 2400.0;
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
    const float dist = length(input.viewPos);

    if (params.x < 0.5) {
        // ---- 地形 chunk：高程带色 + 等高线 + 雾（目标=当前地平线色）+ limb ----
        const float3 n = normalize(input.normal);
        const float lambert = saturate(dot(n, kSunDir));
        float light = kAmbient + (1.0 - kAmbient) * lambert;
        const float h = input.uv.x;
        // 高程带色：谷（湿绿）→ 岩（棕）→ 雪（高海拔白）
        const float tRock = saturate((h + 300.0) / 900.0);
        float3 col = lerp(float3(0.18, 0.30, 0.15), float3(0.42, 0.36, 0.28), tRock);
        col = lerp(col, float3(0.86, 0.89, 0.93),
            smoothstep(1100.0, 1800.0, h));
        // 等高线（params.w = 间距 m；随距离淡出防摩尔纹）
        const float contourPhase = frac(h / max(params.w, 1.0) + 0.5) - 0.5;
        const float contourFade = 1.0 - saturate(dist / 60000.0);
        const float contourLine = smoothstep(0.10, 0.0, abs(contourPhase) * 2.0);
        col = lerp(col, float3(0.80, 0.86, 0.72), contourLine * 0.30 * contourFade);
        col *= light;
        // 掠射大气边缘（limb）：视角掠射增蓝——远观行星的蓝色镶边；贴地时
        // 仅在海平面视线掠射处轻微出现（航空蔚蓝感），强度随海拔因子增强
        const float3 viewDir = -normalize(input.viewPos);
        const float rim = pow(1.0 - saturate(dot(n, viewDir)), 4.0);
        col += float3(0.30, 0.55, 1.0) * rim * (0.25 + 0.75 * params.z) * 0.75;
        // 距离雾：目标色 = 当前海拔地平线色（与天穹在地平线处无缝衔接）
        const float fog = (params.y > 0.0) ? saturate(dist / params.y) : 0.0;
        col = lerp(col, fogColor.rgb, fog * fog);
        return float4(col, 1.0);
    }

    // ---- 大气天穹：视线方向相对 up 的天顶/地平线渐变（色已按海拔预混）----
    const float3 dir = normalize(input.viewPos);
    const float upH = dot(dir, normalize(upVec.xyz));
    const float t = pow(saturate(upH), 0.65);
    float3 col = lerp(horizonColor.rgb, zenithColor.rgb, t);
    // 地平线下（被行星遮挡的区域）压暗——穹面只在行星轮廓外可见
    col *= (upH < 0.0) ? 0.85 : 1.0;
    // 太阳晕（小角度亮斑 + 宽晕；贴地明显、太空收敛为亮点）
    const float sunDot = saturate(dot(dir, kSunDir));
    col += float3(1.0, 0.95, 0.85)
        * (pow(sunDot, 900.0) * 1.6 + pow(sunDot, 8.0) * 0.10 * (1.0 - params.z));
    return float4(col, 1.0);
}
