// =============================================================================
// zrhi_d3d12_unity_target.cpp —— Unity 共享渲染目标实现（TASK-B-UNITY-02）
//
// 一句话：ZRender 把 cube 画进**自己拥有的共享纹理**，导出 NT 句柄让 Unity 采样。
//
// 为什么不是画 Unity 的 backbuffer（验收已判定，不要回退到那条）：
//   ① backbuffer 状态：flip-model 下帧边界是 PRESENT，PRESENT→RENDER_TARGET 无
//      隐式提升，屏障必须由我们插，而我们不知道 Unity 的行程序列；
//   ② 跨队列同步：本桥自建 DIRECT 队列，WaitIdle 只排空自己的队列，不覆盖
//      Unity 队列上未完成的渲染——画一半就被 Unity 采样是必然的竞态。
// 自己拥有纹理后两条同时消失：纹理只由本队列写，写完 fence 等完成，Unity 只读。
//
// 每帧顺序（RenderFrame）：
//   RG(Import color+depth → ClearPass → DrawPass → [ReadbackPass]) →
//   手动屏障 [ColorAttachmentOptimal|TransferSrcOptimal] → ShaderReadOnlyOptimal →
//   Submit → WaitIdle →（回读帧）Map 中心像素
// 返回时纹理恒为 PIXEL_SHADER_RESOURCE 且 GPU 已完成——这既是 Unity 采样的前提，
// 也是"不需要跨队列 fence"的全部理由。
//
// 帧内容（TASK-B-UNITY-03 起由调用方决定，两条路径共用上面的 RG/屏障/回读）：
//   - 场景路径：调用方 SetCamera + DrawMesh 提交了内容 → 画 Unity 的物件
//     （常量 176B，scene_draw.hlsl；逐物件 world/mvp/albedo）
//   - 回落路径：没有提交 → 仍画原来的三 cube 演示（first_draw.hlsl / 96B 常量），
//     保证 --draw 与既有验证路径行为不变
//
// 几何/着色器：cube 与 --draw 同源（.zmesh cube 的 24 顶点 ±0.5 + first_draw.hlsl）。
// 但 DLL 在 Player 里没有仓库路径，无法运行时读文件，故：
//   - 顶点/索引以常量内嵌（数值与 Native/tests/data/cube.zmesh 完全一致）
//   - 两份 HLSL 都以字符串内嵌，**逐字**等于 Native/shaders/{first_draw,scene_draw}.hlsl
//     （first_draw 有单测做字节比对；scene_draw 同规矩，改文件请同步内嵌副本）
// =============================================================================
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <chrono>
#include <cmath>
#include <cstring>
#include <vector>

#include "zplanet/planet_math.h" // TASK-B-PLANET-04：chunk 顶点规范路由 + 噪声高程（生产头，仅标准库）
#include "zrender/rg_render_graph.h"
#include "zrhi/d3d12/zrhi_d3d12_device.h"

namespace zr {
namespace unity_target {
namespace {

// 与 --draw 同值的清屏色（0.05,0.10,0.15 → 8bit (13,26,38)）
constexpr float kClearColor[4] = {0.05f, 0.10f, 0.15f, 1.0f};
constexpr uint32_t kDrawItems = 3u;      // 三个 cube 横向排开（与 --draw 同布局）
constexpr uint32_t kReadbackInterval = 30u; // 每 30 帧回读一次中心像素（全程都回读太浪费屏障）
constexpr float kPi = 3.14159265358979f;

// 常量布局（96B）——与 first_draw.hlsl 的 CBufPerDraw 一一对应
struct DrawConstants {
    float mvp[16];     // row_major
    float lightDir[4]; // xyz = 指向光源
    float lightColor[4];
};
static_assert(sizeof(DrawConstants) == 96, "DrawConstants must be 96 bytes");

// v1 固定顶点布局：Pos3f+Normal3f+Uv2f（32B，与 ZrD3D12_GraphicsInputLayout 一致）
struct TargetVertex {
    float pos[3];
    float normal[3];
    float uv[2];
};
static_assert(sizeof(TargetVertex) == 32, "TargetVertex must be 32 bytes");

// -----------------------------------------------------------------------------
// 嵌入式 HLSL：逐字等于 Native/shaders/first_draw.hlsl（勿单独改动本段——
// 改 shader 请改 .hlsl 再把文件内容整段复制过来，单测会校验两者字节一致）
// -----------------------------------------------------------------------------
const char kFirstDrawHlsl[] = R"ZRHLSL(// =============================================================================
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
)ZRHLSL";

// -----------------------------------------------------------------------------
// 嵌入式场景着色器：逐字等于 Native/shaders/scene_draw.hlsl（改 shader 请改文件
// 后再整段同步到这里；两份不一致会造成"改了没生效"的假象）。
// -----------------------------------------------------------------------------
const char kSceneHlsl[] = R"ZRSCENEHLSL(// =============================================================================
// scene_draw.hlsl —— Unity 场景提交着色器（TASK-B-UNITY-03）
//
// 与 first_draw.hlsl 的关系：**不改** first_draw.hlsl。那份是 --draw 与
// B-UNITY-02 的内嵌基线（单测逐字节比对它，且 --draw 的常量布局是 96B），
// 场景提交需要"逐物件世界矩阵 + 逐物件颜色"，常量布局因此多两块，独立成
// 本文件（内嵌副本在 zrhi_d3d12_unity_target.cpp 的 kSceneHlsl）。
//
// 根签名约定的 b0 常量布局（176B，D3D 行主序存储）：
//   row_major float4x4 mvp      // 64B：proj * view * world
//   row_major float4x4 world    // 64B：model（法线要用它的 3x3，见下）
//   float4 lightDir             // 16B：xyz = 指向光源的方向（世界空间，已归一化）
//   float4 lightColor           // 16B：rgb = 光色
//   float4 albedo               // 16B：rgb = 物件基色（Unity sharedMaterial 色）
//
// 顶点输入与 first_draw.hlsl 完全一致（v1 固定布局，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 为什么法线要用 world 矩阵：Unity 的顶点法线是**模型空间**的，顶点数据按
// mesh 缓存（同一 mesh 被多个 transform 复用），所以法线只能在 VS 里随物件
// 变换——否则所有旋转过的物件都会照出面黑的背光面。
// =============================================================================

cbuffer CBufSceneDraw : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 world;
    float4 lightDir;
    float4 lightColor;
    float4 albedo;
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
    // 法线转到世界空间（旋转 + 均匀缩放；非均匀缩放由 PS 的 normalize 兜住量级）
    output.normal = mul((float3x3)world, input.normal);
    output.uv = input.uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 l = normalize(lightDir.xyz);
    // 双面光照：地面 Plane 与部分资产的绕序不保证，背光面给半强度而不是全黑
    float ndl = saturate(dot(n, l));
    // 环境项抬到 0.45：ZRender 没有 URP 的球谐环境光，纯 Lambert 会把背光面压成
    // 剪影（实测胶囊/角色发黑），这里用固定环境项兜住"能看清"这条底线。
    float3 ambient = float3(0.45f, 0.47f, 0.52f);
    float3 lit = lightColor.rgb * ndl;
    return float4(albedo.rgb * (ambient + lit), 1.0f);
}
)ZRSCENEHLSL";

// -----------------------------------------------------------------------------
// 嵌入式行星着色器（TASK-B-PLANET-04）：逐字等于 Native/shaders/planet2.hlsl
// （B-TECH-04 交付的生产着色器文件——本任务把它接进生产 dll 的渲染路径）。
// 改 shader 请改文件后再整段同步到这里；单测做两份逐字节比对（first_draw 同规矩）。
// 生产通道 v1 只用 mode 0（地形 chunk）；mode 1（大气天穹）保留给窗口验证程序
// 的天穹路径——天穹网格不经 chunk ABI，属演示层。
// -----------------------------------------------------------------------------
const char kPlanet2Hlsl[] = R"ZRPLANET2HLSL(// =============================================================================
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
)ZRPLANET2HLSL";

// 立方体 24 顶点（±0.5，与 Native/tests/data/cube.zmesh 同尺寸；6 面 × 4 顶点）
const TargetVertex kCubeVertices[24] = {
    // +X
    {{0.5f, -0.5f, -0.5f}, {1.0f, 0.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {1.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {1.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
    {{0.5f, 0.5f, -0.5f}, {1.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    // -X
    {{-0.5f, -0.5f, 0.5f}, {-1.0f, 0.0f, 0.0f}, {0.0f, 0.0f}},
    {{-0.5f, -0.5f, -0.5f}, {-1.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
    {{-0.5f, 0.5f, -0.5f}, {-1.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {-1.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    // +Y
    {{-0.5f, 0.5f, -0.5f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, 0.5f, -0.5f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    // -Y
    {{-0.5f, -0.5f, 0.5f}, {0.0f, -1.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {0.0f, -1.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, -0.5f, -0.5f}, {0.0f, -1.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, -0.5f, -0.5f}, {0.0f, -1.0f, 0.0f}, {0.0f, 1.0f}},
    // +Z
    {{-0.5f, -0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {0.0f, 1.0f}},
    // -Z
    {{0.5f, -0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {0.0f, 0.0f}},
    {{-0.5f, -0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {1.0f, 0.0f}},
    {{-0.5f, 0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {1.0f, 1.0f}},
    {{0.5f, 0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {0.0f, 1.0f}},
};
constexpr uint32_t kCubeVertexCount = 24u;
static_assert(sizeof(kCubeVertices) / sizeof(kCubeVertices[0]) == kCubeVertexCount,
    "cube vertex table size must match kCubeVertexCount");
constexpr uint32_t kCubeIndexCount = 36u;
const uint32_t kCubeIndices[36] = {
    0u, 1u, 2u, 0u, 2u, 3u,       // +X
    4u, 5u, 6u, 4u, 6u, 7u,       // -X
    8u, 9u, 10u, 8u, 10u, 11u,    // +Y
    12u, 13u, 14u, 12u, 14u, 15u, // -Y
    16u, 17u, 18u, 16u, 18u, 19u, // +Z
    20u, 21u, 22u, 20u, 22u, 23u, // -Z
};

// ---- 行主序 4x4 矩阵助手（列向量约定：clip = P·V·M·pos；与 --draw 同源）--------
void MatMultiply(const float* a, const float* b, float* out)
{
    float r[16];
    for (int row = 0; row < 4; ++row) {
        for (int col = 0; col < 4; ++col) {
            float sum = 0.0f;
            for (int k = 0; k < 4; ++k) {
                sum += a[row * 4 + k] * b[k * 4 + col];
            }
            r[row * 4 + col] = sum;
        }
    }
    std::memcpy(out, r, sizeof(r));
}

void MatPerspectiveFovLH(float fovY, float aspect, float zn, float zf, float* out)
{
    const float f = 1.0f / std::tan(fovY * 0.5f);
    const float m[16] = { f / aspect, 0.0f, 0.0f, 0.0f, 0.0f, f, 0.0f, 0.0f, 0.0f, 0.0f,
        zf / (zf - zn), -zn * zf / (zf - zn), 0.0f, 0.0f, 1.0f, 0.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationX(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    const float m[16] = { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, c, -s, 0.0f, 0.0f, s, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationY(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    const float m[16] = { c, 0.0f, s, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -s, 0.0f, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatTranslation(float x, float y, float z, float* out)
{
    const float m[16] = { 1.0f, 0.0f, 0.0f, x, 0.0f, 1.0f, 0.0f, y, 0.0f, 0.0f, 1.0f,
        z, 0.0f, 0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

ZrImageBarrier MakeBarrier(ZrImage image, ZrImageLayout prev, ZrImageLayout next,
    ZrAccessBits srcAccess, ZrPipelineStageBits srcStage, ZrAccessBits dstAccess,
    ZrPipelineStageBits dstStage)
{
    ZrImageBarrier barrier {};
    barrier.image = image;
    barrier.prevLayout = prev;
    barrier.newLayout = next;
    barrier.srcAccess = srcAccess;
    barrier.dstAccess = dstAccess;
    barrier.srcStage = srcStage;
    barrier.dstStage = dstStage;
    return barrier;
}

// 目标与整条渲染链（单线程；进程内单例）
//
// TASK-B-UNITY-04：颜色/深度目标改成**双槽**（0/1 轮流），配合 fence 做帧间并行。
// 单槽在"不等本帧完成"的前提下必然被下一帧覆盖，宿主会采到画了一半的内容。
constexpr uint32_t kTargetSlots = 2u;
/// 判定"这一帧真的被阻塞过"的阈值（毫秒）：低于它的等待只是 fence 查询开销
constexpr double kBlockedThresholdMs = 0.05;

// ---- TASK-B-PLANET-04：行星通道常量 ---------------------------------------------
// CB 池上限 3200：B-TECH-04 实测可见峰值 2662 零截断的池口径（设计稿 §7.2）。
constexpr uint32_t kPlanetMaxDrawItems = 3200u;
constexpr uint32_t kPlanetResFar = 16u;  // 远档 16×16 顶点（B-TECH-04 两档口径）
constexpr uint32_t kPlanetResNear = 64u; // 近档 64×64
constexpr uint32_t kPlanetFarVerts = kPlanetResFar * kPlanetResFar + 4u * kPlanetResFar;
constexpr uint32_t kPlanetNearVerts = kPlanetResNear * kPlanetResNear + 4u * kPlanetResNear;
constexpr uint32_t kPlanetFarIndices = (kPlanetResFar - 1u) * (kPlanetResFar - 1u) * 6u
    + 4u * (kPlanetResFar - 1u) * 6u;
constexpr uint32_t kPlanetNearIndices = (kPlanetResNear - 1u) * (kPlanetResNear - 1u) * 6u
    + 4u * (kPlanetResNear - 1u) * 6u;
/// 高程带/等高线/雾的展示常量（B-TECH-04 planet2 口径原样移植——设计稿 §12-5
/// "v1 视觉沿用高程色带+距离雾+limb"）
constexpr double kPlanetFogGroundM = 150000.0;
constexpr double kPlanetFogClimbM = 500000.0;
constexpr double kPlanetClimbAltM = 2.0e7;
constexpr double kPlanetContourM = 500.0;
constexpr float kPlanetZenithBlue[3] = { 0.16f, 0.36f, 0.78f };
constexpr float kPlanetHorizonBlue[3] = { 0.58f, 0.72f, 0.92f };
constexpr float kPlanetSpace[3] = { 0.004f, 0.006f, 0.012f };

struct TargetState {
    ZrD3D12Device* device = nullptr; // 包装对象（CreateFrom 产物）
    ZrImage color[kTargetSlots] {};
    HANDLE sharedHandle[kTargetSlots] = {nullptr, nullptr};
    ZrImage depth[kTargetSlots] {};
    // 真实 desc 保活：RG 侧 Import 时带过去，否则 rg.export 只能报占位值
    ZrImageDesc colorDesc {};
    ZrImageDesc depthDesc {};
    ZrImageView colorRtv[kTargetSlots] {};
    ZrImageView depthDsv[kTargetSlots] {};
    // 帧同步（TASK-B-UNITY-04）：本队列自己的 fence，帧 N 提交后 Signal，
    // 下一帧开头等 N-1 的完成。事件对象只建一次（WaitForSingleObject 复用）。
    ID3D12Fence* syncFence = nullptr;
    HANDLE syncEvent = nullptr;
    uint64_t syncValue = 0u;                              // 已 Signal 的最大值
    uint32_t readableSlot = 0u;                            // 宿主可安全采样的槽位
    bool pendingReadback = false;                          // 上一帧的回读结果待取
    double syncLastMs = 0.0;                               // 最近一次等待耗时
    double syncTotalMs = 0.0;                              // 累计等待耗时
    uint32_t syncFrames = 0u;                              // 发生过等待的帧数
    uint32_t syncBlocked = 0u;                             // 其中真的被阻塞的帧数
    ZrBuffer vb {};
    ZrBuffer ib {};
    // 逐绘常量缓冲池（TASK-B-UNITY-03）：按需增长到本帧绘制项数，之后复用。
    // 上限 = kMaxSceneDraws；每项 256B（根 CBV 对齐裕量），持久 Map。
    ZrBuffer cb[kMaxSceneDraws] {};
    void* cbMapped[kMaxSceneDraws] {};
    uint32_t cbCount = 0u;
    ZrShader vs {};
    ZrShader ps {};
    ZrPipelineState pso {};
    ZrShader sceneVs {};       // 场景提交变体（176B 常量，见 scene_draw.hlsl）
    ZrShader scenePs {};
    ZrPipelineState psoScene {};
    ZrCommandList cmd {};
    IZrQueue* queue = nullptr;
    ZrBuffer readback {};
    void* readbackMapped = nullptr;
    // 中心 1x1 回读的真实脚印（TASK-B-UNITY-05）：RowPitch 是设备给的，不是 width*bpp。
    // 旧版把 256 写死成 kReadbackPitch —— 恰好对 1x1 BGRA8 成立，但那是"假设行距"
    // 这类事故（整面回读只有 1/8 行正确）的同款起点，故改为向 GetCopyableFootprints 问。
    uint32_t readbackPitch = 0u;
    uint64_t readbackBytes = 0u;
    // 整面回读（TASK-B-UNITY-05）：按需建一次、随目标一起释放
    ZrBuffer fullReadback {};
    void* fullReadbackMapped = nullptr;
    uint32_t fullReadbackPitch = 0u;
    uint64_t fullReadbackBytes = 0u;
    // 每个槽位"帧末"的真实 D3D12 状态：整面回读必须从它出发做屏障。
    // （D3D12 的状态跟踪按资源对象走；本模块只用自己那个对象，故这张表就是真值。）
    D3D12_RESOURCE_STATES colorState[kTargetSlots] = {
        D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COMMON};
    uint32_t width = 0u;
    uint32_t height = 0u;
    uint32_t frameCount = 0u;
    uint32_t readbackCount = 0u;
    uint32_t centerRGBA[4] = {0u, 0u, 0u, 0u};
    uint32_t lastError = 0u;
    // ---- TASK-B-PLANET-04：行星通道渲染资源（随目标一起建立/释放）----------------
    ZrShader planetVs {};
    ZrShader planetPs {};
    ZrPipelineState psoPlanet {};
    ZrBuffer planetIbFar {};  // 共享远档索引（grid+四缘裙边，全 chunk 复用）
    ZrBuffer planetIbNear {}; // 共享近档索引（同上）
    ZrBuffer planetCb[kPlanetMaxDrawItems] {};   // 逐绘常量池（256B/项，持久 Map）
    void* planetCbMapped[kPlanetMaxDrawItems] = {};
    uint32_t planetCbCount = 0u;
};

TargetState g_target;

// ---- TASK-B-UNITY-03：场景提交（相机 / 网格表 / 绘制表）-------------------------
// 全部定容静态存储：每帧不分配、不上传顶点（性能底线）。绘制表每帧由
// BeginScene 清空、DrawMesh 追加、RenderFrame 消费。

/// 一帧内的一次绘制（定容数组元素；88B）
struct SceneDrawCmd {
    uint32_t meshSlot = 0u;
    uint32_t meshGeneration = 0u;
    float world[16] = {}; // 行主序 model（平移量在下标 3/7/11）
    float rgba[4] = {1.0f, 1.0f, 1.0f, 1.0f};
};
static_assert(sizeof(SceneDrawCmd) == 88, "SceneDrawCmd must be 88 bytes");

/// 网格句柄表项：句柄 = (代际<<32)|槽位，与 zplugin 的 .zmesh 表同一约定
struct MeshEntry {
    bool used = false;
    uint32_t generation = 0u; // 从 1 起；释放即 +1（0 = 槽从未用过）
    ZrBuffer vb {};
    ZrBuffer ib {};
    uint32_t indexCount = 0u;
};

constexpr uint32_t kMeshSlots = 512u;

/// 场景着色器常量（176B）——与 scene_draw.hlsl 的 CBufSceneDraw 一一对应
struct SceneDrawConstants {
    float mvp[16];   // proj * view * world（行主序，列向量约定）
    float world[16]; // 法线要用的 model
    float lightDir[4];
    float lightColor[4];
    float albedo[4];
};
static_assert(sizeof(SceneDrawConstants) == 176, "SceneDrawConstants must be 176 bytes");

/// 帧内绘制项（渲染链消费的形态：绑定什么、画多少）
struct FrameDraw {
    ZrBuffer vb {};
    ZrBuffer ib {};
    uint32_t indexCount = 0u;
};

/// 行星通道常量（208B，TASK-B-PLANET-04）——与 planet2.hlsl 的 CBufPlanet2
/// 一一对应；CBuf static_assert 先例（B-TECH-04.md:46）：布局错位编译期即红。
struct Planet2DrawConstants {
    float mvp[16];        // clip = proj·view·T(float(anchor−cam))（double 域相减一次 cast）
    float model[16];      // 渲染空间变换 = T(chunkRel)（顶点局部 float 相对锚点）
    float params[4];      // x=模式(0 地形) y=雾距离 z=海拔因子 t w=等高线间距
    float fogColor[4];    // 当前海拔地平线雾色（CPU 预混，B-TECH-04 口径）
    float zenithColor[4];
    float horizonColor[4];
    float upVec[4];       // xyz=渲染空间相机 up（径向经 view 旋转）
};
static_assert(sizeof(Planet2DrawConstants) == 208, "Planet2DrawConstants must be 208 bytes");

struct SceneState {
    bool cameraSet = false;
    float view[16] = {};
    float proj[16] = {};
    uint32_t drawCount = 0u;
    SceneDrawCmd draws[kMaxSceneDraws];
    uint64_t uploadCount = 0u;  // CreateMesh 成功次数（应恒等于 mesh 种类数）
    uint64_t frameCount = 0u;   // 走场景路径的帧数
    uint64_t drawTotal = 0u;    // 累计绘制项
    uint32_t lastDrawCount = 0u;
};

SceneState g_scene;
MeshEntry g_meshes[kMeshSlots];
/// 本帧实际要画的项（RenderFrame 填；定容静态，避免每帧分配）
FrameDraw g_frameDraws[kMaxSceneDraws];

uint64_t MakeMeshHandle(uint32_t slot, uint32_t generation)
{
    return (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(slot);
}

MeshEntry* LookupMesh(uint64_t handle)
{
    const uint32_t slot = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slot >= kMeshSlots || generation == 0u) {
        return nullptr;
    }
    MeshEntry& entry = g_meshes[slot];
    if (!entry.used || entry.generation != generation) {
        return nullptr;
    }
    return &entry;
}

// =============================================================================
// TASK-B-PLANET-04：行星 chunk 通道（句柄表 + 顶点生成 + GPU 生命周期）
//
// 设计依据：Docs/Planning/P2-行星ABI设计.md
//   §4.1 句柄 = 高 32 代际/低 32 槽位，0 恒无效（与 mesh 句柄同协议）；
//   §4.4 生命周期 EMPTY → PENDING_UPLOAD → LIVE → PENDING_FREE → EMPTY；
//        销毁句柄立即失效（代际+1，独占锁内），GPU 资源延迟一帧释放，
//        PENDING_FREE 期间仍被绘制（先画后删，帧内一致）；
//   §4.5 ChunkCreate 校验链（zplugin 层做纯输入域，本层做 STATE 复核 + FULL）；
//   §7.1 所有权：chunk 顶点 CPU 副本只在 PENDING_UPLOAD 队列里活到上传边界；
//        VB 归句柄表槽位；档位重建靠纯函数重生成（无缓存）；
//   §7.3 泄漏防护四层：①代际防误用 ②GetStats 账目恒等式
//        createTotal − destroyTotal == liveChunks（ClearAll/桥级释放按销毁记账，
//        恒等式全程成立）③ClearAll bulk 释放 ④DestroyTargetObjects 随桥兜底；
//   §8   线程：写操作主线程 SRWLOCK 独占；RenderFrame 帧首共享锁取快照
//        （决策拷贝），GPU 上传/释放/重建/绘制全部锁外；
//   §3.2/D3 相机相对模型矩阵：float(anchorAbs − camAbs)——double 域相减后
//        一次 cast（planet_math.h RenderOffsetF「重定位正确路径」同构）。
//
// 状态落点说明（B-PLANET-05→04 接管迁移）：相机原点与 chunk 表都落在本模块——
// zrhi 不反向依赖 zplugin（链接方向 zplugin → zrhi），渲染线程可读的状态必须
// 在消费侧；zplugin 六导出与本组 API 之间是纯薄转发。
// =============================================================================

namespace planet_detail {

constexpr uint32_t kMaxChunks = ZR_PLANET_MAX_CHUNKS; // 8192（zplugin.h §4.1 口径）
constexpr uint32_t kVertBytes = ZR_PLANET_VERTEX_STRIDE_FLOATS * 4u; // 32B/顶点
constexpr uint32_t kLodUnset = 0xFFFFFFFFu; // appliedLod 哨兵：VB 尚无内容
constexpr uint32_t kNeighborReservedMask = 0xF0u; // neighborMask 高 4 位保留

// 槽位状态机（§4.4）
enum PlanetSlotState : uint32_t {
    kSlotEmpty = 0u,
    kSlotPendingUpload = 1u, // 顶点已生成，待渲染线程上传（下一 RenderFrame 生效）
    kSlotLive = 2u,          // VB 已上传，可绘制
    kSlotPendingFree = 3u,   // 句柄已失效；VB 保留到下一 RenderFrame（期间仍绘制）
};

struct PlanetChunkSlot {
    uint32_t state = kSlotEmpty;
    uint32_t generation = 0u;  // 从 1 起；销毁即 +1（0 = 槽从未用过，mesh 表同协议）
    uint32_t faceId = 0u;
    uint32_t cx = 0u;
    uint32_t cy = 0u;
    uint32_t cellsPerEdge = 1u;
    uint32_t desiredLod = 0u;     // 校验过的期望档（SetLod 延迟应用）
    uint32_t appliedLod = kLodUnset; // VB 当前内容档（未上传 = 哨兵）
    uint32_t genTier = 0u;        // pendingVerts 生成时的档（上传前 SetLod 变档则重生成）
    uint32_t neighborMask = 0u;   // 四缘掩码（v1 校验+记录，不改几何——设计稿 §5.2）
    double originAbs[3] = {0.0, 0.0, 0.0}; // 锚点绝对坐标（SetOrigin 可变；顶点不重算）
    uint64_t freeFrame = 0u;      // 进入 PENDING_FREE 时的帧号（到期才释放——先画后删）
    ZrBuffer vb {};
    bool vbNearSized = false;     // 近档容量启动过（退档复用不缩容，B-TECH-04 策略）
    void* vbMapped = nullptr;     // hostVisible 持久映射（建缓冲时取得）
    uint32_t vbBytes = 0u;        // VB 分配容量字节（vertexBytes 账目口径；升容不缩容）
    std::vector<uint8_t> pendingVerts; // PENDING_UPLOAD 期间顶点字节（上传后清空）
};

// 进程级单例（§7.1：句柄表/上下文快照/统计全部进程级，SRWLOCK 保护）
SRWLOCK g_lock = SRWLOCK_INIT;
PlanetChunkSlot g_slots[kMaxChunks];
bool g_worldSet = false;      // SetWorldContext 成功过（v1 单行星静态语义）
uint32_t g_seed = 0u;
double g_center[3] = {0.0, 0.0, 0.0};
double g_camOrigin[3] = {0.0, 0.0, 0.0}; // 相机绝对原点（B-PLANET-05 接管落点）
uint32_t g_occupied = 0u;     // PENDING_UPLOAD + LIVE + PENDING_FREE（FULL 裁决口径）
uint64_t g_createTotal = 0u;
uint64_t g_destroyTotal = 0u;
uint64_t g_lodChangeTotal = 0u;
uint32_t g_drawnLast = 0u;
uint32_t g_culledLast = 0u;
uint32_t g_seamFails = 0u;
double g_lastFrameMs = 0.0;
double g_frameMsSum = 0.0;
uint32_t g_frameMsCount = 0u;

// 帧快照（决策拷贝，§8）——静态定容，避免每帧分配
struct PlanetFrameDraw {
    ZrBuffer vb;
    uint32_t tier;
    double originAbs[3];
};
struct PlanetFrameSnapshot {
    PlanetFrameDraw draws[kPlanetMaxDrawItems];
    uint32_t drawCount = 0u;
    uint32_t culledCount = 0u;
    double camAbs[3] = {0.0, 0.0, 0.0};
    double center[3] = {0.0, 0.0, 0.0};
    bool worldSet = false;
    bool cameraUsable = false; // g_scene.cameraSet（无相机不构 mvp，本帧不画）
};
PlanetFrameSnapshot g_frame;

constexpr double kPi = 3.14159265358979323846;

uint32_t TierVerts(uint32_t tier)
{
    return (tier >= 1u) ? kPlanetNearVerts : kPlanetFarVerts;
}

uint32_t TierRes(uint32_t tier)
{
    return (tier >= 1u) ? kPlanetResNear : kPlanetResFar;
}

uint32_t TierIndices(uint32_t tier)
{
    return (tier >= 1u) ? kPlanetNearIndices : kPlanetFarIndices;
}

/// chunk 弧长（米）：面坐标一格 = 2/total，赤道弧 (π/4)·R 每 1 面坐标
///（B-TECH-04 BuildChunkVerts 同式）
double ChunkArcM(uint32_t cellsPerEdge, uint32_t tier)
{
    const double total = static_cast<double>(cellsPerEdge) * static_cast<double>(TierRes(tier) - 1u);
    return 2.0 / total * (kPi / 4.0) * zplanet::kPlanetRadiusM;
}

/// 裙边深 = 2% chunk 弧长（B-TECH-04：遮蔽跨档 T 型连接与面界缝）
double SkirtDropM(uint32_t cellsPerEdge, uint32_t tier)
{
    return 0.02 * ChunkArcM(cellsPerEdge, tier);
}

// ---- 顶点生成（规范路由消费 zplanet 生产头，§4.2）--------------------------------
//
// 顶点布局 32B：pos3f（= float(posAbs − originAbs)，double 域相减后一次 cast）+
// normal3f（噪声高度场中心差分，B-TECH-04 同式）+ uv2f（uv.x = 高程 m 供
// planet2.hlsl 高程带色；uv.y 预留 0——uv 列不实装纹理坐标，设计稿 §12-5）。
// 返回顶点数（res² + 4·res，含裙边）。
uint32_t GenerateChunkVerts(uint32_t faceId, uint32_t cx, uint32_t cy, uint32_t cellsPerEdge,
    uint32_t tier, const double originAbs[3], std::vector<uint8_t>& out)
{
    const uint32_t res = TierRes(tier);
    const int32_t stride = static_cast<int32_t>(res) - 1;
    const int32_t total = static_cast<int32_t>(cellsPerEdge) * stride;
    const zplanet::Vec3d planetCenter = { g_center[0], g_center[1], g_center[2] };
    const zplanet::CubeFaceBasis basis = zplanet::CubeFaceBasisOf(static_cast<int>(faceId));
    const double cellArcM = ChunkArcM(cellsPerEdge, tier);
    const double skirtDrop = SkirtDropM(cellsPerEdge, tier);
    const uint32_t vertCount = res * res + 4u * res;
    out.assign(static_cast<size_t>(vertCount) * kVertBytes, 0u);
    auto* verts = reinterpret_cast<TargetVertex*>(out.data());
    uint32_t n = 0u;
    auto emit = [&](const zplanet::Vec3d& posAbs, const zplanet::Vec3d& normal,
                    double heightM) {
        TargetVertex& o = verts[n];
        // double 域相减后一次 cast（§4.2/D3——重定位正确路径）
        o.pos[0] = static_cast<float>(posAbs.x - originAbs[0]);
        o.pos[1] = static_cast<float>(posAbs.y - originAbs[1]);
        o.pos[2] = static_cast<float>(posAbs.z - originAbs[2]);
        o.normal[0] = static_cast<float>(normal.x);
        o.normal[1] = static_cast<float>(normal.y);
        o.normal[2] = static_cast<float>(normal.z);
        o.uv[0] = static_cast<float>(heightM);
        o.uv[1] = 0.0f;
        ++n;
    };
    auto emitGridVertex = [&](uint32_t i, uint32_t j) {
        const int32_t gU = zplanet::ChunkVertexIndex(
            static_cast<int32_t>(cx), static_cast<int32_t>(i), stride);
        const int32_t gV = zplanet::ChunkVertexIndex(
            static_cast<int32_t>(cy), static_cast<int32_t>(j), stride);
        const zplanet::PlanetChunkVertex c = zplanet::PlanetChunkVertexAtGlobal(
            planetCenter, static_cast<int>(faceId), gU, gV, total, g_seed);
        // 地形法线：噪声高度场中心差分（±1 全局索引 = ±1 格；越界面界为纯函数
        // 光滑延拓，无分支保确定性——B-TECH-04 同式）
        const zplanet::PlanetChunkVertex pu = zplanet::PlanetChunkVertexAtGlobal(
            planetCenter, static_cast<int>(faceId), gU + 1, gV, total, g_seed);
        const zplanet::PlanetChunkVertex pd = zplanet::PlanetChunkVertexAtGlobal(
            planetCenter, static_cast<int>(faceId), gU - 1, gV, total, g_seed);
        const zplanet::PlanetChunkVertex pv = zplanet::PlanetChunkVertexAtGlobal(
            planetCenter, static_cast<int>(faceId), gU, gV + 1, total, g_seed);
        const zplanet::PlanetChunkVertex pw = zplanet::PlanetChunkVertexAtGlobal(
            planetCenter, static_cast<int>(faceId), gU, gV - 1, total, g_seed);
        const double sx = (pu.heightM - pd.heightM) / (2.0 * cellArcM);
        const double sy = (pv.heightM - pw.heightM) / (2.0 * cellArcM);
        const double d1 = zplanet::VecDot(basis.t1, c.normal);
        const double d2 = zplanet::VecDot(basis.t2, c.normal);
        zplanet::Vec3d t1 = zplanet::VecSub(basis.t1, zplanet::VecScale(c.normal, d1));
        zplanet::Vec3d t2 = zplanet::VecSub(basis.t2, zplanet::VecScale(c.normal, d2));
        t1 = zplanet::VecScale(t1, 1.0 / zplanet::VecLen(t1));
        t2 = zplanet::VecScale(t2, 1.0 / zplanet::VecLen(t2));
        zplanet::Vec3d nrm = zplanet::VecSub(zplanet::VecSub(c.normal,
            zplanet::VecScale(t1, sx)), zplanet::VecScale(t2, sy));
        nrm = zplanet::VecScale(nrm, 1.0 / zplanet::VecLen(nrm));
        emit(c.posAbs, nrm, c.heightM);
    };
    for (uint32_t j = 0; j < res; ++j) {
        for (uint32_t i = 0; i < res; ++i) {
            emitGridVertex(i, j);
        }
    }
    // 裙边：四缘顶点复制 + 沿 −球面方向下垂（缘序与 BuildTierIndices 索引走线一致：
    // e=0 北(i=k,j=0) / 1 南(i=k,j=res−1) / 2 西(i=0,j=k) / 3 东(i=res−1,j=k)）
    for (uint32_t e = 0; e < 4u; ++e) {
        for (uint32_t k = 0; k < res; ++k) {
            const uint32_t i = (e >= 2u) ? ((e == 2u) ? 0u : (res - 1u)) : k;
            const uint32_t j = (e >= 2u) ? k : ((e == 0u) ? 0u : (res - 1u));
            const int32_t gU = zplanet::ChunkVertexIndex(
                static_cast<int32_t>(cx), static_cast<int32_t>(i), stride);
            const int32_t gV = zplanet::ChunkVertexIndex(
                static_cast<int32_t>(cy), static_cast<int32_t>(j), stride);
            const zplanet::PlanetChunkVertex v = zplanet::PlanetChunkVertexAtGlobal(
                planetCenter, static_cast<int>(faceId), gU, gV, total, g_seed);
            const zplanet::Vec3d dropped = zplanet::VecSub(v.posAbs,
                zplanet::VecScale(v.normal, skirtDrop));
            emit(dropped, v.normal, v.heightM);
        }
    }
    return n;
}

// ---- 运行期共享边核查（B-TECH-04 seams 口径移植；seamCheckFails 诊断计数）----------
//
// 范围（v1 如实登记）：仅近档（tier 1）构建时核查——近档是缝宽敏感档；远档共享边
// 的逐位一致由构造性路由保证并有 test_planet.cpp doctest 锁定（§5.1 同档共享边
// 同全局整数索引 → 同一 double 表达式）。近档构建每帧量级 ~百，O(8192) 邻居查找
// 可忽略；远档全量首帧 6144 次构建若逐次全表扫会引入秒级首帧开销，故不查。
void SeamCheckNear(uint32_t faceId, uint32_t cx, uint32_t cy, uint32_t cellsPerEdge,
    uint32_t tier)
{
    const uint32_t res = TierRes(tier);
    const int32_t stride = static_cast<int32_t>(res) - 1;
    const int32_t total = static_cast<int32_t>(cellsPerEdge) * stride;
    const zplanet::Vec3d planetCenter = { g_center[0], g_center[1], g_center[2] };
    // 同 (faceId,cx±1/cy±1,cellsPerEdge) 的已建同档邻居（LIVE 或 PENDING_FREE——
    // 两者 VB 内容仍在；方向对称：A 查 B 时 B 也会查 A，首帧上传序不影响覆盖）
    auto findNeighbor = [&](int32_t nx, int32_t ny) -> const PlanetChunkSlot* {
        if (nx < 0 || ny < 0 || nx >= static_cast<int32_t>(cellsPerEdge)
            || ny >= static_cast<int32_t>(cellsPerEdge)) {
            return nullptr;
        }
        for (uint32_t s = 0; s < kMaxChunks; ++s) {
            const PlanetChunkSlot& o = g_slots[s];
            if (o.faceId == faceId && o.cx == static_cast<uint32_t>(nx)
                && o.cy == static_cast<uint32_t>(ny) && o.cellsPerEdge == cellsPerEdge
                && o.appliedLod == tier
                && (o.state == kSlotLive || o.state == kSlotPendingFree)) {
                return &o;
            }
        }
        return nullptr;
    };
    auto checkEdge = [&](const PlanetChunkSlot* other, uint32_t myFirstI, uint32_t myFirstJ,
        uint32_t otFirstI, uint32_t otFirstJ, int32_t deltaI, int32_t deltaJ) {
        if (other == nullptr) {
            return;
        }
        // 缘上第 k 个共享顶点：我方 = first + k·delta，邻方同 delta（两侧沿同一条
        // 缘推进——共享边的全局索引逐 k 恒等，构造性一致，memcmp 应全绿）
        for (uint32_t k = 0; k < res; ++k) {
            const uint32_t mi = static_cast<uint32_t>(
                static_cast<int32_t>(myFirstI) + static_cast<int32_t>(k) * deltaI);
            const uint32_t mj = static_cast<uint32_t>(
                static_cast<int32_t>(myFirstJ) + static_cast<int32_t>(k) * deltaJ);
            const uint32_t oi = static_cast<uint32_t>(
                static_cast<int32_t>(otFirstI) + static_cast<int32_t>(k) * deltaI);
            const uint32_t oj = static_cast<uint32_t>(
                static_cast<int32_t>(otFirstJ) + static_cast<int32_t>(k) * deltaJ);
            const int32_t gUm = zplanet::ChunkVertexIndex(static_cast<int32_t>(cx),
                static_cast<int32_t>(mi), stride);
            const int32_t gVm = zplanet::ChunkVertexIndex(static_cast<int32_t>(cy),
                static_cast<int32_t>(mj), stride);
            const int32_t gUo = zplanet::ChunkVertexIndex(static_cast<int32_t>(other->cx),
                static_cast<int32_t>(oi), stride);
            const int32_t gVo = zplanet::ChunkVertexIndex(static_cast<int32_t>(other->cy),
                static_cast<int32_t>(oj), stride);
            const zplanet::PlanetChunkVertex a = zplanet::PlanetChunkVertexAtGlobal(
                planetCenter, static_cast<int>(faceId), gUm, gVm, total, g_seed);
            const zplanet::PlanetChunkVertex b = zplanet::PlanetChunkVertexAtGlobal(
                planetCenter, static_cast<int>(faceId), gUo, gVo, total, g_seed);
            if (std::memcmp(&a.posAbs, &b.posAbs, sizeof(a.posAbs)) != 0) {
                ++g_seamFails; // 每缘计 1（首个失配顶点即报，诊断口径）
                return;
            }
        }
    };
    // 东邻 (cx+1,cy)：我方缘 (i=stride, j=k) ↔ 邻方缘 (i=0, j=k)——k 沿 v 推进
    checkEdge(findNeighbor(static_cast<int32_t>(cx) + 1, static_cast<int32_t>(cy)),
        res - 1u, 0u, 0u, 0u, 0, 1);
    // 西邻 (cx−1,cy)：我方缘 (i=0, j=k) ↔ 邻方缘 (i=stride, j=k)
    checkEdge(findNeighbor(static_cast<int32_t>(cx) - 1, static_cast<int32_t>(cy)),
        0u, 0u, res - 1u, 0u, 0, 1);
    // 南邻 (cx,cy+1)：我方缘 (i=k, j=stride) ↔ 邻方缘 (i=k, j=0)——k 沿 u 推进
    checkEdge(findNeighbor(static_cast<int32_t>(cx), static_cast<int32_t>(cy) + 1),
        0u, res - 1u, 0u, 0u, 1, 0);
    // 北邻 (cx,cy−1)：我方缘 (i=k, j=0) ↔ 邻方缘 (i=k, j=stride)
    checkEdge(findNeighbor(static_cast<int32_t>(cx), static_cast<int32_t>(cy) - 1),
        0u, 0u, 0u, res - 1u, 1, 0);
}

/// 确保 chunk VB 具备 tier 容量并返回映射指针。策略照搬 B-TECH-04：远档容量启动、
/// 进近档升容重建（销毁+新建）、退档复用不缩容。返回 nullptr = 失败。
void* EnsureChunkVb(ZrD3D12Device* device, PlanetChunkSlot& slot, uint32_t tier)
{
    const uint32_t needBytes = TierVerts(tier) * kVertBytes;
    if (slot.vb.IsValid() && (tier == 0u || slot.vbNearSized)) {
        return slot.vbMapped; // 复用（远档够用或已近档容量——不缩容）
    }
    if (slot.vb.IsValid()) {
        device->Destroy(slot.vb); // 远→近升容重建（旧 VB 只被已完成帧引用，安全）
        slot.vb = ZrBuffer {};
        slot.vbMapped = nullptr;
        slot.vbBytes = 0u;
    }
    ZrBufferDesc desc {};
    desc.size = needBytes;
    desc.usage = ZrBufferUsage::Vertex;
    desc.hostVisible = true;
    slot.vb = device->CreateBuffer(desc);
    if (!slot.vb.IsValid()) {
        return nullptr;
    }
    IZrBuffer* iface = device->GetBuffer(slot.vb);
    slot.vbMapped = (iface != nullptr) ? iface->Map() : nullptr;
    slot.vbNearSized = (tier >= 1u);
    slot.vbBytes = needBytes;
    return slot.vbMapped;
}

// ---- RenderFrame 帧首处理（§8：上传/释放/档位应用只在渲染线程边界）----------------
void FrameBegin(ZrD3D12Device* device)
{
    if (device == nullptr) {
        return;
    }
    const auto t0 = std::chrono::steady_clock::now();
    const uint64_t frameNo = g_target.frameCount; // 本帧号（未完成；freeFrame 语义见下）

    // (a) 释放到期 PENDING_FREE（freeFrame < 本帧号 → 上一帧已画过，安全销毁）。
    //     独占锁内摘 VB + 槽位归 EMPTY；锁外 device->Destroy（§8 锁内不做 GPU）。
    {
        std::vector<ZrBuffer> toFree;
        {
            ::AcquireSRWLockExclusive(&g_lock);
            for (uint32_t s = 0; s < kMaxChunks; ++s) {
                PlanetChunkSlot& slot = g_slots[s];
                if (slot.state == kSlotPendingFree && slot.freeFrame < frameNo) {
                    if (slot.vb.IsValid()) {
                        toFree.push_back(slot.vb);
                    }
                    slot.vb = ZrBuffer {};
                    slot.vbMapped = nullptr;
                    slot.vbBytes = 0u;
                    slot.vbNearSized = false;
                    slot.appliedLod = kLodUnset;
                    slot.pendingVerts.clear();
                    slot.state = kSlotEmpty;
                    --g_occupied;
                }
            }
            ::ReleaseSRWLockExclusive(&g_lock);
        }
        for (ZrBuffer& buf : toFree) {
            device->Destroy(buf);
        }
    }

    // (b) 上传 PENDING_UPLOAD（§7.1：ChunkCreate 生成的顶点字节活到本边界）。
    //     锁内摘参数+顶点字节 → 锁外建/升容 VB + 写入 → 锁内发布 LIVE。
    struct UploadJob {
        uint32_t slot;
        uint32_t tier;
        uint32_t faceId;
        uint32_t cx;
        uint32_t cy;
        uint32_t cellsPerEdge;
        double originAbs[3];
        std::vector<uint8_t> verts;
    };
    {
        std::vector<UploadJob> jobs;
        {
            ::AcquireSRWLockExclusive(&g_lock);
            for (uint32_t s = 0; s < kMaxChunks; ++s) {
                PlanetChunkSlot& slot = g_slots[s];
                if (slot.state != kSlotPendingUpload) {
                    continue;
                }
                UploadJob job;
                job.slot = s;
                job.tier = slot.desiredLod; // 上传前 SetLod 变档 → 直接以期望档上传
                job.faceId = slot.faceId;
                job.cx = slot.cx;
                job.cy = slot.cy;
                job.cellsPerEdge = slot.cellsPerEdge;
                job.originAbs[0] = slot.originAbs[0];
                job.originAbs[1] = slot.originAbs[1];
                job.originAbs[2] = slot.originAbs[2];
                if (slot.genTier == slot.desiredLod) {
                    job.verts = std::move(slot.pendingVerts); // 生成即转 GPU（§7.1）
                }
                slot.pendingVerts.clear();
                jobs.push_back(std::move(job));
            }
            ::ReleaseSRWLockExclusive(&g_lock);
        }
        for (UploadJob& job : jobs) {
            if (job.verts.empty()) {
                GenerateChunkVerts(job.faceId, job.cx, job.cy, job.cellsPerEdge, job.tier,
                    job.originAbs, job.verts);
            }
            // VB 建/升容/写入在锁外（§8）。VB 指针写入只发生在本渲染线程；
            // 主线程 Destroy 在 PENDING_UPLOAD 态只置状态不碰 VB（上传完成前
            // VB 恒无效），故无竞争；并发 Destroy 后的孤儿路径在下方锁内裁决。
            void* mapped = EnsureChunkVb(device, g_slots[job.slot], job.tier);
            if (mapped != nullptr && !job.verts.empty()) {
                std::memcpy(mapped, job.verts.data(), job.verts.size());
            }
            const bool uploadOk = (mapped != nullptr) && !job.verts.empty();
            ::AcquireSRWLockExclusive(&g_lock);
            PlanetChunkSlot& slot = g_slots[job.slot];
            if (slot.state == kSlotPendingUpload && uploadOk) {
                slot.state = kSlotLive;
                slot.appliedLod = job.tier;
                SeamCheckNear(job.faceId, job.cx, job.cy, job.cellsPerEdge, job.tier);
            } else if (slot.state == kSlotPendingUpload) {
                // 上传失败（建缓冲失败等设备级错误）：保持 LIVE 语义但 VB 无效，
                // 绘制侧按 IsValid() 守卫跳过；错误经 g_target.lastError 可观测
                slot.state = kSlotLive;
                slot.appliedLod = kLodUnset;
            }
            // state 已变（并发 Destroy）→ 孤儿路径：新 VB 从未写进槽位——但
            // EnsureChunkVb 可能已把它挂进槽位（见其实现），槽位状态为
            // PENDING_FREE 时该 VB 会在下一帧 (a) 段正常回收，无泄漏。
            ::ReleaseSRWLockExclusive(&g_lock);
            (void)uploadOk;
        }
    }

    // (c) 档位应用（LIVE 且 desiredLod != appliedLod；B-TECH-04：档变重建、
    //     升容重建、退档复用不缩容）。顶点重生成 + VB 重写在锁外。
    {
        std::vector<uint32_t> lodSlots;
        {
            ::AcquireSRWLockExclusive(&g_lock);
            for (uint32_t s = 0; s < kMaxChunks; ++s) {
                PlanetChunkSlot& slot = g_slots[s];
                if (slot.state == kSlotLive && slot.appliedLod != slot.desiredLod) {
                    lodSlots.push_back(s);
                }
            }
            ::ReleaseSRWLockExclusive(&g_lock);
        }
        for (uint32_t s : lodSlots) {
            ::AcquireSRWLockShared(&g_lock);
            PlanetChunkSlot snap = g_slots[s]; // 参数快照（浅拷贝 VB 句柄）
            ::ReleaseSRWLockShared(&g_lock);
            std::vector<uint8_t> verts;
            GenerateChunkVerts(snap.faceId, snap.cx, snap.cy, snap.cellsPerEdge,
                snap.desiredLod, snap.originAbs, verts);
            void* mapped = EnsureChunkVb(device, g_slots[s], snap.desiredLod);
            const bool ok = (mapped != nullptr);
            if (ok) {
                std::memcpy(mapped, verts.data(), verts.size());
            }
            ::AcquireSRWLockExclusive(&g_lock);
            PlanetChunkSlot& slot = g_slots[s];
            if (slot.state == kSlotLive && slot.generation == snap.generation) {
                if (ok) {
                    slot.appliedLod = slot.desiredLod;
                    ++g_lodChangeTotal;
                    SeamCheckNear(slot.faceId, slot.cx, slot.cy, slot.cellsPerEdge,
                        slot.desiredLod);
                } else {
                    slot.appliedLod = kLodUnset; // 失败：下帧重试（desiredLod 不变）
                }
            }
            ::ReleaseSRWLockExclusive(&g_lock);
        }
    }

    // (d) 绘制快照（共享锁内完成地平线剔除——决策拷贝，§8）：
    //     LIVE + PENDING_FREE（先画后删）且 VB 有效。相机相对模型平移在绘制侧
    //     由 float(anchor − cam) 承载（§3.2），锚点在这里随快照带走。
    {
        ::AcquireSRWLockShared(&g_lock);
        g_frame.drawCount = 0u;
        g_frame.culledCount = 0u;
        g_frame.worldSet = g_worldSet;
        g_frame.camAbs[0] = g_camOrigin[0];
        g_frame.camAbs[1] = g_camOrigin[1];
        g_frame.camAbs[2] = g_camOrigin[2];
        g_frame.center[0] = g_center[0];
        g_frame.center[1] = g_center[1];
        g_frame.center[2] = g_center[2];
        g_frame.cameraUsable = g_scene.cameraSet;
        const double relX = g_camOrigin[0] - g_center[0];
        const double relY = g_camOrigin[1] - g_center[1];
        const double relZ = g_camOrigin[2] - g_center[2];
        const double camLen = std::sqrt(relX * relX + relY * relY + relZ * relZ);
        // 地平线曲率剔除（B-TECH-04.md:76-77 移植）：chunk 法线与相机-心方向夹角
        // > 地平线角 + chunk 半角 + 裙边/噪声裕度 → 不可见。相机深入行星内部/与心
        // 重合时跳过剔除（绘制侧深度兜底；合法编排不会进入该状态）。
        const bool cullUsable = g_worldSet && camLen > zplanet::kPlanetRadiusM * 0.5;
        double horizonAng = 0.0;
        if (cullUsable) {
            const double altM = camLen - zplanet::kPlanetRadiusM;
            horizonAng = std::acos(
                std::min(1.0, zplanet::kPlanetRadiusM / (zplanet::kPlanetRadiusM + altM)));
        }
        for (uint32_t s = 0; s < kMaxChunks; ++s) {
            const PlanetChunkSlot& slot = g_slots[s];
            if (slot.state != kSlotLive && slot.state != kSlotPendingFree) {
                continue;
            }
            if (!slot.vb.IsValid() || slot.appliedLod == kLodUnset) {
                continue; // 上传失败等守卫（不画无效 VB）
            }
            if (cullUsable) {
                const double cNvX = static_cast<double>(slot.originAbs[0]) - g_center[0];
                const double cNvY = static_cast<double>(slot.originAbs[1]) - g_center[1];
                const double cNvZ = static_cast<double>(slot.originAbs[2]) - g_center[2];
                const double cLen = std::sqrt(cNvX * cNvX + cNvY * cNvY + cNvZ * cNvZ);
                const double cosA = (cLen > 0.0)
                    ? ((cNvX * relX + cNvY * relY + cNvZ * relZ) / (cLen * camLen))
                    : 1.0;
                // 半 chunk 角（本 chunk 自己的 cellsPerEdge）+ 裙边/噪声裕度
                const double chunkAngRad
                    = (kPi / 2.0) / static_cast<double>(slot.cellsPerEdge) * 0.5 + 0.05;
                if (cosA < std::cos(horizonAng + chunkAngRad)) {
                    ++g_frame.culledCount;
                    continue;
                }
            }
            if (g_frame.cameraUsable && g_frame.drawCount < kPlanetMaxDrawItems) {
                PlanetFrameDraw& d = g_frame.draws[g_frame.drawCount];
                d.vb = slot.vb;
                d.tier = slot.appliedLod;
                d.originAbs[0] = slot.originAbs[0];
                d.originAbs[1] = slot.originAbs[1];
                d.originAbs[2] = slot.originAbs[2];
                ++g_frame.drawCount;
            }
            // 可见但超 CB 池/无相机：计入剔除侧（不绘制；B-TECH-04 截断口径）
            else if (g_frame.cameraUsable) {
                ++g_frame.culledCount;
            }
        }
        ::ReleaseSRWLockShared(&g_lock);
        g_drawnLast = g_frame.drawCount;
        g_culledLast = g_frame.culledCount;
    }

    const std::chrono::duration<double, std::milli> elapsed =
        std::chrono::steady_clock::now() - t0;
    g_lastFrameMs = elapsed.count();
    g_frameMsSum += g_lastFrameMs;
    ++g_frameMsCount;
}

// ---- 共享档位索引模式表（grid + 四缘裙边；每档全 chunk 复用同一 IB，B-TECH-04）----
std::vector<uint32_t> BuildTierIndices(uint32_t res)
{
    std::vector<uint32_t> idx;
    const uint32_t skirtBase = res * res;
    idx.reserve(static_cast<size_t>((res - 1u) * (res - 1u) * 6u)
        + static_cast<size_t>(4u * (res - 1u) * 6u));
    for (uint32_t j = 0; j + 1u < res; ++j) {
        for (uint32_t i = 0; i + 1u < res; ++i) {
            const uint32_t v00 = j * res + i;
            const uint32_t v10 = v00 + 1u;
            const uint32_t v01 = v00 + res;
            const uint32_t v11 = v01 + 1u;
            idx.push_back(v00);
            idx.push_back(v10);
            idx.push_back(v11);
            idx.push_back(v00);
            idx.push_back(v11);
            idx.push_back(v01);
        }
    }
    // 裙边四缘：e=0 北(j=0) / 1 南(j=res−1) / 2 西(i=0) / 3 东(i=res−1)；
    // 顶点 k 沿缘序——与 GenerateChunkVerts 的裙边发射序一致
    for (uint32_t e = 0; e < 4u; ++e) {
        const uint32_t sBase = skirtBase + e * res;
        for (uint32_t k = 0; k + 1u < res; ++k) {
            uint32_t g0 = 0u;
            uint32_t g1 = 0u;
            if (e == 0u) {
                g0 = k;
                g1 = k + 1u;
            } else if (e == 1u) {
                g0 = (res - 1u) * res + k;
                g1 = g0 + 1u;
            } else if (e == 2u) {
                g0 = k * res;
                g1 = g0 + res;
            } else {
                g0 = k * res + (res - 1u);
                g1 = g0 + res;
            }
            const uint32_t s0 = sBase + k;
            const uint32_t s1 = s0 + 1u;
            idx.push_back(g0);
            idx.push_back(s0);
            idx.push_back(s1);
            idx.push_back(g0);
            idx.push_back(s1);
            idx.push_back(g1);
        }
    }
    return idx;
}

/// 行星逐绘常量池扩容（EnsureConstantBuffers 同构；每项 256B，持久 Map）
bool EnsurePlanetConstantBuffers(uint32_t needed)
{
    if (needed > kPlanetMaxDrawItems || g_target.device == nullptr) {
        return false;
    }
    while (g_target.planetCbCount < needed) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u;
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        ZrBuffer buffer = g_target.device->CreateBuffer(cbDesc);
        if (!buffer.IsValid()) {
            return false;
        }
        IZrBuffer* iface = g_target.device->GetBuffer(buffer);
        void* mapped = (iface != nullptr) ? iface->Map() : nullptr;
        if (mapped == nullptr) {
            g_target.device->Destroy(buffer);
            return false;
        }
        g_target.planetCb[g_target.planetCbCount] = buffer;
        g_target.planetCbMapped[g_target.planetCbCount] = mapped;
        ++g_target.planetCbCount;
    }
    return true;
}

// ---- 桥级兜底（§7.3 第 4 层）：目标/设备释放时随桥清空全部行星 GPU 资源 ------------
void ReleaseGpuOnTeardown(ZrD3D12Device* device)
{
    ::AcquireSRWLockExclusive(&g_lock);
    uint32_t cleared = 0u;
    for (uint32_t s = 0; s < kMaxChunks; ++s) {
        PlanetChunkSlot& slot = g_slots[s];
        if (slot.state == kSlotEmpty) {
            continue;
        }
        slot.generation += 1u; // 句柄全失效（与 ClearAll 同语义）
        if (slot.state == kSlotPendingFree) {
            // 已在 ChunkDestroy 记过销毁账——此处只清场，不重复记账（账目恒等式）
        } else {
            ++cleared; // PENDING_UPLOAD / LIVE → 新增销毁记账
        }
        if (device != nullptr && slot.vb.IsValid()) {
            device->Destroy(slot.vb);
        }
        slot.vb = ZrBuffer {};
        slot.vbMapped = nullptr;
        slot.vbBytes = 0u;
        slot.vbNearSized = false;
        slot.appliedLod = kLodUnset;
        slot.pendingVerts.clear();
        slot.state = kSlotEmpty;
    }
    g_occupied = 0u;
    // 销毁记账保恒等（§7.3②——createTotal − destroyTotal == liveChunks 全程成立）
    g_destroyTotal += cleared;
    g_frame.drawCount = 0u;
    g_frame.culledCount = 0u;
    ::ReleaseSRWLockExclusive(&g_lock);
}

} // namespace planet_detail


/// 等本队列自己的 fence 到 value（TASK-B-UNITY-04）。顺手记一笔等待耗时：
/// "CPU 等待下降了"这条判据的现场数字就来自这里。
void WaitForSync(uint64_t value)
{
    if (g_target.syncFence == nullptr || g_target.syncEvent == nullptr) {
        return;
    }
    const auto start = std::chrono::steady_clock::now();
    if (g_target.syncFence->GetCompletedValue() < value) {
        g_target.syncFence->SetEventOnCompletion(value, g_target.syncEvent);
        WaitForSingleObject(g_target.syncEvent, INFINITE);
    }
    const std::chrono::duration<double, std::milli> elapsed =
        std::chrono::steady_clock::now() - start;
    g_target.syncLastMs = elapsed.count();
    g_target.syncTotalMs += g_target.syncLastMs;
    ++g_target.syncFrames;
    if (g_target.syncLastMs >= kBlockedThresholdMs) {
        ++g_target.syncBlocked;
    }
}

/// 当前在途帧数（已 Signal 但 GPU 还没做完的帧；0 = 队列已排空）
uint32_t FramesInFlight()
{
    if (g_target.syncFence == nullptr) {
        return 0u;
    }
    const uint64_t completed = g_target.syncFence->GetCompletedValue();
    if (completed >= g_target.syncValue) {
        return 0u;
    }
    const uint64_t inFlight = g_target.syncValue - completed;
    return (inFlight > kTargetSlots) ? kTargetSlots : static_cast<uint32_t>(inFlight);
}

/// 释放目标与整条渲染链（保留设备包装：尺寸变化后可直接重建，无需重新接入）
void DestroyTargetObjects()
{
    if (g_target.device == nullptr) {
        // 无设备：行星槽位 VB 恒无效（从未上传），仅需清表态（§7.3 第 4 层兜底）
        planet_detail::ReleaseGpuOnTeardown(nullptr);
        g_target = TargetState {};
        return;
    }
    ZrD3D12Device* device = g_target.device;
    device->WaitIdle();
    // 行星 chunk 表 GPU 侧随桥释放（§7.3 第 4 层）——必须在设备包装仍有效时
    planet_detail::ReleaseGpuOnTeardown(device);
    if (g_target.fullReadback.IsValid()) {
        device->Destroy(g_target.fullReadback);
        g_target.fullReadbackMapped = nullptr;
    }
    if (g_target.readback.IsValid()) {
        device->Destroy(g_target.readback);
    }
    if (g_target.psoScene.IsValid()) {
        device->Destroy(g_target.psoScene);
    }
    if (g_target.psoPlanet.IsValid()) {
        device->Destroy(g_target.psoPlanet);
    }
    if (g_target.planetPs.IsValid()) {
        device->Destroy(g_target.planetPs);
    }
    if (g_target.planetVs.IsValid()) {
        device->Destroy(g_target.planetVs);
    }
    if (g_target.planetIbNear.IsValid()) {
        device->Destroy(g_target.planetIbNear);
    }
    if (g_target.planetIbFar.IsValid()) {
        device->Destroy(g_target.planetIbFar);
    }
    // 行星逐绘常量池按实际增长量释放
    for (uint32_t i = 0; i < g_target.planetCbCount && i < kPlanetMaxDrawItems; ++i) {
        if (g_target.planetCb[i].IsValid()) {
            device->Destroy(g_target.planetCb[i]);
        }
        g_target.planetCbMapped[i] = nullptr;
    }
    if (g_target.scenePs.IsValid()) {
        device->Destroy(g_target.scenePs);
    }
    if (g_target.sceneVs.IsValid()) {
        device->Destroy(g_target.sceneVs);
    }
    if (g_target.pso.IsValid()) {
        device->Destroy(g_target.pso);
    }
    if (g_target.ps.IsValid()) {
        device->Destroy(g_target.ps);
    }
    if (g_target.vs.IsValid()) {
        device->Destroy(g_target.vs);
    }
    // 常量缓冲池按实际增长量释放（不是 kMaxSceneDraws：绝大多数场景只用到几十个）
    for (uint32_t i = 0; i < g_target.cbCount && i < kMaxSceneDraws; ++i) {
        if (g_target.cb[i].IsValid()) {
            device->Destroy(g_target.cb[i]);
        }
        g_target.cbMapped[i] = nullptr;
    }
    if (g_target.ib.IsValid()) {
        device->Destroy(g_target.ib);
    }
    if (g_target.vb.IsValid()) {
        device->Destroy(g_target.vb);
    }
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.depthDsv[slot].IsValid()) {
            device->Destroy(g_target.depthDsv[slot]);
        }
        if (g_target.colorRtv[slot].IsValid()) {
            device->Destroy(g_target.colorRtv[slot]);
        }
    }
    if (g_target.cmd.IsValid()) {
        device->Destroy(g_target.cmd);
    }
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.depth[slot].IsValid()) {
            device->Destroy(g_target.depth[slot]);
        }
        if (g_target.color[slot].IsValid()) {
            device->Destroy(g_target.color[slot]);
        }
    }
    // NT 句柄与资源生命周期独立：资源已释放，句柄本身仍须关掉（否则句柄泄漏）
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.sharedHandle[slot] != nullptr) {
            CloseHandle(g_target.sharedHandle[slot]);
        }
    }
    // fence 与等待事件也要跟着目标一起放（像素级泄漏计数器会看到它们）
    if (g_target.syncFence != nullptr) {
        g_target.syncFence->Release();
    }
    if (g_target.syncEvent != nullptr) {
        CloseHandle(g_target.syncEvent);
    }
    device->WaitIdle();
    g_target = TargetState {};
    g_target.device = device;
}

/// 把逐绘常量缓冲池扩到 needed 个（只增长、不回收；每帧调用只做一次比较）。
/// 为什么要池：每个绘制项要各自一份 96B/176B 常量，而 SetGraphicsConstantBuffer
/// 只接受"整块缓冲"，因此一项一缓冲；按需建一次后写内容，避免每帧建缓冲。
bool EnsureConstantBuffers(uint32_t needed)
{
    if (needed > kMaxSceneDraws || g_target.device == nullptr) {
        return false;
    }
    while (g_target.cbCount < needed) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u; // 根 CBV 对齐裕量
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        ZrBuffer buffer = g_target.device->CreateBuffer(cbDesc);
        if (!buffer.IsValid()) {
            return false;
        }
        IZrBuffer* iface = g_target.device->GetBuffer(buffer);
        void* mapped = (iface != nullptr) ? iface->Map() : nullptr;
        if (mapped == nullptr) {
            g_target.device->Destroy(buffer);
            return false;
        }
        g_target.cb[g_target.cbCount] = buffer;
        g_target.cbMapped[g_target.cbCount] = mapped;
        ++g_target.cbCount;
    }
    return true;
}

/// 建 VB/IB/CB/PSO/命令链（目标与深度已就位）。失败返回 false。
bool BuildRenderObjects(ZrD3D12Device* device)
{
    ZrBufferDesc vbDesc {};
    vbDesc.size = sizeof(kCubeVertices);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    g_target.vb = device->CreateBuffer(vbDesc, kCubeVertices);
    ZrBufferDesc ibDesc {};
    ibDesc.size = sizeof(kCubeIndices);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    g_target.ib = device->CreateBuffer(ibDesc, kCubeIndices);
    if (!g_target.vb.IsValid() || !g_target.ib.IsValid()) {
        return false;
    }

    // 逐绘常量缓冲池：先长到 legacy 三 cube 的量，场景路径再按需继续长
    if (!EnsureConstantBuffers(kDrawItems)) {
        return false;
    }

    g_target.vs = device->CompileHlsl(
        ZrShaderStage::Vertex, kFirstDrawHlsl, sizeof(kFirstDrawHlsl) - 1u, "VsMain");
    g_target.ps = device->CompileHlsl(
        ZrShaderStage::Pixel, kFirstDrawHlsl, sizeof(kFirstDrawHlsl) - 1u, "PsMain");
    if (!g_target.vs.IsValid() || !g_target.ps.IsValid()) {
        return false;
    }

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = g_target.vs;
    psoDesc.pixelShader = g_target.ps;
    psoDesc.rtvFormat = ZrFormat::BGRA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    // 剔除 None：与 --draw 一致的取舍（资产绕序统一归资产管线任务）
    psoDesc.cullMode = ZrCullMode::None;
    g_target.pso = device->CreatePipelineState(psoDesc);
    if (!g_target.pso.IsValid()) {
        return false;
    }

    // 场景提交变体：同一顶点布局/深度/剔除，只是常量布局多两块（world + albedo）。
    // 单独一套 shader/PSO 而不改 first_draw.hlsl —— 后者是 --draw 与 B-UNITY-02
    // 的基线，动它会连带改 --draw 的常量布局与像素校验。
    g_target.sceneVs = device->CompileHlsl(
        ZrShaderStage::Vertex, kSceneHlsl, sizeof(kSceneHlsl) - 1u, "VsMain");
    g_target.scenePs = device->CompileHlsl(
        ZrShaderStage::Pixel, kSceneHlsl, sizeof(kSceneHlsl) - 1u, "PsMain");
    if (!g_target.sceneVs.IsValid() || !g_target.scenePs.IsValid()) {
        return false;
    }
    ZrGraphicsPsoDesc scenePsoDesc = psoDesc;
    scenePsoDesc.vertexShader = g_target.sceneVs;
    scenePsoDesc.pixelShader = g_target.scenePs;
    g_target.psoScene = device->CreatePipelineState(scenePsoDesc);
    if (!g_target.psoScene.IsValid()) {
        return false;
    }

    // ---- 行星通道变体（TASK-B-PLANET-04）：planet2.hlsl 内嵌副本（mode 0 地形
    // chunk；mode 1 天穹保留给窗口验证程序）——同一顶点布局/深度/剔除，常量
    // 布局 208B（Planet2DrawConstants，static_assert 见下）。Cull None：chunk
    // 裙边绕序混用（B-TECH-04 --planet2 同策）。
    g_target.planetVs = device->CompileHlsl(
        ZrShaderStage::Vertex, kPlanet2Hlsl, sizeof(kPlanet2Hlsl) - 1u, "VsMain");
    g_target.planetPs = device->CompileHlsl(
        ZrShaderStage::Pixel, kPlanet2Hlsl, sizeof(kPlanet2Hlsl) - 1u, "PsMain");
    if (!g_target.planetVs.IsValid() || !g_target.planetPs.IsValid()) {
        return false;
    }
    ZrGraphicsPsoDesc planetPsoDesc = psoDesc;
    planetPsoDesc.vertexShader = g_target.planetVs;
    planetPsoDesc.pixelShader = g_target.planetPs;
    planetPsoDesc.cullMode = ZrCullMode::None;
    g_target.psoPlanet = device->CreatePipelineState(planetPsoDesc);
    if (!g_target.psoPlanet.IsValid()) {
        return false;
    }

    // 共享档位索引（grid + 四缘裙边）：远档/近档各一，全 chunk 复用（B-TECH-04）
    {
        const std::vector<uint32_t> farIdx = planet_detail::BuildTierIndices(kPlanetResFar);
        const std::vector<uint32_t> nearIdx = planet_detail::BuildTierIndices(kPlanetResNear);
        if (farIdx.size() != kPlanetFarIndices || nearIdx.size() != kPlanetNearIndices) {
            return false;
        }
        ZrBufferDesc farDesc {};
        farDesc.size = static_cast<uint64_t>(farIdx.size()) * 4u;
        farDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        g_target.planetIbFar = device->CreateBuffer(farDesc, farIdx.data());
        ZrBufferDesc nearDesc {};
        nearDesc.size = static_cast<uint64_t>(nearIdx.size()) * 4u;
        nearDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
        g_target.planetIbNear = device->CreateBuffer(nearDesc, nearIdx.data());
        if (!g_target.planetIbFar.IsValid() || !g_target.planetIbNear.IsValid()) {
            return false;
        }
    }

    g_target.cmd = device->CreateCommandList(ZrQueueType::Graphics);
    g_target.queue = device->GetQueue(ZrQueueType::Graphics);
    if (!g_target.cmd.IsValid() || g_target.queue == nullptr) {
        return false;
    }

    // 帧同步原语（TASK-B-UNITY-04）：本队列自己的 fence + 可复用的事件对象。
    // 用独立 fence 而不是 IZrDevice::WaitIdle，因为后者是"排空到此刻"——
    // 那样每帧都得等本帧干完，正是本任务要消掉的串行化。
    if (g_target.syncFence == nullptr) {
        ID3D12Device* nativeDevice = device->D3D12Device();
        if (nativeDevice == nullptr
            || FAILED(nativeDevice->CreateFence(0u, D3D12_FENCE_FLAG_NONE,
                IID_PPV_ARGS(&g_target.syncFence)))) {
            return false;
        }
        g_target.syncEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (g_target.syncEvent == nullptr) {
            return false;
        }
    }

    // 中心 1x1 回读缓冲（证明纹理里真有画面，而非"Blit 已调用"）。
    // 尺寸与行距都取 1x1 BGRA8 的**实际脚印**：D3D12 的 RowPitch 是 256 对齐后的
    // 值，写死 width*bpp（或任何常数）在非对齐宽度上会逐行错位——本任务 1/8 那件事
    // 的根因就在这一类假设上，所以这里一次也不假设。
    {
        ID3D12Device* nativeDevice = device->D3D12Device();
        if (nativeDevice == nullptr) {
            return false;
        }
        D3D12_RESOURCE_DESC readbackTexDesc {};
        readbackTexDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        readbackTexDesc.Width = 1u;
        readbackTexDesc.Height = 1u;
        readbackTexDesc.DepthOrArraySize = 1u;
        readbackTexDesc.MipLevels = 1u;
        readbackTexDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        readbackTexDesc.SampleDesc.Count = 1u;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
        UINT64 rowBytes = 0u;
        UINT numRows = 0u;
        UINT64 totalBytes = 0u;
        nativeDevice->GetCopyableFootprints(&readbackTexDesc, 0u, 1u, 0u, &placed, &numRows,
            &rowBytes, &totalBytes);
        if (totalBytes == 0u || placed.Footprint.RowPitch == 0u) {
            return false;
        }
        g_target.readbackPitch = placed.Footprint.RowPitch;
        g_target.readbackBytes = totalBytes;
    }
    ZrBufferDesc rbDesc {};
    rbDesc.size = g_target.readbackBytes;
    rbDesc.readback = true;
    g_target.readback = device->CreateBuffer(rbDesc);
    if (!g_target.readback.IsValid()) {
        return false;
    }
    IZrBuffer* rbIfc = device->GetBuffer(g_target.readback);
    g_target.readbackMapped = (rbIfc != nullptr) ? rbIfc->Map() : nullptr;
    return g_target.readbackMapped != nullptr;
}

} // namespace

// =============================================================================
// TASK-B-PLANET-04：行星通道公共 API（zplugin 六导出 + 相机原点存储的薄转发落点）
// 语义契约逐条对齐 zplugin.h 行星声明块组 1/2/3 的注释与设计稿 §14——那里是 ABI
// 权威；本层只实现，不重定义协议。
// =============================================================================

namespace planet {

int SetWorldContext(uint32_t seed, const double centerAbs[3])
{
    if (centerAbs == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    planet_detail::g_seed = seed;
    planet_detail::g_center[0] = centerAbs[0];
    planet_detail::g_center[1] = centerAbs[1];
    planet_detail::g_center[2] = centerAbs[2];
    planet_detail::g_worldSet = true;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

void SetCameraOrigin(double x, double y, double z)
{
    // B-PLANET-05 语义原样迁移（zplugin.h 组 1 注释）：渲染线程单线程约定下
    // 无锁（与既有 g_rhiBackend/网格句柄表同口径）；RenderFrame 消费方同线程。
    planet_detail::g_camOrigin[0] = x;
    planet_detail::g_camOrigin[1] = y;
    planet_detail::g_camOrigin[2] = z;
}

void GetCameraOrigin(double* outOrigin)
{
    outOrigin[0] = planet_detail::g_camOrigin[0];
    outOrigin[1] = planet_detail::g_camOrigin[1];
    outOrigin[2] = planet_detail::g_camOrigin[2];
}

int ChunkCreate(const ZrPlanetChunkDesc& desc, uint64_t* outHandle)
{
    if (outHandle == nullptr) {
        return ZR_PLANET_ERR_NULL; // zplugin 已查，防御保留
    }
    {
        ::AcquireSRWLockShared(&planet_detail::g_lock);
        const bool worldSet = planet_detail::g_worldSet;
        ::ReleaseSRWLockShared(&planet_detail::g_lock);
        if (!worldSet) {
            return ZR_PLANET_ERR_STATE; // §4.5：行星上下文未设置
        }
    }
    // 顶点生成在锁外（§8 表：校验+占槽独占；顶点生成锁外，完成后独占发布）。
    // FULL 检查在发布段（生成成本在 FULL 拒绝时白付——FULL 属异常路径，换取
    // 锁内零重活）。
    std::vector<uint8_t> verts;
    const uint32_t vertCount = planet_detail::GenerateChunkVerts(desc.faceId, desc.cx,
        desc.cy, desc.cellsPerEdge, desc.lodLevel, desc.originAbs, verts);
    if (vertCount == 0u) {
        return ZR_PLANET_ERR_RANGE; // 防御（zplugin 已校验，理论不可达）
    }
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    if (!planet_detail::g_worldSet) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_STATE;
    }
    if (planet_detail::g_occupied >= planet_detail::kMaxChunks) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_FULL; // §4.5：liveChunks + pending ≥ 8192
    }
    uint32_t slotIdx = planet_detail::kMaxChunks;
    for (uint32_t s = 0; s < planet_detail::kMaxChunks; ++s) {
        if (planet_detail::g_slots[s].state == planet_detail::kSlotEmpty) {
            slotIdx = s;
            break;
        }
    }
    if (slotIdx == planet_detail::kMaxChunks) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_FULL; // 占用计数与扫描失步的防御（理论不可达）
    }
    planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[slotIdx];
    if (slot.generation == 0u) {
        slot.generation = 1u; // 槽位首次启用（mesh 表同协议）
    }
    slot.state = planet_detail::kSlotPendingUpload;
    slot.faceId = desc.faceId;
    slot.cx = desc.cx;
    slot.cy = desc.cy;
    slot.cellsPerEdge = desc.cellsPerEdge;
    slot.desiredLod = desc.lodLevel;
    slot.genTier = desc.lodLevel;
    slot.appliedLod = planet_detail::kLodUnset;
    slot.neighborMask = desc.neighborMask; // v1 校验+记录（设计稿 §5.2 方案 A）
    slot.originAbs[0] = desc.originAbs[0];
    slot.originAbs[1] = desc.originAbs[1];
    slot.originAbs[2] = desc.originAbs[2];
    slot.freeFrame = 0u;
    slot.vb = ZrBuffer {};
    slot.vbMapped = nullptr;
    slot.vbBytes = 0u;
    slot.vbNearSized = false;
    slot.pendingVerts = std::move(verts);
    ++planet_detail::g_occupied;
    ++planet_detail::g_createTotal;
    *outHandle = (static_cast<uint64_t>(slot.generation) << 32)
        | static_cast<uint64_t>(slotIdx);
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

int ChunkDestroy(uint64_t handle)
{
    const uint32_t slotIdx = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slotIdx >= planet_detail::kMaxChunks || generation == 0u) {
        return ZR_PLANET_ERR_HANDLE;
    }
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[slotIdx];
    if (slot.state == planet_detail::kSlotEmpty || slot.generation != generation) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_HANDLE; // 代际防误用（§7.3 第 1 层）
    }
    // 句柄立即失效（代际+1，独占锁内——§4.4 语义 1）；VB 延迟一帧（语义 2），
    // freeFrame = 本帧号：本帧 RenderFrame 仍绘制（先画后删，语义 3），下一帧释放。
    slot.generation += 1u;
    slot.state = planet_detail::kSlotPendingFree;
    slot.freeFrame = g_target.frameCount;
    ++planet_detail::g_destroyTotal;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

int ChunkSetOrigin(uint64_t handle, double x, double y, double z)
{
    const uint32_t slotIdx = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slotIdx >= planet_detail::kMaxChunks || generation == 0u) {
        return ZR_PLANET_ERR_HANDLE;
    }
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[slotIdx];
    if (slot.state == planet_detail::kSlotEmpty || slot.generation != generation) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_HANDLE;
    }
    // 顶点不重算（§4.3）：局部坐标相对锚点不变；锚点平移而行星中心未同步平移
    // = 托管侧 bug（地形视觉错位），原生不校验、文档写明。
    slot.originAbs[0] = x;
    slot.originAbs[1] = y;
    slot.originAbs[2] = z;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

int SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask)
{
    const uint32_t slotIdx = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slotIdx >= planet_detail::kMaxChunks || generation == 0u) {
        return ZR_PLANET_ERR_HANDLE;
    }
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[slotIdx];
    if (slot.state == planet_detail::kSlotEmpty || slot.generation != generation) {
        ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
        return ZR_PLANET_ERR_HANDLE;
    }
    // 期望档位记录（下一 RenderFrame 应用——§5.2/D6；同帧多次最后生效即此处
    // 直接覆盖）；neighborMask v1 校验+记录，不改几何。
    slot.desiredLod = lodLevel;
    slot.neighborMask = neighborMask;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

int GetStats(ZrPlanetStats* out)
{
    if (out == nullptr) {
        return ZR_PLANET_ERR_NULL;
    }
    ::AcquireSRWLockShared(&planet_detail::g_lock);
    uint32_t live = 0u;
    uint32_t pendingUp = 0u;
    uint32_t pendingFree = 0u;
    uint32_t pendingLod = 0u;
    uint64_t vertexBytes = 0u;
    for (uint32_t s = 0; s < planet_detail::kMaxChunks; ++s) {
        const planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[s];
        if (slot.state == planet_detail::kSlotEmpty) {
            continue;
        }
        vertexBytes += slot.vbBytes;
        if (slot.state == planet_detail::kSlotPendingUpload) {
            ++pendingUp;
            ++live;
            if (slot.desiredLod != slot.genTier) {
                ++pendingLod; // 上传前变档：上传边界以期望档生效
            }
        } else if (slot.state == planet_detail::kSlotLive) {
            ++live;
            if (slot.appliedLod != slot.desiredLod) {
                ++pendingLod;
            }
        } else { // kSlotPendingFree（句柄已失效——不计 live）
            ++pendingFree;
        }
    }
    out->structSize = static_cast<uint32_t>(sizeof(ZrPlanetStats));
    out->liveChunks = live;
    out->pendingUploads = pendingUp;
    out->pendingFrees = pendingFree;
    out->pendingLod = pendingLod;
    out->drawnLastFrame = planet_detail::g_drawnLast;
    out->culledLastFrame = planet_detail::g_culledLast;
    out->seamCheckFails = planet_detail::g_seamFails;
    out->createTotal = planet_detail::g_createTotal;
    out->destroyTotal = planet_detail::g_destroyTotal;
    out->lodChangeTotal = planet_detail::g_lodChangeTotal;
    out->vertexBytes = vertexBytes;
    out->lastFrameMs = planet_detail::g_lastFrameMs;
    out->avgFrameMs = (planet_detail::g_frameMsCount > 0u)
        ? (planet_detail::g_frameMsSum / static_cast<double>(planet_detail::g_frameMsCount))
        : 0.0;
    ::ReleaseSRWLockShared(&planet_detail::g_lock);
    return ZR_PLANET_OK;
}

void ClearAll(void)
{
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    uint32_t cleared = 0u;
    for (uint32_t s = 0; s < planet_detail::kMaxChunks; ++s) {
        planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[s];
        if (slot.state == planet_detail::kSlotEmpty) {
            continue;
        }
        slot.generation += 1u; // 句柄全失效（§7.3 第 3 层）
        if (slot.state == planet_detail::kSlotPendingFree) {
            continue; // 已按销毁记账——VB 照常到期释放，不重复记账
        }
        ++cleared;
        // 统一延迟语义（与 ChunkDestroy 同构）：全部进 PENDING_FREE 排队——
        // 渲染线程下一 RenderFrame 释放（VB 无效的槽位释放时直接丢弃）。
        slot.state = planet_detail::kSlotPendingFree;
        slot.freeFrame = g_target.frameCount;
    }
    // 销毁记账保恒等（§7.3②）：ClearAll 等价批量 Destroy
    planet_detail::g_destroyTotal += cleared;
    planet_detail::g_frame.drawCount = 0u;
    planet_detail::g_frame.culledCount = 0u;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
}

void TestResetForUnitTests(void)
{
    // 仅 doctest 使用（进程级单例复位，ZR_Upscaler 测试复位同策）。
    // 前提：调用时无存活 GPU 目标（doctest 从不建目标——VB 恒无效，无泄漏路径）。
    ::AcquireSRWLockExclusive(&planet_detail::g_lock);
    for (uint32_t s = 0; s < planet_detail::kMaxChunks; ++s) {
        planet_detail::PlanetChunkSlot& slot = planet_detail::g_slots[s];
        slot.state = planet_detail::kSlotEmpty;
        slot.generation = 0u;
        slot.vb = ZrBuffer {};
        slot.vbMapped = nullptr;
        slot.vbBytes = 0u;
        slot.vbNearSized = false;
        slot.appliedLod = planet_detail::kLodUnset;
        slot.pendingVerts.clear();
    }
    planet_detail::g_occupied = 0u;
    planet_detail::g_createTotal = 0u;
    planet_detail::g_destroyTotal = 0u;
    planet_detail::g_lodChangeTotal = 0u;
    planet_detail::g_drawnLast = 0u;
    planet_detail::g_culledLast = 0u;
    planet_detail::g_seamFails = 0u;
    planet_detail::g_lastFrameMs = 0.0;
    planet_detail::g_frameMsSum = 0.0;
    planet_detail::g_frameMsCount = 0u;
    planet_detail::g_worldSet = false;
    planet_detail::g_seed = 0u;
    planet_detail::g_center[0] = 0.0;
    planet_detail::g_center[1] = 0.0;
    planet_detail::g_center[2] = 0.0;
    planet_detail::g_frame.drawCount = 0u;
    planet_detail::g_frame.culledCount = 0u;
    ::ReleaseSRWLockExclusive(&planet_detail::g_lock);
}

} // namespace planet

int AttachDeviceFromTexture(void* d3d12Texture)
{
    if (d3d12Texture == nullptr) {
        return kErrNull;
    }
    if (g_target.device != nullptr) {
        return kOk; // 幂等：已接入同一张宿主设备的包装就复用（ReleaseTarget 保留包装）
    }
    ID3D12Resource* resource = static_cast<ID3D12Resource*>(d3d12Texture);
    ID3D12Device* device = nullptr;
    if (FAILED(resource->GetDevice(IID_PPV_ARGS(&device))) || device == nullptr) {
        return kErrDevice;
    }
    // GetDevice 已 AddRef；CreateFrom 内部再持有自己的引用，故这里立即释放
    ZrD3D12Device* wrapper = ZrD3D12CreateDeviceFrom(device);
    device->Release();
    if (wrapper == nullptr) {
        return kErrDevice;
    }
    g_target = TargetState {};
    g_target.device = wrapper;
    return kOk;
}

int CreateTarget(uint32_t width, uint32_t height)
{
    if (width == 0u || height == 0u || width > 16384u || height > 16384u) {
        return kErrArgs;
    }
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (g_target.color[0].IsValid()) {
        if (g_target.width == width && g_target.height == height) {
            return kOk; // 同尺寸：复用现有目标（每帧重建会泄漏 —— 明令禁止）
        }
        return kErrState; // 尺寸变化：调用方须先 ReleaseTarget 再重建
    }

    ZrImageDesc colorDesc {};
    colorDesc.format = ZrFormat::BGRA8Unorm;
    colorDesc.width = width;
    colorDesc.height = height;
    // TransferSrc：中心像素回读要走 COPY_SOURCE；Sampled：语义上声明"会被采样"
    colorDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc
        | ZrImageUsage::Sampled;
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        void* shared = nullptr;
        g_target.colorDesc = colorDesc;
        g_target.color[slot] = g_target.device->CreateSharedImage(colorDesc, &shared);
        if (!g_target.color[slot].IsValid() || shared == nullptr) {
            g_target = TargetState {}; // device 包装也已被释放
            return kErrDevice;
        }
        g_target.sharedHandle[slot] = static_cast<HANDLE>(shared);
    }

    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = width;
    depthDesc.height = height;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        g_target.depthDesc = depthDesc;
        g_target.depth[slot] = g_target.device->CreateImage(depthDesc);

        ZrImageViewDesc rtvDesc {};
        rtvDesc.image = g_target.color[slot];
        rtvDesc.viewType = ZrImageViewType::Rtv;
        g_target.colorRtv[slot] = g_target.device->CreateImageView(rtvDesc);

        ZrImageViewDesc dsvDesc {};
        dsvDesc.image = g_target.depth[slot];
        dsvDesc.viewType = ZrImageViewType::Dsv;
        g_target.depthDsv[slot] = g_target.device->CreateImageView(dsvDesc);
    }

    g_target.width = width;
    g_target.height = height;
    g_target.readableSlot = 0u;

    bool viewsOk = true;
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (!g_target.depth[slot].IsValid() || !g_target.colorRtv[slot].IsValid()
            || !g_target.depthDsv[slot].IsValid()) {
            viewsOk = false;
        }
    }

    if (!viewsOk || !BuildRenderObjects(g_target.device)) {
        DestroyTargetObjects();
        return kErrDevice;
    }
    return kOk;
}

int GetTargetHandle(uint64_t* outHandle)
{
    if (outHandle == nullptr) {
        return kErrNull;
    }
    if (g_target.sharedHandle[0] == nullptr) {
        *outHandle = 0u;
        return kErrState;
    }
    *outHandle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(g_target.sharedHandle[0]));
    return kOk;
}

int GetTargetResource(void** outResource)
{
    // 兼容旧入口（B-UNITY-02）：返回 0 号槽位。双槽路径请用 GetTargetResourceAt。
    return GetTargetResourceAt(0u, outResource);
}

int GetTargetResourceAt(uint32_t index, void** outResource)
{
    if (outResource == nullptr) {
        return kErrNull;
    }
    if (index >= kTargetSlots) {
        *outResource = nullptr;
        return kErrArgs;
    }
    if (!g_target.color[index].IsValid() || g_target.device == nullptr) {
        *outResource = nullptr;
        return kErrState;
    }
    *outResource = g_target.device->D3D12ResourceOf(g_target.color[index]);
    return (*outResource != nullptr) ? kOk : kErrDevice;
}

int GetReadableTargetIndex(uint32_t* outIndex)
{
    if (outIndex == nullptr) {
        return kErrNull;
    }
    *outIndex = g_target.readableSlot;
    return kOk;
}

/// 整面回读（TASK-B-UNITY-05）。细节与理由见头文件：状态必须由**拥有纹理的那侧**
/// 转换，行距必须来自设备脚印。
int ReadTargetPixels(void* dst, uint32_t dstRowPitch, uint32_t* outWidth, uint32_t* outHeight)
{
    if (dst == nullptr) {
        return kErrNull;
    }
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (!g_target.color[0].IsValid() || g_target.queue == nullptr || !g_target.cmd.IsValid()) {
        return kErrState;
    }
    const uint32_t width = g_target.width;
    const uint32_t height = g_target.height;
    const uint64_t rowBytes = static_cast<uint64_t>(width) * 4u;
    if (dstRowPitch < rowBytes) {
        return kErrArgs;
    }

    ZrD3D12Device* device = g_target.device;
    ID3D12Device* nativeDevice = device->D3D12Device();
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(g_target.cmd);
    IZrCommandList* cmdList = device->GetCommandList(g_target.cmd);
    ID3D12Resource* texture = device->D3D12ResourceOf(g_target.color[0]);
    if (nativeDevice == nullptr || nativeCmd == nullptr || cmdList == nullptr
        || texture == nullptr) {
        return kErrDevice;
    }

    // 1) 先等本队列排空到此刻：读的必须是"已经画完"的内容（不是正在写的那一帧）
    if (g_target.syncValue > 0u) {
        if (g_target.syncFence != nullptr && g_target.syncEvent != nullptr) {
            if (g_target.syncFence->GetCompletedValue() < g_target.syncValue) {
                g_target.syncFence->SetEventOnCompletion(g_target.syncValue, g_target.syncEvent);
                WaitForSingleObject(g_target.syncEvent, INFINITE);
            }
        } else {
            device->WaitIdle();
        }
    }

    // 2) 真实脚印（RowPitch 是 256 对齐后的值，不是 width*4）
    const D3D12_RESOURCE_DESC texDesc = texture->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed {};
    UINT numRows = 0u;
    UINT64 rowSizeBytes = 0u;
    UINT64 totalBytes = 0u;
    nativeDevice->GetCopyableFootprints(&texDesc, 0u, 1u, 0u, &placed, &numRows, &rowSizeBytes,
        &totalBytes);
    if (placed.Footprint.RowPitch == 0u || totalBytes == 0u) {
        return kErrDevice;
    }

    // 3) READBACK 缓冲（按需建一次；尺寸/行距变了就重建）
    if (!g_target.fullReadback.IsValid() || g_target.fullReadbackBytes < totalBytes
        || g_target.fullReadbackPitch != placed.Footprint.RowPitch) {
        if (g_target.fullReadback.IsValid()) {
            device->Destroy(g_target.fullReadback);
            g_target.fullReadback = ZrBuffer {};
            g_target.fullReadbackMapped = nullptr;
        }
        ZrBufferDesc rbDesc {};
        rbDesc.size = totalBytes;
        rbDesc.readback = true;
        g_target.fullReadback = device->CreateBuffer(rbDesc);
        if (!g_target.fullReadback.IsValid()) {
            return kErrDevice;
        }
        IZrBuffer* iface = device->GetBuffer(g_target.fullReadback);
        g_target.fullReadbackMapped = (iface != nullptr) ? iface->Map() : nullptr;
        if (g_target.fullReadbackMapped == nullptr) {
            device->Destroy(g_target.fullReadback);
            g_target.fullReadback = ZrBuffer {};
            return kErrDevice;
        }
        g_target.fullReadbackPitch = placed.Footprint.RowPitch;
        g_target.fullReadbackBytes = totalBytes;
    }

    // 4) 录制：真实状态 → COPY_SOURCE → 拷贝 → 转回可采样态
    const D3D12_RESOURCE_STATES srvState = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
        | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    const D3D12_RESOURCE_STATES before = g_target.colorState[0];
    cmdList->Begin();
    D3D12_RESOURCE_BARRIER barrier {};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = texture;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    nativeCmd->ResourceBarrier(1u, &barrier);

    D3D12_TEXTURE_COPY_LOCATION copyDst {};
    copyDst.pResource = device->D3D12ResourceOf(g_target.fullReadback);
    copyDst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    copyDst.PlacedFootprint = placed;
    D3D12_TEXTURE_COPY_LOCATION copySrc {};
    copySrc.pResource = texture;
    copySrc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    copySrc.SubresourceIndex = 0u;
    nativeCmd->CopyTextureRegion(&copyDst, 0u, 0u, 0u, &copySrc, nullptr);

    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = srvState;
    nativeCmd->ResourceBarrier(1u, &barrier);

    cmdList->End();
    g_target.queue->Submit(g_target.cmd);
    g_target.colorState[0] = srvState;

    // 5) 等这一次拷贝落盘（不污染 GetSyncStats 的等待统计）
    ++g_target.syncValue;
    if (g_target.syncFence != nullptr) {
        device->D3D12Queue()->Signal(g_target.syncFence, g_target.syncValue);
        if (g_target.syncFence->GetCompletedValue() < g_target.syncValue) {
            g_target.syncFence->SetEventOnCompletion(g_target.syncValue, g_target.syncEvent);
            WaitForSingleObject(g_target.syncEvent, INFINITE);
        }
    } else {
        device->WaitIdle();
    }

    // 6) 逐行拷到调用方缓冲：源行距 = placed.Footprint.RowPitch，目标行距 = dstRowPitch。
    //    绝不 memcpy 整块（那就是"假设紧凑布局"，最后一个行还有尾部对齐字节）。
    const auto* mapped = static_cast<const uint8_t*>(g_target.fullReadbackMapped)
        + placed.Offset; // Offset 只作用于 READBACK 缓冲，不作用于调用方缓冲
    auto* out = static_cast<uint8_t*>(dst);
    const size_t srcPitch = static_cast<size_t>(placed.Footprint.RowPitch);
    for (uint32_t y = 0u; y < height; ++y) {
        std::memcpy(out + static_cast<size_t>(y) * dstRowPitch,
            mapped + static_cast<size_t>(y) * srcPitch, static_cast<size_t>(rowBytes));
    }
    if (outWidth != nullptr) {
        *outWidth = width;
    }
    if (outHeight != nullptr) {
        *outHeight = height;
    }
    return kOk;
}

int GetSyncStats(SyncStats* out)
{
    if (out == nullptr) {
        return kErrNull;
    }
    out->structSize = static_cast<uint32_t>(sizeof(SyncStats));
    out->frames = g_target.frameCount; // RenderFrame 调用数（不是等待次数）
    out->blockedFrames = g_target.syncBlocked;
    out->framesInFlight = FramesInFlight();
    out->lastWaitMs = g_target.syncLastMs;
    out->avgWaitMs = (g_target.syncFrames > 0u)
        ? (g_target.syncTotalMs / static_cast<double>(g_target.syncFrames))
        : 0.0;
    return kOk;
}

int RenderFrame(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (!g_target.color[0].IsValid() || g_target.queue == nullptr || !g_target.cmd.IsValid()) {
        return kErrState;
    }
    ZrD3D12Device* device = g_target.device;
    IZrCommandList* cmdList = device->GetCommandList(g_target.cmd);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(g_target.cmd);
    if (cmdList == nullptr || nativeCmd == nullptr) {
        ++g_target.lastError;
        return kErrDevice;
    }

    // ---- 帧首同步（TASK-B-UNITY-04）：只等**上一帧**（N-1）的 fence --------------
    // 帧 N 提交后不等自己：syncValue 在这一刻 = 帧 N-1 的信号值，等它完成即可。
    // 帧 N-1 在上一个宿主帧就提交了，中间隔了 Unity 自己的一整帧 → 绝大多数时候
    // 已经完成，这里的等待是 0；旧的 WaitIdle 则要等本帧（刚提交）干完。
    if (g_target.syncValue > 0u) {
        WaitForSync(g_target.syncValue);
    }

    // 双槽：本帧画 slot，宿主采样 otherSlot（= 上一帧那份，刚刚确认完成）
    const uint32_t slot = g_target.frameCount & 1u;
    const uint32_t otherSlot = slot ^ 1u;

    // 中心像素回读延后一帧取：等完帧 N-1 之后它的 COPY 才真正落盘
    if (g_target.pendingReadback && g_target.readbackMapped != nullptr) {
        const uint8_t* px = static_cast<const uint8_t*>(g_target.readbackMapped);
        // BGRA 字节序：纹理存的是 B,G,R,A
        g_target.centerRGBA[0] = px[2];
        g_target.centerRGBA[1] = px[1];
        g_target.centerRGBA[2] = px[0];
        g_target.centerRGBA[3] = px[3];
        ++g_target.readbackCount;
        g_target.pendingReadback = false;
    }

    // ---- TASK-B-PLANET-04：行星通道帧首（§8 延迟一帧协议）-------------------------
    // GPU 上传/释放/档位应用只在此边界（锁外），绘制快照（决策拷贝）随后锁外消费。
    // 上传成功 chunk 本帧即活（§7.1"主线程 Create 后下一 RenderFrame 生效"）。
    planet_detail::FrameBegin(device);
    const bool planetPath = planet_detail::g_frame.cameraUsable
        && planet_detail::g_frame.drawCount > 0u;

    // ---- 帧内容：调用方提交优先，无提交回落硬编码三 cube ------------------------
    // 这个分叉是任务判据 V1/V4 的分水岭：有相机 + 有绘制项 = 画 Unity 的场景；
    // 否则保持 B-UNITY-02 的演示（--draw 与既有验证不受影响）。行星通道有内容时
    // 同样跳过 cube 回落（帧非空——回落只为兜底空帧，--draw 无 chunk 不受影响）。
    const bool scenePath = g_scene.cameraSet && g_scene.drawCount > 0u;
    uint32_t frameDrawCount = 0u;
    ZrPipelineState framePso = g_target.pso;
    if (scenePath) {
        if (!EnsureConstantBuffers(g_scene.drawCount)) {
            ++g_target.lastError;
            return kErrDevice;
        }
        float vp[16];
        MatMultiply(g_scene.proj, g_scene.view, vp);
        const float lightDirS[3] = {0.5f, 0.8f, 0.6f};
        const float invLenS = 1.0f
            / std::sqrt(lightDirS[0] * lightDirS[0] + lightDirS[1] * lightDirS[1]
                  + lightDirS[2] * lightDirS[2]);
        for (uint32_t i = 0; i < g_scene.drawCount; ++i) {
            const SceneDrawCmd& cmd = g_scene.draws[i];
            const MeshEntry* mesh = LookupMesh(
                MakeMeshHandle(cmd.meshSlot, cmd.meshGeneration));
            if (mesh == nullptr) {
                continue; // 句柄在提交后被释放：跳过即可，不毁整帧
            }
            SceneDrawConstants constants;
            std::memset(&constants, 0, sizeof(constants));
            MatMultiply(vp, cmd.world, constants.mvp);
            std::memcpy(constants.world, cmd.world, sizeof(constants.world));
            constants.lightDir[0] = lightDirS[0] * invLenS;
            constants.lightDir[1] = lightDirS[1] * invLenS;
            constants.lightDir[2] = lightDirS[2] * invLenS;
            constants.lightColor[0] = 1.0f;
            constants.lightColor[1] = 0.95f;
            constants.lightColor[2] = 0.9f;
            constants.lightColor[3] = 1.0f;
            std::memcpy(constants.albedo, cmd.rgba, sizeof(constants.albedo));
            std::memcpy(g_target.cbMapped[frameDrawCount], &constants, sizeof(constants));
            g_frameDraws[frameDrawCount].vb = mesh->vb;
            g_frameDraws[frameDrawCount].ib = mesh->ib;
            g_frameDraws[frameDrawCount].indexCount = mesh->indexCount;
            ++frameDrawCount;
        }
        framePso = g_target.psoScene;
    }

    if (!scenePath && !planetPath) {
        // ---- 回落路径：绕 Y 匀速旋转 + 固定俯仰，三个 cube 横向排开（B-UNITY-02 原样）
        const float angle = 2.0f * kPi * static_cast<float>(g_target.frameCount % 120u) / 120.0f;
        const float lightDir[3] = {0.5f, 0.8f, 0.6f};
        const float invLen = 1.0f
            / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
                  + lightDir[2] * lightDir[2]);
        const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};

        float proj[16];
        MatPerspectiveFovLH(kPi / 3.0f,
            static_cast<float>(g_target.width) / static_cast<float>(g_target.height), 0.1f, 100.0f,
            proj);
        const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
        float rotY[16];
        MatRotationY(angle, rotY);
        float rotX[16];
        MatRotationX(0.35f, rotX);
        float rx[16];
        MatMultiply(rotX, rotY, rx);
        float pv[16];
        MatMultiply(proj, view, pv);
        for (uint32_t i = 0; i < kDrawItems; ++i) {
            const float tx = (static_cast<float>(i) - 1.0f) * 1.6f;
            float trans[16] = {};
            MatTranslation(tx, 0.0f, 3.2f, trans);
            float model[16];
            MatMultiply(trans, rx, model);
            float mvp[16];
            MatMultiply(pv, model, mvp);
            DrawConstants constants;
            std::memcpy(constants.mvp, mvp, sizeof(mvp));
            constants.lightDir[0] = lightDir[0] * invLen;
            constants.lightDir[1] = lightDir[1] * invLen;
            constants.lightDir[2] = lightDir[2] * invLen;
            constants.lightDir[3] = 0.0f;
            std::memcpy(constants.lightColor, lightColor, sizeof(lightColor));
            std::memcpy(g_target.cbMapped[i], &constants, sizeof(constants));
            g_frameDraws[i].vb = g_target.vb;
            g_frameDraws[i].ib = g_target.ib;
            g_frameDraws[i].indexCount = kCubeIndexCount;
        }
        frameDrawCount = kDrawItems;
    }

    // ---- 行星通道 CB 填充（TASK-B-PLANET-04；§3.2/D3 相机相对模型矩阵）--------------
    // 模型平移 = float(anchorAbs − camAbs)：double 域相减后一次 cast——chunk 不走
    // DrawMesh 的 float world16 的全部理由（100km 爬升段 ULP≈1m 的抖动回流）。
    bool planetDrawActive = false;
    if (planetPath) {
        if (!planet_detail::EnsurePlanetConstantBuffers(planet_detail::g_frame.drawCount)) {
            // 常量池扩容失败（设备级错误）：本帧行星整体跳过，计入剔除侧
            ++g_target.lastError;
            planet_detail::g_drawnLast = 0u;
            planet_detail::g_culledLast += planet_detail::g_frame.drawCount;
        } else {
            float vp[16];
            MatMultiply(g_scene.proj, g_scene.view, vp);
            const double relX = planet_detail::g_frame.camAbs[0]
                - planet_detail::g_frame.center[0];
            const double relY = planet_detail::g_frame.camAbs[1]
                - planet_detail::g_frame.center[1];
            const double relZ = planet_detail::g_frame.camAbs[2]
                - planet_detail::g_frame.center[2];
            const double camLen = std::sqrt(relX * relX + relY * relY + relZ * relZ);
            const double altM = (camLen > 0.0)
                ? (camLen - zplanet::kPlanetRadiusM) : 0.0;
            // 大气色/雾（B-TECH-04 CPU 预混口径原样移植）
            const double altT = std::min(1.0, std::max(0.0,
                (std::log10(std::max(altM, 0.0) + 1.0) - 3.0)
                    / (std::log10(kPlanetClimbAltM) - 3.0)));
            float zenith[3] = {};
            float horizon[3] = {};
            for (int c = 0; c < 3; ++c) {
                zenith[c] = static_cast<float>(static_cast<double>(kPlanetZenithBlue[c])
                    + (static_cast<double>(kPlanetSpace[c])
                          - static_cast<double>(kPlanetZenithBlue[c])) * altT);
                horizon[c] = static_cast<float>(static_cast<double>(kPlanetHorizonBlue[c])
                    + (static_cast<double>(kPlanetSpace[c])
                          - static_cast<double>(kPlanetHorizonBlue[c])) * altT);
            }
            const double fogDist = (altM < 1.0e6)
                ? (kPlanetFogGroundM
                      + (kPlanetFogClimbM - kPlanetFogGroundM) * (altM / 1.0e6))
                : 0.0;
            // 渲染空间 up = view 旋转 × 世界 up（径向；行主序旋转 v'_c = Σ_r m[r*4+c]·v_r）
            double worldUp[3] = {0.0, 1.0, 0.0};
            if (camLen > 0.0) {
                worldUp[0] = relX / camLen;
                worldUp[1] = relY / camLen;
                worldUp[2] = relZ / camLen;
            }
            float upRender[3] = {};
            for (int c = 0; c < 3; ++c) {
                upRender[c] = g_scene.view[0 * 4 + c] * static_cast<float>(worldUp[0])
                    + g_scene.view[1 * 4 + c] * static_cast<float>(worldUp[1])
                    + g_scene.view[2 * 4 + c] * static_cast<float>(worldUp[2]);
            }
            for (uint32_t i = 0; i < planet_detail::g_frame.drawCount; ++i) {
                const planet_detail::PlanetFrameDraw& d = planet_detail::g_frame.draws[i];
                // double 域相减后一次 cast（设计稿 §9 表第 7 行的原生执行点）
                const float tx = static_cast<float>(d.originAbs[0]
                    - planet_detail::g_frame.camAbs[0]);
                const float ty = static_cast<float>(d.originAbs[1]
                    - planet_detail::g_frame.camAbs[1]);
                const float tz = static_cast<float>(d.originAbs[2]
                    - planet_detail::g_frame.camAbs[2]);
                float model[16];
                MatTranslation(tx, ty, tz, model);
                float mvp[16];
                MatMultiply(vp, model, mvp);
                Planet2DrawConstants c {};
                std::memcpy(c.mvp, mvp, sizeof(c.mvp));
                std::memcpy(c.model, model, sizeof(c.model));
                c.params[0] = 0.0f; // mode 0：地形 chunk（mode 1 天穹属窗口演示层）
                c.params[1] = static_cast<float>(fogDist);
                c.params[2] = static_cast<float>(altT);
                c.params[3] = static_cast<float>(kPlanetContourM);
                c.fogColor[0] = horizon[0]; c.fogColor[1] = horizon[1];
                c.fogColor[2] = horizon[2]; c.fogColor[3] = 1.0f;
                c.zenithColor[0] = zenith[0]; c.zenithColor[1] = zenith[1];
                c.zenithColor[2] = zenith[2]; c.zenithColor[3] = 1.0f;
                c.horizonColor[0] = horizon[0]; c.horizonColor[1] = horizon[1];
                c.horizonColor[2] = horizon[2]; c.horizonColor[3] = 1.0f;
                c.upVec[0] = upRender[0]; c.upVec[1] = upRender[1];
                c.upVec[2] = upRender[2]; c.upVec[3] = 0.0f;
                std::memcpy(g_target.planetCbMapped[i], &c, sizeof(c));
            }
            planetDrawActive = true;
        }
    }

    const bool doReadback = (g_target.frameCount % kReadbackInterval) == 0u;

    // ---- 一帧：RG（Import → Clear → CubeDraw → [Readback]）------------------------
    cmdList->Begin();
    RgGraph graph;
    RgHandle colorH = graph.Import(
        RgResourceDesc::ImportImage("zr_shared_target", g_target.color[slot], g_target.colorDesc));
    RgHandle depthH = graph.Import(
        RgResourceDesc::ImportImage("zr_depth", g_target.depth[slot], g_target.depthDesc));

    graph.AddPass("ZRTargetClear",
        [&](RgPass& pass) {
            pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        [&](RgContext& ctx) {
            cmdList->ClearColorImage(ctx.GetImage(colorH), ZrImageLayout::ColorAttachmentOptimal,
                kClearColor, ZrSubresourceRange::All());
            nativeCmd->ClearRenderTargetView(device->D3D12ViewCpuOf(g_target.colorRtv[slot]), kClearColor,
                0u, nullptr);
            nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(g_target.depthDsv[slot]),
                D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
        });

    graph.AddPass("ZRTargetSceneDraw",
        [&](RgPass& pass) {
            pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        [&](RgContext&) {
            cmdList->SetPipelineState(framePso);
            cmdList->SetRenderTarget(g_target.colorRtv[slot], g_target.depthDsv[slot]);
            cmdList->SetViewportScissor(g_target.width, g_target.height);
            // 逐项绑定：场景里每个物件可能是不同 mesh（VB/IB 都得换），
            // 同一 mesh 连续出现时只换常量缓冲（常见情形：一批敌人/道具）。
            ZrBuffer boundVb {};
            ZrBuffer boundIb {};
            for (uint32_t i = 0; i < frameDrawCount; ++i) {
                const FrameDraw& draw = g_frameDraws[i];
                if (draw.vb.raw != boundVb.raw) {
                    ZrBuffer vbs[1] = {draw.vb};
                    cmdList->SetVertexBuffers(vbs, 1u);
                    boundVb = draw.vb;
                }
                if (draw.ib.raw != boundIb.raw) {
                    cmdList->SetIndexBuffer(draw.ib);
                    boundIb = draw.ib;
                }
                cmdList->SetGraphicsConstantBuffer(0u, g_target.cb[i]);
                cmdList->DrawIndexedInstanced(draw.indexCount, 1u, 0u, 0u, 0u);
            }
        });

    // ---- TASK-B-PLANET-04：行星通道（场景路径后追加、同深度缓冲）--------------------
    // planet2.hlsl mode 0；逐 chunk VB + 共享档位 IB + 逐绘 CB。地平线剔除已在
    // 帧首快照完成（drawCount 即剔除后的绘制集合）。
    if (planetDrawActive) {
        graph.AddPass("ZRPlanetDraw",
            [&](RgPass& pass) {
                pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                    ZrAccessBits::ColorAttachmentWrite,
                    ZrPipelineStageBits::ColorAttachmentOutput);
                pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                    ZrAccessBits::DepthStencilAttachmentWrite,
                    ZrPipelineStageBits::LateFragmentTests);
            },
            [&](RgContext&) {
                cmdList->SetPipelineState(g_target.psoPlanet);
                cmdList->SetRenderTarget(g_target.colorRtv[slot], g_target.depthDsv[slot]);
                cmdList->SetViewportScissor(g_target.width, g_target.height);
                for (uint32_t i = 0; i < planet_detail::g_frame.drawCount; ++i) {
                    const planet_detail::PlanetFrameDraw& d
                        = planet_detail::g_frame.draws[i];
                    ZrBuffer vbs[1] = {d.vb};
                    cmdList->SetVertexBuffers(vbs, 1u);
                    cmdList->SetIndexBuffer(
                        (d.tier >= 1u) ? g_target.planetIbNear : g_target.planetIbFar);
                    cmdList->SetGraphicsConstantBuffer(0u, g_target.planetCb[i]);
                    cmdList->DrawIndexedInstanced(planet_detail::TierIndices(d.tier), 1u,
                        0u, 0u, 0u);
                }
            });
    }

    if (doReadback) {
        graph.AddPass("ZRTargetReadback",
            [&](RgPass& pass) {
                pass.Read(colorH, ZrImageLayout::TransferSrcOptimal, ZrAccessBits::TransferRead,
                    ZrPipelineStageBits::Transfer)
                    .MarkRoot();
            },
            [&](RgContext& ctx) {
                ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(colorH));
                const UINT cx = g_target.width / 2u;
                const UINT cy = g_target.height / 2u;
                const D3D12_BOX box = {cx, cy, 0u, cx + 1u, cy + 1u, 1u};
                D3D12_TEXTURE_COPY_LOCATION dst {};
                dst.pResource = device->D3D12ResourceOf(g_target.readback);
                dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                dst.PlacedFootprint.Footprint.Width = 1u;
                dst.PlacedFootprint.Footprint.Height = 1u;
                dst.PlacedFootprint.Footprint.Depth = 1u;
                dst.PlacedFootprint.Footprint.RowPitch = g_target.readbackPitch;
                D3D12_TEXTURE_COPY_LOCATION src {};
                src.pResource = texture;
                src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                src.SubresourceIndex = 0u;
                nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
            });
    }

    if (!graph.Compile()) {
        cmdList->End();
        return kErrState;
    }
    graph.ExecuteAliased(*device, *cmdList);

    // ---- 帧末屏障：→ PIXEL_SHADER_RESOURCE（Unity 采样的前提）---------------------
    // 这是本任务成立的关键一步：返回前纹理必须处于"可采样"状态，Unity 才会把它当作
    // 一张普通纹理绑定 SRV（PIXEL|NON_PIXEL 两级都置上，见 translate 的布局映射）。
    const ZrImageLayout layoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                              : ZrImageLayout::ColorAttachmentOptimal;
    ZrImageBarrier toSrv = MakeBarrier(g_target.color[slot], layoutNow,
        ZrImageLayout::ShaderReadOnlyOptimal,
        doReadback ? ZrAccessBits::TransferRead : ZrAccessBits::ColorAttachmentWrite,
        doReadback ? ZrPipelineStageBits::Transfer : ZrPipelineStageBits::ColorAttachmentOutput,
        ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
    cmdList->ResourceBarrier(&toSrv, 1u, nullptr, 0u);
    // 帧末真实状态（整面回读要从这里出发做屏障；见 ReadTargetPixels）
    g_target.colorState[slot] = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
        | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;

    // 深度回 Undefined（下一帧 RG 起点假设；与 --draw 一致）
    ZrImageBarrier depthReset = MakeBarrier(g_target.depth[slot],
        ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
        ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
        ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
    cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);

    cmdList->End();
    g_target.queue->Submit(g_target.cmd);

    // ---- 提交后只 Signal，不排空（TASK-B-UNITY-04）--------------------------------
    // 旧实现这里 device->WaitIdle()：CPU 必须等本帧 GPU 干完才返回，帧间完全串行。
    // 现在只把"本帧完成"记进 fence：下一帧开头等 N-1 的完成，本帧的完成由再下一帧
    // 回收。宿主采样的是上一帧那个槽位（已确认完成），画面不会被撕裂。
    ++g_target.syncValue;
    if (g_target.syncFence != nullptr) {
        device->D3D12Queue()->Signal(g_target.syncFence, g_target.syncValue);
    }

    graph.FlushRetired(*device);

    ++g_target.frameCount;
    // 交给宿主的永远是"上一帧那份"；帧 0 还没有上一帧 → 保持 0 号槽
    if (g_target.frameCount >= 2u) {
        g_target.readableSlot = otherSlot;
    }
    if (scenePath) {
        ++g_scene.frameCount;
        g_scene.drawTotal += frameDrawCount;
        g_scene.lastDrawCount = frameDrawCount;
    }
    if (doReadback) {
        // 回读结果要等这一帧真正完成才有效 → 下一帧的帧首同步之后取
        g_target.pendingReadback = true;
    }
    return kOk;
}

void ReleaseTarget(void)
{
    // 只释放目标与渲染链，保留设备包装：尺寸变化时可直接再 CreateTarget，
    // 不必回头找宿主要一张纹理重新接入。进程退出时随 DLL 卸载一并回收。
    DestroyTargetObjects();
    // TASK-B-PSO-01：Unity 侧设备包装按设计不在 ReleaseTarget 销毁（DLL 卸载才回收），
    // 析构里的自动刷写因此不会在播放器退出时发生 —— 这里显式刷写 PSO 缓存
    // （幂等：没有新条目时不写盘；尺寸变化时只是空刷写，代价可忽略）。
    if (g_target.device != nullptr) {
        g_target.device->FlushPsoCache();
    }
}

int GetStats(TargetStats* outStats)
{
    if (outStats == nullptr) {
        return kErrNull;
    }
    outStats->frameCount = g_target.frameCount;
    outStats->width = g_target.width;
    outStats->height = g_target.height;
    outStats->readbackCount = g_target.readbackCount;
    outStats->sharedHandle = static_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(g_target.sharedHandle[0]));
    for (uint32_t i = 0; i < 4u; ++i) {
        outStats->centerRGBA[i] = g_target.centerRGBA[i];
    }
    outStats->lastError = g_target.lastError;
    return kOk;
}

bool HasTarget(void)
{
    return g_target.color[0].IsValid();
}

const char* EmbeddedFirstDrawHlsl(uint32_t* outSize)
{
    const uint32_t size = static_cast<uint32_t>(sizeof(kFirstDrawHlsl) - 1u);
    if (outSize != nullptr) {
        *outSize = size;
    }
    return kFirstDrawHlsl;
}

const char* EmbeddedPlanet2Hlsl(uint32_t* outSize)
{
    const uint32_t size = static_cast<uint32_t>(sizeof(kPlanet2Hlsl) - 1u);
    if (outSize != nullptr) {
        *outSize = size;
    }
    return kPlanet2Hlsl;
}

// ---- TASK-B-UNITY-03：场景提交 ------------------------------------------------

int SetCamera(const float* view16, const float* proj16)
{
    if (view16 == nullptr || proj16 == nullptr) {
        return kErrNull;
    }
    std::memcpy(g_scene.view, view16, sizeof(g_scene.view));
    std::memcpy(g_scene.proj, proj16, sizeof(g_scene.proj));
    g_scene.cameraSet = true;
    return kOk;
}

int CreateMesh(const float* vertices, uint32_t vertexCount, uint32_t strideFloats,
    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle)
{
    if (vertices == nullptr || indices == nullptr || outHandle == nullptr) {
        return kErrNull;
    }
    *outHandle = 0u;
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    // 只支持 v1 固定顶点布局（Pos3f+Normal3f+Uv2f = 8 float / 32B）：
    // 与 first_draw.hlsl / ZrD3D12_GraphicsInputLayout 的 stride 同源。
    if (vertexCount == 0u || indexCount == 0u || strideFloats != 8u) {
        return kErrArgs;
    }

    uint32_t slot = kMeshSlots;
    for (uint32_t i = 0; i < kMeshSlots; ++i) {
        if (!g_meshes[i].used) {
            slot = i;
            break;
        }
    }
    if (slot == kMeshSlots) {
        return kErrState; // 槽位耗尽（512 种 mesh 对常规场景足够）
    }

    MeshEntry& entry = g_meshes[slot];
    ZrBufferDesc vbDesc {};
    vbDesc.size = static_cast<uint64_t>(vertexCount) * 32u;
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    const ZrBuffer vb = g_target.device->CreateBuffer(vbDesc, vertices);
    ZrBufferDesc ibDesc {};
    ibDesc.size = static_cast<uint64_t>(indexCount) * 4u;
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    const ZrBuffer ib = g_target.device->CreateBuffer(ibDesc, indices);
    if (!vb.IsValid() || !ib.IsValid()) {
        if (vb.IsValid()) {
            g_target.device->Destroy(vb);
        }
        if (ib.IsValid()) {
            g_target.device->Destroy(ib);
        }
        return kErrDevice;
    }

    if (entry.generation == 0u) {
        entry.generation = 1u; // 槽位首次启用
    }
    entry.used = true;
    entry.vb = vb;
    entry.ib = ib;
    entry.indexCount = indexCount;
    ++g_scene.uploadCount;
    *outHandle = MakeMeshHandle(slot, entry.generation);
    return kOk;
}

void DestroyMesh(uint64_t handle)
{
    MeshEntry* entry = LookupMesh(handle);
    if (entry == nullptr || g_target.device == nullptr) {
        return;
    }
    // 缓冲可能被上一帧的命令列表引用：等 GPU 排空再放（调用频率 = mesh 种类数，非每帧）
    g_target.device->WaitIdle();
    if (entry->vb.IsValid()) {
        g_target.device->Destroy(entry->vb);
    }
    if (entry->ib.IsValid()) {
        g_target.device->Destroy(entry->ib);
    }
    entry->vb = ZrBuffer {};
    entry->ib = ZrBuffer {};
    entry->indexCount = 0u;
    entry->used = false;
    entry->generation += 1u; // 代际推进：旧句柄此后全部失效
}

int DrawMesh(uint64_t handle, const float* world16, const float* rgba)
{
    if (world16 == nullptr) {
        return kErrNull;
    }
    const MeshEntry* mesh = LookupMesh(handle);
    if (mesh == nullptr) {
        return kErrHandle;
    }
    if (g_scene.drawCount >= kMaxSceneDraws) {
        return kErrState;
    }
    SceneDrawCmd& cmd = g_scene.draws[g_scene.drawCount];
    cmd.meshSlot = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    cmd.meshGeneration = static_cast<uint32_t>(handle >> 32);
    std::memcpy(cmd.world, world16, sizeof(cmd.world));
    if (rgba != nullptr) {
        std::memcpy(cmd.rgba, rgba, sizeof(cmd.rgba));
    } else {
        cmd.rgba[0] = 1.0f;
        cmd.rgba[1] = 1.0f;
        cmd.rgba[2] = 1.0f;
        cmd.rgba[3] = 1.0f;
    }
    ++g_scene.drawCount;
    return kOk;
}

int BeginScene(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    g_scene.drawCount = 0u;
    return kOk;
}

int EndScene(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    g_scene.lastDrawCount = g_scene.drawCount;
    return kOk;
}

int GetSceneStats(SceneStats* out)
{
    if (out == nullptr) {
        return kErrNull;
    }
    out->structSize = static_cast<uint32_t>(sizeof(SceneStats));
    out->drawCount = g_scene.lastDrawCount;
    uint32_t meshes = 0u;
    for (uint32_t i = 0; i < kMeshSlots; ++i) {
        if (g_meshes[i].used) {
            ++meshes;
        }
    }
    out->meshCount = meshes;
    out->reserved = 0u;
    out->uploadCount = g_scene.uploadCount;
    out->sceneFrameCount = g_scene.frameCount;
    out->drawTotal = g_scene.drawTotal;
    return kOk;
}

} // namespace unity_target
} // namespace zr
